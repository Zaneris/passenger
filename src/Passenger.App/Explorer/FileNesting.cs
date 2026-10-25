using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Explorer;

/// <summary>File nesting in the Solution view: a file shown under the one it belongs to, one level deep, by its
/// project's DependentUpon and, while "Show files with the same names as nested" is on, by name, a suffix on a parent's
/// name and another on its dependent's after the same start (Counter.razor, Counter.razor.cs). A move or rename of the
/// parent takes its dependents along, renamed as it is.</summary>
public static partial class FileNesting
{
    /// <summary>The languages the CLR rules are made for.</summary>
    private static readonly string[] Languages = [".cs", ".vb"];

    /// <summary>The default rules, (parent suffix, child suffix), Rider 2026.2's: its own, Razor's and Web Forms',
    /// JavaScript's and TypeScript's, Less's and Sass's; shown by File Nesting.</summary>
    public static IReadOnlyList<(string Parent, string Child)> Defaults { get; } = [.. RiderRules().Distinct()];

    /// <summary>The rules matched: the defaults, each combined with those that go on from its child or lead to its
    /// parent (.cs to .resx and .resx to .designer.cs make .cs to .designer.cs).</summary>
    private static readonly IReadOnlyList<(string Parent, string Child)> Combined = Combine(Defaults);

    private static IEnumerable<(string Parent, string Child)> RiderRules()
    {
        foreach (string code in Languages)
        {
            yield return (code, ".designer" + code);
            yield return (code, ".resx");
            yield return (code, ".context" + code);
            yield return (".xaml", ".xaml" + code);
            yield return (".resx", ".designer" + code);
            yield return (".settings", ".designer" + code);
            yield return (".tt", ".generated" + code);
            yield return (".edmx", ".designer" + code);
            yield return (".xsd", ".designer" + code);
            yield return (".cshtml", ".cshtml" + code);
            yield return (".razor", ".razor" + code);
            foreach (string page in (string[])[".aspx", ".asax", ".ascx", ".master"])
            {
                yield return (page, page + code);
                yield return (page, page + ".designer" + code);
            }
        }
        foreach (string markup in (string[])[".razor", ".cshtml"])
        {
            foreach (string asset in (string[])[".css", ".scss", ".sass", ".js"])
            {
                yield return (markup, markup + asset);
            }
        }
        foreach (string child in (string[])[".min.js", ".min.js.map", ".map", ".js.map", "-compiled.js", "-compiled.js.map"])
        {
            yield return (".js", child);
        }
        yield return (".cjs", ".cjs.map");
        yield return (".mjs", ".mjs.map");
        yield return (".jsx", ".js");
        yield return (".jsx", ".js.map");
        yield return (".es6", ".js");
        yield return (".es6", ".js.map");
        foreach ((string source, string[] outputs) in (IEnumerable<(string, string[])>)[(".ts", [".js", ".mjs", ".cjs"]), (".mts", [".js", ".mjs"]), (".cts", [".js", ".cjs"]), (".tsx", [".js", ".jsx"])])
        {
            foreach (string output in outputs)
            {
                yield return (source, output);
                yield return (source, output + ".map");
            }
        }
        foreach (string style in (string[])[".less", ".sass", ".scss"])
        {
            yield return (style, ".css");
            yield return (style, ".css.map");
        }
    }

    private static List<(string Parent, string Child)> Combine(IReadOnlyList<(string Parent, string Child)> rules)
    {
        List<(string Parent, string Child)> all = [.. rules];
        HashSet<(string, string)> known = [.. all.Select(r => (r.Parent.ToLowerInvariant(), r.Child.ToLowerInvariant()))];
        // A rule found by combining is combined in turn, until no new one comes.
        for (int at = 0; at < all.Count; at++)
        {
            (string parent, string child) = all[at];
            foreach ((string p, string c) in all.ToList())
            {
                if (string.Equals(p, child, StringComparison.OrdinalIgnoreCase) && known.Add((parent.ToLowerInvariant(), c.ToLowerInvariant())))
                {
                    all.Add((parent, c));
                }
                if (string.Equals(c, parent, StringComparison.OrdinalIgnoreCase) && known.Add((p.ToLowerInvariant(), child.ToLowerInvariant())))
                {
                    all.Add((p, child));
                }
            }
        }
        return all;
    }

    /// <summary>ASP.NET Core's settings for an environment (appsettings.Development.json) go under its settings for all
    /// (appsettings.json), which no suffix rule says.</summary>
    public const string EnvironmentParent = "appsettings.json";

    public const string EnvironmentChildren = "appsettings.*.json";

    [GeneratedRegex(@"^appsettings\.[^.]+\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex EnvironmentSettings();

    /// <summary>The files of one folder shown under another of them, each to the file it goes under, by path: what the
    /// project says it depends on first, then the name rules when <paramref name="byName"/>. Nesting is one level: a
    /// file whose parent is nested itself goes under that one's parent.</summary>
    public static IReadOnlyDictionary<string, string> Parents(IReadOnlyCollection<string> files, ProjectModel? project, bool byName)
    {
        Dictionary<string, string> named = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            named.TryAdd(Path.GetFileName(file), file);
        }
        Dictionary<string, string> dependentUpon = project?.DependentUpon() ?? [];
        Dictionary<string, List<string>> candidates = new(StringComparer.Ordinal);
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            List<string> found = [];
            if (dependentUpon.TryGetValue(file, out string? declared) && named.TryGetValue(Path.GetFileName(declared), out string? owner)
                && PathNames.Same(owner, declared) && owner != file)
            {
                found.Add(owner);
            }
            if (byName)
            {
                foreach ((string parent, string child) in Combined)
                {
                    if (name.Length > child.Length && name.EndsWith(child, StringComparison.OrdinalIgnoreCase)
                        && named.TryGetValue(name[..^child.Length] + parent, out string? match) && match != file && !found.Contains(match))
                    {
                        found.Add(match);
                    }
                }
                if (EnvironmentSettings().IsMatch(name) && named.TryGetValue(EnvironmentParent, out string? settings) && !found.Contains(settings))
                {
                    found.Add(settings);
                }
            }
            if (found.Count > 0)
            {
                candidates[file] = found;
            }
        }
        Dictionary<string, string> parents = new(StringComparer.Ordinal);
        foreach ((string child, List<string> found) in candidates)
        {
            if (Top(child, found, candidates) is { } top)
            {
                parents[child] = top;
            }
        }
        return parents;
    }

    /// <summary>The file a nested one goes under: of those it could, the first nested in none, else the one the first
    /// goes under; none when they go round in a circle.</summary>
    private static string? Top(string child, List<string> found, Dictionary<string, List<string>> candidates)
    {
        if (found.FirstOrDefault(p => !candidates.ContainsKey(p)) is { } top)
        {
            return top;
        }
        HashSet<string> seen = [child];
        string at = found[0];
        while (candidates.TryGetValue(at, out List<string>? above))
        {
            if (!seen.Add(at))
            {
                return null;
            }
            at = above.FirstOrDefault(p => !candidates.ContainsKey(p)) ?? above[0];
        }
        return at == child ? null : at;
    }

    /// <summary>The files shown under one, where it is on disk now, by name.</summary>
    public static IReadOnlyList<string> DependentsOf(string path, ProjectModel? project, bool byName) =>
        DependentsOf([(path, project)], byName).GetValueOrDefault(path) ?? [];

    /// <summary>The files shown under each of several (each with its project), by the path given: each folder they are in
    /// listed and nested once, so files chosen together in a folder of thousands cost the folder once, not once each.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> DependentsOf(IReadOnlyCollection<(string Path, ProjectModel? Project)> files, bool byName)
    {
        Dictionary<string, IReadOnlyList<string>> found = new(StringComparer.Ordinal);
        foreach (IGrouping<(string Folder, ProjectModel? Project), (string Path, ProjectModel? Project)> together in files
            .Where(f => Path.GetDirectoryName(f.Path) is not null && File.Exists(f.Path))
            .GroupBy(f => (Path.GetDirectoryName(f.Path)!, f.Project)))
        {
            List<string> listed;
            try
            {
                listed = [.. Directory.EnumerateFiles(together.Key.Folder).Select(PathNames.Canonical)];
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            ILookup<string, string> under = Parents(listed, together.Key.Project, byName).ToLookup(p => p.Value, p => p.Key, StringComparer.Ordinal);
            foreach ((string path, _) in together)
            {
                found[path] = [.. under[PathNames.Canonical(path)].Order(StringComparer.OrdinalIgnoreCase)];
            }
        }
        return found;
    }

    /// <summary>A dependent's name once its parent is renamed: the part named after the parent (its name without its
    /// extension) takes the new one's, Counter.razor.cs going with Counter.razor to Clicker.razor.cs; a name that does not
    /// begin with it is kept.</summary>
    public static string Renamed(string dependent, string parentName, string newParentName)
    {
        string name = Path.GetFileName(dependent);
        string stem = Path.GetFileNameWithoutExtension(parentName);
        return stem.Length > 0 && name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(newParentName) + name[stem.Length..]
            : name;
    }
}
