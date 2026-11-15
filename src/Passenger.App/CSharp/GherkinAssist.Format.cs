using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace.Gherkin;

namespace Passenger.App.CSharp;

internal sealed partial class GherkinAssist
{
    /// <summary>Reformat Code of a .feature file (<see cref="GherkinFormatter"/>) by its .editorconfig's <c>gherkin_*</c> keys
    /// and its indentation, only the lines <paramref name="within"/> touches when given, as one undoable edit.</summary>
    public override Task<CodeActions.Reformatted?> FormatAsync(OpenDocument document, TextRange? within) =>
        IsFeature(document) ? ReformatAsync(document, within, "Reformat Code") : Task.FromResult<CodeActions.Reformatted?>(null);

    private async Task<CodeActions.Reformatted?> ReformatAsync(OpenDocument document, TextRange? within, string what)
    {
        if (CodeActions.Reformatted.ReadOnly(document, what) is { } refused)
        {
            return refused;
        }
        using ChangeTracker asked = new(document.Text);
        TextSnapshot snapshot = asked.Snapshot;
        IndentOptions indent = document.Indentation.Options;
        string path = document.Path;
        string language = Bdd?.Checks.Of(document)?.Reading.Dialect.Language ?? GherkinDialects.English;
        List<TextEdit> edits = await BackgroundWork.Run(() =>
        {
            EditorConfig config = EditorConfig.For(path);
            GherkinFormatOptions options = GherkinFormatOptions.Of(key => config[key], indent.UseTabs ? "\t" : new string(' ', indent.Size));
            return LineEdits(snapshot, GherkinFormatter.Format(snapshot.ToString(), options, language), within);
        });
        return CodeActions.Reformatted.Apply(context.Editors, document, asked, edits, what);
    }

    /// <summary>The lines of a text that differ in its laid-out text (which keeps its lines), each replaced whole but its
    /// break; only those a range touches where given.</summary>
    private static List<TextEdit> LineEdits(TextSnapshot text, string laid, TextRange? within)
    {
        string[] lines = laid.Split('\n');
        int first = within is { } range ? text.GetLineFromOffset(range.Start) : 0;
        int last = within is { } until ? text.GetLineFromOffset(until.End) : text.LineCount - 1;
        List<TextEdit> edits = [];
        for (int line = first; line <= last && line < lines.Length && line < text.LineCount; line++)
        {
            string written = text.GetLineText(line);
            string now = lines[line];
            if (written != now)
            {
                edits.Add(new TextEdit(text.GetLineStart(line), written.Length, now));
            }
        }
        return edits;
    }

    /// <summary>A line broken at the end of a table's row in the feature shown (Enter, which types no text) lays out that
    /// table once the change has landed, as Reqnroll's editors do; Undo's and Redo's own changes never.</summary>
    private void FollowLineBreaks(OpenDocument document)
    {
        if (!IsFeature(document))
        {
            return;
        }
        document.Text.Changed += (_, e) =>
        {
            if (e.Reason != ChangeReason.Edit || context.Editors.Active != document || e.Edits is not [{ Length: 0, Text: var put } edit]
                || !put.Contains('\n', StringComparison.Ordinal) || put.Trim().Length > 0)
            {
                return;
            }
            int row = e.Before.GetLineFromOffset(edit.Start);
            string ended = e.Before.GetLineText(row);
            if (ended.TrimStart().StartsWith('|') && e.Before.GetText(edit.Start, e.Before.GetLine(row).End - edit.Start).Trim().Length == 0)
            {
                UiThread.Post(() =>
                {
                    if (context.Editors.Active == document)
                    {
                        AlignTable(document, row, keepCaret: false);
                    }
                });
            }
        };
    }

    /// <summary>A bar typed in a table's row lays out that table, as Reqnroll's editors do.</summary>
    private void AlignTable(OpenDocument document) => AlignTable(document, document.Text.Snapshot.GetLineFromOffset(context.Caret), keepCaret: true);

    /// <summary>The table a line is a row of laid out, the caret kept after the bar it was after where asked.</summary>
    private void AlignTable(OpenDocument document, int line, bool keepCaret)
    {
        TextSnapshot text = document.Text.Snapshot;
        if (line >= text.LineCount || !text.GetLineText(line).TrimStart().StartsWith('|'))
        {
            return;
        }
        int top = line;
        while (top > 0 && text.GetLineText(top - 1).TrimStart().StartsWith('|'))
        {
            top--;
        }
        int bottom = line;
        while (bottom + 1 < text.LineCount && text.GetLineText(bottom + 1).TrimStart().StartsWith('|'))
        {
            bottom++;
        }
        TextRange table = new(text.GetLineStart(top), text.GetLine(bottom).End - text.GetLineStart(top));
        IdeErrors.Observe(keepCaret ? AlignedAsync(document, table) : ReformatAsync(document, table, "Aligning the table"));
    }

    private async Task AlignedAsync(OpenDocument document, TextRange table)
    {
        int caret = context.Caret;
        TextSnapshot before = document.Text.Snapshot;
        int line = before.GetLineFromOffset(caret);
        int bars = before.GetText(before.GetLineStart(line), caret - before.GetLineStart(line)).Count(c => c == '|');
        await ReformatAsync(document, table, "Aligning the table");
        // The caret stays after the bar it was after, wherever the bars moved.
        if (context.Editors.Active == document)
        {
            TextSnapshot after = document.Text.Snapshot;
            string written = after.GetLineText(line);
            int at = 0;
            for (int seen = 0; at < written.Length && seen < bars; at++)
            {
                seen += written[at] == '|' ? 1 : 0;
            }
            context.Editor.Selections = SelectionSet.Single(after.GetLineStart(line) + Math.Min(at, written.Length));
        }
    }
}
