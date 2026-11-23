using System.Text;
using Passenger.Core;

namespace Passenger.Vcs;

/// <summary>What git says a line of an ignore file does in the working tree: the paths under the file's folder it decides
/// (ignores, or for a <c>!</c> line keeps), by their paths from that folder, a directory's ending in '/'; the tracked files
/// it would ignore, which git never does; and the later <c>!</c> line that takes back what it ignores, where one does.</summary>
public sealed record IgnoreLineAnswer(IReadOnlyList<string> Decides, IReadOnlyList<string> Tracked, int? UndoneBy);

/// <summary>What git says of an ignore file's lines (<see cref="Lines"/>, by line number from 1; a line deciding nothing,
/// tracked or not, has none), and the untracked paths under its folder it does not ignore, by their paths from it.</summary>
public sealed record IgnoreReading(IReadOnlyDictionary<int, IgnoreLineAnswer> Lines, IReadOnlyList<string> Untracked)
{
    public IgnoreLineAnswer? Of(int line) => Lines.GetValueOrDefault(line);
}

/// <summary>The ignore file, its line and pattern that decide whether git ignores a path: <see cref="Ignored"/> false for a
/// <c>!</c> line keeping it, or for a tracked file, which git never ignores.</summary>
public sealed record IgnoringLine(string File, int Line, string Pattern, bool Ignored, bool Tracked);

public sealed partial class Git
{
    /// <summary>The name of a folder's ignore file.</summary>
    public const string IgnoreFileName = ".gitignore";

    /// <summary>What git says each line of an ignore file in the tree does, as the file stands on disk: every path under its
    /// folder git lists as ignored, untracked or tracked yet matched, each asked of <c>git check-ignore -v</c>, which names
    /// the line deciding it; a line deciding nothing whose files a later <c>!</c> line keeps is found by asking the same
    /// of the file's lines before that one alone, in a tree of their own. Null where git could not say.</summary>
    public async Task<IgnoreReading?> IgnoreReadingAsync(string ignoreFile)
    {
        string folder = Path.GetDirectoryName(ignoreFile)!;
        string source = Relative(ignoreFile);
        GitResult ignored = await RunAsync(folder, ["ls-files", "-z", "--others", "--ignored", "--exclude-standard", "--directory"]);
        GitResult untracked = await RunAsync(folder, ["ls-files", "-z", "--others", "--exclude-standard", "--directory"]);
        GitResult tracked = await RunAsync(folder, ["ls-files", "-z", "--cached", "--ignored", "--exclude-standard"]);
        if (!ignored.Succeeded || !untracked.Succeeded || !tracked.Succeeded)
        {
            return null;
        }
        string[] ignoredPaths = Split(ignored.Output);
        string[] untrackedPaths = Split(untracked.Output);
        HashSet<string> trackedPaths = new(Split(tracked.Output), StringComparer.Ordinal);
        if (await DecidingAsync(folder, [.. ignoredPaths, .. untrackedPaths, .. trackedPaths], null) is not { } decided)
        {
            return null;
        }
        Dictionary<int, (List<string> Decides, List<string> Tracked)> lines = [];
        Dictionary<int, string> patterns = [];
        foreach ((string file, int line, string pattern, string path) in decided.Where(d => d.Source == source))
        {
            if (!lines.TryGetValue(line, out (List<string> Decides, List<string> Tracked) found))
            {
                found = ([], []);
                lines[line] = found;
                patterns[line] = pattern;
            }
            (trackedPaths.Contains(path) ? found.Tracked : found.Decides).Add(path);
        }
        Dictionary<int, int> undone = [];
        foreach ((int keeper, (List<string> kept, _)) in lines.Where(l => patterns[l.Key].StartsWith('!') && l.Value.Decides.Count > 0).OrderBy(l => l.Key))
        {
            foreach (int line in await UndoneAsync(ignoreFile, source, keeper, kept))
            {
                undone.TryAdd(line, keeper);
            }
        }
        Dictionary<int, IgnoreLineAnswer> answers = [];
        foreach (int line in lines.Keys.Concat(undone.Keys).Distinct())
        {
            (List<string> decides, List<string> trackedOnes) = lines.TryGetValue(line, out (List<string> Decides, List<string> Tracked) found) ? found : ([], []);
            answers[line] = new IgnoreLineAnswer(decides, trackedOnes, undone.TryGetValue(line, out int by) ? by : null);
        }
        return new IgnoreReading(answers, untrackedPaths);
    }

    /// <summary>The ignore file and line that decide a path, as <c>git check-ignore -v</c> names them; null where no line
    /// matches it, or git could not say.</summary>
    public async Task<IgnoringLine?> IgnoringAsync(string path)
    {
        string relative = Relative(path).TrimEnd('/') + (Directory.Exists(path) ? "/" : string.Empty);
        if (await DecidingAsync(Root, [relative], null) is not [{ Source.Length: > 0 } decided])
        {
            return null;
        }
        GitResult listed = await OnFilesAsync(["ls-files", "-z", "--cached"], [relative.TrimEnd('/')]);
        bool isTracked = listed.Succeeded && listed.Output.Length > 0;
        string file = decided.Source.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), decided.Source[2..])
            : Path.IsPathRooted(decided.Source) ? decided.Source : Full(decided.Source);
        return new IgnoringLine(file, decided.Line, decided.Pattern, !decided.Pattern.StartsWith('!') && !isTracked, isTracked);
    }

    /// <summary>The line of each path's ignore file deciding it, as <c>git check-ignore -v --no-index</c> answers for the
    /// paths on its input, from <paramref name="folder"/> (their paths are from it), the source by its path from the tree's
    /// top; a path no line matches has an empty source. In a tree of <paramref name="workTree"/>'s where given.</summary>
    private async Task<IReadOnlyList<(string Source, int Line, string Pattern, string Path)>?> DecidingAsync(string folder, IReadOnlyCollection<string> paths,
        string? workTree)
    {
        if (paths.Count == 0)
        {
            return [];
        }
        bool composed = paths.Any(name => !Ascii.IsValid(name)) && await ComposesAsync();
        StringBuilder names = new();
        foreach (string name in paths)
        {
            names.Append(GitForm(name, composed)).Append('\0');
        }
        GitResult decided = await RunAsync(folder, ["check-ignore", "-z", "-v", "-n", "--no-index", "--stdin"], names.ToString(),
            workTree is null ? null : new Dictionary<string, string> { [GitVariables.WorkTree] = workTree }, answers: [1]);
        if (!decided.Succeeded && decided.Code != 1)
        {
            return null;
        }
        string[] fields = decided.Output.Split('\0');
        List<(string, int, string, string)> found = [];
        for (int at = 0; at + 3 < fields.Length; at += 4)
        {
            found.Add((fields[at], int.TryParse(fields[at + 1], out int line) ? line : 0, fields[at + 2], fields[at + 3]));
        }
        return found;
    }

    /// <summary>The lines of an ignore file before a <c>!</c> line, which ignore what it keeps: those lines alone, the
    /// negations among them blank, asked of git in a tree of their own at the file's place, for the kept paths.</summary>
    private async Task<IReadOnlyList<int>> UndoneAsync(string ignoreFile, string source, int keeper, IReadOnlyList<string> kept)
    {
        string[] written;
        try
        {
            written = (await File.ReadAllTextAsync(ignoreFile)).ReplaceLineEndings("\n").Split('\n');
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        string before = string.Join('\n', written.Take(keeper - 1).Select(l => l.TrimStart().StartsWith('!') ? string.Empty : l)) + "\n";
        using CommitFile alone = CommitFile.Ignoring(source, before);
        // From the tree's top, which lies outside the tree of their own, the paths named from it.
        string prefix = source.Contains('/', StringComparison.Ordinal) ? source[..(source.LastIndexOf('/') + 1)] : string.Empty;
        IReadOnlyList<(string Source, int Line, string Pattern, string Path)>? decided = await DecidingAsync(Root, [.. kept.Select(k => prefix + k)], alone.Tree);
        return [.. (decided ?? []).Where(d => d.Source == source && d.Line > 0).Select(d => d.Line).Distinct()];
    }

    private static string[] Split(string output) => output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
}
