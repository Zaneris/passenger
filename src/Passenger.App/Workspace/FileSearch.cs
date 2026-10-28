using System.Text;
using Microsoft.Win32.SafeHandles;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Workspace;

/// <summary>A match: the file, where it is (line and column from zero, its length), and its line's text.</summary>
public sealed record SearchMatch(string Path, int Line, int Column, int Length, string LineText)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>What Find in Files found: the matches, and the files a regex ran too long in, whose search stopped there (the
/// matches found before the stop listed).</summary>
public sealed record FilesFound(IReadOnlyList<SearchMatch> Matches, IReadOnlyList<string> Stopped)
{
    public static readonly FilesFound None = new([], []);

    /// <summary>The matches Replace in Files replaces: those in the files whose search ended.</summary>
    public IReadOnlyList<SearchMatch> Replaceable => Stopped.Count == 0 ? Matches : [.. Matches.Where(m => !Stopped.Contains(m.Path))];

    /// <summary>Where the search stopped, each file named as <paramref name="name"/> gives it; null when it stopped nowhere.</summary>
    public string? StoppedIn(Func<string, string> name) =>
        Stopped.Count == 0 ? null : $"the regex took too long and stopped in {string.Join(", ", Stopped.Select(name))}";
}

/// <summary>What Replace in Files did beyond the matches: the files written as UTF-8 since their encoding could not hold
/// the replacement, and those left as they were since the system refused the write (read-only, access denied, held),
/// each with why.</summary>
public sealed record FilesReplaced(IReadOnlyList<string> Converted, IReadOnlyList<(string Path, string Why)> Refused)
{
    /// <summary>The matches in files that were replaced, the refused files' left out.</summary>
    public IReadOnlyList<SearchMatch> Replaced(IEnumerable<SearchMatch> matches) => [.. matches.Where(m => !Refused.Any(r => r.Path == m.Path))];

    /// <summary>How many matches in how many files, as the window and an agent are told.</summary>
    public static string Count(IReadOnlyCollection<SearchMatch> matches)
    {
        int files = matches.Select(m => m.Path).Distinct().Count();
        return $"{Plural.Of(matches.Count, "match", "matches")} in {Plural.Of(files, "file")}";
    }

    /// <summary>The refused files, each named as <paramref name="name"/> gives it with why; null when none was.</summary>
    public string? NotReplaced(Func<string, string> name) =>
        Refused.Count == 0 ? null : $"not replaced: {string.Join(", ", Refused.Select(r => $"{name(r.Path)} ({r.Why})"))}";
}

/// <summary>Find in Files over the open folder's files, matching as the editor's Find bar does (the same query and
/// options): every text file the index has (build output and version control left out), or those a scope and a file
/// mask leave, an open file as the editor has it, unsaved changes and all; binary and very large files passed over.
/// Replace All changes an open file as an edit (so it undoes) and writes a closed one in its own encoding.</summary>
public static class FileSearch
{
    /// <summary>The most matches listed.</summary>
    public const int MostMatches = TextSearch.MaxMatches;

    private const long LargestFile = 4 * 1024 * 1024;

    /// <summary>What is wrong with a query (a regex that does not read), or null.</summary>
    public static string? Problem(SearchQuery query)
    {
        if (query.IsEmpty)
        {
            return null;
        }
        try
        {
            _ = SearchPattern.For(query, TextSearch.Apart);
            return null;
        }
        catch (ArgumentException error)
        {
            return error.Message;
        }
    }

    /// <summary>The matches in the files of a place (every file the index has, or those a scope and a mask leave), file
    /// by file in the index's order, at most <see cref="MostMatches"/>; a regex has <see cref="TextSearch.Apart"/> in each,
    /// and the files it ran too long in are named.</summary>
    public static Task<FilesFound> FindAsync(string root, IReadOnlyList<string> files, SearchPlace place, SearchQuery query,
        Editors.OpenTexts open, CancellationToken cancellation) => BackgroundWork.Run(() =>
    {
        List<SearchMatch> found = [];
        List<string> stopped = [];
        if (query.IsEmpty || Problem(query) is not null)
        {
            return FilesFound.None;
        }
        SearchPattern pattern = SearchPattern.For(query, TextSearch.Apart);
        MatchFilter? filter = MatchFilter.For(query);
        byte[] bytes = [];
        foreach (string path in place.Paths(root, files))
        {
            cancellation.ThrowIfCancellationRequested();
            // A preview's or a workbook's tab holds no text, so its file is searched as a closed one is, and passed over as binary.
            TextSnapshot? shown = open.Open(path);
            if (shown is null && filter is not null && !(ReadBytes(path, ref bytes) is int length && filter.MayMatch(bytes.AsSpan(0, length))))
            {
                // A closed file whose bytes hold no match is never read as text.
                continue;
            }
            SearchResult searched;
            if (shown is not null)
            {
                searched = TextSearch.FindAll(shown, pattern, MostMatches - found.Count);
                foreach (TextRange match in searched.Matches)
                {
                    TextPosition at = shown.GetPosition(match.Start);
                    string line = shown.GetLineText(at.Line);
                    found.Add(new SearchMatch(path, at.Line, at.Column, Math.Min(match.Length, Math.Max(0, line.Length - at.Column)), line));
                }
            }
            else if (Read(path) is { } text)
            {
                searched = TextSearch.FindAll(text, pattern, MostMatches - found.Count);
                AddMatches(found, path, text, searched.Matches);
            }
            else
            {
                continue;
            }
            if (searched.Stopped)
            {
                stopped.Add(path);
            }
            if (found.Count >= MostMatches)
            {
                break;
            }
        }
        return new FilesFound(found, stopped);
    }, cancellation);

    /// <summary>A closed file's text as the editor reads it (<see cref="Editors.EditorArea.ReadText"/>); null for one that
    /// is binary (<see cref="FileText.LooksBinary"/>), too large or unreadable.</summary>
    private static string? Read(string path) => new FileInfo(path) is { Exists: true, Length: <= LargestFile } ? Editors.EditorArea.ReadText(path) : null;

    /// <summary>Adds the matches in a closed file's text, each on its line as the editor's lines are read (to a '\n', a
    /// '\r' before it the break's), its column at most the line's length; the lines are walked once, in the matches' order.</summary>
    private static void AddMatches(List<SearchMatch> found, string path, string text, IReadOnlyList<TextRange> matches)
    {
        int line = 0;
        int start = 0;
        int next = text.IndexOf('\n', StringComparison.Ordinal) + 1;
        string? shown = null;
        foreach (TextRange match in matches)
        {
            while (next > 0 && next <= match.Start)
            {
                line++;
                start = next;
                next = text.IndexOf('\n', start) + 1;
                shown = null;
            }
            int end = next == 0 ? text.Length : next - 1 > start && text[next - 2] == '\r' ? next - 2 : next - 1;
            shown ??= text[start..end];
            int column = Math.Min(match.Start - start, end - start);
            found.Add(new SearchMatch(path, line, column, Math.Min(match.Length, Math.Max(0, shown.Length - column)), shown));
        }
    }

    /// <summary>A closed file's bytes read into the search's buffer, grown as one needs, sharing writes and deletes as
    /// <see cref="FileText.Read"/> does: how many there are, or null for a file not there, too large or unreadable.</summary>
    private static int? ReadBytes(string path, ref byte[] buffer)
    {
        try
        {
            using SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long size = RandomAccess.GetLength(file);
            if (size > LargestFile)
            {
                return null;
            }
            if (buffer.Length < size)
            {
                buffer = new byte[Math.Max(size, Math.Min(LargestFile, buffer.Length * 2L))];
            }
            int read = 0;
            while (read < size)
            {
                int got = RandomAccess.Read(file, buffer.AsSpan(read, (int)size - read), read);
                if (got == 0)
                {
                    break;
                }
                read += got;
            }
            return read;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What replacing a query's matches in one file comes to, worked out off the UI thread: an open file's edits
    /// of the text searched, or a closed file as read and its text replaced; or why it is left as it was.</summary>
    public sealed record Replacing(string Path, TextSnapshot? Searched, IReadOnlyList<TextEdit> Edits, FileText? Closed, string? After, string? Why);

    /// <summary>Replaces every match in a file, an open one's text as the editor has it (<paramref name="open"/>), else
    /// its own read from disk, a regex's groups ($1, ${name}) filled in from each match as the Find bar's Replace fills
    /// them; a file whose search a regex ran too long in is left, never replaced in part, as is one whose replaced text
    /// would be too long.</summary>
    public static Replacing Plan(string path, TextSnapshot? open, SearchPattern pattern, string replacement)
    {
        if (open is not null)
        {
            SearchResult found = TextSearch.FindAll(open, pattern, int.MaxValue);
            string? why = Unreplaced(found, replacement);
            return new Replacing(path, open, why is null ? found.Replacements(replacement) : [], null, null, why);
        }
        try
        {
            if (FileText.Load(path) is not { } file)
            {
                return new Replacing(path, null, [], null, null, null);
            }
            SearchResult found = TextSearch.FindAll(file.Text, pattern, int.MaxValue);
            return Unreplaced(found, replacement) is { } why ? new Replacing(path, null, [], null, null, why)
                : new Replacing(path, null, [], file, Applied(file.Text, found.Replacements(replacement)), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new Replacing(path, null, [], null, null, Refusal(error));
        }
    }

    /// <summary>Why a file's matches are left as they were: a regex ran too long there, or replacing them would make its
    /// text too long (<see cref="Refusals.TooLongReplaced"/>); null when they are replaced.</summary>
    private static string? Unreplaced(SearchResult found, string replacement) =>
        found.Stopped ? StoppedWhy : Refusals.TooLongReplaced(found, replacement);

    /// <summary>Why a file the system would not let be read or written is left as it was.</summary>
    public static string Refusal(Exception error) => error switch
    {
        ReadOnlyFileException => ReadOnlyWhy,
        UnauthorizedAccessException => "access denied",
        FileNotFoundException or DirectoryNotFoundException => "gone",
        _ => error.Message,
    };

    /// <summary>Why a read-only file is left as it was: one marked so on disk, or one the editor shows read-only.</summary>
    public const string ReadOnlyWhy = "read-only";

    /// <summary>Why a file a regex ran too long in is left as it was.</summary>
    public const string StoppedWhy = "the regex took too long there";

    private static string Applied(string text, IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0)
        {
            return text;
        }
        StringBuilder replaced = new(text.Length);
        int at = 0;
        foreach (TextEdit edit in edits)
        {
            replaced.Append(text, at, edit.Start - at);
            replaced.Append(edit.Text);
            at = edit.End;
        }
        replaced.Append(text, at, text.Length - at);
        return replaced.ToString();
    }
}
