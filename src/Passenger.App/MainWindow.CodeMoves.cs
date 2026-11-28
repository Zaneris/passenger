using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Text;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>Edits by the code's structure, as Rider's keymaps bind them: Move Statement Up and Down (into and out of blocks),
/// Move Element Left and Right and Unwrap (a choice where several statements hold the caret) by C#'s one parse
/// (<see cref="CodeMoves"/>), each one step of Undo, and Auto-Indent Lines as Vim's = lays lines out. In a file of another
/// language Move Statement moves the lines, as Move Line does, and Move Element a list's elements in brackets, or in XML a
/// tag's attributes. Overwrite and column selection are the window's, in all its editors at once.</summary>
public partial class MainWindow
{
    private void AddCodeMoves()
    {
        Add(A.MoveStatementUp, ActionTitles.MoveStatementUp, ActionGroups.Editor, _ => MoveStatement(up: true), WhyNoChange, ("Code", 3, 0), words: ["Move Member Up"]);
        Add(A.MoveStatementDown, ActionTitles.MoveStatementDown, ActionGroups.Editor, _ => MoveStatement(up: false), WhyNoChange, ("Code", 3, 1), words: ["Move Member Down"]);
        Add(A.MoveElementLeft, ActionTitles.MoveElementLeft, ActionGroups.Editor, _ => MoveElement(right: false), WhyNoChange, ("Code", 3, 2),
            words: ["Move Argument Left", "Move Parameter Left", "Move Attribute Left"]);
        Add(A.MoveElementRight, ActionTitles.MoveElementRight, ActionGroups.Editor, _ => MoveElement(right: true), WhyNoChange, ("Code", 3, 3),
            words: ["Move Argument Right", "Move Parameter Right", "Move Attribute Right"]);
        Add(A.Unwrap, ActionTitles.Unwrap, ActionGroups.Editor, _ => Unwrap(), WhyNoCSharpChange, ("Code", 3, 4), words: ["Remove Enclosing Statement", "Unwrap/Remove"]);
        FollowModes();
        Add(A.AutoIndentLines, ActionTitles.AutoIndentLines, ActionGroups.Editor, _ => IdeErrors.Observe(AutoIndentLinesAsync()), context => actions!.WhyNot(A.Reformat, context),
            ("Code", 3, 5), words: ["Reindent", "Indent Lines"]);
    }

    /// <summary>Overwrite and column selection are the window's, one for all its editors: turned on or off in one, each other
    /// follows, and an editor a split makes takes them as they stand.</summary>
    private void FollowModes()
    {
        bool overwrite = false;
        bool columns = false;
        bool following = false;
        Editors.ForEachEditor(editor =>
        {
            editor.Overwrite = overwrite;
            editor.ColumnMode = columns;
            editor.ModesChanged += (_, _) =>
            {
                if (following)
                {
                    return;
                }
                (overwrite, columns, following) = (editor.Overwrite, editor.ColumnMode, true);
                try
                {
                    foreach (EditorGroup group in Editors.Groups)
                    {
                        group.Editor.Overwrite = overwrite;
                        group.Editor.ColumnMode = columns;
                    }
                }
                finally
                {
                    following = false;
                }
            };
        });
    }

    /// <summary>Why the file shown takes no change: none is, or it is read-only.</summary>
    private static Words? WhyNoChange(ActionContext context) => Refusals.NeedsFile(context) ?? context.ReadOnlyWhy;

    /// <summary>Why the file shown takes no change by C#'s structure.</summary>
    private Words? WhyNoCSharpChange(ActionContext context) => WhyNoCSharpStructure(context) ?? context.ReadOnlyWhy;

    private void MoveStatement(bool up)
    {
        CodeEditor editor = Editors.CodeEditor;
        if (Editors.Active?.Syntax?.Current is not { } csharp)
        {
            editor.TryExecute(up ? EditorActions.MoveLineUp : EditorActions.MoveLineDown);
            return;
        }
        if (CodeMoves.StatementMoveOf(csharp, editor.Selections.Primary.Range, up) is not { } move)
        {
            Refuse(up ? Refusals.NoStatementAbove : Refusals.NoStatementBelow);
            return;
        }
        editor.ApplyPlan(move.Beside is { } beside
            ? TextEditing.SwapLines(csharp.Text, editor.Selections, move.Moving, beside)
            : TextEditing.MoveLines(csharp.Text, editor.Selections, move.Moving, move.Before, move.Indent!, move.Strings));
    }

    /// <summary>Move Element: in C#, by its parse; elsewhere the element of the innermost list in brackets around the caret
    /// (a XAML markup extension's, a JSON array's, a call's arguments), else in XML the attribute of the tag the caret is in.</summary>
    private void MoveElement(bool right)
    {
        CodeEditor editor = Editors.CodeEditor;
        if (Editors.Active is not { } shown)
        {
            return;
        }
        TextSnapshot text = editor.Document.Snapshot;
        int caret = editor.Selections.Primary.Caret;
        (TextRange Moving, TextRange Beside)? found = shown.Syntax?.Current is { } csharp ? CodeMoves.ElementSpans(csharp, caret, right)
            : ListElements.Spans(text, editor.Highlighter, caret, right)
                ?? (shown.Language.Markup == MarkupKind.Xml ? Passenger.Xaml.Syntax.XmlTree.Parse(text.ToString()).AttributeSpans(caret, right) : null);
        if (found is not { } spans)
        {
            Refuse(right ? Refusals.NoElementRight : Refusals.NoElementLeft);
            return;
        }
        (TextRange moving, TextRange beside) = spans;
        string movingText = text.GetText(moving.Start, moving.Length);
        string besideText = text.GetText(beside.Start, beside.Length);
        TextEdit[] edits = right
            ? [new TextEdit(moving.Start, moving.Length, besideText), new TextEdit(beside.Start, beside.Length, movingText)]
            : [new TextEdit(beside.Start, beside.Length, movingText), new TextEdit(moving.Start, moving.Length, besideText)];
        // The caret goes with the element, where it stood in it: before the other's text on the left, past it on the right.
        int to = (right ? beside.Start + besideText.Length - moving.Length : beside.Start) + (caret - moving.Start);
        editor.ApplyEdits(edits, SelectionSet.Single(to));
    }

    /// <summary>Unwrap: the one statement around the caret holding others taken out, or, where several hold it, the one chosen
    /// from a list at the caret, innermost first.</summary>
    private void Unwrap()
    {
        CodeEditor editor = Editors.CodeEditor;
        if (Editors.Active is not { Syntax.Current: { } csharp } shown)
        {
            return;
        }
        IReadOnlyList<Unwrapping> choices = CodeMoves.UnwrapChoices(csharp, editor.Selections.Primary.Caret);
        if (choices.Count == 0)
        {
            Refuse(Refusals.NothingToUnwrap);
            return;
        }
        if (choices.Count == 1)
        {
            Unwrap(editor, csharp, choices[0]);
            return;
        }
        Assist.OfferAtCaret(TitleOf(A.Unwrap), [.. choices.Select(c => new CSharp.Choice($"Unwrap '{c.Name}'", c.Header, c, DetailIsCode: true))], chosen =>
        {
            // The file as it stands once chosen: a choice made from an older parse takes nothing out.
            if (chosen?.Value is Unwrapping unwrapping && Editors.Active == shown && ReferenceEquals(shown.Syntax?.Current, csharp))
            {
                Unwrap(Editors.CodeEditor, csharp, unwrapping);
            }
            return Task.CompletedTask;
        });
    }

    private void Unwrap(CodeEditor editor, ParsedText csharp, Unwrapping unwrapping)
    {
        editor.ApplyPlan(TextEditing.Unwrapped(csharp.Text, editor.Selections, unwrapping.Whole, unwrapping.Kept, unwrapping.Strings));
        status.Text = $"Unwrapped {unwrapping.Name}";
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
