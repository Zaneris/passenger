using Passenger.Core;

namespace Passenger.Lsp;

/// <summary>A document's URI as the protocol has it and its path as the IDE has it, one from the other. The path is
/// always the canonical one (<see cref="PathNames.Canonical"/>), whichever spelling the server used, so a location it
/// gives finds the open file it names, and two URIs of one file are one document however each spells it.</summary>
public static class DocumentUris
{
    public static string FromPath(string path) => new Uri(PathNames.Canonical(path)).AbsoluteUri;

    public static string ToPath(string uri) => PathNames.Canonical(LocalPath(uri));

    /// <summary>The path a file is to take, as a rename's newUri names it: its folder's canonical path, and its own name as
    /// the URI spells it, where <see cref="ToPath"/> gives the name of the file there as the disk spells it, so a new name
    /// differing only in letter case (myClass.cs to MyClass.cs, one file on macOS and Windows) is kept.</summary>
    public static string ToNewPath(string uri)
    {
        string local = Path.TrimEndingDirectorySeparator(Path.GetFullPath(LocalPath(uri)));
        return Path.GetDirectoryName(local) is { } folder ? Path.Join(PathNames.Canonical(folder), Path.GetFileName(local)) : PathNames.Canonical(local);
    }

    /// <summary>A file URI's local path as given.</summary>
    private static string LocalPath(string uri)
    {
        string local = new Uri(uri).LocalPath;
        // A Windows drive as vscode-uri writes it, file:///c%3A/..., which System.Uri leaves as /c:/...
        if (OperatingSystem.IsWindows() && local.Length >= 3 && local[0] is '/' or '\\' && char.IsAsciiLetter(local[1]) && local[2] == ':')
        {
            local = local[1..];
        }
        return local;
    }

    /// <summary>Whether two URIs name one document: the same text, or two files whose paths (<see cref="ToPath"/>) name one
    /// place as the file system compares names, however each spells it.</summary>
    public static bool Same(string uri, string other) =>
        uri == other || Uri.TryCreate(uri, UriKind.Absolute, out Uri? one) && one.IsFile && Uri.TryCreate(other, UriKind.Absolute, out Uri? two) && two.IsFile
            && PathNames.Same(ToPath(uri), ToPath(other));

    /// <summary>The locations of <paramref name="own"/> that none of <paramref name="served"/> names already: one starting at
    /// the same place of the same document (<see cref="Same"/>), as a server's answer and Passenger's own finding may both
    /// hold a usage. Each kept in its order.</summary>
    public static IReadOnlyList<LspLocation> Besides(IEnumerable<LspLocation> own, IEnumerable<LspLocation> served)
    {
        ILookup<LspPosition, string> at = served.ToLookup(s => s.Range.Start, s => s.Uri);
        return [.. own.Where(o => !at[o.Range.Start].Any(uri => Same(uri, o.Uri)))];
    }

    /// <summary>The file name a source generator gave a document Roslyn's server names by a URI of its own (no file on
    /// disk, its hint name in the query), else null.</summary>
    public static string? GeneratedName(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) || parsed.Scheme != "roslyn-source-generated")
        {
            return null;
        }
        string? hint = parsed.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0] == "hintName")?[1];
        return Uri.UnescapeDataString(hint ?? parsed.Segments[^1]);
    }

    /// <summary>Each URI's path, made once however many places name it: making a path canonical asks the disk.</summary>
    public static Dictionary<string, string> ToPaths(IEnumerable<string> uris)
    {
        Dictionary<string, string> paths = new(StringComparer.Ordinal);
        foreach (string uri in uris)
        {
            if (!paths.ContainsKey(uri))
            {
                paths[uri] = ToPath(uri);
            }
        }
        return paths;
    }
}
