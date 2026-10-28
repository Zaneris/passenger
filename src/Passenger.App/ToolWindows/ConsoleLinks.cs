using System.Buffers;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.ToolWindows;

/// <summary>What a link in a console or the terminal opens: a file at a line and column (from zero) in the editor, or an
/// address in the person's browser.</summary>
public abstract record LinkTarget;

public sealed record FileLink(string Path, TextPosition Position) : LinkTarget
{
    /// <summary>Whether the line is its compiler's count, not the editor's: a compiler's message or a stack frame (its PDB's)
    /// in a file Roslyn compiles, which counts its lines as the language server does, or a XAML compiler's message or frame
    /// in a XAML file, counted as an XML reader counts them; read in the file's text as it is opened
    /// (<see cref="CSharp.ServerPlaces.PositionOfCompiled"/>).</summary>
    public bool CompiledLines { get; init; }
}

public sealed record WebLink(Uri Address) : LinkTarget;

/// <summary>A link in a line of text: where it starts in the line, how long it is, and what it opens. A hidden one (the
/// terminal's file paths) is drawn as plain text until Cmd or Ctrl is held over it.</summary>
public readonly record struct FoundLink(int Start, int Length, LinkTarget Target, bool Hidden = false);

/// <summary>The links in a console's lines: web addresses, a .NET stack frame's file and line ("at X in
/// /path/File.cs:line 12"), and a compiler's message ("File.cs(12,5): error ..."). The terminal's (<see cref="FilePaths"/>)
/// add any path to a file, with a line and column after it or not. A path is a link only to a file there is, a relative
/// one found under the folders given (the project's first); a stack frame's <c>/_/</c>, a deterministic build's name for
/// the source root, is relative too. A line's links are kept while its text is the same, found again by its characters, and
/// the editor's for a line while it starts where it did, so drawing it again and the pointer moving over it copy and make
/// nothing. As a highlighter it colours them, for a view with no colours of its own.</summary>
public sealed partial class ConsoleLinks : IHighlighter
{
    /// <summary>Lines longer than this have no links: output written without line breaks costs nothing more to draw.</summary>
    private const int LongestLine = 4096;

    /// <summary>How many lines' links, and files' answers, are kept before they are forgotten all at once.</summary>
    private const int Kept = 2048;

    private readonly Dictionary<string, FoundLink[]> lines = [];
    private readonly Dictionary<string, string?> files = new(PathNames.Comparer);

    /// <summary><see cref="lines"/> read by a line's characters, so a line kept is found with no string made of it.</summary>
    private readonly Dictionary<string, FoundLink[]>.AlternateLookup<ReadOnlySpan<char>> kept;

    /// <summary>The editor's links last given for a line, by where it starts, with the links they were made from: the
    /// same while the line's text and its place are.</summary>
    private readonly Dictionary<int, (FoundLink[] Found, TextLink[] Links)> given = [];

    public ConsoleLinks() => kept = lines.GetAlternateLookup<ReadOnlySpan<char>>();

    public event EventHandler? Changed;

    /// <summary>How many lines' characters have been made a string to find their links: once for each line not kept.</summary>
    public long LinesCopied { get; private set; }

    /// <summary>Where a relative path is looked for, in order: the project the output came from first.</summary>
    public Func<IReadOnlyList<string>> Folders { get; set; } = () => [];

    /// <summary>Where the paths of the containers whose output this is are the workspace's files (a container run's images),
    /// tried first for a path that starts at the root; null for output from this machine alone.</summary>
    public Func<Containers.ContainerPaths?> Paths { get; set; } = () => null;

    /// <summary>Whether any path to a file is a link (hidden); a console links only frames and compiler messages.</summary>
    public bool FilePaths { get; init; }

    /// <summary>Forgets what was found, for output from another program (another project's relative paths) or a file
    /// made or deleted since.</summary>
    public void Forget()
    {
        lines.Clear();
        files.Clear();
        given.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
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
        List<FoundLink> found = [];
        foreach (Match url in Url().Matches(line))
        {
            if (Web(url.Value) is { } target)
            {
                Claim(found, new FoundLink(url.Index, url.Length, target));
            }
        }
        // Merged logs name the resource before the program's own text: "web-1 | " or "[apiservice] ".
        int skip = ResourcePrefix().Match(line) is { Success: true } prefix ? prefix.Length : 0;
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
        else if (Compiler().Match(line, skip, line.Length - skip) is { Success: true } message && Resolve(message.Groups["path"].Value, project: ProjectOf(line)) is { } source)
        {
            Group path = message.Groups["path"];
            // A XAML file's place is its compiler's only in a compiler's message: a failed binding's line names the editor's.
            bool compiled = CSharp.ServerPlaces.CountedByRoslyn(source)
                || (CSharp.ServerPlaces.CountedAsXml(source) && Diagnostic().IsMatch(line, message.Index + message.Length));
            Claim(found, new FoundLink(path.Index, message.Groups["close"].Index + 1 - path.Index,
                new FileLink(source, Position(message.Groups["line"], message.Groups["column"])) { CompiledLines = compiled }));
        }
        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        FoundLink[] links = [.. found];
        if (lines.Count >= Kept)
        {
            lines.Clear();
        }
        lines[line] = links;
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

    /// <summary>An address the browser opens (www. taken as http); a file: URL opens its file in the editor.</summary>
    private LinkTarget? Web(string text)
    {
        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            text = "http://" + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? address))
        {
            return null;
        }
        if (address.IsFile)
        {
            return Exists(address.LocalPath) is { } file ? new FileLink(file, default) : null;
        }
        return new WebLink(address);
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

    /// <summary>The file a path names, if there is one: as given when absolute (from the home folder after ~), else under
    /// the project named, then each of the folders. A deterministic build's <c>/_/</c> stands for those folders too.</summary>
    private string? Resolve(string path, string? project = null, bool deterministic = false)
    {
        if (path.Length == 0 || path.Length > 1024 || path.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }
        string relative = path;
        if (path.StartsWith('/') && Paths() is { IsEmpty: false } map && map.ToHost(path) is { } host && Exists(host) is { } mapped)
        {
            return mapped;
        }
        if (deterministic && (path.StartsWith("/_/", StringComparison.Ordinal) || path.StartsWith(@"\_\", StringComparison.Ordinal)))
        {
            relative = path[3..];
        }
        else if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal))
        {
            return Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]));
        }
        else if (Path.IsPathFullyQualified(path))
        {
            return Exists(path);
        }
        else if (Path.IsPathRooted(path))
        {
            return null;
        }
        if (project is not null && Exists(Path.Combine(project, relative)) is { } inProject)
        {
            return inProject;
        }
        foreach (string folder in Folders())
        {
            if (Exists(Path.Combine(folder, relative)) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>The file's canonical path, or null when no file is there; each path asked once.</summary>
    private string? Exists(string path)
    {
        if (files.TryGetValue(path, out string? known))
        {
            return known;
        }
        string? found;
        try
        {
            found = File.Exists(path) ? PathNames.Canonical(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            found = null;
        }
        if (files.Count >= Kept)
        {
            files.Clear();
        }
        files[path] = found;
        return found;
    }

    /// <summary>A web address (mailto:, http, https, ftp, ftps, news, and www. without a scheme) to its last character that
    /// cannot end a sentence, or a file: URL.</summary>
    [GeneratedRegex(@"\b(?:mailto:|(?:news|(?:ht|f)tps?)://|(?<![\p{L}0-9_.])www\.)[-A-Za-z0-9+$&@#/%?=~_|!:,.;]*[-A-Za-z0-9+$&@#/%=~_|]|\bfile:(?:/|[A-Za-z]:)[^\s()\[\]]*[^\s()\[\]:;?!,.]")]
    private static partial Regex Url();

    /// <summary>A .NET stack frame, a line beginning "at": "at Type.Method(args) in /path/File.cs:line 12", the path after
    /// the method's closing parenthesis, so one with spaces (or " in ") is whole.</summary>
    [GeneratedRegex(@"^\s*at\s[^\r\n]*?\) in (?<path>[^\r\n]+?):line (?<line>\d{1,9})\b")]
    private static partial Regex Frame();

    /// <summary>The container's name and bar <c>docker compose logs</c> puts before each line of a service's.</summary>
    [GeneratedRegex(@"^(?:[\w.-]+-\d+\s+\| |\[[^\]\r\n]+\] )")]
    private static partial Regex ResourcePrefix();

    /// <summary>A compiler's or MSBuild's message, the place first on its line: "File.cs(12,5): error CS1002: ...", "1>"
    /// before it when Visual Studio numbered the projects. The path may hold parentheses of its own ("Shop (copy)").</summary>
    [GeneratedRegex(@"^\s*(?:\d+>)?(?<path>[^\s>][^\r\n]*?)\((?<line>\d{1,9})(?:,(?<column>\d{1,9}))?(?:[-,]\d{1,9})*(?<close>\))\s*:")]
    private static partial Regex Compiler();

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
