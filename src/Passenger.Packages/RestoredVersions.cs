using System.Text.Json;
using Passenger.Core;

namespace Passenger.Packages;

/// <summary>A package a project gets through another package or a project it references, never naming it itself: the
/// version its restore resolved for a framework (null for a project of one), and what brings it in, each package the
/// project names and each project it references whose dependencies reach it.</summary>
public sealed record TransitiveUse(string? Framework, string Version, IReadOnlyList<string> BroughtBy);

/// <summary>The package versions a project's restore resolved, as its project.assets.json says them: for each target
/// framework, by the name the project gives it (its alias in TargetFrameworks), each package's version, and what each
/// package and project there depends on, so what the project gets only through another is known.</summary>
public sealed class RestoredVersions
{
    private readonly Dictionary<string, Framework> byFramework;

    private RestoredVersions(Dictionary<string, Framework> byFramework) => this.byFramework = byFramework;

    public static RestoredVersions None { get; } = new(new Dictionary<string, Framework>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The version of a package a framework's restore resolved (a project of one framework names none); null where
    /// it resolved none.</summary>
    public string? Of(string? framework, string id) => Named(framework)?.Packages.GetValueOrDefault(id);

    /// <summary>Whether any framework's restore resolved a package, however the project came by it.</summary>
    public bool Holds(string id) => byFramework.Values.Any(f => f.Packages.ContainsKey(id));

    /// <summary>The packages a framework's restore resolved, with their versions (a project of one framework names none).</summary>
    public IReadOnlyDictionary<string, string> All(string? framework) => Named(framework)?.Packages ?? new Dictionary<string, string>();

    /// <summary>The packages each framework's restore resolved that the project names in none of its references, by
    /// package, each framework's use (its name null where the project has one), what brings each in sorted.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<TransitiveUse>> Transitive()
    {
        Dictionary<string, List<TransitiveUse>> found = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, Framework framework) in byFramework)
        {
            foreach ((string id, SortedSet<string> by) in framework.Reached())
            {
                (found.TryGetValue(id, out List<TransitiveUse>? uses) ? uses : found[id] = []).Add(
                    new TransitiveUse(byFramework.Count == 1 ? null : name, framework.Packages[id], [.. by]));
            }
        }
        return found.ToDictionary(p => p.Key, p => (IReadOnlyList<TransitiveUse>)p.Value, StringComparer.OrdinalIgnoreCase);
    }

    private Framework? Named(string? framework) =>
        framework is not null ? byFramework.GetValueOrDefault(framework) : byFramework.Count == 1 ? byFramework.Values.First() : null;

    /// <summary>An assets file's targets read (a runtime's own targets passed over); none where it does not read as one.</summary>
    public static RestoredVersions Read(Stream assets)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(assets);
            JsonElement root = read.RootElement;
            if (JsonMembers.Object(root, "targets") is not { } targets)
            {
                return None;
            }
            // A target is named as its framework's key under project.frameworks: its targetAlias, and the packages named there.
            Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> named = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty framework in JsonMembers.Properties(JsonMembers.Object(root, "project"), "frameworks"))
            {
                aliases[framework.Name] = JsonMembers.Text(framework.Value, "targetAlias") is { Length: > 0 } alias ? alias : framework.Name;
                named[framework.Name] = new HashSet<string>(JsonMembers.Properties(framework.Value, "dependencies").Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
            }
            Dictionary<string, Framework> found = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty target in targets.EnumerateObject().Where(t => !t.Name.Contains('/', StringComparison.Ordinal) && t.Value.ValueKind == JsonValueKind.Object))
            {
                Framework framework = new(named.GetValueOrDefault(target.Name) ?? []);
                foreach (JsonProperty library in target.Value.EnumerateObject())
                {
                    if (library.Name.Split('/', 2) is [{ Length: > 0 } id, { Length: > 0 } version]
                        && JsonMembers.Value(library.Value, "type") is { ValueKind: JsonValueKind.String } type)
                    {
                        string[] dependencies = [.. JsonMembers.Properties(library.Value, "dependencies").Select(d => d.Name)];
                        if (type.ValueEquals("package"))
                        {
                            framework.Packages[id] = version;
                            framework.Dependencies[id] = dependencies;
                        }
                        else if (type.ValueEquals("project"))
                        {
                            framework.Projects[id] = dependencies;
                        }
                    }
                }
                found[aliases.GetValueOrDefault(target.Name) ?? target.Name] = framework;
            }
            return new RestoredVersions(found);
        }
        catch (JsonException)
        {
            // A restore being written, or a file that is no assets file: nothing said.
            return None;
        }
    }

    /// <summary>One framework's restore: its packages' versions, what each package and each referenced project depends on,
    /// and the packages the project names.</summary>
    private sealed class Framework(HashSet<string> named)
    {
        public Dictionary<string, string> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string[]> Dependencies { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string[]> Projects { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Each package the project does not name, with what brings it in: a package it names, or a project it
        /// references (one no other referenced project depends on), whose dependencies reach it.</summary>
        public Dictionary<string, SortedSet<string>> Reached()
        {
            Dictionary<string, SortedSet<string>> reached = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> under = new(Projects.Values.SelectMany(d => d), StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> roots = Packages.Keys.Where(named.Contains).Concat(Projects.Keys.Where(p => !under.Contains(p)));
            foreach (string root in roots)
            {
                HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase) { root };
                Stack<string> next = new(Of(root));
                while (next.TryPop(out string? id))
                {
                    if (!seen.Add(id))
                    {
                        continue;
                    }
                    if (Packages.ContainsKey(id) && !named.Contains(id))
                    {
                        (reached.TryGetValue(id, out SortedSet<string>? by) ? by : reached[id] = new SortedSet<string>(StringComparer.OrdinalIgnoreCase)).Add(root);
                    }
                    foreach (string dependency in Of(id))
                    {
                        next.Push(dependency);
                    }
                }
            }
            return reached;
        }

        private string[] Of(string id) => Dependencies.GetValueOrDefault(id) ?? Projects.GetValueOrDefault(id) ?? [];
    }
}
