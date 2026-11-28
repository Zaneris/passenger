namespace Passenger.App;

/// <summary>Which regions a file opens folded: those it was folded with when it last closed (kept with the folder,
/// after a restart too, in its session's <see cref="ProjectSession.Folds"/>), else those Settings' Code Folding folds
/// by default. Code, Folding's actions are the editor's own.</summary>
public partial class MainWindow
{
    private void AddFolding()
    {
        Editors.Opened += (_, document) =>
        {
            document.FoldsAtFirst = session.Folds.Of(document.Path) ?? (region => settings.FoldsByDefault(region.Kind));
            document.OwnFoldsAtFirst = session.Folds.OwnOf(document.Path);
        };
        Editors.Closed += (_, document) => session.Folds.Remember(document);
    }

    /// <summary>Collapse Selection: the selection folded as a fold of the person's own.</summary>
    private void CollapseSelection()
    {
        if (Editors.Active is not Workspace.OpenDocument document)
        {
            return;
        }
        Passenger.Text.Selection now = Editors.CodeEditor.Selections.Primary;
        if (now.IsEmpty)
        {
            Refuse(Refusals.NothingSelectedToFold);
            return;
        }
        // The caret stands before the fold, as one left inside it would open it again.
        Editors.CodeEditor.Selections = Passenger.Text.SelectionSet.Single(now.Start);
        document.FoldOwn(now.Range);
    }

    /// <summary>Collapse Block: the block around the caret folded as a fold of the person's own, C#'s braces by its parse,
    /// another language's as its colours pair them.</summary>
    private void CollapseBlock()
    {
        if (Editors.Active is not Workspace.OpenDocument document)
        {
            return;
        }
        Passenger.Editor.CodeEditor editor = Editors.CodeEditor;
        int caret = editor.Selections.Primary.Caret;
        Passenger.Text.TextRange? block = document.Syntax?.Current is { } csharp
            ? Languages.CSharp.CodeStructure.Block(csharp, caret, end: false)
            : BracesAround(editor, caret, end: false);
        if (block is { } braces)
        {
            editor.Selections = Passenger.Text.SelectionSet.Single(braces.Start);
            document.FoldOwn(braces);
        }
        else
        {
            Refuse(Refusals.NoBlockHere);
        }
    }
}
