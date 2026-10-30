using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>Where Find in Files looks: the whole solution, one project, a directory, or a named scope (open files, the
/// files chosen in the Solution view).</summary>
public enum SearchScopeKind
{
    Solution,
    Project,
    Directory,
    Scope,
}

/// <summary>A search's scope as the popup has it: its kind, what each kind names (the project's file, the directory and
/// whether its subdirectories are searched, the named scope), and the file mask, null while it is off.</summary>
public sealed record SearchScope(SearchScopeKind Kind, string? Project, string? Directory, bool Recursive, string? Named, string? Mask)
{
    /// <summary>Why the scope cannot be searched as it stands, or null.</summary>
    public string? Problem() => Kind switch
    {
        SearchScopeKind.Project when string.IsNullOrEmpty(Project) => "No project to search",
        SearchScopeKind.Directory when string.IsNullOrWhiteSpace(Directory) || !System.IO.Directory.Exists(Directory) => "Directory is not found",
        SearchScopeKind.Scope when string.IsNullOrEmpty(Named) => "No scope to search",
        _ => Mask is null ? null : FileMask.Problem(Mask),
    };
}

/// <summary>A file mask: wildcards (<c>*</c>, <c>?</c>) separated by commas that a file's name must match, letter case
/// aside; one after a <c>!</c> leaves the files it matches out, and exclusions alone leave the rest in.</summary>
public sealed class FileMask
{
    private readonly Regex? included;
    private readonly Regex? excluded;

    private FileMask(Regex? included, Regex? excluded)
    {
        this.included = included;
        this.excluded = excluded;
    }

    /// <summary>Why a mask does not read, or null.</summary>
    public static string? Problem(string mask) =>
        mask.Trim().Length == 0 ? "Empty file mask"
        : mask.Contains(';', StringComparison.Ordinal) ? "File masks should be comma-separated"
        : null;

    /// <summary>The mask written, or null for one with nothing in it.</summary>
    public static FileMask? Parse(string? mask)
    {
        if (mask is null || Problem(mask) is not null)
        {
            return null;
        }
        List<string> included = [];
        List<string> excluded = [];
        foreach (string part in mask.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith('!'))
            {
                excluded.Add(Pattern(part[1..].Trim()));
            }
            else
            {
                included.Add(Pattern(part));
            }
        }
        return new FileMask(Compiled(included), Compiled(excluded));
    }

    /// <summary>Whether a file's name passes the mask.</summary>
    public bool Matches(string name) => (included?.IsMatch(name) ?? true) && !(excluded?.IsMatch(name) ?? false);

    private static Regex? Compiled(List<string> patterns) =>
        patterns.Count == 0 ? null : new Regex($"^(?:{string.Join('|', patterns)})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A wildcard as a pattern: every character itself but * and ?.</summary>
    private static string Pattern(string wildcard)
    {
        StringBuilder pattern = new();
        foreach (char c in wildcard)
        {
            pattern.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        }
        return pattern.ToString();
    }
}

/// <summary>The files a search reads, as the window resolves a scope: every file of the folder's index, or those under
/// any of the folders (only a folder's own files unless recursive) but those under a folder left out, then the files
/// named (a project's walked as the search reads them); each must pass the mask. A folder the index holds none of, outside
/// the open folder or in one it passes over (node_modules, bin), is walked for its files.</summary>
public sealed record SearchPlace(bool All, IReadOnlyList<string> Folders, bool Recursive, IReadOnlyList<string> LeftOut, IEnumerable<string> Files, FileMask? Mask)
{
    public static SearchPlace Everything(FileMask? mask) => new(true, [], true, [], [], mask);

    /// <summary>The paths to read, canonical and each once: the index's first, in its order. Each says whether a link
    /// listed inside the open folder (or inside a folder walked outside it) leads out of it, as a walk of the folders never
    /// goes: such a file is searched, but no Replace writes it. A worker's, as what is walked is read from the disk.</summary>
    internal IEnumerable<(string Path, bool Outside)> Paths(string root, IReadOnlyList<string> indexed)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        IEnumerable<(string Full, string Within)> inIndex = indexed.Select(relative => (Path.Combine(root, relative), root)).Where(file => All || Holds(file.Item1));
        IEnumerable<(string Full, string Within)> walked = Folders.Where(f => !PathNames.Within(f, root) || IgnoredFolders.AnyIn(root, PathNames.Below(f, root)))
            .SelectMany(folder => FileIndex.Walk(folder).Select(relative => (Path.Combine(folder, relative), PathNames.Within(folder, root) ? root : folder)).Where(file => Holds(file.Item1)));
        foreach ((string full, string within) in inIndex.Concat(walked).Where(file => Passes(file.Full)))
        {
            string path = PathNames.Canonical(full);
            if (seen.Add(path))
            {
                yield return (path, !PathNames.Within(path, within));
            }
        }
        foreach (string file in Files.Where(f => Passes(f) && seen.Add(f)))
        {
            yield return (file, PathNames.IsUnder(file, root) && !PathNames.IsUnder(PathNames.Canonical(file), root));
        }
    }

    private bool Holds(string path) =>
        Folders.Any(f => Recursive ? PathNames.IsUnder(path, f) : PathNames.Same(Path.GetDirectoryName(path) ?? string.Empty, f))
        && !LeftOut.Any(f => PathNames.IsUnder(path, f));

    private bool Passes(string path) => Mask?.Matches(Path.GetFileName(path)) ?? true;
}

/// <summary>What Find in Files keeps for every folder, in the store's find.json: the file mask (null while it is off),
/// the masks used lately, the latest first, and whether a directory is searched with its subdirectories.</summary>
public sealed record FindSettingsState : IStateDocument<FindSettingsState>
{
    /// <summary>The most masks kept.</summary>
    public const int RecentLimit = 15;

    public string? FileMask { get; init; }

    public List<string> RecentFileMasks { get; init; } = [];

    public bool Recursive { get; init; } = true;

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    /// <summary>A blank mask is none, and a recent one blank or twice is left out.</summary>
    public FindSettingsState Clamped() => this with
    {
        FileMask = string.IsNullOrWhiteSpace(FileMask) ? null : FileMask,
        RecentFileMasks = [.. (RecentFileMasks ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.Ordinal).Take(RecentLimit)],
    };
}

/// <summary>What Find in Files keeps for a folder: the scope chosen, the project, directory and named scope the popup
/// showed, and the directories searched lately, the latest first.</summary>
public sealed record FindScopeState : IStateDocument<FindScopeState>
{
    public const int RecentLimit = 15;

    public SearchScopeKind Scope { get; init; }

    public string? Project { get; init; }

    public string? Directory { get; init; }

    public string? Named { get; init; }

    public List<string> RecentDirectories { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    /// <summary>A scope this build does not have is the solution, and a recent directory blank or twice is left out.</summary>
    public FindScopeState Clamped() => this with
    {
        Scope = Enum.IsDefined(Scope) ? Scope : SearchScopeKind.Solution,
        RecentDirectories = [.. (RecentDirectories ?? []).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.Ordinal).Take(RecentLimit)],
    };
}
