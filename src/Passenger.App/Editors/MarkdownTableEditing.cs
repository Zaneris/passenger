using Avalonia.Controls;
using Avalonia.VisualTree;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>Gives the inline editor its table blocks, using the same reader and grid as the preview. A grid click
/// selects that cell's source, so editing and saving use the ordinary editor and the original Markdown buffer.</summary>
internal sealed class MarkdownTableEditing
{
    private static readonly MarkdownPage Page = new(static _ => { });
    private readonly CodeEditor editor;
    private readonly Debounce reading;
    private TextDocument? document;
    private TextDocument? followed;
    private List<Entry> tables = [];

    public MarkdownTableEditing(CodeEditor editor)
    {
        this.editor = editor;
        reading = new Debounce(TypingPauses.Previews, ReadAsync);
        editor.AttachedToVisualTree += (_, _) => Follow(document);
        editor.DetachedFromVisualTree += (_, _) => Follow(null);
    }

    public void Show(TextDocument? next)
    {
        document = next;
        Follow(editor.IsAttachedToVisualTree() ? next : null);
    }

    private void Follow(TextDocument? next)
    {
        if (followed == next)
        {
            return;
        }
        reading.Stop();
        if (followed is not null)
        {
            followed.Changed -= OnChanged;
        }
        followed = next;
        tables.Clear();
        editor.ShowMarkdownBlocks(null, []);
        if (next is not null)
        {
            next.Changed += OnChanged;
            IdeErrors.Observe(ReadAsync());
        }
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs change)
    {
        for (int i = tables.Count - 1; i >= 0; i--)
        {
            Entry entry = tables[i];
            if (change.Edits.Any(edit => edit.Start <= entry.Source.End && edit.End >= entry.Source.Start
                && (edit.Start <= entry.Source.Start || edit.End >= entry.Source.End)))
            {
                tables.RemoveAt(i);
                continue;
            }
            entry.IsCurrent &= !change.Edits.Any(edit => edit.Start < entry.Source.End && edit.End > entry.Source.Start);
            entry.Source = TextEditing.MapRange(entry.Source, change.Edits);
        }
        Present(change.After);
        reading.Restart();
    }

    private async Task ReadAsync()
    {
        if (followed is not { } shown)
        {
            return;
        }
        TextSnapshot snapshot = shown.Snapshot;
        List<(Markdown.Table Table, TextRange Source)> read = await BackgroundWork.Run(() => TablesOf(shown, snapshot));
        if (followed != shown || !ReferenceEquals(shown.Snapshot, snapshot))
        {
            return;
        }
        Dictionary<Markdown.Table, Entry> kept = new(ReferenceEqualityComparer.Instance);
        foreach (Entry entry in tables)
        {
            kept[entry.Table] = entry;
        }
        tables = new List<Entry>(read.Count);
        foreach ((Markdown.Table table, TextRange source) in read)
        {
            Entry entry = kept.TryGetValue(table, out Entry? existing) ? existing : new Entry(this, table);
            entry.Source = source;
            entry.IsCurrent = true;
            tables.Add(entry);
        }
        Present(snapshot);
    }

    private static List<(Markdown.Table Table, TextRange Source)> TablesOf(TextDocument document, TextSnapshot snapshot)
    {
        Markdown.Parsed read = Markdown.ReadOf(document, snapshot);
        List<(Markdown.Table Table, TextRange Source)> found = [];
        foreach (Markdown.Placed block in read.Placed)
        {
            if (block.Block is not Markdown.Table table)
            {
                continue;
            }
            int start = read.Text.Start(block.Line);
            int end = read.Text.End(block.End - 1);
            int first = snapshot.GetLineFromOffset(start);
            int last = snapshot.GetLineFromOffset(end);
            if (start == snapshot.GetLineStart(first) && end == snapshot.GetLine(last).End && last - first + 1 == block.End - block.Line)
            {
                found.Add((table, TextRange.FromBounds(start, end)));
            }
        }
        return found;
    }

    private void Present(TextSnapshot snapshot) => editor.ShowMarkdownBlocks(snapshot,
        [.. tables.Select(entry => new MarkdownEditorBlock(snapshot.GetLineFromOffset(entry.Source.Start),
            snapshot.GetLineFromOffset(entry.Source.End) + 1, entry.Create, entry.IsCurrent))]);

    private void Edit(Entry entry, int row, int column)
    {
        TextSnapshot snapshot = editor.Document.Snapshot;
        int first = snapshot.GetLineFromOffset(entry.Source.Start);
        LineSpan line = snapshot.GetLine(first + (row == 0 ? 0 : row + 1));
        List<TextRange> cells = Markdown.CellRanges(snapshot.GetText(line.Start, line.Length));
        TextRange cell = column < cells.Count ? cells[column] : new TextRange(line.Length, 0);
        editor.Focus();
        editor.Select(new TextRange(line.Start + cell.Start, cell.Length));
    }

    private sealed class Entry
    {
        public Entry(MarkdownTableEditing editor, Markdown.Table table)
        {
            Table = table;
            Create = () => Markdown.TableOf(Table, Page, (row, column) => editor.Edit(this, row, column), editor.editor.LineHeight);
        }

        public Markdown.Table Table { get; }
        public TextRange Source { get; set; }
        public bool IsCurrent { get; set; }
        public Func<Control> Create { get; }
    }
}
