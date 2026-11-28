using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.App;

/// <summary>Extend Selection (Ctrl+W, Cmd+Alt+Right on the Mac) and Shrink Selection (Ctrl+Shift+W, Cmd+Alt+Left):
/// every caret's selection grows to the next range of code around it (in XML and XAML, of the markup around it), and
/// shrinks back through the ranges it grew by, until a selection changes some other way.</summary>
public partial class MainWindow
{
    private readonly Stack<SelectionSet> grownFrom = [];
    private SelectionSet? grownTo;

    private void ExtendSelection()
    {
        if (Editors.Active is not OpenDocument document)
        {
            return;
        }
        CodeEditor editor = Editors.CodeEditor;
        SelectionSet now = editor.Selections;
        if (grownTo is null || !now.SameAs(grownTo))
        {
            grownFrom.Clear();
        }
        TextSnapshot text = document.Text.Snapshot;
        ParsedText? csharp = document.Syntax?.Current;
        // XML and XAML grow by their markup, read once for every caret.
        Func<TextRange, IEnumerable<TextRange>>? markup = null;
        if (csharp is null && document.Language.Markup == MarkupKind.Xml)
        {
            Passenger.Xaml.Syntax.XmlTree tree = Passenger.Xaml.Syntax.XmlTree.Parse(text.ToString());
            markup = selection => Passenger.Xaml.Features.XmlSelectionRanges.Around(tree, text, selection);
        }
        SelectionSet wider = now.With(s => SelectionRanges.Extend(text, csharp, s.Range, markup) is { } next ? new Selection(next.Start, next.End) : s);
        if (wider.SameAs(now))
        {
            return;
        }
        grownFrom.Push(now);
        editor.Selections = grownTo = wider;
    }

    private void ShrinkSelection()
    {
        CodeEditor editor = Editors.CodeEditor;
        if (grownTo is null || grownFrom.Count == 0 || !editor.Selections.SameAs(grownTo))
        {
            grownFrom.Clear();
            return;
        }
        editor.Selections = grownTo = grownFrom.Pop();
    }
}
