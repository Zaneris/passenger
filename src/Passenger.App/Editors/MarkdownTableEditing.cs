using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>Gives the inline editor its table blocks, using the same reader and grid as the preview. A grid click
/// selects that cell's source, so editing and saving use the ordinary editor and the original Markdown buffer.</summary>
internal sealed partial class MarkdownTableEditing
{
    private static readonly MarkdownPage Page = new(static _ => { });
    private readonly CodeEditor editor;
    private readonly Debounce reading;
    private TextDocument? document;
    private TextDocument? followed;
    private List<Entry> tables = [];
    private CellEditor? active;
    private (int Start, int Row, int Column)? pending;
    private bool writesCell;

    private bool CanEditInline => editor.PresentsMarkdown && followed == editor.Document && !editor.IsReadOnly;

    public MarkdownTableEditing(CodeEditor editor)
    {
        this.editor = editor;
        reading = new Debounce(TypingPauses.Previews, ReadAsync);
        MakeToolbar();
        editor.AttachedToVisualTree += (_, _) => Follow(document);
        editor.DetachedFromVisualTree += (_, _) => Follow(null);
    }

    public void Show(TextDocument? next)
    {
        document = next;
        FollowReadOnly();
        Follow(editor.IsAttachedToVisualTree() ? next : null);
    }

    private void Follow(TextDocument? next)
    {
        if (followed == next)
        {
            return;
        }
        ResetToolbar();
        reading.Stop();
        pending = null;
        CloseCell(present: false);
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
        if (active is { } open && !writesCell && change.Edits.Any(edit => Touches(edit, open.Entry.Source)))
        {
            CloseCell(present: false);
        }
        for (int i = tables.Count - 1; i >= 0; i--)
        {
            Entry entry = tables[i];
            if (writesCell && active?.Entry == entry)
            {
                entry.Source = TextRange.FromBounds(TextEditing.MapOffset(entry.Source.Start, change.Edits, afterInsertions: false),
                    TextEditing.MapOffset(entry.Source.End, change.Edits, afterInsertions: true));
                entry.IsCurrent = false;
                continue;
            }
            if (change.Edits.Any(edit => Touches(edit, entry.Source) && (edit.Start <= entry.Source.Start || edit.End >= entry.Source.End)))
            {
                tables.RemoveAt(i);
                continue;
            }
            entry.IsCurrent &= !change.Edits.Any(edit => edit.Start < entry.Source.End && edit.End > entry.Source.Start);
            entry.Source = TextEditing.MapRange(entry.Source, change.Edits);
        }
        if (!writesCell && active is { Range: { } range } stays)
        {
            stays.Range = TextEditing.MapRange(range, change.Edits);
        }
        Present(change.After);
        reading.Restart();
    }

    private static bool Touches(TextEdit edit, TextRange source) => edit.Start <= source.End && edit.End >= source.Start;

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
            Entry entry = active is { } open && open.Entry.Source.Start == source.Start
                && open.Entry.Table.Rows.Count == table.Rows.Count && open.Entry.Table.Aligns.Count == table.Aligns.Count
                ? open.Entry : kept.TryGetValue(table, out Entry? existing) ? existing : new Entry(this, table);
            entry.Table = table;
            entry.Source = source;
            entry.IsCurrent = true;
            tables.Add(entry);
        }
        if (active is { } gone && !tables.Contains(gone.Entry))
        {
            CloseCell();
        }
        if (pending is { } next)
        {
            if (tables.FirstOrDefault(entry => entry.Source.Start == next.Start) is { } target)
            {
                MarkdownTableSource source = Source(target);
                editor.RevealOffset(source.Line(Math.Clamp(next.Row, 0, source.Rows - 1)).Start);
            }
            else
            {
                pending = null;
            }
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

    private void Present(TextSnapshot snapshot)
    {
        if (followed == editor.Document)
        {
            editor.ShowMarkdownBlocks(snapshot, [.. tables.Select(entry => new MarkdownEditorBlock(
                snapshot.GetLineFromOffset(entry.Source.Start), snapshot.GetLineFromOffset(entry.Source.End) + 1,
                entry.Create, entry.IsCurrent, active?.Entry == entry || (entry.IsCurrent && pending?.Start == entry.Source.Start)))]);
        }
    }

    private MarkdownTableSource Source(Entry entry) => new(editor.Document.Snapshot,
        editor.Document.Snapshot.GetLineFromOffset(entry.Source.Start), entry.Table.Rows.Count + 1, entry.Table.Aligns.Count);

    private Control Make(Entry entry)
    {
        ScrollViewer table = Markdown.TableOf(entry.Table, Page, (row, column, cell) => Open(entry, row, column, cell), editor.LineHeight);
        if (pending?.Start == entry.Source.Start)
        {
            Dispatcher.UIThread.Post(() => OpenPending(entry, table), DispatcherPriority.Loaded);
        }
        return table;
    }

    private void OpenPending(Entry entry, ScrollViewer table)
    {
        if (pending is not { } next || entry.Source.Start != next.Start || !table.IsAttachedToVisualTree())
        {
            return;
        }
        pending = null;
        Grid grid = (Grid)((Border)table.Content!).Child!;
        Open(entry, Math.Clamp(next.Row, 0, entry.Table.Rows.Count), Math.Clamp(next.Column, 0, entry.Table.Aligns.Count - 1), grid);
    }

    private sealed class Entry
    {
        public Entry(MarkdownTableEditing editor, Markdown.Table table)
        {
            Table = table;
            Create = () => editor.Make(this);
        }

        public Markdown.Table Table { get; set; }
        public TextRange Source { get; set; }
        public bool IsCurrent { get; set; }
        public Func<Control> Create { get; }
    }
}
