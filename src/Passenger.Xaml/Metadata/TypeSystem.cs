using Microsoft.CodeAnalysis.CSharp;

namespace Passenger.Xaml.Metadata;

/// <summary>An xmlns URI's CLR namespace, as an assembly's <c>XmlnsDefinition</c> maps it.</summary>
public sealed record XmlnsDefinition(string Uri, string ClrNamespace, string AssemblyName);

/// <summary>Every type a project's XAML can name: its referenced assemblies' public types and its own C#'s, by full name
/// and by namespace, with the xmlns URIs the assemblies map to their namespaces. Immutable, built off the UI thread;
/// a change to the project's C# builds a new one from the assemblies already read.</summary>
public sealed class TypeSystem
{
    private readonly Dictionary<string, ClrType> byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ClrType>> byNamespace = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ClrType>> nestedIn = new(StringComparer.Ordinal);
    private readonly List<XmlnsDefinition> definitions = [];
    private readonly Dictionary<string, string> prefixes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<SourcePlace>> parts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CSharpParseOptions> parsing = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<string> globalUsings;

    public TypeSystem(IReadOnlyList<AssemblyTypes> assemblies, IReadOnlyList<SourceTypes> sources, string assemblyName, IReadOnlyList<string>? implicitUsings = null)
    {
        AssemblyName = assemblyName;
        Assemblies = assemblies;
        foreach (AssemblyTypes assembly in assemblies)
        {
            foreach (ClrType type in assembly.Types)
            {
                // The first assembly that declares a name keeps it, as the compiler's first reference does.
                if (byName.TryAdd(type.FullName, type))
                {
                    Index(type);
                }
            }
            Definitions(assembly.Attributes, assembly.Name);
        }
        List<string> usings = [.. implicitUsings ?? []];
        foreach (IGrouping<string, ClrType> parts in sources.SelectMany(s => s.Types).GroupBy(t => t.FullName, StringComparer.Ordinal))
        {
            ClrType type = parts.Count() == 1 ? parts.First() : Merged([.. parts]);
            this.parts[type.FullName] = [.. parts.Select(p => p.Origin.Place).OfType<SourcePlace>()];
            if (byName.TryGetValue(type.FullName, out ClrType? metadata))
            {
                Unindex(metadata);
            }
            byName[type.FullName] = type;
            Index(type);
        }
        foreach (SourceTypes source in sources)
        {
            usings.AddRange(source.GlobalUsings);
            Definitions(source.Attributes, assemblyName);
            if (source.Parsing is { } options)
            {
                parsing[source.Path] = options;
            }
        }
        globalUsings = [.. usings.Distinct(StringComparer.Ordinal)];
        Namespaces = [.. byNamespace.Keys.Where(n => n.Length > 0).Order(StringComparer.Ordinal)];
    }

    /// <summary>The project's own assembly's name, which its source types are in.</summary>
    public string AssemblyName { get; }

    public IReadOnlyList<AssemblyTypes> Assemblies { get; }

    /// <summary>Every CLR namespace that holds a type, in order.</summary>
    public IReadOnlyList<string> Namespaces { get; }

    public IReadOnlyList<XmlnsDefinition> XmlnsDefinitions => definitions;

    /// <summary>The prefix each xmlns URI suggests for itself (<c>XmlnsPrefix</c>).</summary>
    public IReadOnlyDictionary<string, string> XmlnsPrefixes => prefixes;

    public IEnumerable<ClrType> All => byName.Values;

    public ClrType? Find(string fullName) => byName.GetValueOrDefault(fullName);

    /// <summary>Where the project's C# declares a type, each part of a partial one (a code-behind and its other files).</summary>
    public IReadOnlyList<SourcePlace> PlacesOf(string fullName) => parts.GetValueOrDefault(fullName) ?? [];

    /// <summary>What a C# file whose types these are was parsed with (<see cref="SourceTypes.Parsing"/>); null for a file
    /// not read as C#.</summary>
    public CSharpParseOptions? ParsingOf(string path) => parsing.GetValueOrDefault(path);

    /// <summary>The top-level types of a CLR namespace.</summary>
    public IReadOnlyList<ClrType> InNamespace(string clrNamespace) => byNamespace.TryGetValue(clrNamespace, out List<ClrType>? types) ? types : [];

    /// <summary>The types an xmlns URI's CLR namespaces hold.</summary>
    public IEnumerable<string> NamespacesOf(string uri) => definitions.Where(d => d.Uri == uri).Select(d => d.ClrNamespace).Distinct(StringComparer.Ordinal);

    /// <summary>The CLR namespaces an xmlns URI means: a <c>clr-namespace:</c> or <c>using:</c> one's own, else those the
    /// assemblies map it to.</summary>
    public IReadOnlyList<string> ClrNamespacesOf(string? uri)
    {
        if (uri is null)
        {
            return [];
        }
        if (uri.StartsWith("using:", StringComparison.Ordinal))
        {
            return [uri["using:".Length..].Trim()];
        }
        if (uri.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            string rest = uri["clr-namespace:".Length..];
            int semicolon = rest.IndexOf(';');
            return [(semicolon < 0 ? rest : rest[..semicolon]).Trim()];
        }
        return [.. NamespacesOf(uri)];
    }

    /// <summary>The type a reference names: from metadata by its full name; from source as the compiler would resolve it
    /// in its scope (its enclosing types, its namespace and those around it, its aliases, its usings, the project's global
    /// ones). A <c>Nullable&lt;T&gt;</c> is its <c>T</c>.</summary>
    public ClrType? Resolve(TypeRef? reference)
    {
        if (reference is null || reference.IsGenericParameter)
        {
            return null;
        }
        if (reference.Name == "System.Nullable`1" && reference.Arguments.Count == 1)
        {
            return Resolve(reference.Arguments[0]);
        }
        if (reference.Scope is not { } scope)
        {
            return Find(reference.Name);
        }
        return Resolve(reference.Name, scope);
    }

    /// <summary>A name as source writes it, resolved in a scope.</summary>
    public ClrType? Resolve(string name, SourceScope scope)
    {
        if (TypeNames.FromKeyword(name) is { } keyword)
        {
            return Find(keyword);
        }
        int dot = name.IndexOf('.');
        string first = dot < 0 ? name : name[..dot];
        if (scope.Aliases.TryGetValue(first, out string? aliased))
        {
            return Find(dot < 0 ? aliased : aliased + name[dot..]);
        }
        for (int i = scope.Enclosing.Count - 1; i >= 0; i--)
        {
            if (Find($"{scope.Enclosing[i]}+{name.Replace('.', '+')}") is { } nested)
            {
                return nested;
            }
        }
        for (string? space = scope.Namespace; space is not null; space = Outer(space))
        {
            if (Find(space.Length > 0 ? $"{space}.{name}" : name) is { } found)
            {
                return found;
            }
        }
        if (Find(name) is { } full)
        {
            return full;
        }
        foreach (string used in scope.Usings.Concat(globalUsings))
        {
            if (Find($"{used}.{name}") is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>The namespace that holds another (<c>A.B</c> for <c>A.B.C</c>), the global one's empty, then none.</summary>
    private static string? Outer(string space)
    {
        if (space.Length == 0)
        {
            return null;
        }
        int dot = space.LastIndexOf('.');
        return dot < 0 ? string.Empty : space[..dot];
    }

    /// <summary>A type's base class, when it resolves to a class (a source type's first base may be an interface).</summary>
    public ClrType? BaseOf(ClrType type)
    {
        if (type.Kind is ClrTypeKind.Interface or ClrTypeKind.Enum || Resolve(type.Base) is not { } found || found.Kind == ClrTypeKind.Interface || found == type)
        {
            return null;
        }
        return found;
    }

    /// <summary>The interfaces a type names, its first base too when that resolves to one; never the type itself, which
    /// source on its way to another name can say (<c>interface IViewModel : IViewModel</c>).</summary>
    public IEnumerable<ClrType> InterfacesOf(ClrType type)
    {
        if (type.Base is { } first && Resolve(first) is { Kind: ClrTypeKind.Interface } asInterface && asInterface != type)
        {
            yield return asInterface;
        }
        foreach (TypeRef named in type.Interfaces)
        {
            if (Resolve(named) is { } found && found != type)
            {
                yield return found;
            }
        }
    }

    /// <summary>A type and its bases, nearest first, each once: half-typed source can make a cycle of bases
    /// (<c>class A : B</c> and <c>class B : A</c>), which ends where it comes round.</summary>
    public IEnumerable<ClrType> SelfAndBases(ClrType type)
    {
        HashSet<ClrType> seen = [];
        for (ClrType? at = type; at is not null && seen.Add(at); at = BaseOf(at))
        {
            yield return at;
        }
    }

    /// <summary>Whether a type is, derives from or implements the one named: its bases and every interface they reach, each
    /// looked at once, so a cycle of them (<c>interface A : B</c> and <c>interface B : A</c>, half-typed) ends.</summary>
    public bool IsA(ClrType type, string fullName)
    {
        HashSet<ClrType> seen = [];
        Stack<ClrType> left = new([type]);
        while (left.TryPop(out ClrType? at))
        {
            if (!seen.Add(at))
            {
                continue;
            }
            if (at.FullName == fullName)
            {
                return true;
            }
            if (BaseOf(at) is { } baseType)
            {
                left.Push(baseType);
            }
            foreach (ClrType implemented in InterfacesOf(at))
            {
                left.Push(implemented);
            }
        }
        return false;
    }

    /// <summary>Every member of a type and its bases, nearest first: a member a type hides keeps only the nearest.</summary>
    public IEnumerable<ClrMember> MembersOf(ClrType type)
    {
        HashSet<(string, MemberKind)> seen = [];
        foreach (ClrType at in SelfAndBases(type))
        {
            foreach (ClrMember member in at.Members)
            {
                if (member.Kind is MemberKind.Constructor || member.Kind == MemberKind.Method || seen.Add((member.Name, member.Kind)))
                {
                    yield return member;
                }
            }
        }
    }

    /// <summary>The types nested in one.</summary>
    public IReadOnlyList<ClrType> NestedIn(ClrType type) => nestedIn.TryGetValue(type.FullName, out List<ClrType>? nested) ? nested : [];

    private void Index(ClrType type)
    {
        if (type.DeclaringTypeName is { } declaring)
        {
            if (!nestedIn.TryGetValue(declaring, out List<ClrType>? nested))
            {
                nestedIn[declaring] = nested = [];
            }
            nested.Add(type);
            return;
        }
        if (!byNamespace.TryGetValue(type.Namespace, out List<ClrType>? types))
        {
            byNamespace[type.Namespace] = types = [];
        }
        types.Add(type);
    }

    private void Unindex(ClrType type)
    {
        if (type.DeclaringTypeName is { } declaring)
        {
            nestedIn.GetValueOrDefault(declaring)?.Remove(type);
        }
        else
        {
            byNamespace.GetValueOrDefault(type.Namespace)?.Remove(type);
        }
    }

    private void Definitions(IReadOnlyList<ClrAttribute> attributes, string assembly)
    {
        foreach (ClrAttribute attribute in attributes)
        {
            if (attribute.ShortName == "XmlnsDefinition" && attribute.StringArgument(0) is { } uri && attribute.StringArgument(1) is { } clr)
            {
                definitions.Add(new XmlnsDefinition(uri, clr, assembly));
            }
            else if (attribute.ShortName == "XmlnsPrefix" && attribute.StringArgument(0) is { } prefixed && attribute.StringArgument(1) is { } prefix)
            {
                prefixes.TryAdd(prefixed, prefix);
            }
        }
    }

    /// <summary>A partial type's parts as one: every part's members and attributes, the base whichever part names it.</summary>
    private static ClrType Merged(IReadOnlyList<ClrType> parts)
    {
        ClrType first = parts.FirstOrDefault(p => p.Base is not null) ?? parts[0];
        return new ClrType(first.Namespace, first.MetadataName, first.Kind, first.Origin, merged => new ClrTypeDetails(
            first.Base,
            [.. parts.SelectMany(p => p.Base is not null && p != first ? [p.Base, .. p.Interfaces] : p.Interfaces)],
            [.. parts.SelectMany(p => p.Attributes)],
            [.. parts.SelectMany(p => p.Members.Select(m => new ClrMember(merged, m.Name, m.Kind, m.Type, m.IsStatic, m.IsPublic, m.CanWrite, m.Parameters,
                m.ParameterNames, m.Attributes, m.Place, m.Constant)))],
            parts.Any(p => p.IsAbstract), parts.Any(p => p.IsStatic), parts.All(p => p.IsPublic),
            !parts.Any(p => p.Members.Any(m => m.Kind == MemberKind.Constructor))
                || parts.Any(p => p.Members.Any(m => m.Kind == MemberKind.Constructor && m.Parameters.Count == 0 && m.IsPublic)),
            first.GenericParameters), first.DeclaringTypeName);
    }
}
