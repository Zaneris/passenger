using Passenger.App.CSharp;
using Passenger.App.Endpoints;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Vcs;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>Each open file Passenger reads for itself marked where it cannot use what the file says, as it opens and again
/// once typing pauses: an .editorconfig's sections that match no file because their glob cannot be used (a range in
/// reverse order), and its keys nothing reads or values their keys do not take (<see cref="EditorConfigKnown"/>, checked
/// again as the server's projects change); a .gitattributes' values git reads by nothing; a .gitignore's lines that ignore
/// nothing in the working tree, match only tracked files or are taken back by a later line, as git answers for the file
/// saved (checked again as the working tree changes); a .slnx that is not well-formed XML where its reader stopped. A
/// UI-thread object.</summary>
internal sealed class FileChecks(Func<CSharpService?> csharp, Func<Git?> repository) : OpenFileChecks<FileChecks.Check>(Settle)
{
    /// <summary>How long typing pauses before the file is checked again.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    /// <summary>The kinds of file checked: which files each takes, who checks them (as an agent is told while a check is under
    /// way) and the marks a file's text gets.</summary>
    private readonly Check[] kinds =
    [
        new(path => Language.ForPath(path) == Language.EditorConfig, "Passenger's reading of its sections and keys",
            async (path, text) => await EditorConfigMarksAsync(csharp(), path, text.ToString())),
        new(path => Language.ForPath(path) == Language.GitAttributes, "Passenger's reading of its attributes",
            async (_, text) => GitAttributesMarks(text.ToString(), repository() is { } git ? await git.DriversAsync() : null)),
        new(GitIgnoreAssist.IsGitIgnore, GitIgnoreChecker, async (path, text) => await GitIgnoreMarksAsync(repository(), path, text)),
        new(path => Path.GetExtension(path).Equals(".slnx", StringComparison.OrdinalIgnoreCase), "Passenger's reading of the solution", (path, text) => Task.FromResult(SolutionMarks(path, text))),
        new(HttpFile.Is, "Passenger's reading of its requests", (path, text) => Task.FromResult(RequestMarks(path, text))),
    ];

    protected override (string Checker, Check Kept)? Follow(OpenDocument document) =>
        kinds.FirstOrDefault(k => k.Takes(document.Path)) is { } kind ? (kind.Checker, kind) : null;

    protected override async Task CheckAsync(OpenDocument document, Check kind)
    {
        int version = document.Text.Version;
        Show(document, version, await kind.Marks(document.Path, document.Text.Snapshot));
    }

    /// <summary>An .editorconfig's sections whose glob matches no file, and its pairs nothing reads or whose values their
    /// keys do not take (a Roslyn option's by the server's own reading), each marked as a warning on what is wrong.</summary>
    private static async Task<IReadOnlyList<Marker>> EditorConfigMarksAsync(CSharpService? service, string path, string text)
    {
        EditorConfigKnown known = EditorConfigKnown.For(service is null ? null : await service.EditorConfigKeysAsync(path));
        EditorConfig.ConfigText read = EditorConfig.Lines(text);
        List<Marker> marks = [.. EditorConfig.Refused(read).Select(r => new Marker(r.Glob, MarkerKind.Warning, $"No file matches this section: {r.Why}"))];
        foreach ((EditorConfig.ConfigPair pair, bool preamble) in read.Preamble.Select(p => (p, true)).Concat(read.Sections.SelectMany(s => s.Pairs).Select(p => (p, false))))
        {
            string key = text.Substring(pair.KeyAt.Start, pair.KeyAt.Length);
            if (known.Problem(key, pair.Value, preamble) is ({ } why, bool onValue))
            {
                // A diagnostic no analyzer reports is marked on its id.
                TextRange marked = onValue && pair.ValueAt.Length > 0 ? pair.ValueAt
                    : EditorConfigKnown.IdOf(key) is { } id ? new TextRange(pair.KeyAt.Start + EditorConfigKnown.Diagnostic.Length, id.Length) : pair.KeyAt;
                marks.Add(new Marker(marked, MarkerKind.Warning, why));
            }
        }
        EditorConfig.ConfigPair[] options = [.. read.Sections.SelectMany(s => s.Pairs).Where(p => known.KeyOf(p.Key)?.From == EditorConfigKnown.Roslyn
            && EditorConfig.Word(p.Value) != EditorConfig.Unset)];
        if (service is not null && known.Server is not null)
        {
            foreach (int refused in await service.EditorConfigRefusedAsync([.. options.Select(p => new EditorConfigPair(p.Key, p.Value))]))
            {
                marks.Add(new Marker(options[refused].ValueAt, MarkerKind.Warning, known.RefusedBy(options[refused].Key, options[refused].Value)));
            }
        }
        return [.. marks.OrderBy(m => m.Range.Start)];
    }

    /// <summary>The working tree changed, which what a .gitignore's lines ignore is read against.</summary>
    public void WorkingTreeChanged() => Changed(kind => kind.Checker == GitIgnoreChecker);

    private const string GitIgnoreChecker = "Git's reading of its lines";

    /// <summary>A .gitignore's lines as git answers for the file saved (none while the text differs from it): one taken back
    /// by a later line, or matching only tracked files, a warning; one ignoring nothing in the working tree faded.</summary>
    private static async Task<IReadOnlyList<Marker>> GitIgnoreMarksAsync(Git? git, string path, TextSnapshot text)
    {
        string? saved = await BackgroundWork.Run(() => FileText.Load(path)?.Text);
        if (git is null || saved?.ReplaceLineEndings("\n") != text.ToString().ReplaceLineEndings("\n") || await git.IgnoreReadingAsync(path) is not { } reading)
        {
            return [];
        }
        List<Marker> marks = [];
        for (int line = 0; line < text.LineCount; line++)
        {
            TextRange written = TextRange.Trimmed(text, TextRange.FromBounds(text.GetLine(line).Start, text.GetLine(line).End));
            string pattern = text.GetText(written.Start, written.Length);
            if (pattern.Length == 0 || pattern.StartsWith('#'))
            {
                continue;
            }
            IgnoreLineAnswer? answer = reading.Of(line + 1);
            if (answer is { Decides.Count: > 0 })
            {
                continue;
            }
            (MarkerKind kind, string why) = answer switch
            {
                { UndoneBy: { } by } => (MarkerKind.Warning, $"Ignores nothing: line {by} takes back everything it ignores"),
                { Tracked.Count: > 0 } => (MarkerKind.Warning, string.Join(". ", GitIgnoreAssist.Said(pattern, answer))),
                _ => (MarkerKind.Unnecessary, pattern.StartsWith('!') ? "Keeps nothing in the working tree" : "Ignores nothing in the working tree"),
            };
            marks.Add(new Marker(written, kind, why));
        }
        return marks;
    }

    /// <summary>A .gitattributes' values git reads by nothing: an eol other than lf or crlf, a diff or merge driver neither
    /// built in nor declared in the repository's configuration, each marked as a warning on the value.</summary>
    private static IReadOnlyList<Marker> GitAttributesMarks(string text, IReadOnlySet<(string Kind, string Name)>? declared)
    {
        List<Marker> marks = [];
        for (int at = 0; at < text.Length; at = TextNavigation.NextLineStart(text, at))
        {
            foreach (GitAttributeNames.Written written in GitAttributeNames.Read(text, at, TextNavigation.LineEnd(text, at))?.Attributes ?? [])
            {
                if (written is { Valued: true, Mark: '\0' } && GitAttributeNames.Problem(text[written.Start..written.End], text[written.ValueStart..written.ValueEnd], declared) is { } why)
                {
                    marks.Add(new Marker(TextRange.FromBounds(written.ValueStart, written.ValueEnd), MarkerKind.Warning, why));
                }
            }
        }
        return marks;
    }

    /// <summary>A .slnx's text that does not read marked as an error where its XML reader stopped (its lines, a lone '\r'
    /// ending one): on the character there, or on the last before it that is not white space.</summary>
    private static IReadOnlyList<Marker> SolutionMarks(string path, TextSnapshot text)
    {
        if (SolutionModel.Unread(path, text.ToString()) is not { } unread)
        {
            return [];
        }
        int at = Math.Clamp(Lsp.ServerLines.OfXml(text).OffsetOf(Math.Max(0, unread.Line - 1), Math.Max(0, unread.Column - 1)), 0, text.Length);
        while (at > 0 && (at == text.Length || char.IsWhiteSpace(text[at])))
        {
            at--;
        }
        return [new Marker(new TextRange(at, at < text.Length ? 1 : 0), MarkerKind.Error, $"The solution does not read: {unread.Why}")];
    }

    /// <summary>An .http file's lines that do not read as what they say, as errors on what does not read.</summary>
    private static IReadOnlyList<Marker> RequestMarks(string path, TextSnapshot text)
    {
        List<Marker> marks = [];
        foreach (HttpFileProblem problem in HttpFile.Parse(path, text.ToString()).Problems.Where(p => p.Line < text.LineCount))
        {
            LineSpan line = text.GetLine(problem.Line);
            int start = line.Start + Math.Min(problem.Column, line.Length);
            marks.Add(new Marker(new TextRange(start, Math.Min(problem.Length, line.End - start)), MarkerKind.Error, problem.Why));
        }
        return marks;
    }

    /// <summary>A kind of file checked.</summary>
    internal sealed record Check(Func<string, bool> Takes, string Checker, Func<string, TextSnapshot, Task<IReadOnlyList<Marker>>> Marks);
}
