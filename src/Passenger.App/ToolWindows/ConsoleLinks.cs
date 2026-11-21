using System.Buffers;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.ToolWindows;

/// <summary>What a link in a console or the terminal opens: a file at a line and column (from zero) in the editor, a
/// folder in the person's file manager, or an address in the person's browser.</summary>
public abstract record LinkTarget;

public sealed record FileLink(string Path, TextPosition Position) : LinkTarget
{
    /// <summary>Whether the line is its compiler's count, not the editor's: a compiler's message or a stack frame (its PDB's)
    /// in a file Roslyn compiles, which counts its lines as the language server does, or a XAML compiler's message or frame
    /// in a XAML file, counted as an XML reader counts them; read in the file's text as it is opened
    /// (<see cref="CSharp.ServerPlaces.PositionOfCompiled"/>).</summary>
    public bool CompiledLines { get; init; }
}

/// <summary>A folder a click shows in the person's file manager, such as where a publish landed.</summary>
public sealed record FolderLink(string Path) : LinkTarget;

public sealed record WebLink(Uri Address) : LinkTarget;

/// <summary>A path on this machine a program linked in the terminal (OSC 8), as it named it: its file opens in the editor,
/// or its folder in the file manager, whichever the disk has once it is clicked.</summary>
public sealed record ProgramFileLink(string Path) : LinkTarget;

/// <summary>A link in a line of text: where it starts in the line, how long it is, and what it opens. A hidden one (the
/// terminal's file paths) is drawn as plain text until Cmd or Ctrl is held over it.</summary>
public readonly record struct FoundLink(int Start, int Length, LinkTarget Target, bool Hidden = false);

/// <summary>The links in a console's lines: web addresses (one a container's program names as its own where its container
/// publishes it here, <see cref="Reaching"/>), a .NET stack frame's file and line ("at X in /path/File.cs:line 12"), and a
/// compiler's message ("File.cs(12,5): error ..."). The terminal's (<see cref="FilePaths"/>) add any path to a file, with a
/// line and column after it or not. A path is a link only to a file there is, a relative one found under the folders given
/// (the project's first); the disk, and where a container's address is reached, asked off the window's thread, the line's
/// links coming with the answer; a stack frame's <c>/_/</c>, a deterministic build's name for the source root, is relative
/// too. A stretch of the IDE's own words is a link too (<see cref="Claim"/>), as the folder a publish landed in. A line's
/// links are kept while its text is the same, found again by its characters, and the editor's for a line while it starts
/// where it did, so drawing it again and the pointer moving over it copy and make nothing. As a highlighter it colours
/// them, for a view with no colours of its own.</summary>
public sealed partial class ConsoleLinks : IHighlighter
{
    /// <summary>Lines longer than this have no links: output written without line breaks costs nothing more to draw.</summary>
    private const int LongestLine = 4096;

    /// <summary>How many lines' links, and files' answers, are kept before they are forgotten all at once.</summary>
    private const int Kept = 2048;

    private readonly Dictionary<string, FoundLink[]> lines = [];

    /// <summary>The links the IDE itself wrote, by the line's text, which no scan of the line's words would find again:
    /// kept until the console's text goes (<see cref="Forget"/>), where a line found by its own words is forgotten sooner.</summary>
    private readonly Dictionary<string, FoundLink[]> claimed = [];

    private readonly Dictionary<string, string?> files = new(PathNames.Comparer);

    /// <summary>The paths being asked of the disk, off the window's thread, or waiting to be: each asked once at a time.</summary>
    private readonly HashSet<string> asking = new(PathNames.Comparer);

    private readonly List<string> toAsk = [];

    /// <summary>Lines whose links wait on a path being asked, read again once its answer lands.</summary>
    private readonly HashSet<string> waiting = new(StringComparer.Ordinal);

    /// <summary>Whether the line being read waits on a path being asked.</summary>
    private bool waited;

    /// <summary>Whether a batch of paths is being asked now.</summary>
    private bool lookingUp;

    /// <summary>Where each address a container's program named as its own is reached from here, by the container its line
    /// named and the address (<see cref="Reached"/>), as answered: null where it is published nowhere.</summary>
    private readonly Dictionary<(string? Named, Uri Address), Uri?> reached = [];

    /// <summary>The addresses being asked where they are reached, off the window's thread.</summary>
    private readonly HashSet<(string? Named, Uri Address)> reaching = [];

    private Func<string?, Uri, Task<Uri?>>? reachedBy;

    private Func<Containers.ContainerPaths?>? mappedBy;

    /// <summary>How many times what was found has been forgotten, so answers asked before are passed over.</summary>
    private int forgotten;

    /// <summary><see cref="lines"/> read by a line's characters, so a line kept is found with no string made of it.</summary>
    private readonly Dictionary<string, FoundLink[]>.AlternateLookup<ReadOnlySpan<char>> kept;

    /// <summary>The editor's links last given for a line, by where it starts, with the links they were made from: the
    /// same while the line's text and its place are.</summary>
    private readonly Dictionary<int, (FoundLink[] Found, TextLink[] Links)> given = [];

    public ConsoleLinks() => kept = lines.GetAlternateLookup<ReadOnlySpan<char>>();

    public event EventHandler? Changed;

    /// <summary>How many lines' characters have been made a string to find their links: once for each line not kept.</summary>
    public long LinesCopied { get; private set; }

    /// <summary>Every path asked of the disk since the console last forgot what it found (or last kept as many), as found.</summary>
    public IReadOnlyCollection<string> Asked => files.Keys;

    /// <summary>Whether paths found in lines are being asked of the disk, or addresses where they are reached: their lines'
    /// links come once they are answered.</summary>
    public bool Looking => asking.Count > 0 || reaching.Count > 0;

    /// <summary>Where a relative path is looked for, in order: the project the output came from first.</summary>
    public Func<IReadOnlyList<string>> Folders { get; set; } = () => [];

    /// <summary>Where the paths of the containers whose output this is are the workspace's files (a container run's images),
    /// tried first for a path that starts at the root: the run's own, given as it starts, which another program's output
    /// takes with it (<see cref="Forget"/>); null for output of programs on this machine. Set, the lines' links are found
    /// again.</summary>
    public Func<Containers.ContainerPaths?>? Paths
    {
        get => mappedBy;
        set
        {
            if (value != mappedBy)
            {
                mappedBy = value;
                ForgetFound();
            }
        }
    }

    /// <summary>Whether any path to a file is a link (hidden); a console links only frames and compiler messages.</summary>
    public bool FilePaths { get; init; }

    /// <summary>For output of programs in containers, where an address one names as its own (where it says it listens, <see
    /// cref="WebAddress.OnItsOwnMachine"/>) is reached from here, given the container its line's log prefix names (Compose's
    /// <c>web-1</c>) or null: null where it is published nowhere, then no link. Asked off the window's thread once for each
    /// address until the console's text goes, which takes this with it (<see cref="Forget"/>); null for output of programs on
    /// this machine. Set, the lines' links are found again.</summary>
    public Func<string?, Uri, Task<Uri?>>? Reaching
    {
        get => reachedBy;
        set
        {
            if (value != reachedBy)
            {
                reachedBy = value;
                ForgetFound();
            }
        }
    }

    /// <summary>Forgets what was found, for output from another program (another project's relative paths, where it ran, its
    /// containers' paths and addresses) or a file made or deleted since, and the IDE's own claims with it; the program's
    /// containers kept where it is <paramref name="sameProgram"/>, whose console was emptied while it runs on.</summary>
    public void Forget(bool sameProgram = false)
    {
        claimed.Clear();
        files.Clear();
        if (!sameProgram)
        {
            reachedBy = null;
            mappedBy = null;
        }
        ForgetFound();
    }

    /// <summary>Forgets each line's links and what they wait on, to be found again as the lines are drawn.</summary>
    private void ForgetFound()
    {
        lines.Clear();
        given.Clear();
        asking.Clear();
        toAsk.Clear();
        waiting.Clear();
        reached.Clear();
        reaching.Clear();
        forgotten++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Makes a stretch of a line the IDE itself wrote a link, found with the line's others as it is drawn: the
    /// folder a publish landed in, whose words no console's pattern would know. A line's text is its key, so the same line
    /// said again is linked the same. The claim is kept until the console's text goes (<see cref="Forget"/>), where a line
    /// found by its own words is forgotten sooner; a line longer than a console links is not linked.</summary>
    public void Claim(string line, int start, int length, LinkTarget target)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, line.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, line.Length - start);
        if (line.Length > LongestLine)
        {
            return;
        }
        FoundLink[] before = claimed.TryGetValue(line, out FoundLink[]? said) ? said : [];
        FoundLink link = new(start, length, target);
        if (before.Any(own => link.Start < own.Start + own.Length && own.Start < link.Start + link.Length))
        {
            return;
        }
        FoundLink[] found = [.. before, link];
        Array.Sort(found, static (one, other) => one.Start.CompareTo(other.Start));
        claimed[line] = found;
        if (lines.TryGetValue(line, out FoundLink[]? cached))
        {
            // The line was drawn before the claim: its links take the IDE's own in, which win where they overlap.
            lines[line] = Merged(found, cached);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The links of a line: the IDE's own words, then what the line's text names where it does not overlap them.</summary>
    private static FoundLink[] Merged(IReadOnlyList<FoundLink> said, IEnumerable<FoundLink> found)
    {
        List<FoundLink> links = [.. said];
        foreach (FoundLink link in found)
        {
            if (!links.Any(own => link.Start < own.Start + own.Length && own.Start < link.Start + link.Length))
            {
                links.Add(link);
            }
        }
        links.Sort(static (one, other) => one.Start.CompareTo(other.Start));
        return [.. links];
    }

    /// <summary>The links over a stretch of a text, in the link colour.</summary>
    public IReadOnlyList<SyntaxSpan> GetSpans(TextSnapshot text, int start, int length)
    {
        List<SyntaxSpan>? spans = null;
        int end = start + length;
        for (int line = text.GetLineFromOffset(start); line < text.LineCount && text.GetLineStart(line) < end; line++)
        {
            foreach (TextLink link in On(text, line))
            {
                if (link.Range.End > start && link.Range.Start < end)
                {
                    (spans ??= []).Add(new SyntaxSpan(link.Range.Start, link.Range.Length, TokenKind.Link));
                }
            }
        }
        return spans ?? (IReadOnlyList<SyntaxSpan>)[];
    }

    /// <summary>The links on a line of a text, in its offsets, as the editor asks for them: those it was given for the line
    /// before while its text and start are the same, the line read into a pooled buffer to find them.</summary>
    public IReadOnlyList<TextLink> On(TextSnapshot text, int line)
    {
        LineSpan span = text.GetLine(line);
        if (span.Length == 0 || span.Length > LongestLine)
        {
            return [];
        }
        char[] chars = ArrayPool<char>.Shared.Rent(span.Length);
        FoundLink[] found;
        try
        {
            text.CopyTo(span.Start, chars.AsSpan(0, span.Length));
            found = In(chars.AsSpan(0, span.Length));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
        if (found.Length == 0)
        {
            return [];
        }
        if (given.TryGetValue(span.Start, out (FoundLink[] Found, TextLink[] Links) made) && ReferenceEquals(made.Found, found))
        {
            return made.Links;
        }
        TextLink[] links = new TextLink[found.Length];
        for (int i = 0; i < found.Length; i++)
        {
            links[i] = new TextLink(new TextRange(span.Start + found[i].Start, found[i].Length), found[i].Target);
        }
        if (given.Count >= Kept)
        {
            given.Clear();
        }
        given[span.Start] = (found, links);
        return links;
    }

    /// <summary>The links in one line of text given by its characters (a terminal's row), as <see cref="In(string)"/>
    /// finds them; a string is made of the line only when it is not kept.</summary>
    public FoundLink[] In(ReadOnlySpan<char> line)
    {
        if (line.Length == 0 || line.Length > LongestLine)
        {
            return [];
        }
        if (kept.TryGetValue(line, out FoundLink[]? links))
        {
            return links;
        }
        LinesCopied++;
        return In(line.ToString());
    }

    /// <summary>The links in one line of text, in order and apart.</summary>
    public FoundLink[] In(string line)
    {
        if (line.Length == 0 || line.Length > LongestLine)
        {
            return [];
        }
        if (lines.TryGetValue(line, out FoundLink[]? kept))
        {
            return kept;
        }
        waited = false;
        List<FoundLink> found = [];
        // Merged logs name the resource before the program's own text: "web-1 | " or "[apiservice] ".
        Match prefix = ResourcePrefix().Match(line);
        int skip = prefix.Success ? prefix.Length : 0;
        string? container = prefix.Success ? prefix.Groups["named"].Value : null;
        foreach (ValueMatch url in WebAddresses.In(line))
        {
            if (Web(line.Substring(url.Index, url.Length), container) is { } target)
            {
                Claim(found, new FoundLink(url.Index, url.Length, target));
            }
        }
        if (Frame().Match(line, skip, line.Length - skip) is { Success: true } frame && Resolve(frame.Groups["path"].Value.Trim(), deterministic: true) is { } framed)
        {
            Group path = frame.Groups["path"];
            Claim(found, new FoundLink(path.Index, frame.Index + frame.Length - path.Index,
                new FileLink(framed, Position(frame.Groups["line"], null)) { CompiledLines = CSharp.ServerPlaces.CountedApart(framed) }));
        }
        if (FilePaths)
        {
            foreach (Match place in AnyPath().Matches(line))
            {
                Group path = place.Groups["path"];
                string named = path.Value;
                if (place.Groups["line"].Success || named.TrimEnd('.').Length == named.Length)
                {
                    LinkTarget? file = Named(named) is { } there ? new FileLink(there, place.Groups["line"].Success ? Position(place.Groups["line"], place.Groups["column"]) : default) : null;
                    if (file is not null)
                    {
                        Claim(found, new FoundLink(place.Index, place.Length, file, Hidden: true));
                    }
                }
                else if (Named(named.TrimEnd('.')) is { } sentence)
                {
                    // A sentence's full stop after a file's name is not the name's.
                    Claim(found, new FoundLink(path.Index, named.TrimEnd('.').Length, new FileLink(sentence, default), Hidden: true));
                }
            }
        }
        else if (GodotFrame().Match(line, skip, line.Length - skip) is { Success: true } godot && Resolve(godot.Groups["path"].Value) is { } raised)
        {
            Group path = godot.Groups["path"];
            Claim(found, new FoundLink(path.Index, godot.Groups["line"].Index + godot.Groups["line"].Length - path.Index,
                new FileLink(raised, Position(godot.Groups["line"], null)) { CompiledLines = CSharp.ServerPlaces.CountedApart(raised) }));
        }
        else if (Compiler().Match(line, skip, line.Length - skip) is { Success: true } message && Resolve(message.Groups["path"].Value, project: ProjectOf(line)) is { } source)
        {
            Group path = message.Groups["path"];
            // A XAML file's place is its compiler's only in a compiler's message: a failed binding's line names the editor's.
            bool compiled = CSharp.ServerPlaces.CountedByRoslyn(source)
                || (CSharp.ServerPlaces.CountedAsXml(source) && Diagnostic().IsMatch(line, message.Index + message.Length));
            Claim(found, new FoundLink(path.Index, message.Groups["close"].Index + 1 - path.Index,
                new FileLink(source, Position(message.Groups["line"], message.Groups["column"])) { CompiledLines = compiled }));
        }
        else if (CompilerFile().Match(line, skip, line.Length - skip) is { Success: true } placeless && Resolve(placeless.Groups["path"].Value, project: ProjectOf(line)) is { } whole)
        {
            Group path = placeless.Groups["path"];
            Claim(found, new FoundLink(path.Index, path.Length, new FileLink(whole, default)));
        }
        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        FoundLink[] links = claimed.TryGetValue(line, out FoundLink[]? said) ? Merged(said, found) : [.. found];
        if (lines.Count >= Kept)
        {
            lines.Clear();
            waiting.Clear();
        }
        lines[line] = links;
        if (waited)
        {
            waiting.Add(line);
        }
        return links;
    }

    /// <summary>The file a path in the terminal names: one that looks like a path (a folder or an extension in it), there.</summary>
    private string? Named(string path) =>
        path is "." or ".." || path.AsSpan().IndexOfAny(@"/\.") < 0 ? null : Resolve(path);

    /// <summary>Adds a link unless one found before (a web address, then a frame) already covers any of it.</summary>
    private static void Claim(List<FoundLink> found, FoundLink link)
    {
        foreach (FoundLink other in found)
        {
            if (link.Start < other.Start + other.Length && other.Start < link.Start + link.Length)
            {
                return;
            }
        }
        found.Add(link);
    }

    /// <summary>An address the browser opens (www. taken as http), one a container's program names as its own where it is
    /// reached from here (the container its line names, <paramref name="named"/>, or the console's), one a program on this
    /// machine listens at on every interface at localhost (<see cref="WebAddress.Callable"/>); a file: URL opens its file in
    /// the editor.</summary>
    private LinkTarget? Web(string text, string? named)
    {
        if (WebAddresses.Parse(text) is not { } address)
        {
            return null;
        }
        if (address.IsFile)
        {
            return FoundPaths.OfFileAddress(address) is { } local && Exists(local) is { } file ? new FileLink(file, default) : null;
        }
        if (reachedBy is not null && WebAddress.OnItsOwnMachine(address))
        {
            return Reached(named, address) is { } here ? new WebLink(here) : null;
        }
        // Where a program on this machine listens on every interface, which no browser can name.
        return new WebLink(WebAddress.Of(WebAddress.Callable(address.OriginalString)) ?? address);
    }

    /// <summary>Where an address a container's program names as its own is reached from here, once answered: null while it
    /// is being asked, the line being read waiting on it, or where its container publishes it nowhere.</summary>
    private Uri? Reached(string? named, Uri address)
    {
        if (reached.TryGetValue((named, address), out Uri? here))
        {
            return here;
        }
        waited = true;
        if (reaching.Add((named, address)))
        {
            IdeErrors.Observe(ReachAsync(named, address, reachedBy!, forgotten));
        }
        return null;
    }

    /// <summary>Asks where an address is reached from here, off the window's thread; as its answer lands, the lines that
    /// waited on it are read again and the views told.</summary>
    private async Task ReachAsync(string? named, Uri address, Func<string?, Uri, Task<Uri?>> by, int asked)
    {
        // On a later turn however soon answered, so the line asking is kept, waiting, before its answer lands.
        Uri? here = await BackgroundWork.Run(() => by(named, address))
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);
        UiThread.Check();
        if (asked != forgotten)
        {
            return;
        }
        reaching.Remove((named, address));
        reached[(named, address)] = here;
        ReadWaitingAgain();
    }

    /// <summary>A line and column, both from one in the text, as a position from zero; no column is the line's start.</summary>
    private static TextPosition Position(Group line, Group? column) => new(
        Math.Max(0, int.Parse(line.Value, System.Globalization.CultureInfo.InvariantCulture) - 1),
        column is { Success: true } ? Math.Max(0, int.Parse(column.Value, System.Globalization.CultureInfo.InvariantCulture) - 1) : 0);

    /// <summary>The folder of the project MSBuild names at a message's end ("[/path/Hello.csproj]"), whose files its
    /// relative paths are.</summary>
    private static string? ProjectOf(string line) =>
        Project().Match(line) is { Success: true } project && Path.IsPathFullyQualified(project.Groups["project"].Value)
            ? Path.GetDirectoryName(project.Groups["project"].Value)
            : null;

    /// <summary>The file a path names, if there is one: the first there of those it may name (<see cref="Candidates"/>);
    /// none while an answer before it has not come, the line waiting on it.</summary>
    private string? Resolve(string path, string? project = null, bool deterministic = false)
    {
        if (path.Length == 0 || path.Length > 1024 || path.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }
        bool unknown = false;
        foreach (string candidate in Candidates(path, project, deterministic))
        {
            // Every candidate up to the first known to be there is asked in one batch, so a line waits for one answer.
            if (!Known(candidate, out string? file))
            {
                unknown = true;
            }
            else if (file is not null)
            {
                return unknown ? null : file;
            }
        }
        return null;
    }

    /// <summary>The files a path may name, in order: as given when absolute (from the home folder after ~), a container's
    /// path first as the workspace's, else under the project named, then each of the folders. A deterministic build's
    /// <c>/_/</c> stands for those folders too, and a Godot game's res:// for its project's, which the folders hold.</summary>
    private IEnumerable<string> Candidates(string path, string? project, bool deterministic)
    {
        string relative = path.StartsWith(GodotResource, StringComparison.Ordinal) ? path[GodotResource.Length..] : path;
        if (relative != path)
        {
            path = relative;
        }
        else if (path.StartsWith('/') && mappedBy?.Invoke() is { IsEmpty: false } map && map.ToHost(path) is { } host)
        {
            yield return host;
        }
        if (deterministic && (path.StartsWith("/_/", StringComparison.Ordinal) || path.StartsWith(@"\_\", StringComparison.Ordinal)))
        {
            relative = path[3..];
        }
        else if (PathNames.Expanded(path) is { } home)
        {
            yield return home;
            yield break;
        }
        else if (Path.IsPathFullyQualified(path))
        {
            yield return path;
            yield break;
        }
        else if (Path.IsPathRooted(path))
        {
            yield break;
        }
        if (project is not null)
        {
            yield return Path.Combine(project, relative);
        }
        foreach (string folder in Folders())
        {
            yield return Path.Combine(folder, relative);
        }
    }

    /// <summary>The file's canonical path, or null when no file is there or it is being asked (<see cref="Known"/>).</summary>
    private string? Exists(string path) => Known(path, out string? file) ? file : null;

    /// <summary>Whether the disk's answer for a path is known, the file it names in <paramref name="file"/>: each path asked
    /// once, off the window's thread (a line drawn never waits on the disk), the line being read waiting on it; one naming a
    /// share, a device or an automount root outside the folders given never (<see cref="FoundPaths"/>), so a line reaches
    /// nothing.</summary>
    private bool Known(string path, out string? file)
    {
        if (files.TryGetValue(path, out file))
        {
            return true;
        }
        if (!FoundPaths.Askable(path) && !FoundPaths.Askable(path, Folders()))
        {
            return true;
        }
        waited = true;
        if (asking.Add(path))
        {
            toAsk.Add(path);
            Ask();
        }
        return false;
    }

    /// <summary>Asks the disk about the paths waiting to be asked, a batch at a time off the window's thread; as its
    /// answers land, the lines that waited on them are read again and the views told.</summary>
    private void Ask()
    {
        if (lookingUp || toAsk.Count == 0)
        {
            return;
        }
        lookingUp = true;
        string[] batch = [.. toAsk];
        toAsk.Clear();
        IdeErrors.Observe(AskAsync(batch, forgotten));
    }

    private async Task AskAsync(string[] batch, int asked)
    {
        string?[] found;
        try
        {
            // On a later turn however soon answered, so a line asking is kept, waiting, before its answers land.
            found = await BackgroundWork.Run(() => Array.ConvertAll(batch, Lookup))
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);
        }
        finally
        {
            lookingUp = false;
        }
        UiThread.Check();
        if (asked == forgotten)
        {
            if (files.Count + batch.Length > Kept)
            {
                files.Clear();
            }
            for (int i = 0; i < batch.Length; i++)
            {
                files[batch[i]] = found[i];
                asking.Remove(batch[i]);
            }
            ReadWaitingAgain();
        }
        Ask();
    }

    /// <summary>Reads again the lines that waited on an answer now landed, and tells the views.</summary>
    private void ReadWaitingAgain()
    {
        string[] again = [.. waiting];
        waiting.Clear();
        foreach (string line in again)
        {
            lines.Remove(line);
            In(line);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The file's canonical path as the disk has it, or null when no file is there.</summary>
    private static string? Lookup(string path)
    {
        try
        {
            return File.Exists(path) ? PathNames.Canonical(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A .NET stack frame, a line beginning "at": "at Type.Method(args) in /path/File.cs:line 12", the path after
    /// the method's closing parenthesis, so one with spaces (or " in ") is whole.</summary>
    [GeneratedRegex(@"^\s*at\s[^\r\n]*?\) in (?<path>[^\r\n]+?):line (?<line>\d{1,9})\b")]
    private static partial Regex Frame();

    /// <summary>Where a Godot game's own files are named from, its project's folder.</summary>
    private const string GodotResource = Passenger.Workspace.GodotResPaths.Scheme;

    /// <summary>The place a Godot game's error or warning was raised at, on the line after it: "   at: void Main._Process(double)
    /// (res://Main.cs:19)", the function before the place in parentheses at the line's end.</summary>
    [GeneratedRegex(@"^\s*at:\s[^\r\n]*\((?<path>[^()\r\n]+?):(?<line>\d{1,9})\)\s*$")]
    private static partial Regex GodotFrame();

    /// <summary>The container's name and bar <c>docker compose logs</c> puts before each line of a service's (its project's
    /// name left out), or the name in brackets an AppHost's merged log puts there.</summary>
    [GeneratedRegex(@"^(?:(?<named>[\w.-]+-\d+)\s+\| |\[(?<named>[^\]\r\n]+)\] )")]
    private static partial Regex ResourcePrefix();

    /// <summary>A compiler's or MSBuild's message, the place first on its line: "File.cs(12,5): error CS1002: ...", "1>"
    /// before it when Visual Studio numbered the projects. The path may hold parentheses of its own ("Shop (copy)").</summary>
    [GeneratedRegex(@"^\s*(?:\d+>)?(?<path>[^\s>][^\r\n]*?)\((?<line>\d{1,9})(?:,(?<column>\d{1,9}))?(?:[-,]\d{1,9})*(?<close>\))\s*:")]
    private static partial Regex Compiler();

    /// <summary>A compiler's or MSBuild's message about a file as a whole, which names no place in it: "Views/Main.axaml :
    /// Avalonia warning AVLN3001: ...", "1>" before it when Visual Studio numbered the projects.</summary>
    [GeneratedRegex(@"^\s*(?:\d+>)?(?<path>[^\s>][^\r\n]*?)\s*:\s*(?:[A-Za-z][\w.]*\s+)?(?:error|warning)\s+[A-Za-z]+\d+\s*:")]
    private static partial Regex CompilerFile();

    /// <summary>What follows a compiler's message's place: its severity and code, MSBuild's subcategory perhaps before them
    /// ("Avalonia error AVLN2000:", "error MC3000:").</summary>
    [GeneratedRegex(@"\G\s*(?:[A-Za-z][\w.]*\s+)?(?:error|warning)\s+[A-Za-z]+\d+\s*:")]
    private static partial Regex Diagnostic();

    /// <summary>The project MSBuild names at the end of a message.</summary>
    [GeneratedRegex(@"\[(?<project>[^\[\]]+\.\w*proj)\]\s*$")]
    private static partial Regex Project();

    /// <summary>A path, absolute or relative, and what may follow it: ":12", ":12:5", ":12-14", "(12,5)" or
    /// " (12, 5)".</summary>
    [GeneratedRegex(@"(?<![\w.~/\\@+-])(?<path>(?:[A-Za-z]:[\\/]|~?[\\/]|\.{1,2}[\\/])?[\w.@+-]+(?:[\\/][\w.@+-]+)*)(?::(?<line>\d{1,9})(?::(?<column>\d{1,9}))?(?:-\d{1,9})?|\s?\((?<line>\d{1,9})(?:,\s?(?<column>\d{1,9}))?\))?(?![\w\\/])")]
    private static partial Regex AnyPath();
}
