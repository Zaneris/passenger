using Passenger.App.Endpoints;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>Each open file Passenger reads for itself marked where it cannot use what the file says, as it opens and again
/// once typing pauses: an .editorconfig's sections that match no file because their glob cannot be used (a range in
/// reverse order), and a .slnx that is not well-formed XML where its reader stopped. A UI-thread object.</summary>
internal sealed class FileChecks() : OpenFileChecks<FileChecks.Check>(Settle)
{
    /// <summary>How long typing pauses before the file is checked again.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    /// <summary>The kinds of file checked: which files each takes, who checks them (as an agent is told while a check is under
    /// way) and the marks a file's text gets.</summary>
    private static readonly Check[] Kinds =
    [
        new(path => Path.GetFileName(path) == EditorConfig.FileName, "Passenger's reading of its sections' globs", (_, text) =>
            [.. EditorConfig.Refused(text.ToString()).Select(r => new Marker(r.Glob, MarkerKind.Warning, $"No file matches this section: {r.Why}"))]),
        new(path => Path.GetExtension(path).Equals(".slnx", StringComparison.OrdinalIgnoreCase), "Passenger's reading of the solution", SolutionMarks),
        new(HttpFile.Is, "Passenger's reading of its requests", RequestMarks),
    ];

    protected override (string Checker, Check Kept)? Follow(OpenDocument document) =>
        Kinds.FirstOrDefault(k => k.Takes(document.Path)) is { } kind ? (kind.Checker, kind) : null;

    protected override Task CheckAsync(OpenDocument document, Check kind)
    {
        Show(document, document.Text.Version, kind.Marks(document.Path, document.Text.Snapshot));
        return Task.CompletedTask;
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
    internal sealed record Check(Func<string, bool> Takes, string Checker, Func<string, TextSnapshot, IReadOnlyList<Marker>> Marks);
}
