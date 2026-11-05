using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>The open files' texts as the editor had them at one moment (<see cref="EditorArea.Texts"/>), each a snapshot
/// any thread reads, and every other file's as read from disk: what work off the UI thread reads files through.</summary>
public sealed class OpenTexts
{
    private readonly Dictionary<string, TextSnapshot> open = new(PathNames.Comparer);

    /// <summary>No file open, every text the disk's: the reader without a window.</summary>
    public static OpenTexts None { get; } = new([]);

    internal OpenTexts(IEnumerable<OpenDocument> documents)
    {
        foreach (OpenDocument document in documents.Where(d => d.HoldsText))
        {
            open[document.Path] = document.Text.Snapshot;
        }
    }

    /// <summary>The open files' paths.</summary>
    public IEnumerable<string> Paths => open.Keys;

    /// <summary>An open file's text; null for one not open.</summary>
    public TextSnapshot? Open(string path) => open.GetValueOrDefault(path);

    /// <summary>A file's text, an open one's as the editor had it, else the disk's; null when it cannot be read.</summary>
    public TextSnapshot? TextOf(string path) => Open(path) ?? EditorArea.Read(path);

    /// <summary>A file's text as <see cref="TextOf"/> reads it, as a string.</summary>
    public string? Text(string path) => Open(path) is { } text ? text.ToString() : EditorArea.ReadText(path);
}
