using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.App;

/// <summary>Moves by the code's structure, as Rider's keymaps bind them: Method Up and Down, the next and previous usage of
/// the symbol at the caret, the containing declaration gone to or selected, the code block's start and end (moved to, or
/// the selection drawn out to them) and Go to Region. C#'s come from its one parse (<see cref="CodeStructure"/>); a usage
/// is one the editor highlights, in any language a server answers for; a code block in another language is a pair of
/// braces its colours pair.</summary>
public partial class MainWindow
{
    /// <summary>Why a move by C#'s structure has nowhere to go in the file shown.</summary>
    private Words? WhyNoCSharpStructure(ActionContext context) =>
        Refusals.NeedsFile(context) ?? (Editors.Active?.Syntax?.Current is null ? Refusals.NotCSharp : null);

    private void MoveToMember(bool down)
    {
        if (Editors.Active?.Syntax?.Current is not { } csharp)
        {
            return;
        }
        CodeEditor editor = Editors.CodeEditor;
        if (CodeStructure.Member(csharp, editor.Selections.Primary.Caret, down) is { } start)
        {
            editor.Selections = SelectionSet.Single(start);
        }
    }

    private void MoveToUsage(bool next)
    {
        CodeEditor editor = Editors.CodeEditor;
        int caret = editor.Selections.Primary.Caret;
        IReadOnlyList<UsageHighlight> uses = editor.UsageHighlights;
        UsageHighlight[] beyond = next ? [.. uses.Where(u => u.Range.Start > caret).OrderBy(u => u.Range.Start)]
            : [.. uses.Where(u => u.Range.End < caret).OrderByDescending(u => u.Range.Start)];
        if (uses.Count == 0)
        {
            Refuse(Refusals.NoUsagesHighlighted);
        }
        else if (beyond.Length > 0)
        {
            editor.Selections = SelectionSet.Single(beyond[0].Range.Start);
        }
    }

    private void ToContainingDeclaration(bool select)
    {
        if (Editors.Active?.Syntax?.Current is not { } csharp)
        {
            return;
        }
        CodeEditor editor = Editors.CodeEditor;
        Selection now = editor.Selections.Primary;
        if (CodeStructure.Containing(csharp, now.Range, select) is { } found)
        {
            editor.Selections = select ? SelectionSet.Single(new Selection(found.Whole.Start, found.Whole.End)) : SelectionSet.Single(found.Name);
        }
    }

    /// <summary>Code Block Start or End: the caret to the brace of the block around it, or with <paramref name="extend"/>
    /// the selection drawn out to it from where it was anchored.</summary>
    private void ToCodeBlock(bool end, bool extend)
    {
        if (Editors.Active is not OpenDocument document)
        {
            return;
        }
        CodeEditor editor = Editors.CodeEditor;
        Selection now = editor.Selections.Primary;
        TextRange? block = document.Syntax?.Current is { } csharp ? CodeStructure.Block(csharp, now.Caret, end) : BracesAround(editor, now.Caret, end);
        if (block is not { } braces)
        {
            return;
        }
        int to = end ? braces.End : braces.Start;
        editor.Selections = SelectionSet.Single(now.MoveTo(to, extend));
    }

    /// <summary>The innermost pair of braces around an offset in a file of another language, as its colours pair them; one
    /// the caret stands at already (before the opening, after the closing) gives the pair around it.</summary>
    private static TextRange? BracesAround(CodeEditor editor, int caret, bool end)
    {
        if (editor.Highlighter is not IBraceMatcher pairs)
        {
            return null;
        }
        TextSnapshot text = editor.Document.Snapshot;
        string written = text.ToString();
        for (int at = written.LastIndexOf('{', Math.Max(0, Math.Min(caret, written.Length - 1))); at >= 0; at = at == 0 ? -1 : written.LastIndexOf('{', at - 1))
        {
            if (pairs.PairAt(text, at, far: true) is { } pair && pair.Matching.Start >= caret - (end ? 1 : 0))
            {
                TextRange braces = TextRange.FromBounds(at, pair.Matching.End);
                if ((end ? caret != braces.End : caret != braces.Start) && caret <= braces.End)
                {
                    return braces;
                }
            }
        }
        return null;
    }

    private void GoToRegion()
    {
        if (Editors.Active is not OpenDocument document || document.Syntax?.Current is not { } csharp)
        {
            return;
        }
        IReadOnlyList<(string Name, int Start)> regions = CodeStructure.Regions(csharp);
        Assist.ShowPlaces(TitleOf(AppActions.GoToRegion), document, [.. regions.Select(r => r.Start)], [.. regions.Select(r => r.Name)], Refusals.NoRegions(document.Name));
    }
}
