using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Workspace;

/// <summary>Each open file a check of Passenger's own takes (<see cref="Follow"/>), checked as it opens, again once its
/// typing pauses, and as what it is read against changes (<see cref="Changed"/>, <see cref="CheckAll"/>): every run held as
/// the file's check (<see cref="OpenDocument.Checking"/>), so an agent's diagnostics waits for marks of the text as it
/// stands, and the marks the check's own until it lets the file go. What it keeps of each file is a
/// <typeparamref name="TKept"/>. A UI-thread object.</summary>
internal abstract class OpenFileChecks<TKept>(TimeSpan settle) : IDisposable where TKept : class
{
    private readonly Dictionary<OpenDocument, Followed> documents = [];

    /// <summary>Who checks an open file it takes, as an agent is told while the check is under way, and what it keeps of the
    /// file from now on; null for a file it takes no part in.</summary>
    protected abstract (string Checker, TKept Kept)? Follow(OpenDocument document);

    /// <summary>An open file checked as its text stands, its marks shown where they still stand for it (<see cref="Show"/>).</summary>
    protected abstract Task CheckAsync(OpenDocument document, TKept kept);

    /// <summary>What it kept of a file it lets go is let go too.</summary>
    protected virtual void LetGo(OpenDocument document, TKept kept)
    {
    }

    /// <summary>An edit of a file it checks, heard as it is made, before the file's pause starts again.</summary>
    protected virtual void Edited(TKept kept, DocumentChangedEventArgs e)
    {
    }

    public void Attach(OpenDocument document)
    {
        UiThread.Check();
        if (documents.ContainsKey(document) || Follow(document) is not (string checker, TKept kept))
        {
            return;
        }
        Debounce pause = new(settle, () => CheckAsync(document, kept), () => document.Checking(checker));
        Followed followed = new(checker, kept, pause, (_, e) =>
        {
            Edited(kept, e);
            pause.Restart();
        });
        documents[document] = followed;
        document.Text.Changed += followed.Typed;
        IdeErrors.Observe(HeldAsync(document, followed));
    }

    /// <summary>Who marks an open file, as an agent is told while the check is under way; null for one this takes no part in.</summary>
    public string? CheckOf(OpenDocument document) => documents.TryGetValue(document, out Followed? followed) ? followed.Checker : null;

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        if (documents.Remove(document, out Followed? gone))
        {
            document.Text.Changed -= gone.Typed;
            gone.Pause.Stop();
            LetGo(document, gone.Kept);
            if (document.Markers.Count > 0)
            {
                document.Markers = [];
            }
        }
    }

    /// <summary>What the open files are read against changed: each checked again once such changes and its typing pause,
    /// held as its check meanwhile.</summary>
    public void Changed()
    {
        UiThread.Check();
        foreach (Followed followed in documents.Values)
        {
            followed.Pause.Restart();
        }
    }

    /// <summary>Its checks end with the window's session: every file let go, so none it keeps open is checked again by it.</summary>
    public void Dispose()
    {
        foreach (OpenDocument document in documents.Keys.ToList())
        {
            Detach(document);
        }
    }

    /// <summary>Each open file checked again now.</summary>
    protected void CheckAll()
    {
        UiThread.Check();
        foreach ((OpenDocument document, Followed followed) in documents.ToList())
        {
            IdeErrors.Observe(HeldAsync(document, followed));
        }
    }

    /// <summary>What it keeps of an open file it checks; null for one it does not.</summary>
    protected TKept? KeptOf(OpenDocument document) => documents.TryGetValue(document, out Followed? followed) ? followed.Kept : null;

    /// <summary>Whether an open file is checked still, its text the version a check read.</summary>
    protected bool Stands(OpenDocument document, int version) => documents.ContainsKey(document) && document.Text.Version == version;

    /// <summary>A check's marks of an open file, where it is checked still and its text is the version they were made for;
    /// whether they stand for it.</summary>
    protected bool Show(OpenDocument document, int version, IReadOnlyList<Marker> marks)
    {
        if (!Stands(document, version))
        {
            return false;
        }
        document.Markers = marks;
        return true;
    }

    private async Task HeldAsync(OpenDocument document, Followed followed)
    {
        using OpenDocument.CheckHold hold = document.Checking(followed.Checker);
        await CheckAsync(document, followed.Kept);
    }

    /// <summary>A file it checks: who checks it, what it keeps of it, and its typing's pause.</summary>
    private sealed record Followed(string Checker, TKept Kept, Debounce Pause, EventHandler<DocumentChangedEventArgs> Typed);
}
