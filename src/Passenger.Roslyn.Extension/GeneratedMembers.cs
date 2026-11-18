using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using static Passenger.Roslyn.Extension.SyntaxReads;

namespace Passenger.Roslyn.Extension;

/// <summary>What source generators add to the types the solution's own files declare, as the compilation has it, whichever
/// generator made it: each member declared in generated code alone that the project's own code reaches (XAML compiled into
/// it among that code), with where a person goes to it in the type's own files. A multi-targeted project's frameworks are
/// each read, a member any of them makes counted once.</summary>
public sealed class GeneratedMembers : IExtensionWorkspaceMessageHandler<GeneratedMembersAsked, GeneratedMembersFound>
{
    public async Task<GeneratedMembersFound> ExecuteAsync(GeneratedMembersAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        Solution solution = context.Solution;
        Dictionary<string, (List<string> Files, Dictionary<(string, string, int), GeneratedMember> Members)> found = new(StringComparer.Ordinal);
        foreach (Project project in solution.Projects.Where(p => message.Projects.Any(path => SamePath(p.FilePath, path))))
        {
            if (await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation)
            {
                continue;
            }
            HashSet<INamedTypeSymbol> types = new(SymbolEqualityComparer.Default);
            foreach (SourceGeneratedDocument generated in await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await generated.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false) is { } tree)
                {
                    TypesDeclaredIn(compilation.GetSemanticModel(tree), await tree.GetRootAsync(cancellationToken).ConfigureAwait(false), types, cancellationToken);
                }
            }
            Add(solution, compilation, types, found, cancellationToken);
        }
        foreach (string file in message.Files)
        {
            foreach (DocumentId id in DocumentIdsOf(solution, file))
            {
                if (solution.GetDocument(id) is not { } document || document is SourceGeneratedDocument
                    || await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root
                    || !root.DescendantNodes(Holds).OfType<TypeDeclarationSyntax>().Any(t => t.Modifiers.Any(m => m.ValueText == "partial"))
                    || await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation)
                {
                    continue;
                }
                HashSet<INamedTypeSymbol> types = new(SymbolEqualityComparer.Default);
                TypesDeclaredIn(compilation.GetSemanticModel(root.SyntaxTree), root, types, cancellationToken);
                Add(solution, compilation, types, found, cancellationToken);
            }
        }
        return new GeneratedMembersFound([.. found.Where(t => t.Value.Members.Count > 0)
            .Select(t => new GeneratedType(t.Key, t.Value.Files, [.. t.Value.Members.Values]))]);
    }

    /// <summary>Whether a walk for the types a file declares goes into a node: a namespace's or a type's.</summary>
    private static bool Holds(SyntaxNode node) => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax;

    /// <summary>The partial types a tree declares, nested ones too.</summary>
    private static void TypesDeclaredIn(SemanticModel model, SyntaxNode root, HashSet<INamedTypeSymbol> types, CancellationToken cancel)
    {
        foreach (TypeDeclarationSyntax declaration in root.DescendantNodes(Holds).OfType<TypeDeclarationSyntax>())
        {
            if (declaration.Modifiers.Any(m => m.ValueText == "partial") && model.GetDeclaredSymbol(declaration, cancel) is INamedTypeSymbol type)
            {
                types.Add(type);
            }
        }
    }

    /// <summary>Each type's members declared in generated code alone, where the type's own files declare it too.</summary>
    private static void Add(Solution solution, Compilation compilation, HashSet<INamedTypeSymbol> types,
        Dictionary<string, (List<string> Files, Dictionary<(string, string, int), GeneratedMember> Members)> found, CancellationToken cancel)
    {
        foreach (INamedTypeSymbol type in types)
        {
            List<SyntaxReference> own = [.. type.DeclaringSyntaxReferences.Where(r => solution.GetDocument(r.SyntaxTree) is { FilePath: { Length: > 0 } } and not SourceGeneratedDocument)];
            if (own.Count == 0 || own.Count == type.DeclaringSyntaxReferences.Length)
            {
                continue;
            }
            string name = MetadataNameOf(type);
            if (!found.TryGetValue(name, out (List<string> Files, Dictionary<(string, string, int), GeneratedMember> Members) entry))
            {
                found[name] = entry = ([], []);
            }
            foreach (SyntaxReference reference in own.OrderBy(r => r.SyntaxTree.FilePath, StringComparer.Ordinal))
            {
                if (!entry.Files.Contains(reference.SyntaxTree.FilePath))
                {
                    entry.Files.Add(reference.SyntaxTree.FilePath);
                }
            }
            (string Path, Place At)? declared = NamePlace(own.OrderBy(r => r.SyntaxTree.FilePath, StringComparer.Ordinal).First(), cancel);
            foreach (ISymbol member in type.GetMembers())
            {
                if (Kind(member) is not { } kind || member.IsImplicitlyDeclared || member.DeclaredAccessibility == Accessibility.Private
                    || !SymbolNames.IsGenerated(member, solution))
                {
                    continue;
                }
                (string Path, Place At)? at = MakerOf(member, solution, compilation, cancel) is { } maker
                    && maker.DeclaringSyntaxReferences.FirstOrDefault(r => solution.GetDocument(r.SyntaxTree) is { FilePath: { Length: > 0 } } and not SourceGeneratedDocument) is { } made
                    ? NamePlace(made, cancel) : declared;
                if (at is not { } place)
                {
                    continue;
                }
                IReadOnlyList<IParameterSymbol> parameters = member is IMethodSymbol method ? method.Parameters : [];
                entry.Members.TryAdd((member.Name, kind, parameters.Count), new GeneratedMember(member.Name, kind, TypeOf(TypeOfMember(member)), member.IsStatic, Writable(member),
                    [.. parameters.Select(p => TypeOf(p.Type))], [.. parameters.Select(p => p.Name)], [.. member.GetAttributes().Select(Attribute).OfType<MemberAttribute>()],
                    place.Path, place.At));
            }
        }
    }

    /// <summary>The field or method of a member's own type, declared in its own files and marked with an attribute (what a
    /// generator makes members of), that the member's generated code first names; null where it names none.</summary>
    private static ISymbol? MakerOf(ISymbol member, Solution solution, Compilation compilation, CancellationToken cancel)
    {
        foreach (SyntaxReference reference in member.DeclaringSyntaxReferences)
        {
            SyntaxNode node = reference.GetSyntax(cancel);
            SemanticModel model = compilation.GetSemanticModel(reference.SyntaxTree);
            foreach (SimpleNameSyntax name in node.DescendantNodes(n => n is not AttributeListSyntax).OfType<SimpleNameSyntax>())
            {
                SymbolInfo bound = model.GetSymbolInfo(name, cancel);
                foreach (ISymbol named in bound.Symbol is { } symbol ? [symbol] : bound.CandidateSymbols)
                {
                    ISymbol definition = named.OriginalDefinition;
                    if (definition is IFieldSymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol
                        && !SymbolEqualityComparer.Default.Equals(definition, member.OriginalDefinition)
                        && SymbolEqualityComparer.Default.Equals(definition.ContainingType, member.ContainingType.OriginalDefinition)
                        && !definition.GetAttributes().IsEmpty && !SymbolNames.IsGenerated(definition, solution))
                    {
                        return definition;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>Where a declaration's name is, in its file.</summary>
    private static (string Path, Place At)? NamePlace(SyntaxReference reference, CancellationToken cancel)
    {
        SyntaxNode node = reference.GetSyntax(cancel);
        SyntaxToken name = node switch
        {
            BaseTypeDeclarationSyntax type => type.Identifier,
            MethodDeclarationSyntax method => method.Identifier,
            PropertyDeclarationSyntax property => property.Identifier,
            VariableDeclaratorSyntax variable => variable.Identifier,
            EventDeclarationSyntax @event => @event.Identifier,
            _ => node.GetFirstToken(),
        };
        return reference.SyntaxTree.FilePath is { Length: > 0 } path ? (path, PlaceOf(name.GetLocation().GetLineSpan().StartLinePosition)) : null;
    }

    /// <summary>A member's kind as a documentation id names it, for one XAML may name: a property but an indexer, a field, an
    /// ordinary method, an event; null for any other.</summary>
    private static string? Kind(ISymbol member) => member switch
    {
        IPropertySymbol { IsIndexer: false, ExplicitInterfaceImplementations.IsEmpty: true } => "P",
        IFieldSymbol => "F",
        IMethodSymbol { MethodKind: MethodKind.Ordinary, ExplicitInterfaceImplementations.IsEmpty: true } => "M",
        IEventSymbol { ExplicitInterfaceImplementations.IsEmpty: true } => "E",
        _ => null,
    };

    private static ITypeSymbol TypeOfMember(ISymbol member) => member switch
    {
        IPropertySymbol property => property.Type,
        IFieldSymbol field => field.Type,
        IMethodSymbol method => method.ReturnType,
        IEventSymbol @event => @event.Type,
        _ => throw new ArgumentException($"{member.Kind} has no type", nameof(member)),
    };

    /// <summary>Whether the project's own code can set it: a property with a setter it reaches, a field neither read-only nor
    /// constant, an event.</summary>
    private static bool Writable(ISymbol member) => member switch
    {
        IPropertySymbol property => property.SetMethod is { DeclaredAccessibility: not Accessibility.Private },
        IFieldSymbol field => !field.IsReadOnly && !field.IsConst,
        IEventSymbol => true,
        _ => false,
    };

    /// <summary>A type as its metadata names it; one no metadata name holds (a pointer, a function pointer) an object.</summary>
    private static MemberType TypeOf(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => TypeOf(array.ElementType) with { IsArray = true },
        ITypeParameterSymbol parameter => new MemberType(parameter.Name, [], false, true),
        INamedTypeSymbol named => new MemberType(MetadataNameOf(named.OriginalDefinition), [.. named.TypeArguments.Select(TypeOf)], false, false),
        _ => new MemberType("System.Object", [], false, false),
    };

    /// <summary>A named type's metadata name: its namespace's and its own with its arity, a nested one's after its declaring
    /// type's and a '+'.</summary>
    private static string MetadataNameOf(INamedTypeSymbol type)
    {
        if (type.ContainingType is { } outer)
        {
            return $"{MetadataNameOf(outer)}+{type.MetadataName}";
        }
        return type.ContainingNamespace is { IsGlobalNamespace: false } space ? $"{space.ToDisplayString()}.{type.MetadataName}" : type.MetadataName;
    }

    /// <summary>An attribute by its class's metadata name, its constructor's arguments as text; none for one whose class is
    /// unknown.</summary>
    private static MemberAttribute? Attribute(AttributeData attribute) => attribute.AttributeClass is { } named
        ? new MemberAttribute(MetadataNameOf(named), [.. attribute.ConstructorArguments.Select(argument => argument switch
        {
            { Kind: TypedConstantKind.Type, Value: INamedTypeSymbol type } => MetadataNameOf(type),
            { Kind: TypedConstantKind.Primitive or TypedConstantKind.Enum, Value: { } value } => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        })])
        : null;
}
