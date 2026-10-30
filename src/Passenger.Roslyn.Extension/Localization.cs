using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;
using static Passenger.Roslyn.Extension.SyntaxReads;

namespace Passenger.Roslyn.Extension;

/// <summary>The localized strings a C# or Razor file reads (<see cref="LocalizationSource"/>).</summary>
public sealed class LocalizedReads : IExtensionWorkspaceMessageHandler<LocalizedReadsRequest, LocalizedReadsFound>
{
    public Task<LocalizedReadsFound> ExecuteAsync(LocalizedReadsRequest message, ExtensionMessageContext context, CancellationToken cancellationToken) =>
        LocalizationSource.ReadsAsync(context.Solution, message.Path, cancellationToken);
}

/// <summary>The localized strings a file's code reads, as Roslyn binds them, never by names alone: a constant key given to the
/// indexer or GetString of an <c>IStringLocalizer&lt;T&gt;</c> or <c>IHtmlLocalizer&lt;T&gt;</c>, and a property of a class a
/// .resx generates (ResXFileCodeGenerator's Designer class, Arcade's), each with the resource's name as ResourceManager looks
/// it up: for a localizer, T's full name as ASP.NET Core's ResourceManagerStringLocalizerFactory makes it (under
/// LocalizationOptions.ResourcesPath, or the assembly's [ResourceLocation], less its root namespace); for a generated class,
/// the name it gives its ResourceManager. A Razor file is read through the C# Razor's generator makes of it, each key placed
/// in the .razor by the generated code's line directives, so a component's own type and its usings are the compiler's.</summary>
internal static class LocalizationSource
{
    private const string StringLocalizer = "Microsoft.Extensions.Localization.IStringLocalizer<T>";
    private const string HtmlLocalizer = "Microsoft.AspNetCore.Mvc.Localization.IHtmlLocalizer<TResource>";
    private const string Options = "Microsoft.Extensions.Localization.LocalizationOptions";
    private const string RootNamespaceAttribute = "Microsoft.Extensions.Localization.RootNamespaceAttribute";
    private const string ResourceLocationAttribute = "Microsoft.Extensions.Localization.ResourceLocationAttribute";
    private const string ResourceManager = "System.Resources.ResourceManager";

    /// <summary>The ResourcesPath each project's code sets on LocalizationOptions, by each document's text version: a document
    /// is read again only once its text changed.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<DocumentId, (VersionStamp Version, string? Path)> ResourcesPaths = new();

    public static async Task<LocalizedReadsFound> ReadsAsync(Solution solution, string path, CancellationToken cancel)
    {
        List<LocalizedKey> keys = [];
        List<LocalizedResource> resources = [];
        HashSet<(string Key, int Line, int Character)> seen = [];
        Dictionary<Resource, int?> described = [];
        bool razor = RazorFiles.Is(path);
        foreach ((Document document, Project project) in await DocumentsOfAsync(solution, path, razor, cancel).ConfigureAwait(false))
        {
            if (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root)
            {
                continue;
            }
            HashSet<string> classes = await ResourceClassNamesAsync(project, cancel).ConfigureAwait(false);
            if (!MayRead(root, classes) || await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
            {
                continue;
            }
            SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
            foreach ((string key, TextSpan span, bool exact, Resource resource) in Read(root, model, classes, cancel))
            {
                Place? at;
                Place? end;
                if (razor)
                {
                    FileLinePositionSpan mapped = root.SyntaxTree.GetMappedLineSpan(span, cancel);
                    if (!mapped.HasMappedPath || !SamePath(mapped.Path, path))
                    {
                        continue;
                    }
                    (at, end) = (new Place(mapped.StartLinePosition.Line, mapped.StartLinePosition.Character), new Place(mapped.EndLinePosition.Line, mapped.EndLinePosition.Character));
                }
                else
                {
                    (at, end) = (PlaceAt(text, span.Start), PlaceAt(text, span.End));
                }
                if (!seen.Add((key, at.Line, at.Character)))
                {
                    continue;
                }
                if (!described.TryGetValue(resource, out int? index))
                {
                    LocalizedResource? made = await DescribeAsync(solution, resource, cancel).ConfigureAwait(false);
                    index = made is null ? null : resources.Count;
                    if (made is not null)
                    {
                        resources.Add(made);
                    }
                    described[resource] = index;
                }
                if (index is { } known)
                {
                    keys.Add(new LocalizedKey(key, known, at, end, exact));
                }
            }
        }
        return new LocalizedReadsFound(keys, resources);
    }

    /// <summary>The documents a file is: a C# file's own (one a target framework), or for a Razor file the C# Razor's generator
    /// makes of it in each project that has it.</summary>
    private static async Task<IReadOnlyList<(Document Document, Project Project)>> DocumentsOfAsync(Solution solution, string path, bool razor, CancellationToken cancel)
    {
        if (!razor)
        {
            return [.. solution.GetDocumentIdsWithFilePath(path).Select(solution.GetDocument).OfType<Document>().Select(d => (d, d.Project))];
        }
        List<(Document, Project)> generated = [];
        foreach (Project project in solution.Projects)
        {
            if (project.AdditionalDocuments.FirstOrDefault(d => SamePath(d.FilePath, path)) is not { } markup
                || SymbolNames.HintOf(project, markup.FilePath!) is not { } hint)
            {
                continue;
            }
            IEnumerable<SourceGeneratedDocument> made = await project.GetSourceGeneratedDocumentsAsync(cancel).ConfigureAwait(false);
            SourceGeneratedDocument? mine = made.FirstOrDefault(d => d.HintName.EndsWith(hint, StringComparison.Ordinal));
            if (mine is null)
            {
                foreach (SourceGeneratedDocument candidate in made)
                {
                    if (await candidate.GetSyntaxTreeAsync(cancel).ConfigureAwait(false) is { } tree
                        && tree.GetLineMappings(cancel).Any(m => m.MappedSpan.HasMappedPath && SamePath(m.MappedSpan.Path, path)))
                    {
                        mine = candidate;
                        break;
                    }
                }
            }
            if (mine is not null)
            {
                generated.Add((mine, project));
            }
        }
        return generated;
    }

    /// <summary>What reads a resource: a localizer of a type, or a class a .resx generates.</summary>
    private sealed record Resource(INamedTypeSymbol Type, bool Localizer)
    {
        public bool Equals(Resource? other) => other is not null && Localizer == other.Localizer && SymbolEqualityComparer.Default.Equals(Type, other.Type);

        public override int GetHashCode() => HashCode.Combine(SymbolEqualityComparer.Default.GetHashCode(Type), Localizer);
    }

    /// <summary>The keys a document reads, each with its stretch (the key's characters in a plainly written string, a property's
    /// name, else the whole expression) and what it reads them from.</summary>
    private static IEnumerable<(string Key, TextSpan Span, bool Exact, Resource Resource)> Read(SyntaxNode root, SemanticModel model, HashSet<string> classes, CancellationToken cancel)
    {
        foreach (SyntaxNode node in root.DescendantNodes())
        {
            cancel.ThrowIfCancellationRequested();
            switch (node)
            {
                case ElementAccessExpressionSyntax access when access.ArgumentList.Arguments is [{ Expression: { } argument }, ..]
                    && !(access.Parent is AssignmentExpressionSyntax assignment && assignment.Left == access)
                    && model.GetSymbolInfo(access, cancel).Symbol is IPropertySymbol { IsIndexer: true }
                    && LocalizedType(model.GetTypeInfo(access.Expression, cancel).Type) is { } type
                    && Constant(argument, model, cancel) is { } key:
                    yield return (key, Span(argument, key, out bool indexed), indexed, new Resource(type, true));
                    break;
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetString" } member } call
                    when call.ArgumentList.Arguments is [{ Expression: { } argument }, ..]
                    && model.GetSymbolInfo(call, cancel).Symbol is IMethodSymbol { Name: "GetString" }
                    && LocalizedType(model.GetTypeInfo(member.Expression, cancel).Type) is { } type
                    && Constant(argument, model, cancel) is { } key:
                    yield return (key, Span(argument, key, out bool called), called, new Resource(type, true));
                    break;
                case MemberAccessExpressionSyntax { Name: IdentifierNameSyntax name } member when classes.Contains(RightmostName(member.Expression))
                    && model.GetSymbolInfo(member, cancel).Symbol is IPropertySymbol { IsStatic: true, Type.SpecialType: SpecialType.System_String } property
                    && ResourceClass(property.ContainingType) && GeneratedKey(property, cancel) is { } generated:
                    yield return (generated, name.Span, true, new Resource(property.ContainingType, false));
                    break;
            }
        }
    }

    /// <summary>Whether a document can read a localized string, by its syntax alone: a localizer's indexer or GetString given a
    /// constant, or a member of a class a .resx generates (by its name); only such a document's semantics are read.</summary>
    private static bool MayRead(SyntaxNode root, HashSet<string> classes) =>
        root.DescendantNodes().Any(node => node switch
        {
            ElementAccessExpressionSyntax access => access.ArgumentList.Arguments.Count > 0,
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetString" } } => true,
            MemberAccessExpressionSyntax member => classes.Count > 0 && classes.Contains(RightmostName(member.Expression)),
            _ => false,
        });

    /// <summary>The names of the classes a .resx generates that a project's code can reach: its own and its references'
    /// classes with a static ResourceManager property, found by name in their declarations, binding nothing.</summary>
    private static async Task<HashSet<string>> ResourceClassNamesAsync(Project project, CancellationToken cancel)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (Project reached in project.ProjectReferences.Select(r => project.Solution.GetProject(r.ProjectId)).OfType<Project>().Prepend(project))
        {
            if (await reached.GetCompilationAsync(cancel).ConfigureAwait(false) is not { } compilation)
            {
                continue;
            }
            foreach (ISymbol symbol in compilation.GetSymbolsWithName("ResourceManager", SymbolFilter.Member, cancel))
            {
                if (symbol is IPropertySymbol { IsStatic: true } && ResourceClass(symbol.ContainingType))
                {
                    names.Add(symbol.ContainingType.Name);
                }
            }
        }
        return names;
    }

    /// <summary>Whether a class is one a .resx generates: it has a static ResourceManager property of that type.</summary>
    private static bool ResourceClass(INamedTypeSymbol? type) =>
        type is not null && type.GetMembers("ResourceManager").OfType<IPropertySymbol>().Any(p => p.IsStatic && p.Type.ToDisplayString() == ResourceManager);

    /// <summary>The key a generated class's property reads: the constant its getter gives a call (ResourceManager.GetString,
    /// Arcade's GetResourceString), which the generator writes as the .resx names it.</summary>
    private static string? GeneratedKey(IPropertySymbol property, CancellationToken cancel)
    {
        foreach (SyntaxReference reference in property.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(cancel) is not PropertyDeclarationSyntax declared)
            {
                continue;
            }
            SyntaxNode? getter = (SyntaxNode?)declared.ExpressionBody ?? declared.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
            foreach (InvocationExpressionSyntax call in getter?.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>() ?? [])
            {
                if (call.ArgumentList.Arguments is [{ Expression: LiteralExpressionSyntax literal }, ..] && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    return literal.Token.ValueText;
                }
            }
        }
        return null;
    }

    /// <summary>The type a localizer reads the strings of: T of the <c>IStringLocalizer&lt;T&gt;</c> or <c>IHtmlLocalizer&lt;T&gt;</c>
    /// it is or implements; none for a localizer of no type.</summary>
    private static INamedTypeSymbol? LocalizedType(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named)
        {
            return null;
        }
        foreach (INamedTypeSymbol face in named.AllInterfaces.Prepend(named))
        {
            if (face.OriginalDefinition.ToDisplayString() is StringLocalizer or HtmlLocalizer && face.TypeArguments is [INamedTypeSymbol { TypeKind: not TypeKind.Error } localized])
            {
                return localized;
            }
        }
        return null;
    }

    /// <summary>A resource as Passenger finds its files: the name ResourceManager looks it up by, what reads it, the project
    /// whose files it is and that project's root namespace, and the files beside the code that name it by MSBuild's
    /// DependentUpon convention or that a generated class came from; none for a type outside the solution's source.</summary>
    private static async Task<LocalizedResource?> DescribeAsync(Solution solution, Resource resource, CancellationToken cancel)
    {
        INamedTypeSymbol type = resource.Type;
        if (FullName(type) is not { } full || solution.GetProject(type.ContainingAssembly, cancel) is not { FilePath: { } projectFile } project)
        {
            return null;
        }
        string shown = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        string[] declared = [.. type.Locations.Where(l => l.IsInSource && l.SourceTree?.FilePath is { Length: > 0 }).Select(l => l.SourceTree!.FilePath).Distinct(StringComparer.Ordinal)];
        if (!resource.Localizer)
        {
            // The generated class's own words for its resource: new ResourceManager("Name", …) or new ResourceManager(typeof(T)).
            string? named = ManagerName(type, cancel);
            string[] beside = [.. declared.Where(d => d.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)).Select(d => d[..^".Designer.cs".Length])];
            return named is null ? null : new LocalizedResource(named, LocalizedKinds.Class, shown, projectFile, RootNamespaceOf(project), beside);
        }
        // ResourceManagerStringLocalizerFactory: under a resources path, the root namespace, the path and the rest of the name.
        IAssemblySymbol assembly = type.ContainingAssembly;
        string root = AttributeValue(assembly, RootNamespaceAttribute) ?? assembly.Name;
        string? location = AttributeValue(assembly, ResourceLocationAttribute) is { } given ? given + "." : await ResourcesPathAsync(solution, project, cancel).ConfigureAwait(false);
        location = location?.Replace('/', '.').Replace('\\', '.');
        string name = string.IsNullOrEmpty(location) ? full
            : root + "." + location + (full.StartsWith(root + ".", StringComparison.Ordinal) ? full[(root.Length + 1)..] : full);
        // MSBuild names a .resx after the class of the .cs file of its name beside it (EmbeddedResourceUseDependentUponConvention).
        string[] dependent = string.IsNullOrEmpty(location)
            ? [.. declared.Where(d => Path.GetFileNameWithoutExtension(d) == type.Name && d.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Select(d => d[..^".cs".Length])]
            : [];
        return new LocalizedResource(name, LocalizedKinds.Localizer, shown, projectFile, RootNamespaceOf(project), dependent);
    }

    /// <summary>The root namespace MSBuild names a project's embedded files under: its RootNamespace, which is its assembly's
    /// name unless it sets another.</summary>
    private static string RootNamespaceOf(Project project) => project.DefaultNamespace is { Length: > 0 } given ? given : project.AssemblyName;

    /// <summary>The ResourcesPath the options of a project's localization set, the project's own code or that of a project
    /// referencing it (the app that runs a library's components): a constant assigned to LocalizationOptions.ResourcesPath,
    /// with a separator at its end as the factory makes it; null where none sets it.</summary>
    private static async Task<string?> ResourcesPathAsync(Solution solution, Project project, CancellationToken cancel)
    {
        IEnumerable<ProjectId> reading = solution.GetProjectDependencyGraph().GetProjectsThatTransitivelyDependOnThisProject(project.Id).Prepend(project.Id);
        foreach (Project each in reading.Select(solution.GetProject).OfType<Project>())
        {
            foreach (Document document in each.Documents)
            {
                VersionStamp version = await document.GetTextVersionAsync(cancel).ConfigureAwait(false);
                if (!ResourcesPaths.TryGetValue(document.Id, out (VersionStamp Version, string? Path) known) || known.Version != version)
                {
                    known = (version, await DocumentResourcesPathAsync(document, cancel).ConfigureAwait(false));
                    ResourcesPaths[document.Id] = known;
                }
                if (known.Path is { Length: > 0 } path)
                {
                    return path.TrimEnd('/', '\\') + ".";
                }
            }
        }
        return null;
    }

    private static async Task<string?> DocumentResourcesPathAsync(Document document, CancellationToken cancel)
    {
        SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
        // Only a document that names the property is bound.
        if (!text.ToString().Contains("ResourcesPath", StringComparison.Ordinal)
            || await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root
            || await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
        {
            return null;
        }
        foreach (AssignmentExpressionSyntax assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ResourcesPath" } or IdentifierNameSyntax { Identifier.ValueText: "ResourcesPath" }
                && model.GetSymbolInfo(assignment.Left, cancel).Symbol is IPropertySymbol { ContainingType: { } owner } && owner.ToDisplayString() == Options
                && Constant(assignment.Right, model, cancel) is { } path)
            {
                return path;
            }
        }
        return null;
    }

    /// <summary>The name a generated class gives its ResourceManager: the string it is made with, or the full name of the type
    /// it is made from.</summary>
    private static string? ManagerName(INamedTypeSymbol type, CancellationToken cancel)
    {
        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
        {
            foreach (BaseObjectCreationExpressionSyntax made in reference.GetSyntax(cancel).DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
            {
                if (made.ArgumentList?.Arguments is not [{ Expression: { } first }, ..]
                    || made is ObjectCreationExpressionSyntax { Type: var named } && !named.ToString().EndsWith("ResourceManager", StringComparison.Ordinal))
                {
                    continue;
                }
                if (first is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    return literal.Token.ValueText;
                }
                if (first is TypeOfExpressionSyntax typeOf && typeOf.Type.ToString().Split('.')[^1] == type.Name)
                {
                    return FullName(type);
                }
            }
        }
        return null;
    }

    private static string? AttributeValue(IAssemblySymbol assembly, string attribute) =>
        assembly.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == attribute)?.ConstructorArguments.FirstOrDefault().Value as string;

    /// <summary>A type's full name as reflection gives it (<c>Type.FullName</c>): its namespace, a nested type after a '+';
    /// none for a generic type, whose name no resource is given.</summary>
    private static string? FullName(INamedTypeSymbol type)
    {
        if (type.IsGenericType)
        {
            return null;
        }
        string name = type.MetadataName;
        for (INamedTypeSymbol? outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            if (outer.IsGenericType)
            {
                return null;
            }
            name = outer.MetadataName + "+" + name;
        }
        return type.ContainingNamespace is { IsGlobalNamespace: false } space ? space.ToDisplayString() + "." + name : name;
    }

    private static string RightmostName(ExpressionSyntax expression) => expression switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    /// <summary>A key's stretch: its characters within a plainly written string, else the whole expression.</summary>
    private static TextSpan Span(ExpressionSyntax written, string key, out bool exact)
    {
        exact = written is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) && literal.Token.Text == "\"" + key + "\"";
        return exact ? new TextSpan(written.SpanStart + 1, key.Length) : written.Span;
    }
}
