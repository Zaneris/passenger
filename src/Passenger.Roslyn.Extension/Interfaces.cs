using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Passenger.Core;

namespace Passenger.Roslyn.Extension;

/// <summary>Extract Interface: an interface of the members chosen from a class, struct or record, in a file of its own
/// beside the type's, in the type's namespace, its types simplified and imported, and the type made to implement
/// it.</summary>
internal static class Interfaces
{
    public static async Task<InterfaceSource> DescribeAsync(Document document, Place at, CancellationToken cancel)
    {
        (INamedTypeSymbol? type, string? problem) = await FindAsync(document, at, cancel).ConfigureAwait(false);
        if (type is null)
        {
            return new InterfaceSource(problem, string.Empty, string.Empty, string.Empty, string.Empty, []);
        }
        string name = "I" + type.Name;
        return new InterfaceSource(null, type.Name, name, name + ".cs", Namespace(type), [.. Candidates(type).Select(Describe)]);
    }

    public static async Task<Refactored> ExtractAsync(Document document, InterfaceExtraction extraction, CancellationToken cancel)
    {
        (INamedTypeSymbol? type, string? problem) = await FindAsync(document, extraction.At, cancel).ConfigureAwait(false);
        if (type is null)
        {
            return Changes.Refused(problem!);
        }
        int offset = await Changes.OffsetAsync(document, extraction.At, cancel).ConfigureAwait(false);
        (_, BaseTypeDeclarationSyntax? found) = await Changes.TypeAtAsync(document, offset, cancel).ConfigureAwait(false);
        if (found is not TypeDeclarationSyntax declaration)
        {
            return Changes.Refused("Extract Interface extracts from a class, a struct or a record: put the caret on one");
        }
        string name = extraction.Name.Trim();
        if (!Changes.IsName(name))
        {
            return Changes.Refused(name.Length == 0 ? "The interface needs a name" : $"'{name}' is not a valid interface name");
        }
        if (type.ContainingNamespace.GetTypeMembers(name).Length > 0 || type.GetTypeMembers(name).Length > 0)
        {
            return Changes.Refused($"A type named '{name}' already exists in {(Namespace(type) is { Length: > 0 } holder ? $"namespace '{holder}'" : "the global namespace")}");
        }
        string fileName = extraction.FileName.Trim();
        if (FileProblem(document, fileName, type.Name) is { } wrongFile)
        {
            return Changes.Refused(wrongFile);
        }
        List<ISymbol> members = [];
        foreach (string id in extraction.Members)
        {
            if (Candidates(type).FirstOrDefault(m => DocumentationCommentId.CreateDeclarationId(m) == id) is not { } member)
            {
                return Changes.Refused($"{id} is not a public member of '{type.Name}' an interface can have");
            }
            members.Add(member);
        }
        if (members.Count == 0)
        {
            return Changes.Refused("Choose the members the interface is to have");
        }

        SyntaxGenerator generator = SyntaxGenerator.GetGenerator(document);
        List<ITypeParameterSymbol> typeParameters = Kept(type, members);
        SyntaxNode made = generator.InterfaceDeclaration(name, typeParameters.Select(t => t.Name),
            type.DeclaredAccessibility == Accessibility.Public ? Accessibility.Public : Accessibility.Internal,
            members: members.Select(m => Declare(generator, m)));
        foreach (ITypeParameterSymbol parameter in typeParameters)
        {
            SpecialTypeConstraintKind kinds = (parameter.HasReferenceTypeConstraint ? SpecialTypeConstraintKind.ReferenceType : 0)
                | (parameter.HasValueTypeConstraint ? SpecialTypeConstraintKind.ValueType : 0)
                | (parameter.HasConstructorConstraint ? SpecialTypeConstraintKind.Constructor : 0);
            if (kinds != SpecialTypeConstraintKind.None || parameter.ConstraintTypes.Length > 0)
            {
                made = generator.WithTypeConstraint(made, parameter.Name, kinds, parameter.ConstraintTypes.Select(t => generator.TypeExpression(t)));
            }
        }

        // The interface's file: in the type's namespace, declared as the type's file declares its own.
        SyntaxNode root = (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
        MemberDeclarationSyntax body = (MemberDeclarationSyntax)made;
        CompilationUnitSyntax unit = SyntaxFactory.CompilationUnit();
        if (Namespace(type) is { Length: > 0 } space)
        {
            unit = unit.AddMembers(root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().Any()
                ? SyntaxFactory.FileScopedNamespaceDeclaration(SyntaxFactory.ParseName(space)).AddMembers(body)
                : SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(space)).AddMembers(body));
        }
        else
        {
            unit = unit.AddMembers(body);
        }
        string folder = Path.GetDirectoryName(document.FilePath!)!;
        Document file = document.Project.AddDocument(fileName, unit, document.Folders, Path.Combine(folder, fileName));
        file = await ImportAdder.AddImportsAsync(file, Simplifier.AddImportsAnnotation, cancellationToken: cancel).ConfigureAwait(false);
        file = await Simplifier.ReduceAsync(file, Simplifier.Annotation, cancellationToken: cancel).ConfigureAwait(false);
        file = await Formatter.FormatAsync(file, cancellationToken: cancel).ConfigureAwait(false);
        file = await Changes.EndingInLineBreakAsync(file, cancel).ConfigureAwait(false);

        // The type implements it, named as it is in the type's own namespace.
        Document source = file.Project.Solution.GetDocument(document.Id)!;
        DocumentEditor editor = await DocumentEditor.CreateAsync(source, cancel).ConfigureAwait(false);
        TypeDeclarationSyntax target = (TypeDeclarationSyntax)(await source.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!.FindNode(declaration.Span);
        string implemented = typeParameters.Count == 0 ? name : $"{name}<{string.Join(", ", typeParameters.Select(t => t.Name))}>";
        editor.AddInterfaceType(target, SyntaxFactory.ParseTypeName(implemented));
        SyntaxAnnotation changed = new();
        editor.ReplaceNode(target, (current, _) => current.WithAdditionalAnnotations(changed));
        Document implementing = editor.GetChangedDocument();
        SyntaxNode implementingRoot = (await implementing.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
        if (implementingRoot.GetAnnotatedNodes(changed).FirstOrDefault() is TypeDeclarationSyntax { BaseList: { } bases } implementer)
        {
            // From the type's name, so the space before the colon is the formatter's too.
            implementing = await Formatter.FormatAsync(implementing, Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(implementer.Identifier.SpanStart, bases.FullSpan.End),
                cancellationToken: cancel).ConfigureAwait(false);
        }
        return await Changes.BetweenAsync(document.Project.Solution, implementing.Project.Solution, cancel).ConfigureAwait(false);
    }

    /// <summary>Why a new file of a name cannot be made beside a document; null when it can.</summary>
    public static string? FileProblem(Document document, string fileName, string what)
    {
        if (fileName.Length == 0)
        {
            return "file name is empty";
        }
        if (!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return "The new file name extension is not valid for source code files";
        }
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.Contains('/') || fileName.Contains('\\'))
        {
            return $"'{fileName}' is not a file name";
        }
        if (document.FilePath is not { } path)
        {
            return "The file is not on disk";
        }
        string target = Path.Combine(Path.GetDirectoryName(path)!, fileName);
        if (File.Exists(target) || document.Project.Solution.Projects.SelectMany(p => p.Documents).Any(d => d.FilePath is { } other && PathNames.Same(other, target)))
        {
            return $"Unable to move {what} because a file with the name '{fileName}' already exists";
        }
        return null;
    }

    private static async Task<(INamedTypeSymbol? Type, string? Problem)> FindAsync(Document document, Place at, CancellationToken cancel)
    {
        int offset = await Changes.OffsetAsync(document, at, cancel).ConfigureAwait(false);
        (INamedTypeSymbol? type, _) = await Changes.TypeAtAsync(document, offset, cancel).ConfigureAwait(false);
        if (type is null || type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return (null, "Extract Interface extracts from a class, a struct or a record: put the caret on one");
        }
        if (!Candidates(type).Any())
        {
            return (null, $"'{type.Name}' has no public members an interface can have");
        }
        return (type, null);
    }

    /// <summary>The members an interface of a type can have: its public instance methods, properties, indexers and events,
    /// in their order.</summary>
    private static IEnumerable<ISymbol> Candidates(INamedTypeSymbol type) => type.GetMembers().Where(m =>
        m.DeclaredAccessibility == Accessibility.Public && !m.IsStatic && !m.IsImplicitlyDeclared
        && m switch
        {
            IMethodSymbol method => method.MethodKind == MethodKind.Ordinary && method.CanBeReferencedByName,
            IPropertySymbol { IsIndexer: true } => true,
            IPropertySymbol or IEventSymbol => m.CanBeReferencedByName,
            _ => false,
        });

    private static Member Describe(ISymbol member) => new(DocumentationCommentId.CreateDeclarationId(member) ?? member.Name, Text(member), member switch
    {
        IMethodSymbol => "method",
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IEventSymbol => "event",
        _ => "member",
    });

    /// <summary>A member as a list of members shows it: its name and parameter types, then its type.</summary>
    public static string Text(ISymbol member)
    {
        string Type(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        string Parameters(IEnumerable<IParameterSymbol> parameters) => string.Join(", ", parameters.Select(p => Type(p.Type)));
        return member switch
        {
            IMethodSymbol method => $"{method.Name}{(method.TypeParameters.Length > 0 ? $"<{string.Join(", ", method.TypeParameters.Select(t => t.Name))}>" : string.Empty)}"
                + $"({Parameters(method.Parameters)}): {Type(method.ReturnType)}",
            IPropertySymbol { IsIndexer: true } indexer => $"this[{Parameters(indexer.Parameters)}]: {Type(indexer.Type)}",
            IPropertySymbol property => $"{property.Name}: {Type(property.Type)}",
            IEventSymbol @event => $"{@event.Name}: {Type(@event.Type)}",
            IFieldSymbol field => $"{field.Name}: {Type(field.Type)}",
            _ => member.Name,
        };
    }

    /// <summary>A member's declaration in the interface: its signature with its types fully named for the simplifier to
    /// shorten, with only the accessors others can call.</summary>
    public static SyntaxNode Declare(SyntaxGenerator generator, ISymbol member)
    {
        switch (member)
        {
            case IMethodSymbol method:
                return generator.MethodDeclaration(method);
            case IPropertySymbol property:
                bool get = property.GetMethod is { DeclaredAccessibility: Accessibility.Public };
                bool set = property.SetMethod is { DeclaredAccessibility: Accessibility.Public };
                DeclarationModifiers access = get && !set ? DeclarationModifiers.ReadOnly : !get && set ? DeclarationModifiers.WriteOnly : DeclarationModifiers.None;
                SyntaxNode declared = property.IsIndexer
                    ? generator.IndexerDeclaration(property.Parameters.Select(p => generator.ParameterDeclaration(p)), generator.TypeExpression(property.Type), Accessibility.NotApplicable, access)
                    : generator.PropertyDeclaration(property.Name, generator.TypeExpression(property.Type), Accessibility.NotApplicable, access);
                return set && property.SetMethod!.IsInitOnly ? InitOnly(declared) : declared;
            case IEventSymbol @event:
                return generator.EventDeclaration(@event.Name, generator.TypeExpression(@event.Type));
            default:
                throw new InvalidOperationException($"{member.Name} cannot be an interface's member");
        }
    }

    private static SyntaxNode InitOnly(SyntaxNode declared) =>
        declared.ReplaceNodes(declared.DescendantNodes().OfType<AccessorDeclarationSyntax>().Where(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)),
            (accessor, _) => SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));

    /// <summary>The type's type parameters the interface declares, in their order: those the members' signatures name, and
    /// every one the constraints of those name, as far as they go.</summary>
    private static List<ITypeParameterSymbol> Kept(INamedTypeSymbol type, IReadOnlyList<ISymbol> members)
    {
        HashSet<ITypeParameterSymbol> kept = new(type.TypeParameters.Where(t => members.Any(m => Uses(m, t))), SymbolEqualityComparer.Default);
        Queue<ITypeParameterSymbol> next = new(kept);
        while (next.TryDequeue(out ITypeParameterSymbol? parameter))
        {
            foreach (ITypeParameterSymbol named in type.TypeParameters)
            {
                if (parameter.ConstraintTypes.Any(c => Mentions(c, named)) && kept.Add(named))
                {
                    next.Enqueue(named);
                }
            }
        }
        return [.. type.TypeParameters.Where(kept.Contains)];
    }

    /// <summary>Whether a member's signature names one of its type's type parameters, a generic method's constraints
    /// included.</summary>
    private static bool Uses(ISymbol member, ITypeParameterSymbol parameter)
    {
        IEnumerable<ITypeSymbol> types = member switch
        {
            IMethodSymbol method => [method.ReturnType, .. method.Parameters.Select(p => p.Type), .. method.TypeParameters.SelectMany(t => t.ConstraintTypes)],
            IPropertySymbol property => [property.Type, .. property.Parameters.Select(p => p.Type)],
            IEventSymbol @event => [@event.Type],
            _ => [],
        };
        return types.Any(t => Mentions(t, parameter));
    }

    private static bool Mentions(ITypeSymbol type, ITypeParameterSymbol parameter) => type switch
    {
        ITypeParameterSymbol named => SymbolEqualityComparer.Default.Equals(named, parameter),
        IArrayTypeSymbol array => Mentions(array.ElementType, parameter),
        IPointerTypeSymbol pointer => Mentions(pointer.PointedAtType, parameter),
        INamedTypeSymbol generic => generic.TypeArguments.Any(t => Mentions(t, parameter)),
        _ => false,
    };

    public static string Namespace(INamedTypeSymbol type) => type.ContainingNamespace is { IsGlobalNamespace: false } space ? space.ToDisplayString() : string.Empty;
}
