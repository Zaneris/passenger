using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;

namespace Passenger.Roslyn.Extension;

/// <summary>Pull Members Up and Push Members Down: members of a class moved into a base class (or declared in an
/// interface it implements) or into every derived class chosen, each one made abstract where it stays when asked, its
/// overrides then marked so. The files they go to get the usings of the file they came from that they need.</summary>
internal static class MemberMoves
{
    public static async Task<Hierarchy> DescribeAsync(Document document, Place at, CancellationToken cancel)
    {
        int offset = await Changes.OffsetAsync(document, at, cancel).ConfigureAwait(false);
        (INamedTypeSymbol? type, _) = await Changes.TypeAtAsync(document, offset, cancel).ConfigureAwait(false);
        if (type is null || type.TypeKind != TypeKind.Class || type.IsRecord)
        {
            return new Hierarchy("Pull Members Up and Push Members Down move a class's members: put the caret on one", string.Empty, [], [], []);
        }
        List<Member> bases = [];
        for (INamedTypeSymbol? up = type.BaseType; up is not null && InSource(up); up = up.BaseType)
        {
            bases.Add(TypeOf(up));
        }
        bases.AddRange(type.AllInterfaces.Where(InSource).Select(TypeOf));
        IEnumerable<INamedTypeSymbol> derived = await SymbolFinder.FindDerivedClassesAsync(type, document.Project.Solution, transitive: false, cancellationToken: cancel).ConfigureAwait(false);
        return new Hierarchy(null, type.Name, [.. Movable(type).Select(MemberOf)], bases, [.. derived.Where(InSource).Select(TypeOf)]);
    }

    public static async Task<Refactored> PullUpAsync(Document document, MembersMove move, CancellationToken cancel)
    {
        (INamedTypeSymbol? type, string? problem) = await SourceAsync(document, move, cancel).ConfigureAwait(false);
        if (type is null)
        {
            return Changes.Refused(problem!);
        }
        Compilation compilation = (await document.Project.GetCompilationAsync(cancel).ConfigureAwait(false))!;
        if (DocumentationCommentId.GetFirstSymbolForDeclarationId(move.Target, compilation) is not INamedTypeSymbol target
            || !(SymbolEqualityComparer.Default.Equals(target, type.BaseType) || Bases(type).Contains(target, SymbolEqualityComparer.Default) || type.AllInterfaces.Contains(target, SymbolEqualityComparer.Default))
            || !InSource(target))
        {
            return Changes.Refused($"Choose a base type of '{type.Name}' in this solution to pull members up to");
        }
        List<ISymbol> members = Chosen(type, move.Members);
        if (members.Count == 0)
        {
            return Changes.Refused("Select the members to pull up");
        }
        Solution solution = document.Project.Solution;
        Editors editors = new(solution, cancel);
        TypeDeclarationSyntax into = await DeclarationAsync(target, cancel).ConfigureAwait(false);
        DocumentEditor targetEditor = await editors.OfAsync(into).ConfigureAwait(false);
        HashSet<Document> sources = [];
        bool needsAbstract = false;
        foreach (ISymbol member in members)
        {
            SyntaxNode declared = await DeclarationAsync(member, cancel).ConfigureAwait(false);
            sources.Add(solution.GetDocument(declared.SyntaxTree)!);
            DocumentEditor sourceEditor = await editors.OfAsync(declared).ConfigureAwait(false);
            if (target.TypeKind == TypeKind.Interface)
            {
                if (member.DeclaredAccessibility != Accessibility.Public || member.IsStatic)
                {
                    return Changes.Refused($"'{member.Name}' is not a public instance member, which an interface could declare");
                }
                editors.AddMember(targetEditor, into, Interfaces.Declare(targetEditor.Generator, member));
                continue;
            }
            if (move.Abstract.Contains(Id(member)))
            {
                if (member is IFieldSymbol || member.IsStatic)
                {
                    return Changes.Refused($"'{member.Name}' cannot be made abstract");
                }
                needsAbstract = true;
                editors.AddMember(targetEditor, into, Abstract(targetEditor.Generator, declared));
                sourceEditor.ReplaceNode(declared, (current, generator) => generator.WithModifiers(current, generator.GetModifiers(current).WithIsAbstract(false).WithIsVirtual(false).WithIsOverride(true)));
                continue;
            }
            SyntaxNode moved = member.DeclaredAccessibility == Accessibility.Private ? targetEditor.Generator.WithAccessibility(Alone(declared), Accessibility.Protected) : Alone(declared);
            editors.AddMember(targetEditor, into, moved);
            Remove(sourceEditor, declared);
        }
        if (needsAbstract && !target.IsAbstract)
        {
            targetEditor.ReplaceNode(into, (current, generator) => generator.WithModifiers(current, generator.GetModifiers(current).WithIsAbstract(true)));
        }
        Solution changed = await editors.ChangedAsync(sources).ConfigureAwait(false);
        return await Changes.BetweenAsync(solution, changed, cancel).ConfigureAwait(false);
    }

    public static async Task<Refactored> PushDownAsync(Document document, MembersMove move, CancellationToken cancel)
    {
        (INamedTypeSymbol? type, string? problem) = await SourceAsync(document, move, cancel).ConfigureAwait(false);
        if (type is null)
        {
            return Changes.Refused(problem!);
        }
        Solution solution = document.Project.Solution;
        IEnumerable<INamedTypeSymbol> derived = await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: false, cancellationToken: cancel).ConfigureAwait(false);
        HashSet<string> wanted = [.. move.Target.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        List<INamedTypeSymbol> targets = [.. derived.Where(d => InSource(d) && wanted.Contains(Id(d)))];
        if (targets.Count == 0)
        {
            return Changes.Refused($"Choose the classes derived from '{type.Name}' to push members down to");
        }
        List<ISymbol> members = Chosen(type, move.Members);
        if (members.Count == 0)
        {
            return Changes.Refused("Select the members to push down");
        }
        Editors editors = new(solution, cancel);
        TypeDeclarationSyntax from = await DeclarationAsync(type, cancel).ConfigureAwait(false);
        DocumentEditor sourceEditor = await editors.OfAsync(from).ConfigureAwait(false);
        bool needsAbstract = false;
        List<TypeDeclarationSyntax> intos = [];
        foreach (INamedTypeSymbol target in targets)
        {
            intos.Add(await DeclarationAsync(target, cancel).ConfigureAwait(false));
        }
        foreach (ISymbol member in members)
        {
            SyntaxNode declared = await DeclarationAsync(member, cancel).ConfigureAwait(false);
            bool keepAbstract = move.Abstract.Contains(Id(member));
            if (keepAbstract && (member is IFieldSymbol || member.IsStatic))
            {
                return Changes.Refused($"'{member.Name}' cannot be made abstract");
            }
            foreach ((INamedTypeSymbol target, TypeDeclarationSyntax into) in targets.Zip(intos))
            {
                if (Declares(target, member))
                {
                    continue;
                }
                DocumentEditor targetEditor = await editors.OfAsync(into).ConfigureAwait(false);
                SyntaxNode copy = keepAbstract || member.IsAbstract
                    ? targetEditor.Generator.WithModifiers(declared, targetEditor.Generator.GetModifiers(declared).WithIsAbstract(false).WithIsVirtual(false).WithIsOverride(true))
                    : Alone(declared);
                editors.AddMember(targetEditor, into, copy);
            }
            if (keepAbstract)
            {
                needsAbstract = true;
                sourceEditor.ReplaceNode(declared, (current, generator) => Abstract(generator, current).WithTriviaFrom(current));
            }
            else
            {
                Remove(sourceEditor, declared);
            }
        }
        if (needsAbstract && !type.IsAbstract)
        {
            sourceEditor.ReplaceNode(from, (current, generator) => generator.WithModifiers(current, generator.GetModifiers(current).WithIsAbstract(true)));
        }
        Solution changed = await editors.ChangedAsync([solution.GetDocument(from.SyntaxTree)!]).ConfigureAwait(false);
        return await Changes.BetweenAsync(solution, changed, cancel).ConfigureAwait(false);
    }

    /// <summary>The class at the caret, when it is one whose members can move.</summary>
    private static async Task<(INamedTypeSymbol? Type, string? Problem)> SourceAsync(Document document, MembersMove move, CancellationToken cancel)
    {
        int offset = await Changes.OffsetAsync(document, move.At, cancel).ConfigureAwait(false);
        (INamedTypeSymbol? type, _) = await Changes.TypeAtAsync(document, offset, cancel).ConfigureAwait(false);
        return type is null || type.TypeKind != TypeKind.Class || type.IsRecord
            ? (null, "Pull Members Up and Push Members Down move a class's members: put the caret on one")
            : (type, null);
    }

    /// <summary>The members of a class that can move: its methods, properties, indexers, events and fields, declared in
    /// source, in their order.</summary>
    private static IEnumerable<ISymbol> Movable(INamedTypeSymbol type) => type.GetMembers().Where(m =>
        !m.IsImplicitlyDeclared && m.Locations.Any(l => l.IsInSource) && m switch
        {
            IMethodSymbol method => method.MethodKind == MethodKind.Ordinary,
            IPropertySymbol or IEventSymbol => true,
            IFieldSymbol field => field.AssociatedSymbol is null,
            _ => false,
        });

    private static List<ISymbol> Chosen(INamedTypeSymbol type, IReadOnlyList<string> ids) => [.. Movable(type).Where(m => ids.Contains(Id(m)))];

    private static IEnumerable<INamedTypeSymbol> Bases(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? up = type.BaseType; up is not null; up = up.BaseType)
        {
            yield return up;
        }
    }

    private static bool InSource(ISymbol symbol) => symbol.Locations.Any(l => l.IsInSource);

    private static string Id(ISymbol symbol) => DocumentationCommentId.CreateDeclarationId(symbol) ?? symbol.Name;

    private static Member TypeOf(INamedTypeSymbol type) => new(Id(type), type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        type.TypeKind == TypeKind.Interface ? "interface" : "class");

    private static Member MemberOf(ISymbol member) => new(Id(member), Interfaces.Text(member), member switch
    {
        IMethodSymbol => "method",
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IEventSymbol => "event",
        _ => "field",
    });

    /// <summary>A member's declaration: a field's whole declaration when it declares that field alone.</summary>
    private static async Task<SyntaxNode> DeclarationAsync(ISymbol member, CancellationToken cancel)
    {
        SyntaxNode node = await member.DeclaringSyntaxReferences[0].GetSyntaxAsync(cancel).ConfigureAwait(false);
        return node is VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax field } && field.Declaration.Variables.Count == 1 ? field : node;
    }

    /// <summary>A member's declaration as it goes into another type: one variable of a field declaring several as a field
    /// declaration of its own, with the field's modifiers, attributes and comment.</summary>
    private static SyntaxNode Alone(SyntaxNode declared) => declared is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: BaseFieldDeclarationSyntax field } list } variable
        ? field.WithDeclaration(list.WithVariables(SyntaxFactory.SingletonSeparatedList(variable.WithoutTrivia())))
        : declared;

    /// <summary>Whether a type already declares a member as the one moved would be declared there: a method or indexer
    /// of the same parameters, anything else of the same name and kind.</summary>
    private static bool Declares(INamedTypeSymbol type, ISymbol member) => type.GetMembers(member.Name).Any(m => m.Kind == member.Kind && !m.IsImplicitlyDeclared && (m, member) switch
    {
        (IMethodSymbol theirs, IMethodSymbol ours) => theirs.Arity == ours.Arity
            && SameParameters((theirs.Arity == 0 ? theirs : theirs.Construct([.. ours.TypeParameters])).Parameters, ours.Parameters),
        (IPropertySymbol theirs, IPropertySymbol ours) => SameParameters(theirs.Parameters, ours.Parameters),
        _ => true,
    });

    /// <summary>Whether two lists of parameters overload as one: the same types, each passed by reference or by value
    /// alike.</summary>
    private static bool SameParameters(IReadOnlyList<IParameterSymbol> theirs, IReadOnlyList<IParameterSymbol> ours) =>
        theirs.Count == ours.Count && theirs.Zip(ours).All(p => SymbolEqualityComparer.Default.Equals(p.First.Type, p.Second.Type)
            && (p.First.RefKind == RefKind.None) == (p.Second.RefKind == RefKind.None));

    private static async Task<TypeDeclarationSyntax> DeclarationAsync(INamedTypeSymbol type, CancellationToken cancel) =>
        (TypeDeclarationSyntax)await type.DeclaringSyntaxReferences[0].GetSyntaxAsync(cancel).ConfigureAwait(false);

    /// <summary>A member made abstract: its signature kept, its body gone, the line it ended kept, for the formatter to
    /// space.</summary>
    private static SyntaxNode Abstract(SyntaxGenerator generator, SyntaxNode declaration)
    {
        SyntaxToken semicolon = SyntaxFactory.Token(SyntaxKind.SemicolonToken).WithTrailingTrivia(declaration.GetLastToken().TrailingTrivia);
        SyntaxNode bare = declaration switch
        {
            MethodDeclarationSyntax method => Ended(method.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)).WithSemicolonToken(semicolon),
            BasePropertyDeclarationSyntax property when property.AccessorList is { } accessors =>
                property.WithAccessorList(accessors.WithAccessors(SyntaxFactory.List(accessors.Accessors.Select(a =>
                    Ended(a.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))))),
            PropertyDeclarationSyntax { ExpressionBody: not null } property => Ended(property.WithExpressionBody(null).WithSemicolonToken(default))
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))).WithTrailingTrivia(semicolon.TrailingTrivia)),
            _ => declaration,
        };
        if (bare is PropertyDeclarationSyntax { Initializer: not null } initialized)
        {
            bare = initialized.WithInitializer(null).WithSemicolonToken(default);
        }
        return generator.WithModifiers(bare, generator.GetModifiers(bare).WithIsAbstract(true).WithIsVirtual(false).WithIsOverride(false))
            .WithAdditionalAnnotations(Formatter.Annotation);
    }

    /// <summary>A node whose last token is followed by nothing, its body having gone.</summary>
    private static T Ended<T>(T node) where T : SyntaxNode => node.ReplaceToken(node.GetLastToken(), node.GetLastToken().WithTrailingTrivia());

    /// <summary>A member taken out, with its comment, and what follows it no longer led by a blank line when it came first.</summary>
    private static void Remove(DocumentEditor editor, SyntaxNode declared)
    {
        if (declared.Parent is TypeDeclarationSyntax holder && holder.Members.FirstOrDefault() == declared && holder.Members.Count > 1)
        {
            editor.ReplaceNode(holder.Members[1], (current, _) => Changes.WithBlankLinesOf(current, declared));
        }
        editor.RemoveNode(declared, SyntaxRemoveOptions.KeepNoTrivia);
    }

    /// <summary>The editors of every file a move changes, and the solution they make, the files members went to given
    /// the usings of the files they came from that they need, and formatted where members went in.</summary>
    private sealed class Editors(Solution solution, CancellationToken cancel)
    {
        private readonly Dictionary<DocumentId, DocumentEditor> editors = [];
        private readonly Dictionary<SyntaxNode, int> added = [];

        /// <summary>A member added at the end of a type, a blank line before it unless it is the type's first, with its
        /// documentation comment and no other trivia, for the formatter to indent.</summary>
        public void AddMember(DocumentEditor editor, TypeDeclarationSyntax into, SyntaxNode member)
        {
            int before = added.GetValueOrDefault(into);
            added[into] = before + 1;
            IEnumerable<SyntaxTrivia> comment = member.GetLeadingTrivia().Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
            SyntaxTriviaList leading = into.Members.Count + before > 0 ? SyntaxFactory.TriviaList(SyntaxFactory.EndOfLine("\n")) : default;
            editor.AddMember(into, member.WithLeadingTrivia(leading.AddRange(comment)).WithTrailingTrivia(SyntaxFactory.EndOfLine("\n")).WithAdditionalAnnotations(Formatter.Annotation));
        }

        public async Task<DocumentEditor> OfAsync(SyntaxNode node)
        {
            Document document = solution.GetDocument(node.SyntaxTree)!;
            if (!editors.TryGetValue(document.Id, out DocumentEditor? editor))
            {
                editor = await DocumentEditor.CreateAsync(document, cancel).ConfigureAwait(false);
                editors[document.Id] = editor;
            }
            return editor;
        }

        public async Task<Solution> ChangedAsync(IEnumerable<Document> sources)
        {
            Solution changed = solution;
            foreach ((DocumentId id, DocumentEditor editor) in editors)
            {
                changed = changed.WithDocumentSyntaxRoot(id, editor.GetChangedRoot());
            }
            foreach (DocumentId id in editors.Keys)
            {
                Document document = changed.GetDocument(id)!;
                document = await WithUsingsOfAsync(document, sources.Where(s => s.Id != id).Select(s => solution.GetDocument(s.Id)!), cancel).ConfigureAwait(false);
                document = await Formatter.FormatAsync(document, Formatter.Annotation, cancellationToken: cancel).ConfigureAwait(false);
                changed = document.Project.Solution;
            }
            return changed;
        }
    }

    /// <summary>A file given the usings of other files that it lacks, less those its code then does not need (CS8019).</summary>
    private static async Task<Document> WithUsingsOfAsync(Document document, IEnumerable<Document> others, CancellationToken cancel)
    {
        CompilationUnitSyntax root = (CompilationUnitSyntax)(await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
        HashSet<string> had = [.. root.Usings.Select(u => u.ToString())];
        List<UsingDirectiveSyntax> added = [];
        foreach (Document other in others)
        {
            CompilationUnitSyntax theirs = (CompilationUnitSyntax)(await other.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
            foreach (UsingDirectiveSyntax directive in theirs.Usings.Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None)))
            {
                if (had.Add(directive.ToString()))
                {
                    added.Add(directive.WithoutTrivia().WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed));
                }
            }
        }
        if (added.Count == 0)
        {
            return document;
        }
        SyntaxAnnotation mark = new();
        Document widened = document.WithSyntaxRoot(root.AddUsings([.. added.Select(u => u.WithAdditionalAnnotations(mark, Formatter.Annotation))]));
        SemanticModel model = (await widened.GetSemanticModelAsync(cancel).ConfigureAwait(false))!;
        SyntaxNode wide = (await widened.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
        HashSet<SyntaxNode> unused = [.. model.GetDiagnostics(cancellationToken: cancel).Where(d => d.Id == "CS8019").Select(d => wide.FindNode(d.Location.SourceSpan))];
        List<SyntaxNode> drop = [.. wide.GetAnnotatedNodes(mark).Where(unused.Contains)];
        return drop.Count == 0 ? widened : widened.WithSyntaxRoot(wide.RemoveNodes(drop, SyntaxRemoveOptions.KeepNoTrivia)!);
    }
}
