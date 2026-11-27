using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Text;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>Edits by the code's structure, as Rider's keymaps bind them: Move Statement Up and Down, Move Element Left and
/// Right and Unwrap by C#'s one parse (<see cref="CodeMoves"/>), each one step of Undo, and Auto-Indent Lines as Vim's =
/// lays lines out. In a file of another language Move Statement moves the lines, as Move Line does.</summary>
public partial class MainWindow
{
    private void AddCodeMoves()
    {
        Add(A.MoveStatementUp, "Move Statement Up", "Editor", _ => MoveStatement(up: true), WhyNoChange, ("Code", 3, 0), words: ["Move Member Up"]);
        Add(A.MoveStatementDown, "Move Statement Down", "Editor", _ => MoveStatement(up: false), WhyNoChange, ("Code", 3, 1), words: ["Move Member Down"]);
        Add(A.MoveElementLeft, "Move Element Left", "Editor", _ => MoveElement(right: false), WhyNoCSharpChange, ("Code", 3, 2), words: ["Move Argument Left", "Move Parameter Left"]);
        Add(A.MoveElementRight, "Move Element Right", "Editor", _ => MoveElement(right: true), WhyNoCSharpChange, ("Code", 3, 3), words: ["Move Argument Right", "Move Parameter Right"]);
        Add(A.Unwrap, "Unwrap", "Editor", _ => Unwrap(), WhyNoCSharpChange, ("Code", 3, 4), words: ["Remove Enclosing Statement", "Unwrap/Remove"]);
        Add(A.AutoIndentLines, "Auto-Indent Lines", "Editor", _ => IdeErrors.Observe(AutoIndentLinesAsync()), context => actions!.WhyNot(A.Reformat, context),
            ("Code", 3, 5), words: ["Reindent", "Indent Lines"]);
    }

    /// <summary>Why the file shown takes no change: none is, or it is read-only.</summary>
    private static string? WhyNoChange(ActionContext context) => Refusals.NeedsFile(context) ?? context.ReadOnlyWhy;

    /// <summary>Why the file shown takes no change by C#'s structure.</summary>
    private string? WhyNoCSharpChange(ActionContext context) => WhyNoCSharpStructure(context) ?? context.ReadOnlyWhy;

    private void MoveStatement(bool up)
    {
        CodeEditor editor = Editors.CodeEditor;
        if (Editors.Active?.Syntax?.Current is not { } csharp)
        {
            editor.TryExecute(up ? EditorActions.MoveLineUp : EditorActions.MoveLineDown);
            return;
        }
        if (CodeMoves.StatementLines(csharp, editor.Selections.Primary.Range, up) is not { } lines)
        {
            Refuse(up ? Refusals.NoStatementAbove : Refusals.NoStatementBelow);
            return;
        }
        editor.ApplyPlan(TextEditing.SwapLines(csharp.Text, editor.Selections, lines.Moving, lines.Beside));
    }

    private void MoveElement(bool right)
    {
        CodeEditor editor = Editors.CodeEditor;
        if (Editors.Active?.Syntax?.Current is not { } csharp)
        {
            return;
        }
        int caret = editor.Selections.Primary.Caret;
        if (CodeMoves.ElementSpans(csharp, caret, right) is not { } spans)
        {
            Refuse(right ? Refusals.NoElementRight : Refusals.NoElementLeft);
            return;
        }
        (TextRange moving, TextRange beside) = spans;
        string movingText = csharp.Text.GetText(moving.Start, moving.Length);
        string besideText = csharp.Text.GetText(beside.Start, beside.Length);
        TextEdit[] edits = right
            ? [new TextEdit(moving.Start, moving.Length, besideText), new TextEdit(beside.Start, beside.Length, movingText)]
            : [new TextEdit(beside.Start, beside.Length, movingText), new TextEdit(moving.Start, moving.Length, besideText)];
        // The caret goes with the element, where it stood in it: before the other's text on the left, past it on the right.
        int to = (right ? beside.Start + besideText.Length - moving.Length : beside.Start) + (caret - moving.Start);
        editor.ApplyEdits(edits, SelectionSet.Single(to));
    }

    private void Unwrap()
    {
        CodeEditor editor = Editors.CodeEditor;
        if (Editors.Active?.Syntax?.Current is not { } csharp)
        {
            return;
        }
        if (CodeMoves.UnwrapLines(csharp, editor.Selections.Primary.Caret) is not { } unwrap)
        {
            Refuse(Refusals.NothingToUnwrap);
            return;
        }
        editor.ApplyPlan(TextEditing.Unwrapped(csharp.Text, editor.Selections, unwrap.Whole, unwrap.Kept));
        status.Text = $"Unwrapped {unwrap.Name}";
    }

    /// <summary>Auto-Indent Lines: the lines the carets touch laid out as Vim's = lays them out (<see cref="CSharp.CodeActions.ReformatLinesAsync"/>),
    /// its work held from its first moment.</summary>
    private async Task AutoIndentLinesAsync()
    {
        if (Editors.Active is not { } shown || actions!.Work.Begin(A.AutoIndentLines, $"Indenting lines of {shown.Name}…", stoppable: false) is not { } work)
        {
            return;
        }
        using (work)
        {
            List<(int First, int Last)> runs = [];
            foreach (int line in TextEditing.TouchedLines(shown.Text.Snapshot, Editors.CodeEditor.Selections))
            {
                if (runs.Count > 0 && runs[^1].Last == line - 1)
                {
                    runs[^1] = (runs[^1].First, line);
                }
                else
                {
                    runs.Add((line, line));
                }
            }
            await Assist.Actions.ReformatLinesAsync(shown, runs);
            work.Tell(true, $"Indented {Plural.Of(runs.Sum(r => r.Last - r.First + 1), "line")} of {shown.Name}");
        }
    }
}
