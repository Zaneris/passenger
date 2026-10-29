using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Workspace;

/// <summary>Each open .editorconfig's sections that match no file because their glob cannot be used (a range in reverse
/// order) marked on their globs, as it opens and again once typing pauses. A UI-thread object.</summary>
internal sealed class EditorConfigChecks : IDisposable
{
    /// <summary>How long typing pauses before the file's sections are checked again.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    /// <summary>Who checks the file, as an agent is told while its check is under way.</summary>
    private const string Checker = "Passenger's reading of its sections' globs";

    private readonly Dictionary<OpenDocument, (Debounce Wait, EventHandler<DocumentChangedEventArgs> Changed)> documents = [];

    public void Attach(OpenDocument document)
    {
        UiThread.Check();
        if (documents.ContainsKey(document) || Path.GetFileName(document.Path) != EditorConfig.FileName)
        {
            return;
        }
        Debounce wait = new(Settle, () =>
        {
            Mark(document);
            return Task.CompletedTask;
        }, () => document.Checking(Checker));
        EventHandler<DocumentChangedEventArgs> changed = (_, _) => wait.Restart();
        document.Text.Changed += changed;
        documents[document] = (wait, changed);
        Mark(document);
    }

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        if (documents.Remove(document, out (Debounce Wait, EventHandler<DocumentChangedEventArgs> Changed) gone))
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
        if (!documents.ContainsKey(document))
        {
            return;
        }
        IReadOnlyList<EditorConfig.RefusedSection> refused = EditorConfig.Refused(document.Text.Snapshot.ToString());
        if (refused.Count > 0 || document.Markers.Count > 0)
        {
            document.Markers = [.. refused.Select(r => new Marker(r.Glob, MarkerKind.Warning, $"No file matches this section: {r.Why}"))];
        }
    }
}
