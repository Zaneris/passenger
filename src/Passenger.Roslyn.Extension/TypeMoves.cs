using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Simplification;

namespace Passenger.Roslyn.Extension;

/// <summary>Move to Another File and Move to Namespace for a type declared in a namespace or at a file's top: the
/// type taken into a new file beside its own with the usings it needs, or into another namespace, every file that uses
/// it given a using of that namespace (or its qualified name changed) and the type a using of the one it left where it
/// still uses that one's types.</summary>
internal static class TypeMoves
{
    public static async Task<TypeSource> DescribeAsync(Document document, Place at, CancellationToken cancel)
    {
        (INamedTypeSymbol? type, BaseTypeDeclarationSyntax? declaration, string? problem) = await FindAsync(document, at, cancel).ConfigureAwait(false);
        if (type is null || declaration is null)
        {
            return new TypeSource(problem, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0);
        }
        return new TypeSource(null, type.Name, Kind(type), Interfaces.Namespace(type), Path.GetFileName(document.FilePath ?? string.Empty), type.Name + ".cs",
            TopLevelTypes(declaration.SyntaxTree.GetRoot(cancel)).Count());
    }

    public static async Task<Refactored> ToFileAsync(Document document, TypeToFile move, CancellationToken cancel)
    {
        (INamedTypeSymbol? type, BaseTypeDeclarationSyntax? declaration, string? problem) = await FindAsync(document, move.At, cancel).ConfigureAwait(false);
        if (type is null || declaration is null)
        {
            return Changes.Refused(problem!);
        }
        CompilationUnitSyntax root = (CompilationUnitSyntax)(await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
        if (TopLevelTypes(root).Count() < 2)
        {
            return Changes.Refused($"'{type.Name}' is the only type in {Path.GetFileName(document.FilePath)}: there is nothing to move it away from");
        }
        string fileName = move.FileName.Trim();
        if (Interfaces.FileProblem(document, fileName, type.Name) is { } wrongFile)
        {
            return Changes.Refused(wrongFile);
        }
        Solution solution = await MovedToFileAsync(document, root, declaration, fileName, cancel).ConfigureAwait(false);
        return await Changes.BetweenAsync(document.Project.Solution, solution, cancel).ConfigureAwait(false);
    }

    public static async Task<Refactored> ToNamespaceAsync(Document document, TypeToNamespace move, CancellationToken cancel)
    {
        (INamedTypeSymbol? type, BaseTypeDeclarationSyntax? declaration, string? problem) = await FindAsync(document, move.At, cancel).ConfigureAwait(false);
        if (type is null || declaration is null)
        {
            return Changes.Refused(problem!);
        }
        string from = Interfaces.Namespace(type);
        if (RefactoringChecks.WhyNotNamespace(move.Namespace, type.Name, from) is { } wrong)
        {
            return Changes.Refused(wrong);
        }
        string target = RefactoringChecks.Namespace(move.Namespace);
        Compilation compilation = (await document.Project.GetCompilationAsync(cancel).ConfigureAwait(false))!;
        if (compilation.GetTypeByMetadataName($"{target}.{type.MetadataName}") is not null)
        {
            return Changes.Refused($"A type named '{type.Name}' already exists in namespace '{target}'");
        }
        Solution before = document.Project.Solution;

        // Its uses first, as the solution has them: each named in full in the new namespace, for the simplifier to shorten.
        SyntaxAnnotation moved = new();
        Dictionary<DocumentId, DocumentEditor> editors = [];
        async Task<DocumentEditor> EditorOf(Document file)
        {
            if (!editors.TryGetValue(file.Id, out DocumentEditor? editor))
            {
                editor = await DocumentEditor.CreateAsync(file, cancel).ConfigureAwait(false);
                editors[file.Id] = editor;
            }
            return editor;
        }
        foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(type, before, cancel).ConfigureAwait(false))
        {
            foreach (ReferenceLocation place in referenced.Locations)
            {
                if (place.IsImplicit || !place.Location.IsInSource || place.Document is SourceGeneratedDocument
                    || (place.Document.Id == document.Id && declaration.Span.Contains(place.Location.SourceSpan)))
                {
                    continue;
                }
                SyntaxNode where = (await place.Document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!.FindNode(place.Location.SourceSpan, getInnermostNodeForTie: true);
                if (where is not SimpleNameSyntax name || name.Parent is UsingDirectiveSyntax)
                {
                    continue;
                }
                DocumentEditor editor = await EditorOf(place.Document).ConfigureAwait(false);
                if (name.Parent is QualifiedNameSyntax qualified && qualified.Right == name || name.Parent is MemberAccessExpressionSyntax access && access.Name == name)
                {
                    SyntaxNode whole = name.Parent;
                    editor.ReplaceNode(whole, (current, _) => Qualified(target, ((ExpressionSyntax)current).ChildNodes().OfType<SimpleNameSyntax>().Last(), current));
                }
                else if (!InNamespace(name, target))
                {
                    editor.ReplaceNode(name, (current, _) => Qualified(target, (SimpleNameSyntax)current, current));
                }
            }
        }

        // The type's own names of what is in the namespace it leaves, given the same treatment, so its file gets a using of it.
        SemanticModel model = (await document.GetSemanticModelAsync(cancel).ConfigureAwait(false))!;
        DocumentEditor own = await EditorOf(document).ConfigureAwait(false);
        foreach (SimpleNameSyntax name in declaration.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (name.Parent is QualifiedNameSyntax { Right: var right } && right == name || name.Parent is MemberAccessExpressionSyntax { Name: var member } && member == name
                || name.Parent is NameColonSyntax or NameEqualsSyntax || name.Parent is MemberBindingExpressionSyntax)
            {
                continue;
            }
            if (model.GetSymbolInfo(name, cancel).Symbol is INamedTypeSymbol used && !SymbolEqualityComparer.Default.Equals(used.OriginalDefinition, type)
                && used.ContainingType is null && Interfaces.Namespace(used) is { Length: > 0 } space && Visible(space, from) && !Visible(space, target))
            {
                own.ReplaceNode(name, (current, _) => Qualified(space, (SimpleNameSyntax)current, current));
            }
        }

        own.ReplaceNode(declaration, (current, _) => current.WithAdditionalAnnotations(moved));
        Solution solution = before;
        foreach ((DocumentId id, DocumentEditor editor) in editors)
        {
            solution = solution.WithDocumentSyntaxRoot(id, editor.GetChangedRoot());
        }
        Document file = solution.GetDocument(document.Id)!;
        CompilationUnitSyntax root = (CompilationUnitSyntax)(await file.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
        BaseTypeDeclarationSyntax placed = root.GetAnnotatedNodes(moved).OfType<BaseTypeDeclarationSyntax>().Single();
        Solution renamed;
        if (placed.Parent is BaseNamespaceDeclarationSyntax { Parent: CompilationUnitSyntax } holder && holder.Members.Count == 1)
        {
            renamed = solution.WithDocumentSyntaxRoot(file.Id, root.ReplaceNode(holder, holder.WithName(SyntaxFactory.ParseName(target).WithTriviaFrom(holder.Name))));
        }
        else if (placed.Parent is CompilationUnitSyntax && TopLevelTypes(root).Count() == 1)
        {
            renamed = solution.WithDocumentSyntaxRoot(file.Id, Wrapped(root, placed, target));
        }
        else if (placed.Parent is NamespaceDeclarationSyntax block && block.Parent is CompilationUnitSyntax)
        {
            NamespaceDeclarationSyntax made = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(target))
                .AddMembers(Changes.WithoutLeadingBlankLines(placed))
                .WithLeadingTrivia(SyntaxFactory.EndOfLine("\n"))
                .WithAdditionalAnnotations(Microsoft.CodeAnalysis.Formatting.Formatter.Annotation);
            CompilationUnitSyntax without = root.RemoveNode(placed, SyntaxRemoveOptions.KeepNoTrivia)!;
            NamespaceDeclarationSyntax left = without.Members.OfType<NamespaceDeclarationSyntax>().First(n => n.Name.ToString() == block.Name.ToString());
            renamed = solution.WithDocumentSyntaxRoot(file.Id, without.InsertNodesAfter(left, [made]));
        }
        else
        {
            // A file-scoped namespace holds one namespace only: the type goes to a file of its own, then to its namespace.
            if (Interfaces.FileProblem(file, type.Name + ".cs", type.Name) is { } wrongFile)
            {
                return Changes.Refused(wrongFile);
            }
            Solution apart = await MovedToFileAsync(file, root, placed, type.Name + ".cs", cancel).ConfigureAwait(false);
            Document created = apart.GetDocument(apart.GetChanges(solution).GetProjectChanges().SelectMany(p => p.GetAddedDocuments()).First())!;
            CompilationUnitSyntax createdRoot = (CompilationUnitSyntax)(await created.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
            BaseTypeDeclarationSyntax there = createdRoot.GetAnnotatedNodes(moved).OfType<BaseTypeDeclarationSyntax>().Single();
            renamed = apart.WithDocumentSyntaxRoot(created.Id, Retargeted(createdRoot, there, target));
        }

        renamed = await WithoutLostUsingsAsync(renamed, from, cancel).ConfigureAwait(false);
        foreach (DocumentId id in renamed.GetChanges(before).GetProjectChanges().SelectMany(p => p.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true).Concat(p.GetAddedDocuments())).ToList())
        {
            Document changed = renamed.GetDocument(id)!;
            changed = await ImportAdder.AddImportsAsync(changed, Simplifier.AddImportsAnnotation, cancellationToken: cancel).ConfigureAwait(false);
            changed = await Simplifier.ReduceAsync(changed, Simplifier.Annotation, cancellationToken: cancel).ConfigureAwait(false);
            changed = await Microsoft.CodeAnalysis.Formatting.Formatter.FormatAsync(changed, Microsoft.CodeAnalysis.Formatting.Formatter.Annotation, cancellationToken: cancel).ConfigureAwait(false);
            renamed = changed.Project.Solution;
        }
        return await Changes.BetweenAsync(before, renamed, cancel).ConfigureAwait(false);
    }

    /// <summary>The type at the caret, when it is one Move can take: declared in a namespace or at a file's top.</summary>
    private static async Task<(INamedTypeSymbol? Type, BaseTypeDeclarationSyntax? Declaration, string? Problem)> FindAsync(Document document, Place at, CancellationToken cancel)
    {
        int offset = await Changes.OffsetAsync(document, at, cancel).ConfigureAwait(false);
        (_, BaseTypeDeclarationSyntax? declaration) = await Changes.TypeAtAsync(document, offset, cancel).ConfigureAwait(false);
        while (declaration is not null && declaration.Parent is BaseTypeDeclarationSyntax outer)
        {
            declaration = outer;
        }
        if (declaration is null || (await document.GetSemanticModelAsync(cancel).ConfigureAwait(false))!.GetDeclaredSymbol(declaration, cancel) is not INamedTypeSymbol top)
        {
            return (null, null, "Move moves a type: put the caret on one");
        }
        return (top, declaration, null);
    }

    private static string Kind(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Interface => "interface",
        TypeKind.Struct => type.IsRecord ? "record struct" : "struct",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ => type.IsRecord ? "record" : "class",
    };

    private static IEnumerable<BaseTypeDeclarationSyntax> TopLevelTypes(SyntaxNode root) =>
        root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<BaseTypeDeclarationSyntax>();

    /// <summary>The solution with a type taken out of its file into a new one beside it: the new file the old one less every
    /// other type and assembly attribute, and less the usings it then does not need.</summary>
    private static async Task<Solution> MovedToFileAsync(Document document, CompilationUnitSyntax root, BaseTypeDeclarationSyntax declaration, string fileName, CancellationToken cancel)
    {
        SyntaxAnnotation kept = new();
        CompilationUnitSyntax marked = root.ReplaceNode(declaration, declaration.WithAdditionalAnnotations(kept));
        BaseTypeDeclarationSyntax moving = marked.GetAnnotatedNodes(kept).OfType<BaseTypeDeclarationSyntax>().Single();
        List<SyntaxNode> others = [.. TopLevelTypes(marked).Where(t => t != moving), .. marked.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<DelegateDeclarationSyntax>(), .. marked.AttributeLists, .. marked.Members.OfType<GlobalStatementSyntax>()];
        CompilationUnitSyntax alone = marked.RemoveNodes(others, SyntaxRemoveOptions.KeepNoTrivia)!;
        alone = alone.RemoveNodes(alone.DescendantNodes().OfType<NamespaceDeclarationSyntax>().Where(n => !n.DescendantNodes().OfType<MemberDeclarationSyntax>().Any(m => m.HasAnnotation(kept))),
            SyntaxRemoveOptions.KeepNoTrivia)!;
        // Alone, it is spaced from what holds it as the first member there was.
        BaseTypeDeclarationSyntax lone = alone.GetAnnotatedNodes(kept).OfType<BaseTypeDeclarationSyntax>().Single();
        alone = alone.ReplaceNode(lone, Changes.WithBlankLinesOf(lone, declaration.Parent!.ChildNodes().OfType<MemberDeclarationSyntax>().First()));

        SyntaxNode? next = declaration.Parent!.ChildNodes().OfType<MemberDeclarationSyntax>().SkipWhile(m => m != declaration).Skip(1).FirstOrDefault();
        bool first = declaration.Parent!.ChildNodes().OfType<MemberDeclarationSyntax>().First() == declaration;
        CompilationUnitSyntax left = root.TrackNodes(next is null ? [declaration] : [declaration, next]);
        left = left.RemoveNode(left.GetCurrentNode(declaration)!, SyntaxRemoveOptions.KeepNoTrivia)!;
        if (first && next is not null && left.GetCurrentNode(next) is { } following)
        {
            left = left.ReplaceNode(following, Changes.WithBlankLinesOf(following, declaration));
        }

        string folder = Path.GetDirectoryName(document.FilePath!)!;
        Solution solution = document.Project.Solution.WithDocumentSyntaxRoot(document.Id, left);
        Document created = solution.GetProject(document.Project.Id)!.AddDocument(fileName, alone, document.Folders, Path.Combine(folder, fileName));
        return await WithoutUnusedUsingsAsync(created, cancel).ConfigureAwait(false);
    }

    /// <summary>A new file less the usings its code does not need, as the compiler finds them (CS8019).</summary>
    private static async Task<Solution> WithoutUnusedUsingsAsync(Document document, CancellationToken cancel)
    {
        SemanticModel model = (await document.GetSemanticModelAsync(cancel).ConfigureAwait(false))!;
        SyntaxNode root = (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
        List<UsingDirectiveSyntax> unused = [.. model.GetDiagnostics(cancellationToken: cancel)
            .Where(d => d.Id is "CS8019" or "CS0105")
            .Select(d => root.FindNode(d.Location.SourceSpan))
            .OfType<UsingDirectiveSyntax>()
            .Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None))
            .Distinct()];
        if (unused.Count == 0)
        {
            return document.Project.Solution;
        }
        SyntaxNode less = root.RemoveNodes(unused, SyntaxRemoveOptions.KeepNoTrivia)!;
        if (less is CompilationUnitSyntax unit && unit.Usings.Count == 0 && unit.Members.FirstOrDefault() is { } top)
        {
            less = unit.ReplaceNode(top, Changes.WithoutLeadingBlankLines(top));
        }
        return document.WithSyntaxRoot(less).Project.Solution;
    }

    /// <summary>A file of one type put in a namespace: the namespaces around it given way to one of the new name, or, with
    /// none, one wrapped around it.</summary>
    private static CompilationUnitSyntax Retargeted(CompilationUnitSyntax root, BaseTypeDeclarationSyntax type, string target)
    {
        if (type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().LastOrDefault() is not { } outermost)
        {
            return Wrapped(root, type, target);
        }
        if (outermost is FileScopedNamespaceDeclarationSyntax)
        {
            return root.ReplaceNode(outermost, outermost.WithName(SyntaxFactory.ParseName(target).WithTriviaFrom(outermost.Name)));
        }
        NamespaceDeclarationSyntax made = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(target))
            .AddMembers(type)
            .WithTriviaFrom(outermost)
            .WithAdditionalAnnotations(Microsoft.CodeAnalysis.Formatting.Formatter.Annotation);
        return root.ReplaceNode(outermost, made);
    }

    /// <summary>A file whose one type is at its top put in a namespace: a file-scoped one where the language has them.</summary>
    private static CompilationUnitSyntax Wrapped(CompilationUnitSyntax root, BaseTypeDeclarationSyntax type, string target)
    {
        CompilationUnitSyntax without = root.RemoveNode(type, SyntaxRemoveOptions.KeepNoTrivia)!;
        bool fileScoped = ((CSharpParseOptions)root.SyntaxTree.Options).LanguageVersion >= LanguageVersion.CSharp10;
        MemberDeclarationSyntax holder = fileScoped
            ? SyntaxFactory.FileScopedNamespaceDeclaration(SyntaxFactory.ParseName(target)).AddMembers(type.WithLeadingTrivia(type.GetLeadingTrivia().Insert(0, SyntaxFactory.EndOfLine("\n"))))
            : SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(target)).AddMembers(type);
        return without.AddMembers(holder.WithAdditionalAnnotations(Microsoft.CodeAnalysis.Formatting.Formatter.Annotation));
    }

    /// <summary>A name made fully qualified in a namespace and marked for the simplifier to shorten and the import adder to
    /// give a using, with the trivia of what it stands in for.</summary>
    private static SyntaxNode Qualified(string space, SimpleNameSyntax name, SyntaxNode replaced)
    {
        string text = $"global::{space}.{name.WithoutTrivia()}";
        SyntaxNode made = replaced is ExpressionSyntax expression && !SyntaxFacts.IsInNamespaceOrTypeContext(expression)
            ? SyntaxFactory.ParseExpression(text)
            : SyntaxFactory.ParseName(text);
        return made.WithTriviaFrom(replaced).WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation);
    }

    /// <summary>Whether a name is inside a namespace declaration of a namespace or one within it, where the namespace's
    /// types are in scope by their simple names.</summary>
    private static bool InNamespace(SyntaxNode name, string space) => FullNamespace(name) is { } inside && Visible(space, inside);

    private static string? FullNamespace(SyntaxNode node)
    {
        List<string> parts = [.. node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString())];
        return parts.Count == 0 ? null : string.Join(".", parts);
    }

    /// <summary>Whether a namespace's types are in scope by simple name inside a namespace: it is that one or holds it.</summary>
    private static bool Visible(string space, string inside) => inside == space || inside.StartsWith(space + ".", StringComparison.Ordinal);

    /// <summary>Usings of a namespace that no longer exists (the type moved was all it held) taken out of every file.</summary>
    private static async Task<Solution> WithoutLostUsingsAsync(Solution solution, string space, CancellationToken cancel)
    {
        if (space.Length == 0)
        {
            return solution;
        }
        foreach (ProjectId projectId in solution.ProjectIds)
        {
            Project project = solution.GetProject(projectId)!;
            Compilation compilation = (await project.GetCompilationAsync(cancel).ConfigureAwait(false))!;
            INamespaceSymbol? found = compilation.GlobalNamespace;
            foreach (string part in space.Split('.'))
            {
                found = found?.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
            }
            if (found is not null)
            {
                continue;
            }
            foreach (DocumentId id in project.DocumentIds)
            {
                Document document = solution.GetDocument(id)!;
                SyntaxNode root = (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
                List<UsingDirectiveSyntax> lost = [.. root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                    .Where(u => u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name?.ToString() == space)];
                if (lost.Count > 0)
                {
                    solution = solution.WithDocumentSyntaxRoot(id, root.RemoveNodes(lost, SyntaxRemoveOptions.KeepNoTrivia)!);
                }
            }
        }
        return solution;
    }
}
