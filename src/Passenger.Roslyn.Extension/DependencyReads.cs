using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;

namespace Passenger.Roslyn.Extension;

/// <summary>What the map reads of the solution. Each document's declarations and uses are read once for what it was read
/// from (<see cref="DocumentReadsKept{T}"/>), so a level asked again after an edit binds only what the edit reached; a level
/// is put together from them as it is asked.</summary>
internal static class DependencyMap
{
    private static readonly DocumentReadsKept<DocumentDependencies> Reads = new();

    /// <summary>How a type is named on the map: with the types it is nested in and its type parameters.</summary>
    private static readonly SymbolDisplayFormat TypeShown = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    /// <summary>How a member is named on the map: with its parameters' types, as an overload is told apart.</summary>
    private static readonly SymbolDisplayFormat MemberShown = new(
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>The name the global namespace is shown by, as C# names it.</summary>
    public const string GlobalNamespace = "<global namespace>";

    /// <summary>The level below a node (<see cref="DependencyIds"/>), the projects for none, over the solution's C# projects
    /// but those <paramref name="unchosen"/> (a multi-targeted project's frameworks not chosen), each edge broken where one of
    /// its uses breaks <paramref name="rules"/>.</summary>
    public static async Task<DependenciesFound> LevelAsync(Solution solution, string? asked, IReadOnlySet<ProjectId> unchosen, LayerRules? rules, CancellationToken cancel)
    {
        Chosen chosen = Chosen.Of(solution, unchosen);
        if (asked is null)
        {
            return chosen.Projects(rules);
        }
        if (DependencyIds.Read(asked) is not { } of || !chosen.ByPath.TryGetValue(of.Project, out Project? project))
        {
            return new DependenciesFound([], []);
        }
        IReadOnlyList<DocumentDependencies> read = await Reads.OfAsync(project, project.Documents, ReadAsync, cancel).ConfigureAwait(false);
        Level level = new(of.Project, read, chosen, rules);
        return of.Level switch
        {
            0 => level.Namespaces(),
            1 => level.Types(of.Own!),
            2 => level.Members(of.Own!),
            _ => new DependenciesFound([], []),
        };
    }

    /// <summary>The ids from the project down of the symbol at a place in a document, else of the declaration around it, else
    /// of the document's first type.</summary>
    public static async Task<IReadOnlyList<string>> PlaceAsync(Document document, int position, CancellationToken cancel)
    {
        if (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root
            || await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
        {
            return [];
        }
        ISymbol? found = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancel).ConfigureAwait(false);
        ISymbol? symbol = found is INamespaceSymbol { IsGlobalNamespace: false } space ? space : Target(found);
        for (SyntaxNode? node = root.FindToken(position).Parent; symbol is null && node is not null; node = node.Parent)
        {
            symbol = node is MemberDeclarationSyntax or VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax } ? Target(model.GetDeclaredSymbol(node, cancel)) : null;
        }
        symbol ??= root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<BaseTypeDeclarationSyntax>()
            .Select(t => model.GetDeclaredSymbol(t, cancel)).FirstOrDefault(t => t is not null);
        symbol ??= root is CompilationUnitSyntax unit && model.GetDeclaredSymbol(unit, cancel) is { } entry ? entry.ContainingType : null;
        string? project = symbol is INamespaceSymbol ? document.Project.FilePath
            : symbol is null ? null
            : document.Project.Solution.Projects.FirstOrDefault(p => p.AssemblyName == (symbol as INamedTypeSymbol ?? symbol.ContainingType)?.ContainingAssembly?.Name && p.FilePath is not null)?.FilePath;
        if (project is null || symbol is null)
        {
            return [];
        }
        if (symbol is INamespaceSymbol named)
        {
            return [DependencyIds.Project(project), DependencyIds.Namespace(project, named.ToDisplayString())];
        }
        INamedTypeSymbol type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        List<string> path = [DependencyIds.Project(project), DependencyIds.Namespace(project, NamespaceOf(type)), DependencyIds.Type(project, IdOf(type))];
        if (symbol is not INamedTypeSymbol)
        {
            path.Add(DependencyIds.Member(project, IdOf(symbol)));
        }
        return path;
    }

    /// <summary>The uses in a document's code that break layer rules: each where it is written, by the layers of the code
    /// using (its project, its type's namespace, the document) and the code used (its project, namespace and the file
    /// declaring its type), in the document's order.</summary>
    public static async Task<IReadOnlyList<LayerBreak>> BrokenAsync(Document document, LayerRules rules, CancellationToken cancel)
    {
        if (rules.Layers.Count == 0 || await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not CompilationUnitSyntax root
            || await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
        {
            return [];
        }
        Solution solution = document.Project.Solution;
        Dictionary<string, string> projects = new(StringComparer.Ordinal);
        foreach (Project project in solution.Projects.Where(p => p.FilePath is not null))
        {
            projects.TryAdd(project.AssemblyName, Path.GetFileNameWithoutExtension(project.FilePath!));
        }
        string own = Path.GetFileNameWithoutExtension(document.Project.FilePath ?? document.Project.Name);
        SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
        List<LayerBreak> broken = [];
        Walker walker = new(model, document.FilePath, cancel)
        {
            Heard = (span, user, used) =>
            {
                INamedTypeSymbol owner = used as INamedTypeSymbol ?? used.ContainingType;
                if (rules.Of(own, NamespaceOf(user), document.FilePath) is { } from && owner.ContainingAssembly?.Name is { } assembly
                    && projects.TryGetValue(assembly, out string? project)
                    && rules.Of(project, NamespaceOf(owner), FileOf(owner)) is { } to
                    && LayerRules.Breaks(from, to))
                {
                    LinePositionSpan at = text.Lines.GetLinePositionSpan(span);
                    broken.Add(new LayerBreak(new Place(at.Start.Line, at.Start.Character), new Place(at.End.Line, at.End.Character), from.Name, to.Name,
                        used.ToDisplayString(used is INamedTypeSymbol ? TypeShown : SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
                }
            },
        };
        walker.Read(root);
        return broken;
    }

    /// <summary>A document's types and members declared, and what each uses, its code bound only where it declares a type
    /// (or holds a program's top-level statements).</summary>
    private static async Task<DocumentReadsKept<DocumentDependencies>.Read> ReadAsync(Document document, CancellationToken cancel)
    {
        if (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not CompilationUnitSyntax root
            || !root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).Any(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or GlobalStatementSyntax)
            || await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
        {
            return new(DocumentDependencies.None, Bound: false);
        }
        Walker walker = new(model, document.FilePath, cancel);
        walker.Read(root);
        return new(walker.Found(), Bound: true);
    }

    /// <summary>What a use names, as the map keeps it: a type of the solution's own code, or a member of one (an accessor as
    /// its property or event, an extension method called on a value as declared); null for anything else (a local, a
    /// parameter, a namespace, a type parameter, what a package or the framework declares).</summary>
    internal static ISymbol? Target(ISymbol? symbol)
    {
        switch (symbol)
        {
            case IAliasSymbol alias:
                return Target(alias.Target);
            case IMethodSymbol { ReducedFrom: { } extension }:
                return Target(extension);
            case IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise, AssociatedSymbol: { } owner }:
                return Target(owner);
            case IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction }:
                return null;
            case INamedTypeSymbol { TypeKind: TypeKind.Error }:
            case INamedTypeSymbol { IsAnonymousType: true }:
                return null;
            case INamedTypeSymbol type:
                return Written(type.OriginalDefinition);
            case IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol when symbol.ContainingType is { IsAnonymousType: false, TypeKind: not TypeKind.Error }:
                return Written(symbol.OriginalDefinition);
            default:
                return null;
        }

        static ISymbol? Written(ISymbol symbol) => symbol.Locations.Any(l => l.IsInSource) ? symbol : null;
    }

    /// <summary>A symbol's id, its documentation id where it has one.</summary>
    internal static string IdOf(ISymbol symbol) => symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString();

    /// <summary>The file a type is first declared in; none for one declared in no file.</summary>
    internal static string? FileOf(INamedTypeSymbol type) => type.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath;

    /// <summary>A type's namespace by its full name, the global namespace's empty.</summary>
    internal static string NamespaceOf(INamedTypeSymbol type) => type.ContainingNamespace is { IsGlobalNamespace: false } space ? space.ToDisplayString() : string.Empty;

    /// <summary>A type's kind as the map shows it: a record's as a record, else as a symbol's kind is named.</summary>
    private static string KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
        INamedTypeSymbol { IsRecord: true } => "record",
        _ => SymbolKinds.Of(symbol),
    };

    /// <summary>The solution's projects the map shows: its C# projects, each multi-targeted one's in the framework chosen for
    /// it (else its first), by their files, and which of them builds each assembly.</summary>
    private sealed class Chosen(Solution solution)
    {
        public Dictionary<string, Project> ByPath { get; } = new(PathNames.Comparer);

        public Dictionary<string, string> PathOfAssembly { get; } = new(StringComparer.Ordinal);

        public static Chosen Of(Solution solution, IReadOnlySet<ProjectId> unchosen)
        {
            Chosen chosen = new(solution);
            foreach (Project project in solution.Projects)
            {
                if (project.Language == LanguageNames.CSharp && project.FilePath is { } path && !unchosen.Contains(project.Id) && chosen.ByPath.TryAdd(path, project))
                {
                    chosen.PathOfAssembly.TryAdd(project.AssemblyName, path);
                }
            }
            return chosen;
        }

        /// <summary>The projects, each with the projects it references, a reference broken where the rules put the two projects
        /// in layers the one may not use the other of.</summary>
        public DependenciesFound Projects(LayerRules? rules)
        {
            List<DependencyNode> nodes = [];
            List<DependencyEdge> edges = [];
            foreach ((string path, Project project) in ByPath.OrderBy(p => Path.GetFileNameWithoutExtension(p.Key), StringComparer.OrdinalIgnoreCase))
            {
                nodes.Add(new DependencyNode(DependencyIds.Project(path), Path.GetFileNameWithoutExtension(path), "project", path, new Place(0, 0), project.DocumentIds.Count > 0));
                HashSet<string> referenced = new(PathNames.Comparer);
                foreach (ProjectReference reference in project.ProjectReferences)
                {
                    if (solution.GetProject(reference.ProjectId)?.FilePath is { } other && ByPath.ContainsKey(other) && !PathNames.Same(other, path) && referenced.Add(other))
                    {
                        edges.Add(new DependencyEdge(DependencyIds.Project(path), [DependencyIds.Project(other)], new Dictionary<string, int> { [DependencyKinds.References] = 1 },
                            rules is not null && rules.Breaks(Path.GetFileNameWithoutExtension(path), path, Path.GetFileNameWithoutExtension(other), other)));
                    }
                }
            }
            return new DependenciesFound(nodes, edges);
        }
    }

    /// <summary>A project's levels, put together from its documents' reads, each edge broken where one of its uses breaks
    /// the rules: the code using placed by its project, its type's namespace and its document, the code used by its project,
    /// its namespace and the file its type is first declared in.</summary>
    private sealed class Level(string project, IReadOnlyList<DocumentDependencies> read, Chosen chosen, LayerRules? rules)
    {
        /// <summary>Each type declared, at its first declaration, by its id.</summary>
        private readonly Dictionary<string, DeclaredType> types = Declared(read);

        public DependenciesFound Namespaces()
        {
            List<DependencyNode> nodes = [.. types.Values.GroupBy(t => t.Namespace, StringComparer.Ordinal).OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase)
                .Select(n => new DependencyNode(DependencyIds.Namespace(project, n.Key), n.Key.Length == 0 ? GlobalNamespace : n.Key, "namespace", n.First().Path, n.First().At, Expands: true))];
            Edges edges = new();
            foreach (Use use in read.SelectMany(r => r.Uses))
            {
                if (types.TryGetValue(use.FromType, out DeclaredType? from) && chosen.PathOfAssembly.TryGetValue(use.ToAssembly, out string? to))
                {
                    edges.Add(DependencyIds.Namespace(project, from.Namespace), [DependencyIds.Project(to), DependencyIds.Namespace(to, use.ToNamespace)], use, Breaks(use, from.Namespace));
                }
            }
            return new DependenciesFound(nodes, edges.Listed());
        }

        public DependenciesFound Types(string space)
        {
            HashSet<string> members = [.. read.SelectMany(r => r.Members).Select(m => m.Type)];
            List<DependencyNode> nodes = [.. types.Values.Where(t => t.Namespace == space).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => new DependencyNode(DependencyIds.Type(project, t.Id), t.Name, t.Kind, t.Path, t.At, members.Contains(t.Id)))];
            Edges edges = new();
            foreach (Use use in read.SelectMany(r => r.Uses))
            {
                if (types.TryGetValue(use.FromType, out DeclaredType? from) && from.Namespace == space && To(use) is { } to)
                {
                    edges.Add(DependencyIds.Type(project, use.FromType), to, use, Breaks(use, from.Namespace));
                }
            }
            return new DependenciesFound(nodes, edges.Listed());
        }

        public DependenciesFound Members(string type)
        {
            List<DependencyNode> nodes = [.. read.SelectMany(r => r.Members).Where(m => m.Type == type).DistinctBy(m => m.Id)
                .Select(m => new DependencyNode(DependencyIds.Member(project, m.Id), m.Name, m.Kind, m.Path, m.At, Expands: false))];
            Edges edges = new();
            foreach (Use use in read.SelectMany(r => r.Uses))
            {
                if (use.FromType == type && To(use) is { } to)
                {
                    edges.Add(use.FromMember is { } member ? DependencyIds.Member(project, member) : DependencyIds.Type(project, type), to, use,
                        Breaks(use, types.GetValueOrDefault(type)?.Namespace ?? string.Empty));
                }
            }
            return new DependenciesFound(nodes, edges.Listed());
        }

        /// <summary>Whether a use of a type of a namespace breaks the rules.</summary>
        private bool Breaks(Use use, string space) =>
            rules is not null && chosen.PathOfAssembly.TryGetValue(use.ToAssembly, out string? to)
            && rules.Of(Path.GetFileNameWithoutExtension(project), space, use.FromFile) is { } user
            && rules.Of(Path.GetFileNameWithoutExtension(to), use.ToNamespace, use.ToFile) is { } used && LayerRules.Breaks(user, used);

        /// <summary>The ids from its project down to the member of what a use names, none where the project building it is not
        /// one the map shows.</summary>
        private IReadOnlyList<string>? To(Use use)
        {
            if (!chosen.PathOfAssembly.TryGetValue(use.ToAssembly, out string? to))
            {
                return null;
            }
            List<string> path = [DependencyIds.Project(to), DependencyIds.Namespace(to, use.ToNamespace), DependencyIds.Type(to, use.ToType)];
            if (use.ToMember is { } member)
            {
                path.Add(DependencyIds.Member(to, member));
            }
            return path;
        }

        private static Dictionary<string, DeclaredType> Declared(IReadOnlyList<DocumentDependencies> read)
        {
            Dictionary<string, DeclaredType> found = new(StringComparer.Ordinal);
            foreach (DeclaredType type in read.SelectMany(r => r.Types))
            {
                found.TryAdd(type.Id, type);
            }
            return found;
        }
    }

    /// <summary>A level's edges as they are added, each pair of ends once with its count of each kind, broken where any use
    /// of it is.</summary>
    private sealed class Edges
    {
        private readonly Dictionary<string, (string From, IReadOnlyList<string> To, Dictionary<string, int> Kinds)> byEnds = new(StringComparer.Ordinal);
        private readonly HashSet<string> broken = new(StringComparer.Ordinal);

        public void Add(string from, IReadOnlyList<string> to, Use use, bool breaks)
        {
            // A use of what stands inside the node itself is no edge of its level.
            if (to.Contains(from))
            {
                return;
            }
            string key = from + "\n" + string.Join('\n', to);
            if (!byEnds.TryGetValue(key, out (string, IReadOnlyList<string>, Dictionary<string, int> Kinds) edge))
            {
                byEnds[key] = edge = (from, to, new Dictionary<string, int>(StringComparer.Ordinal));
            }
            edge.Kinds[use.Kind] = edge.Kinds.GetValueOrDefault(use.Kind) + use.Count;
            if (breaks)
            {
                broken.Add(key);
            }
        }

        public IReadOnlyList<DependencyEdge> Listed() => [.. byEnds.Select(e => new DependencyEdge(e.Value.From, e.Value.To, e.Value.Kinds, broken.Contains(e.Key)))];
    }

    /// <summary>A type a document declares: its id, its name, its kind, its namespace, and where its name is written.</summary>
    private sealed record DeclaredType(string Id, string Name, string Kind, string Namespace, string? Path, Place At);

    /// <summary>A member a document declares, of a type by its id.</summary>
    private sealed record DeclaredMember(string Type, string Id, string Name, string Kind, string? Path, Place At);

    /// <summary>Uses in a type's code, or one of its members', in a document, of a type or member of the solution's: by the
    /// assembly declaring it, its namespace, its type, the file its type is first declared in and its member, how, and how
    /// many times.</summary>
    private sealed record Use(string FromType, string? FromMember, string? FromFile, string ToAssembly, string ToNamespace, string ToType, string? ToFile, string? ToMember,
        string Kind, int Count);

    /// <summary>What a document's uses are counted by: all of a <see cref="Use"/> but its count.</summary>
    private readonly record struct UseKey(string FromType, string? FromMember, string ToAssembly, string ToNamespace, string ToType, string? ToFile, string? ToMember, string Kind);

    private sealed record DocumentDependencies(IReadOnlyList<DeclaredType> Types, IReadOnlyList<DeclaredMember> Members, IReadOnlyList<Use> Uses)
    {
        public static readonly DocumentDependencies None = new([], [], []);
    }

    /// <summary>A document read for the map: each declaration it meets, and each name, creation, attribute and operator in
    /// a type's code bound to what it uses, told by where it stands (<see cref="KindAt"/>).</summary>
    private sealed class Walker(SemanticModel model, string? path, CancellationToken cancel) : CSharpSyntaxWalker
    {
        private readonly List<DeclaredType> types = [];
        private readonly List<DeclaredMember> members = [];
        private readonly Dictionary<UseKey, int> uses = [];

        /// <summary>Told of each use as it is read: where it is written, the type whose code uses, and what it uses.</summary>
        public Action<TextSpan, INamedTypeSymbol, ISymbol>? Heard { get; init; }

        /// <summary>The type and the member whose code is being read; none outside every type.</summary>
        private INamedTypeSymbol? type;
        private ISymbol? member;

        public void Read(CompilationUnitSyntax root)
        {
            // A program's top-level statements are its entry point's, in the type the compiler makes for it.
            if (root.Members.OfType<GlobalStatementSyntax>().FirstOrDefault() is { } first && model.GetDeclaredSymbol(root, cancel) is { ContainingType: { } program } entry)
            {
                Place at = PlaceOf(first.GetLocation());
                types.Add(new DeclaredType(IdOf(program), program.ToDisplayString(TypeShown), KindOf(program), NamespaceOf(program), path, at));
                members.Add(new DeclaredMember(IdOf(program), IdOf(entry), "top-level statements", "method", path, at));
                foreach (GlobalStatementSyntax statement in root.Members.OfType<GlobalStatementSyntax>())
                {
                    Within(program, entry, statement);
                }
            }
            foreach (SyntaxNode node in root.Members.Where(m => m is not GlobalStatementSyntax))
            {
                Visit(node);
            }
        }

        public DocumentDependencies Found() => new(types, members,
            [.. uses.Select(u => new Use(u.Key.FromType, u.Key.FromMember, path, u.Key.ToAssembly, u.Key.ToNamespace, u.Key.ToType, u.Key.ToFile, u.Key.ToMember, u.Key.Kind, u.Value))]);

        public override void Visit(SyntaxNode? node)
        {
            if (node is null)
            {
                return;
            }
            switch (node)
            {
                case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax when model.GetDeclaredSymbol(node, cancel) is INamedTypeSymbol declared:
                    types.Add(new DeclaredType(IdOf(declared), declared.ToDisplayString(TypeShown), KindOf(declared), NamespaceOf(declared), path, PlaceOf(declared, node)));
                    // A record's positional parameters declare its properties.
                    if (node is RecordDeclarationSyntax { ParameterList: { } positional })
                    {
                        foreach (ParameterSyntax parameter in positional.Parameters)
                        {
                            if (declared.GetMembers(parameter.Identifier.ValueText).OfType<IPropertySymbol>().FirstOrDefault() is { } property)
                            {
                                Declare(declared, property, parameter);
                            }
                        }
                    }
                    Within(declared, null, node);
                    return;
                case BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or EnumMemberDeclarationSyntax when type is not null && model.GetDeclaredSymbol(node, cancel) is { } declared:
                    Declare(type, declared, node);
                    Within(type, declared, node);
                    return;
                case BaseFieldDeclarationSyntax field when type is not null:
                    ISymbol? first = null;
                    foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                    {
                        if (model.GetDeclaredSymbol(variable, cancel) is { } declared)
                        {
                            first ??= declared;
                            Declare(type, declared, variable);
                        }
                    }
                    if (first is not null)
                    {
                        Within(type, first, node);
                        return;
                    }
                    break;
                case VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax } variable when type is not null && model.GetDeclaredSymbol(variable, cancel) is { } declared:
                    Within(type, declared, node);
                    return;
            }
            if (type is not null)
            {
                Note(node);
            }
            base.Visit(node);
        }

        private void Within(INamedTypeSymbol now, ISymbol? declared, SyntaxNode node)
        {
            cancel.ThrowIfCancellationRequested();
            (INamedTypeSymbol? outerType, ISymbol? outerMember) = (type, member);
            (type, member) = (now, declared);
            base.Visit(node);
            (type, member) = (outerType, outerMember);
        }

        private void Declare(INamedTypeSymbol owner, ISymbol declared, SyntaxNode node)
        {
            if (declared is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove })
            {
                return;
            }
            members.Add(new DeclaredMember(IdOf(owner), IdOf(declared), declared.ToDisplayString(MemberShown), SymbolKinds.Of(declared), path, PlaceOf(declared, node)));
        }

        /// <summary>What a node of a type's code uses, where it uses anything of the solution's.</summary>
        private void Note(SyntaxNode node)
        {
            switch (node)
            {
                case SimpleNameSyntax name when Target(Bound(model.GetSymbolInfo(name, cancel))) is { } target && KindAt(name, target) is { } kind:
                    Add(target, kind, name.Span);
                    break;
                case BaseObjectCreationExpressionSyntax or ConstructorInitializerSyntax or PrimaryConstructorBaseTypeSyntax
                    when Target(Bound(model.GetSymbolInfo(node, cancel))) is { } made:
                    Add(made, DependencyKinds.Calls, node is ObjectCreationExpressionSyntax { Type: { } type } ? type.Span : node.Span);
                    break;
                case AttributeSyntax attributed when Bound(model.GetSymbolInfo(node, cancel)) is IMethodSymbol { ContainingType: { } attribute } && Target(attribute) is { } named:
                    Add(named, DependencyKinds.Attribute, attributed.Name.Span);
                    break;
                case BinaryExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax or AssignmentExpressionSyntax
                    when Bound(model.GetSymbolInfo(node, cancel)) is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } operation && Target(operation) is { } op:
                    Add(op, DependencyKinds.Calls, node switch
                    {
                        BinaryExpressionSyntax binary => binary.OperatorToken.Span,
                        AssignmentExpressionSyntax assigned => assigned.OperatorToken.Span,
                        PrefixUnaryExpressionSyntax prefix => prefix.OperatorToken.Span,
                        PostfixUnaryExpressionSyntax postfix => postfix.OperatorToken.Span,
                        _ => node.Span,
                    });
                    break;
            }
        }

        /// <summary>How a name uses what it binds to, by where it stands: a base type's, a field's or a member's signature's type,
        /// a call, or any other use; null where what it names is counted otherwise (an attribute's name, a created type's, a
        /// type a static member is reached through).</summary>
        private string? KindAt(SimpleNameSyntax name, ISymbol target)
        {
            if (name.Parent is QualifiedNameSyntax qualified && qualified.Left == name
                || name.Parent is MemberAccessExpressionSyntax reached && reached.Expression == name && target is INamedTypeSymbol)
            {
                return null;
            }
            SyntaxNode top = name;
            bool argument = false;
            while (top.Parent is QualifiedNameSyntax or AliasQualifiedNameSyntax or ArrayTypeSyntax or NullableTypeSyntax or PointerTypeSyntax or RefTypeSyntax
                or ScopedTypeSyntax or TupleElementSyntax or TupleTypeSyntax or TypeArgumentListSyntax or GenericNameSyntax)
            {
                argument |= top.Parent is TypeArgumentListSyntax;
                top = top.Parent;
            }
            return top.Parent switch
            {
                AttributeSyntax attribute when attribute.Name == top => null,
                ObjectCreationExpressionSyntax made when made.Type == top => null,
                BaseTypeSyntax when !argument => target is INamedTypeSymbol { TypeKind: TypeKind.Interface } && type!.TypeKind != TypeKind.Interface
                    ? DependencyKinds.Implements : DependencyKinds.Inherits,
                VariableDeclarationSyntax { Parent: BaseFieldDeclarationSyntax } declared when declared.Type == top => DependencyKinds.FieldType,
                PropertyDeclarationSyntax property when property.Type == top => DependencyKinds.FieldType,
                EventDeclarationSyntax raised when raised.Type == top => DependencyKinds.FieldType,
                IndexerDeclarationSyntax indexer when indexer.Type == top => DependencyKinds.ParameterType,
                MethodDeclarationSyntax method when method.ReturnType == top => DependencyKinds.ParameterType,
                OperatorDeclarationSyntax operation when operation.ReturnType == top => DependencyKinds.ParameterType,
                ConversionOperatorDeclarationSyntax conversion when conversion.Type == top => DependencyKinds.ParameterType,
                DelegateDeclarationSyntax called when called.ReturnType == top => DependencyKinds.ParameterType,
                ParameterSyntax { Parent.Parent: BaseMethodDeclarationSyntax or IndexerDeclarationSyntax or DelegateDeclarationSyntax or TypeDeclarationSyntax } taken
                    when taken.Type == top => DependencyKinds.ParameterType,
                _ => target is IMethodSymbol && Called(name) ? DependencyKinds.Calls : DependencyKinds.Uses,
            };
        }

        /// <summary>Whether a name is what a call calls: alone, after a dot, or after a conditional access's.</summary>
        private static bool Called(SimpleNameSyntax name) => name.Parent switch
        {
            InvocationExpressionSyntax call => call.Expression == name,
            MemberAccessExpressionSyntax access when access.Name == name => access.Parent is InvocationExpressionSyntax call && call.Expression == access,
            MemberBindingExpressionSyntax binding => binding.Parent is InvocationExpressionSyntax call && call.Expression == binding,
            _ => false,
        };

        private void Add(ISymbol target, string kind, TextSpan span)
        {
            Heard?.Invoke(span, type!, target);
            INamedTypeSymbol owner = target as INamedTypeSymbol ?? target.ContainingType;
            if (owner.ContainingAssembly?.Name is not { } assembly)
            {
                return;
            }
            UseKey key = new(IdOf(type!), member is null ? null : IdOf(member), assembly, NamespaceOf(owner), IdOf(owner), FileOf(owner),
                target is INamedTypeSymbol ? null : IdOf(target), kind);
            uses[key] = uses.GetValueOrDefault(key) + 1;
        }

        /// <summary>What a binding found, or its first candidate where it found several or none fitting.</summary>
        private static ISymbol? Bound(SymbolInfo info) => info.Symbol ?? info.CandidateSymbols.FirstOrDefault();

        private Place PlaceOf(ISymbol declared, SyntaxNode node) =>
            PlaceOf(declared.Locations.FirstOrDefault(l => l.SourceTree == node.SyntaxTree && node.Span.Contains(l.SourceSpan)) ?? node.GetLocation());

        private static Place PlaceOf(Location location)
        {
            LinePosition start = location.GetLineSpan().StartLinePosition;
            return new Place(start.Line, start.Character);
        }
    }
}
