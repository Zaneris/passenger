using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Passenger.App.Vcs;
using Passenger.App.Run;

namespace Passenger.App;

/// <summary>Read as Text: what the thing with the keys says, opened as plain text in the one view a screen reader reads
/// line by line (<see cref="PlainTextView"/>), Escape giving the keys back to where they were: in the terminal its last
/// command and output, in a diff its changes, in the Tests window the chosen test's result, in Problems the chosen problem,
/// in the status bar its last message, in the editor the problems on the caret's line and what the card at the caret says.</summary>
public partial class MainWindow
{
    /// <summary>A thing's text to read: what it is, how it is read, and what is said where it says nothing.</summary>
    private sealed record Readable(string Title, Func<CancellationToken, Task<string>> Text, string None);

    private void AddReadAsText()
    {
        actions!.Add(new IdeAction(AppActions.ReadAsText, ActionTitles.ReadAsText, ActionGroups.View, context => IdeErrors.Observe(ReadAsTextAsync(context)))
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

    /// <summary>What the element with the keys says as text, by where it is: the terminal, a diff, the Tests window,
    /// Problems, the status bar, the files' editor; else the status bar's last message; null with none.</summary>
    private Readable? ReadableAt(ActionContext context)
    {
        Visual? focused = context.Focused as Visual;
        if (focused?.FindAncestorOfType<Terminal.TerminalView>(includeSelf: true) is { Session: { } session })
        {
            return new("Terminal: last command and its output", _ => Task.FromResult(session.LastCommandText()), Refusals.NothingToRead.Shown);
        }
        if (focused?.FindAncestorOfType<DiffView>(includeSelf: true) is { } diff)
        {
            return new($"Changes: {diff.Subject}", _ => Task.FromResult(diff.ChangesAsText()), Refusals.NothingToRead.Shown);
        }
        if (context.ToolWindow?.Id == ToolWindowIds.Tests || (focused is not null && Bottom.Tests.IsVisualAncestorOf(focused)))
        {
            return Bottom.Tests.ChosenAsText() is { } chosen ? new(chosen.Title, _ => Task.FromResult(chosen.Text), Refusals.NothingToRead.Shown) : null;
        }
        if (context.ToolWindow?.Id == ToolWindowIds.Problems)
        {
            return Bottom.Problems.ChosenRow?.Problem is { } problem ? new($"Problem: {problem.Where}", _ => Task.FromResult(ProblemText(problem)), Refusals.NothingToRead.Shown) : null;
        }
        Border statusBar = StatusBarFrame;
        if (focused is not null && (focused == statusBar || statusBar.IsVisualAncestorOf(focused)))
        {
            return LastMessage();
        }
        if (Editors.Active is { } file && (focused is null || focused == Editors.CodeEditor))
        {
            Passenger.Text.TextPosition caret = Editors.CodeEditor.CaretPosition;
            return new($"{file.Title}, line {caret.Line + 1}, column {caret.Column + 1}", assist.WordsAtCaretAsync, Refusals.NothingAtCaret.Shown);
        }
        return LastMessage();
    }

    /// <summary>A problem as text: its kind, code and message, then where it is, by line and column, and its project.</summary>
    private static string ProblemText(Problem problem)
    {
        string kind = problem.Severity switch
        {
            ProblemSeverity.Error => "Error",
            ProblemSeverity.Warning => "Warning",
            _ => "Note",
        };
        string where = problem.Path is { } path ? $"At line {problem.Position.Line + 1}, column {problem.Position.Column + 1} of {path}" : string.Empty;
        string project = problem.Project is { } named ? $"In {named}" : string.Empty;
        return string.Join('\n', new[] { $"{kind}{(problem.Code is { Length: > 0 } code ? $" {code}" : string.Empty)}: {problem.Message}", where, project }.Where(l => l.Length > 0));
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
        return new("Last message", _ => Task.FromResult(said), Refusals.NothingToRead.Shown);
    }
}
