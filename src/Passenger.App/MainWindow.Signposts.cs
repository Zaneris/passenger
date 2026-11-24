using Passenger.App.CSharp;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App;

public partial class MainWindow
{
    /// <summary>How many lines below its signpost a declaration's name is looked for: past its attributes.</summary>
    private const int SignpostReach = 24;

    /// <summary>The signposts above the declarations of the C# files shown.</summary>
    private Signposts? signposts;

    /// <summary>Whether the Show Usages about to run was asked by a signpost's usages.</summary>
    private bool signpostAsking;

    private void AddSignposts()
    {
        signposts = new Signposts(Editors, () => session.CSharp, session.Vcs, settings);
        assist.AboveWords = Signposts.HoverOf;
        Editors.ForEachEditor(editor => editor.RowAbovePressed += (_, press) => IdeErrors.Observe(PressSignpostAsync(editor, press)));
    }

    /// <summary>A press on a signpost's word: its usages, or what implements, overrides or derives from it, as Show Usages
    /// and Go to Implementation show them from its declaration's name; or its authors' newest commit, chosen in the Git
    /// window's log.</summary>
    private async Task PressSignpostAsync(CodeEditor editor, RowAbovePress press)
    {
        if (press.Context || press.Word.Data is not SignpostWord word)
        {
            return;
        }
        if (word.Kind == SignpostKind.Authors)
        {
            if (word.Commit is { IsCommitted: true } commit)
            {
                ShowToolWindow(ToolWindowIds.Git);
                GitWindow.ShowLog();
                await GitLog.SelectHashAsync(commit.Hash);
            }
            return;
        }
        if (NameBelow(editor.Document.Snapshot, press.Line, word.Name) is not int name)
        {
            return;
        }
        editor.Selections = SelectionSet.Single(name);
        signpostAsking = word.Kind == SignpostKind.Usages;
        Perform(word.Kind == SignpostKind.Usages ? AppActions.ShowUsages : AppActions.GoToImplementation);
        signpostAsking = false;
    }

    /// <summary>Where a declaration's name first stands as a whole word from the start of the line its signpost heads, a few
    /// lines on at most; null where it is not there.</summary>
    private static int? NameBelow(TextSnapshot text, int line, string name)
    {
        for (int at = line; at < Math.Min(text.LineCount, line + SignpostReach); at++)
        {
            string code = text.GetLineText(at);
            for (int found = code.IndexOf(name, StringComparison.Ordinal); found >= 0; found = code.IndexOf(name, found + 1, StringComparison.Ordinal))
            {
                int end = found + name.Length;
                if ((found == 0 || !AssistContext.IsIdentifierChar(code[found - 1])) && (end >= code.Length || !AssistContext.IsIdentifierChar(code[end])))
                {
                    return text.GetLineStart(at) + found;
                }
            }
        }
        return null;
    }
}
