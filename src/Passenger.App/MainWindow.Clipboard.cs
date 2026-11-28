using Avalonia.Input.Platform;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>What the window puts on the clipboard and takes off it again: the texts copied, newest first, for Paste from
/// History; Copy Reference's name of what the caret stands on; and the words of the other open files, which Cyclic Expand
/// Word offers after the file's own.</summary>
public partial class MainWindow
{
    /// <summary>The texts copied in the window (its editors', Copy Path's and Copy Reference's), newest first, each once, at
    /// most <see cref="MostCopied"/>, with whether each was whole lines.</summary>
    private readonly List<CopiedText> copied = [];

    private const int MostCopied = 100;

    /// <summary>The texts Paste from History offers, newest first.</summary>
    public IReadOnlyList<CopiedText> Copied => copied;

    private void AddClipboard()
    {
        Editors.ForEachEditor(editor =>
        {
            editor.Copied += (_, text) => Remember(text);
            editor.WordsElsewhere = () => Editors.Documents.Where(d => d.Text != editor.Document).Select(d => d.Text.Snapshot.ToString());
        });
    }

    private void Remember(CopiedText text)
    {
        copied.RemoveAll(c => c.Text == text.Text);
        copied.Insert(0, text);
        if (copied.Count > MostCopied)
        {
            copied.RemoveAt(MostCopied);
        }
    }

    /// <summary>Text put on the clipboard and remembered for Paste from History, said in the status bar (as <paramref name="said"/>
    /// where it is given).</summary>
    private async Task CopyAsync(string text, string? said = null)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            Remember(new CopiedText(text, WholeLines: false));
            status.Text = said ?? $"Copied {text}";
        }
    }

    /// <summary>Paste from History: the texts copied, newest first (the clipboard's own first where another program put it
    /// there), each by its first line, in a popup narrowed as words are typed; the one chosen is pasted at the carets of
    /// the editor shown as it opened, as Paste pastes, and goes on the clipboard.</summary>
    public async Task<ChoicePopup> ShowPasteHistoryAsync()
    {
        CodeEditor editor = Editors.CodeEditor;
        if (Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { Length: > 0 } now && !copied.Any(c => c.Text == now))
        {
            Remember(new CopiedText(now, WholeLines: false));
        }
        ChoicePopup popup = new(TitleOf(AppActions.PasteFromHistory), WindowWords.SearchCopied, [.. copied.Select(Chosen)], Refusals.NothingCopied);
        popups.Show(popup, chosen =>
        {
            if (chosen?.Value is CopiedText text)
            {
                editor.PasteText(text.Text, text.WholeLines);
            }
        });
        return popup;

        static Choice Chosen(CopiedText text)
        {
            string[] lines = text.Text.TrimEnd('\r', '\n').Split('\n');
            return new Choice(lines[0].Trim(), lines.Length > 1 ? $"+{Plural.Of(lines.Length - 1, "line")}" : string.Empty, text);
        }
    }

    /// <summary>Copy Reference: the symbol at the caret named whole where C# names one there, else the file's path and the
    /// caret's line (<see cref="CSharp.CodeAssist.ReferenceAsync"/>), put on the clipboard.</summary>
    private async Task CopyReferenceAsync() =>
        await CopyAsync(await Assist.ReferenceAsync(Editors.Active!, Editors.CodeEditor.Selections.Primary.Caret, session.Folder));
}
