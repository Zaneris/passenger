using System.Collections.Concurrent;
using System.Text.Json;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Lsp;

/// <summary>A change on disk as the protocol numbers it.</summary>
public enum FileChangeType
{
    Created = 1,
    Changed = 2,
    Deleted = 3,
}

/// <summary>A file or folder changed on disk, by its canonical path.</summary>
public readonly record struct FileEvent(string Path, FileChangeType Change);

/// <summary>One watcher a server registered: the paths under <see cref="Folder"/> whose names from it match
/// <see cref="Pattern"/> (with no folder, whole paths that match it), for the changes of <see cref="Kinds"/>. Its pattern is
/// read once (<see cref="Shape"/>), so a path is matched without a string made.</summary>
public sealed record FileWatch(string? Folder, string Pattern, WatchKinds Kinds)
{
    /// <summary>The pattern as read, of whichever pattern the watcher has.</summary>
    private readonly Shape shape = Shape.Of(Pattern);

    public string Pattern
    {
        get;
        init
        {
            field = value;
            shape = Shape.Of(value);
        }
    } = Pattern;

    /// <summary>The watcher as a log shows it.</summary>
    public string Shown => $"{Folder}{(Folder is null ? "" : " ")}{Pattern} ({Kinds})";

    /// <summary>Whether a file of this name may match the glob, by its last name alone and in any letter case: false only
    /// for a name it never matches, told without a string made.</summary>
    public bool MayMatch(ReadOnlySpan<char> name)
    {
        if (shape.Names.Length == 0)
        {
            return true;
        }
        foreach ((string text, bool whole) in shape.Names)
        {
            if (whole ? name.Equals(text, StringComparison.OrdinalIgnoreCase) : name.EndsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether the watcher takes every assembly under its folder however deep (<c>**/*.dll</c> alone): a folder of
    /// reference assemblies watched whole rather than file by file, as Roslyn's server watches the .NET installation's packs.</summary>
    public bool EveryAssembly => Folder is not null && Pattern == "**/*.dll";

    /// <summary>Whether the pattern reaches into the folders under <see cref="Folder"/>, where it can match a path of more
    /// than one name.</summary>
    public bool Recursive => shape.Recursive;

    /// <summary>Whether the watcher asks for this change of this path.</summary>
    public bool Covers(FileEvent change)
    {
        WatchKinds kind = change.Change switch
        {
            FileChangeType.Created => WatchKinds.Create,
            FileChangeType.Deleted => WatchKinds.Delete,
            _ => WatchKinds.Change,
        };
        return (Kinds & kind) != 0 && Matches(change.Path);
    }

    /// <summary>Whether the watcher's glob matches a path, whatever changed there, without a string made: a path outside its
    /// folder, or whose name its glob's last name cannot match (<see cref="MayMatch"/>), told at once, as a build's churn is.
    /// Where the path's folder takes names in any letter case, so does the glob.</summary>
    public bool Matches(string path)
    {
        if (Folder is not null && !PathNames.IsUnder(path, Folder) || !MayMatch(Path.GetFileName(path.AsSpan())))
        {
            return false;
        }
        // From the separator after the folder; with none, the whole path, which the glob reads as from the root.
        ReadOnlySpan<char> below = Folder is null ? path.AsSpan().TrimStart(PathNames.Separators) : path.AsSpan(Folder.AsSpan().TrimEnd(PathNames.Separators).Length);
        foreach (PathGlob choice in shape.Choices)
        {
            if (choice.Matches(below, rooted: true))
            {
                return true;
            }
        }
        foreach (PathGlob choice in shape.Lowered)
        {
            if (choice.Matches(below, rooted: true))
            {
                return PathNames.CaseIgnoredIn(Path.GetDirectoryName(path.AsSpan()) is { IsEmpty: false } folder ? folder : path);
            }
        }
        return false;
    }

    /// <summary>Whether a folder may hold a path the watcher matches, or lies on the way down to its folder: one under its
    /// folder no deeper than the glob's names reach (any depth past a <c>**</c>), or one over it.</summary>
    public bool Reaches(string folder)
    {
        if (Folder is null)
        {
            return Recursive;
        }
        if (PathNames.Within(Folder, folder))
        {
            return true;
        }
        if (!PathNames.IsUnder(folder, Folder))
        {
            return false;
        }
        if (shape.AnyDepth)
        {
            return true;
        }
        int names = 1;
        foreach (char at in PathNames.Below(folder, Folder))
        {
            names += PathNames.Separators.Contains(at) ? 1 : 0;
        }
        return names <= shape.Depth;
    }

    /// <summary>A watcher as the protocol gives it: its glob a string, or a pattern relative to a folder or workspace
    /// folder's URI; its kinds every change unless it names them. Null for one that cannot be read.</summary>
    internal static FileWatch? Read(JsonElement watcher)
    {
        if (JsonMembers.Value(watcher, "globPattern") is not { } glob)
        {
            return null;
        }
        WatchKinds kinds = JsonMembers.Number(watcher, "kind") is { } given ? (WatchKinds)given : WatchKinds.All;
        if (glob.ValueKind == JsonValueKind.String)
        {
            return new FileWatch(null, glob.GetString()!, kinds);
        }
        if (JsonMembers.Text(glob, "pattern") is not { } pattern || JsonMembers.Value(glob, "baseUri") is not { } place)
        {
            return null;
        }
        string? uri = place.ValueKind == JsonValueKind.String ? place.GetString() : JsonMembers.Text(place, "uri");
        return uri is not null && Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) && parsed.IsFile
            ? new FileWatch(DocumentUris.ToPath(uri), pattern, kinds)
            : null;
    }

    /// <summary>A pattern as read once, the same for every watcher of it: its choices' globs (a pattern whose whole is one
    /// brace of choices, <c>{**/*.cs,**/*.csproj}</c>, each matched as a pattern of its own, so one starting <c>**/</c>
    /// matches a file in the folder itself, as it would alone), as written and in lower case; what a file's name must end
    /// with, or be, to match; and how deep it reaches.</summary>
    private sealed class Shape
    {
        private static readonly ConcurrentDictionary<string, Shape> Read = new(StringComparer.Ordinal);

        private Shape(string pattern)
        {
            string[] choices = ChoicesOf(pattern);
            Choices = [.. choices.Select(choice => PathGlob.Of("/" + choice.TrimStart('/')))];
            Lowered = [.. choices.Select(choice => PathGlob.Of("/" + choice.TrimStart('/'), lowered: true))];
            Names = NamesOf(pattern);
            Recursive = pattern.Contains('/', StringComparison.Ordinal) || pattern.Contains("**", StringComparison.Ordinal);
            AnyDepth = pattern.Contains("**", StringComparison.Ordinal);
            Depth = pattern.Trim('/').Count('/');
        }

        public static Shape Of(string pattern) => Read.GetOrAdd(pattern, static p => new Shape(p));

        public PathGlob[] Choices { get; }

        public PathGlob[] Lowered { get; }

        /// <summary>What a file's name must end with, or be (<see cref="MayMatch"/>); none where any name may match.</summary>
        public (string Text, bool Whole)[] Names { get; }

        public bool Recursive { get; }

        /// <summary>Whether a <c>**</c> takes the pattern to any depth.</summary>
        public bool AnyDepth { get; }

        /// <summary>How many names below the folder the pattern's own '/' part.</summary>
        public int Depth { get; }

        /// <summary>What a pattern's last name asks a file's name to end with or be, each choice's where the whole pattern is
        /// one brace of them; none where it is not of those shapes.</summary>
        private static (string Text, bool Whole)[] NamesOf(string pattern)
        {
            string[] choices = ChoicesOf(pattern);
            if (choices.Length > 1)
            {
                (string Text, bool Whole)[][] each = [.. choices.Select(NamesOf)];
                return each.Any(names => names.Length == 0) ? [] : [.. each.SelectMany(names => names).Distinct()];
            }
            string last = pattern[(pattern.LastIndexOf('/') + 1)..];
            if (last.Length == 0 || last.IndexOfAny(['?', '[', ']', '!', '}']) >= 0 && !last.StartsWith("*.{", StringComparison.Ordinal))
            {
                return [];
            }
            if (last.StartsWith("*.{", StringComparison.Ordinal))
            {
                string inner = last[3..];
                return inner.EndsWith('}') && inner[..^1] is var alternatives && alternatives.IndexOfAny(['*', '?', '[', ']', '{', '}', '!', '/']) < 0
                    ? [.. alternatives.Split(',').Select(a => ("." + a, false))]
                    : [];
            }
            if (last[0] == '*')
            {
                string rest = last[1..];
                return rest.Length == 0 || rest.IndexOfAny(['*', '{']) >= 0 ? [] : [(rest, false)];
            }
            return last.IndexOfAny(['*', '{']) >= 0 ? [] : [(last, true)];
        }

        /// <summary>A pattern whose whole is one brace of choices as its choices; any other pattern as itself.</summary>
        private static string[] ChoicesOf(string pattern)
        {
            if (pattern.Length < 2 || pattern[0] != '{' || pattern[^1] != '}')
            {
                return [pattern];
            }
            List<string> choices = [];
            int depth = 0;
            int start = 1;
            for (int i = 1; i < pattern.Length - 1; i++)
            {
                switch (pattern[i])
                {
                    case '\\':
                        i++;
                        break;
                    case '{':
                        depth++;
                        break;
                    case '}' when depth == 0:
                        // The first brace closes before the end: the pattern is more than one brace.
                        return [pattern];
                    case '}':
                        depth--;
                        break;
                    case ',' when depth == 0:
                        choices.Add(pattern[start..i]);
                        start = i + 1;
                        break;
                }
            }
            choices.Add(pattern[start..^1]);
            return depth == 0 ? [.. choices] : [pattern];
        }
    }
}

/// <summary>The kinds of change a watcher asks for, as the protocol's flags.</summary>
[Flags]
public enum WatchKinds
{
    None = 0,
    Create = 1,
    Change = 2,
    Delete = 4,
    All = Create | Change | Delete,
}
