using System.Text;

namespace Passenger.App.Testing;

/// <summary>A run's coverage as a person reads it by code: each assembly, its namespaces, their types and the types' members,
/// each with what its lines come to. What the compiler makes of a member (an async method's state machine, a lambda, a local
/// function, an iterator) counts as the member it was written in, the one of that name starting nearest above it; a nested
/// type the person wrote is a type of its own, named with its outer one's.</summary>
internal static class CoverageTree
{
    /// <summary>The tree of a report's types, counted from the merged lines of its files (a type no file was found for from
    /// its own lines).</summary>
    public static IReadOnlyList<CoverageNode> Of(IReadOnlyList<CoverageClass> classes, IReadOnlyDictionary<string, IReadOnlyList<CoveredLine>> files)
    {
        Dictionary<string, Dictionary<int, CoveredLine>> merged = new(Core.PathNames.Comparer);
        // A type of no file found keeps its lines here, under its own name in place of a path.
        Dictionary<(string, int), CoveredLine> own = [];
        foreach ((string path, IReadOnlyList<CoveredLine> lines) in files)
        {
            merged[path] = lines.ToDictionary(l => l.Line);
        }
        Dictionary<(string Project, string Namespace, string Type), TypeBuilt> types = [];
        List<(TypeBuilt Type, string Origin, CoverageClass Class, CoverageMethod Method)> made = [];
        foreach (CoverageClass type in classes)
        {
            (string ns, string name, bool compiled, string? origin) = Named(type.Name);
            if (!types.TryGetValue((type.Package, ns, name), out TypeBuilt? built))
            {
                types[(type.Package, ns, name)] = built = new TypeBuilt(type.Package, ns, name);
            }
            built.Add(type, !compiled, Lines(type, type.Lines));
            foreach (CoverageMethod method in type.Methods)
            {
                if ((origin ?? OriginOf(method.Name)) is { } written)
                {
                    made.Add((built, written, type, method));
                }
                else
                {
                    built.Member(Shown(method.Name, name), method.Signature, IsAccessor(method.Name)).Add(type.Path, Lines(type, method.Lines));
                }
            }
        }
        foreach ((TypeBuilt type, string origin, CoverageClass of, CoverageMethod method) in made)
        {
            int first = method.Lines.Count > 0 ? method.Lines.Min(l => l.Line) : 0;
            MemberBuilt member = type.Members.Values
                .Where(m => m.Name == origin && m.Path is not null && Core.PathNames.Same(m.Path, of.Path ?? string.Empty) && m.First <= first)
                .MaxBy(m => m.First) ?? type.Member(origin, string.Empty, accessor: true);
            member.Add(of.Path, Lines(of, method.Lines));
        }
        List<CoverageNode> projects = [];
        foreach (IGrouping<string, TypeBuilt> project in types.Values.GroupBy(t => t.Project, StringComparer.Ordinal))
        {
            HashSet<(string, int)> projectLines = [];
            List<CoverageNode> children = [];
            foreach (IGrouping<string, TypeBuilt> space in project.GroupBy(t => t.Namespace, StringComparer.Ordinal))
            {
                HashSet<(string, int)> spaceLines = [];
                List<CoverageNode> typeNodes = [];
                foreach (TypeBuilt type in space)
                {
                    typeNodes.Add(type.Node(Count));
                    spaceLines.UnionWith(type.Lines);
                }
                projectLines.UnionWith(spaceLines);
                if (space.Key.Length == 0)
                {
                    children.AddRange(typeNodes);
                    continue;
                }
                children.Add(new CoverageNode(CoverageNodeKind.Namespace, space.Key, $"{project.Key}.{space.Key}")
                {
                    Counts = Count(spaceLines),
                    Children = Sorted(typeNodes),
                });
            }
            projects.Add(new CoverageNode(CoverageNodeKind.Project, project.Key, project.Key) { Counts = Count(projectLines), Children = Sorted(children) });
        }
        return Sorted(projects);

        CoverageCounts Count(IEnumerable<(string Path, int Line)> lines)
        {
            CoverageCounts counts = default;
            foreach ((string path, int line) in lines)
            {
                if (merged.TryGetValue(path, out Dictionary<int, CoveredLine>? its) && its.TryGetValue(line, out CoveredLine found))
                {
                    counts = counts.Plus(found);
                }
                else if (own.TryGetValue((path, line), out CoveredLine kept))
                {
                    counts = counts.Plus(kept);
                }
            }
            return counts;
        }

        IEnumerable<(string, int)> Lines(CoverageClass type, IReadOnlyList<CoveredLine> lines)
        {
            string key = type.Path ?? $"\0{type.Package}/{type.Name}";
            foreach (CoveredLine line in lines)
            {
                if (type.Path is null)
                {
                    own[(key, line.Line)] = own.TryGetValue((key, line.Line), out CoveredLine was) ? was.And(line) : line;
                }
                yield return (key, line.Line);
            }
        }
    }

    private static List<CoverageNode> Sorted(List<CoverageNode> nodes) => [.. nodes.OrderBy(n => n.Kind == CoverageNodeKind.Namespace ? 0 : 1).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>A report's type name read: its namespace, its name as written (a nested type's with its outer type's, a generic's
    /// with its parameters' places), whether the compiler made it, and the member it was made from, where its name says one
    /// (an async method's or an iterator's state machine; a lambdas' class says none, its methods do).</summary>
    internal static (string Namespace, string Name, bool Made, string? Origin) Named(string reported)
    {
        string[] parts = reported.Split('/', '+');
        string outer = parts[0];
        int dot = outer.LastIndexOf('.');
        string ns = dot < 0 ? string.Empty : outer[..dot];
        StringBuilder name = new(Generic(dot < 0 ? outer : outer[(dot + 1)..]));
        string? origin = null;
        bool made = false;
        foreach (string part in parts.Skip(1))
        {
            if (part.StartsWith('<'))
            {
                made = true;
                origin ??= OriginOf(part) ?? string.Empty;
                continue;
            }
            if (!made)
            {
                name.Append('.').Append(Generic(part));
            }
        }
        return (ns, name.ToString(), made, origin is { Length: > 0 } ? origin : null);
    }

    /// <summary>The member a compiler-made name was made from, as its angle brackets hold it: "Run" of "&lt;Run&gt;d__3"
    /// or "&lt;Run&gt;b__0_0"; null for a name the compiler did not make, or one naming no member ("&lt;&gt;c").</summary>
    internal static string? OriginOf(string name)
    {
        int close = name.IndexOf('>', StringComparison.Ordinal);
        if (!name.StartsWith('<') || close < 1)
        {
            return null;
        }
        string inside = name[1..close];
        // A local function's state machine is named for the local function, inside its member's own brackets.
        return inside.Length == 0 ? null : inside.StartsWith('<') ? OriginOf(inside) : Accessor(inside) ?? inside;
    }

    /// <summary>A member as a person names it: a constructor by its type, a property's or an event's accessors by it.</summary>
    private static string Shown(string method, string type) => method switch
    {
        ".ctor" => type[(type.LastIndexOf('.') + 1)..],
        ".cctor" => $"static {type[(type.LastIndexOf('.') + 1)..]}",
        "Finalize" => $"~{type[(type.LastIndexOf('.') + 1)..]}",
        _ => Accessor(method) ?? method,
    };

    private static bool IsAccessor(string method) => Accessor(method) is not null;

    private static string? Accessor(string method)
    {
        foreach (string prefix in (string[])["get_", "set_", "add_", "remove_"])
        {
            if (method.StartsWith(prefix, StringComparison.Ordinal) && method.Length > prefix.Length)
            {
                return method[prefix.Length..];
            }
        }
        return null;
    }

    /// <summary>A generic type's name with its parameters' places, as C# writes it in typeof: "Box&lt;&gt;" of "Box`1".</summary>
    private static string Generic(string name) =>
        name.IndexOf('`', StringComparison.Ordinal) is int tick and > 0 && int.TryParse(name.AsSpan(tick + 1), out int count) && count > 0
            ? $"{name[..tick]}<{new string(',', count - 1)}>" : name;

    /// <summary>A report's signature with its types' namespaces left off: "(Int32, List`1&lt;String&gt;)".</summary>
    internal static string Signature(string signature)
    {
        StringBuilder shown = new(signature.Length);
        int word = -1;
        foreach (char c in signature)
        {
            if (c == '.' && word >= 0)
            {
                shown.Length = word;
                word = -1;
                continue;
            }
            if (Core.CSharpNames.Continues(c) || c == '`')
            {
                if (word < 0)
                {
                    word = shown.Length;
                }
            }
            else
            {
                word = -1;
            }
            shown.Append(c);
            if (c == ',')
            {
                shown.Append(' ');
            }
        }
        return shown.ToString();
    }

    /// <summary>A type as it is built: its classes' lines, its members, and the file and line it starts at.</summary>
    private sealed class TypeBuilt(string project, string ns, string name)
    {
        public string Project { get; } = project;

        public string Namespace { get; } = ns;

        public HashSet<(string, int)> Lines { get; } = [];

        public Dictionary<(string, string), MemberBuilt> Members { get; } = [];

        private string? path;
        private int first = int.MaxValue;

        public void Add(CoverageClass type, bool written, IEnumerable<(string Path, int Line)> lines)
        {
            foreach ((string Path, int Line) line in lines)
            {
                Lines.Add(line);
                if (written && type.Path is not null && (path is null || Core.PathNames.Same(path, type.Path)) && line.Line < first)
                {
                    path = type.Path;
                    first = line.Line;
                }
            }
        }

        public MemberBuilt Member(string shown, string signature, bool accessor)
        {
            (string, string) key = (shown, accessor ? string.Empty : signature);
            if (!Members.TryGetValue(key, out MemberBuilt? member))
            {
                Members[key] = member = new MemberBuilt(shown, accessor ? string.Empty : signature);
            }
            return member;
        }

        public CoverageNode Node(Func<IEnumerable<(string, int)>, CoverageCounts> count)
        {
            HashSet<string> overloaded = [.. Members.Values.GroupBy(m => m.Name).Where(g => g.Count() > 1).Select(g => g.Key)];
            List<CoverageNode> members = [.. Members.Values.Where(m => m.Lines.Count > 0).Select(m =>
            {
                string shown = overloaded.Contains(m.Name) && m.Signature.Length > 0 ? m.Name + CoverageTree.Signature(m.Signature) : m.Name;
                return new CoverageNode(CoverageNodeKind.Member, shown, $"{Project}.{Namespace}.{name}.{shown}")
                {
                    Counts = count(m.Lines),
                    Path = m.Path,
                    Line = m.First,
                };
            }).OrderBy(n => n.Line).ThenBy(n => n.Name, StringComparer.Ordinal)];
            return new CoverageNode(CoverageNodeKind.Type, name, $"{Project}.{Namespace}.{name}")
            {
                Counts = count(Lines),
                Path = path ?? members.FirstOrDefault(m => m.Path is not null)?.Path,
                Line = path is null ? members.FirstOrDefault(m => m.Path is not null)?.Line ?? 0 : first,
                Children = members,
            };
        }
    }

    /// <summary>A member as it is built: its lines, of one file, and the first of them.</summary>
    private sealed class MemberBuilt(string name, string signature)
    {
        public string Name { get; } = name;

        public string Signature { get; } = signature;

        public HashSet<(string, int)> Lines { get; } = [];

        public string? Path { get; private set; }

        public int First { get; private set; } = int.MaxValue;

        public void Add(string? path, IEnumerable<(string, int Line)> lines)
        {
            Path ??= path;
            foreach ((string, int Line) line in lines)
            {
                Lines.Add(line);
                First = Math.Min(First, line.Line);
            }
        }
    }
}
