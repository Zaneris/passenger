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
        if (document.IsReadOnly)
        {
            return new CodeActions.Reformatted(false, false, $"{what} was not applied: {Refusals.ReadOnly(document.Name)}");
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
        if (edits.Count == 0 || !context.Editors.Documents.Contains(document))
        {
            return new CodeActions.Reformatted(true, false, "Already formatted");
        }
        if (asked.Map(edits) is not { } mapped)
        {
            return new CodeActions.Reformatted(false, false, $"{what} was not applied: the text it changes was edited meanwhile");
        }
        WorkspaceEdits.ApplyTo(context.Editors, document, mapped);
        return new CodeActions.Reformatted(true, true, "Reformatted");
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

    /// <summary>A bar typed in a table's row lays out that table, as Reqnroll's editors do.</summary>
    private void AlignTable(OpenDocument document)
    {
        TextSnapshot text = document.Text.Snapshot;
        int line = text.GetLineFromOffset(context.Caret);
        if (!text.GetLineText(line).TrimStart().StartsWith('|'))
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
        IdeErrors.Observe(AlignedAsync(document, table));
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
