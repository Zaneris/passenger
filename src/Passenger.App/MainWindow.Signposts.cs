using Passenger.App.CSharp;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App;

public partial class MainWindow
{
    /// <summary>How many lines below its signpost a declaration's name is looked for: past its attributes.</summary>
    private const int SignpostReach = 24;

    /// <summary>The signposts above the declarations of the C# files shown.</summary>
    private Signposts? signposts;

    private void AddSignposts()
    {
        signposts = new Signposts(Editors, () => session.CSharp, session.Vcs, settings);
        assist.AboveWords = Signposts.HoverOf;
        Editors.ForEachEditor(editor => editor.RowAbovePressed += (_, press) => IdeErrors.Observe(PressSignpostAsync(editor, press)));
    }

    /// <summary>A press on a signpost's word: its usages, the places its count counts, in Show Usages' popup under its
    /// declaration's name; what implements, overrides or derives from it, as Go to Implementation shows them from that name;
    /// its authors' newest commit, chosen in the Git window's log, or its complexity increments at the ends of lines.</summary>
    private async Task PressSignpostAsync(CodeEditor editor, RowAbovePress press)
    {
        if (press.Context)
        {
            return;
        }
        if (press.Word.Data is ComplexityWord complexity)
        {
            signposts?.ToggleComplexity(editor.Document, complexity);
            return;
        }
        if (press.Word.Data is not SignpostWord word)
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
        // The declaration's own name where the row is of the text as it stands, never a word of its attributes above it.
        if ((word.Version == editor.Document.Version ? word.NameOffset : NameBelow(editor.Document.Snapshot, press.Line, word.Name)) is not int name)
        {
            return;
        }
        editor.Selections = SelectionSet.Single(name);
        if (word.Kind == SignpostKind.Inheriting)
        {
            Perform(AppActions.GoToImplementation);
            return;
        }
        if (Editors.Documents.FirstOrDefault(d => d.Text == editor.Document) is not { } document || session.CSharp?.For(document)?.Lsp is not { } lsp)
        {
            return;
        }
        usagesAsked = new UsagesAsked(editor, editor.Document.Snapshot, editor.Selections, popups.Shown);
        IReadOnlyList<Passenger.Roslyn.Extension.SignpostCount> counted;
        try
        {
            counted = await lsp.SignpostsAsync([name], usages: true, inheriting: false, places: true);
        }
        catch (LanguageServerFailed failed)
        {
            status.Text = $"The usages of {word.Name} could not be read: {failed.Reason}";
            return;
        }
        if (counted is not [{ Places: { } places }])
        {
            return;
        }
        // The list and its count are one answer: a count of another version of the code is counted again.
        if (SignpostDeclarations.Usages(places.Count) != press.Word.Text)
        {
            signposts?.Recount();
        }
        await ShowUsagesAsync(new UsagesFound($"Usages of {word.Name}", [.. places.Select(Located)], null, name));

        static LspLocation Located(Passenger.Roslyn.Extension.UriSpan place) =>
            new(place.Uri, new LspRange(new LspPosition(place.Start.Line, place.Start.Character), new LspPosition(place.End.Line, place.End.Character)));
    }

    /// <summary>Where a declaration's name first stands as a whole word from the start of the line its signpost heads, a few
    /// lines on at most, for a row of a text since changed; null where it is not there.</summary>
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
