using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Passenger.App.Vcs;
using Passenger.App.Run;

namespace Passenger.App;

/// <summary>Read as Text: what the thing with the keys says, opened as plain text in the one view a screen reader reads
/// line by line (<see cref="PlainTextView"/>), Escape giving the keys back to where they were: in the terminal its last
/// command and output, in a diff its changes, in the Tests window the chosen test's result, in the status bar its last
/// message, in the editor the problems on the caret's line and what the card at the caret says.</summary>
public partial class MainWindow
{
    /// <summary>A thing's text to read: what it is, how it is read, and what is said where it says nothing.</summary>
    private sealed record Readable(string Title, Func<CancellationToken, Task<string>> Text, string None);

    private void AddReadAsText()
    {
        actions!.Add(new IdeAction(AppActions.ReadAsText, "Read as Text", "View", context => IdeErrors.Observe(ReadAsTextAsync(context)))
        {
            WhyNot = context => context.ByAgent ? Refusals.AsksThePerson
                : Reading(context.Focused) is not null ? Refusals.ReadingAlready
                : ReadableAt(context) is null ? Refusals.NothingToRead
                : null,
            Menus = [new MenuPlace("View", 1, 3)],
            FromTerminal = true,
            Words = ["Accessible View", "Plain Text", "Screen Reader", "Read Aloud"],
        });
    }

    /// <summary>The text view open, while one is.</summary>
    public PlainTextView? PlainText => popups.Open as PlainTextView;

    /// <summary>Opens what the thing with the keys says as plain text (<see cref="ReadableAt"/>), or says why it cannot.</summary>
    private async Task ReadAsTextAsync(ActionContext context)
    {
        UiThread.Check();
        if (ReadableAt(context) is not { } readable)
        {
            Refuse(Refusals.NothingToRead);
            return;
        }
        string text = await readable.Text(CancellationToken.None);
        if (text.Length == 0)
        {
            Refuse(readable.None);
            return;
        }
        ShowAsText(readable.Title, text);
    }

    /// <summary>Text shown in the view to read, which has the keys until Escape or Close gives them back.</summary>
    public PlainTextView ShowAsText(string title, string text)
    {
        UiThread.Check();
        PlainTextView view = new(title, text, () => popups.Close());
        popups.Show(view);
        return view;
    }

    /// <summary>The view an element is in, while it is in one.</summary>
    private static PlainTextView? Reading(object? focused) =>
        (focused as Visual)?.FindAncestorOfType<PlainTextView>(includeSelf: true);

    /// <summary>What the element with the keys says as text, by where it is: the terminal, a diff, the Tests window, the
    /// status bar, the files' editor; else the status bar's last message; null with none.</summary>
    private Readable? ReadableAt(ActionContext context)
    {
        Visual? focused = context.Focused as Visual;
        if (focused?.FindAncestorOfType<Terminal.TerminalView>(includeSelf: true) is { Session: { } session })
        {
            return new("Terminal: last command and its output", _ => Task.FromResult(session.LastCommandText()), Refusals.NothingToRead);
        }
        if (focused?.FindAncestorOfType<DiffView>(includeSelf: true) is { } diff)
        {
            return new($"Changes: {diff.Subject}", _ => Task.FromResult(diff.ChangesAsText()), Refusals.NothingToRead);
        }
        if (context.ToolWindow?.Id == ToolWindowIds.Tests || (focused is not null && Bottom.Tests.IsVisualAncestorOf(focused)))
        {
            return Bottom.Tests.ChosenAsText() is { } chosen ? new(chosen.Title, _ => Task.FromResult(chosen.Text), Refusals.NothingToRead) : null;
        }
        Border statusBar = StatusBarFrame;
        if (focused is not null && (focused == statusBar || statusBar.IsVisualAncestorOf(focused)))
        {
            return LastMessage();
        }
        if (Editors.Active is { } file && (focused is null || focused == Editors.CodeEditor))
        {
            Passenger.Text.TextPosition caret = Editors.CodeEditor.CaretPosition;
            return new($"{file.Title}, line {caret.Line + 1}, column {caret.Column + 1}", assist.WordsAtCaretAsync, Refusals.NothingAtCaret);
        }
        return LastMessage();
    }

    /// <summary>The status bar's last message, with its kind where it is an error or a warning; null while it says nothing.</summary>
    private Readable? LastMessage()
    {
        (string? text, ProblemSeverity? severity, _) = status.Said;
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        string said = severity switch
        {
            ProblemSeverity.Error => $"Error: {text}",
            ProblemSeverity.Warning => $"Warning: {text}",
            _ => text,
        };
        return new("Last message", _ => Task.FromResult(said), Refusals.NothingToRead);
    }
}
