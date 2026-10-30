using Passenger.App.Endpoints;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>Each open file Passenger reads for itself marked where it cannot use what the file says, as it opens and again
/// once typing pauses: an .editorconfig's sections that match no file because their glob cannot be used (a range in
/// reverse order), and a .slnx that is not well-formed XML where its reader stopped. A UI-thread object.</summary>
internal sealed class FileChecks : IDisposable
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

    private readonly Dictionary<OpenDocument, (Check Kind, Debounce Wait, EventHandler<DocumentChangedEventArgs> Changed)> documents = [];

    public void Attach(OpenDocument document)
    {
        UiThread.Check();
        if (documents.ContainsKey(document) || Kinds.FirstOrDefault(k => k.Takes(document.Path)) is not { } kind)
        {
            return;
        }
        Debounce wait = new(Settle, () =>
        {
            Mark(document);
            return Task.CompletedTask;
        }, () => document.Checking(kind.Checker));
        EventHandler<DocumentChangedEventArgs> changed = (_, _) => wait.Restart();
        document.Text.Changed += changed;
        documents[document] = (kind, wait, changed);
        Mark(document);
    }

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        if (documents.Remove(document, out (Check Kind, Debounce Wait, EventHandler<DocumentChangedEventArgs> Changed) gone))
        {
            document.Text.Changed -= gone.Changed;
            gone.Wait.Stop();
            if (document.Markers.Count > 0)
            {
                document.Markers = [];
            }
        }
    }

    public void Dispose()
    {
        foreach (OpenDocument document in documents.Keys.ToList())
        {
            Detach(document);
        }
    }

    private void Mark(OpenDocument document)
    {
        if (!documents.TryGetValue(document, out (Check Kind, Debounce Wait, EventHandler<DocumentChangedEventArgs> Changed) checking))
        {
            return;
        }
        IReadOnlyList<Marker> marks = checking.Kind.Marks(document.Path, document.Text.Snapshot);
        if (marks.Count > 0 || document.Markers.Count > 0)
        {
            document.Markers = [.. marks];
        }
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
    private sealed record Check(Func<string, bool> Takes, string Checker, Func<string, TextSnapshot, IReadOnlyList<Marker>> Marks);
}
