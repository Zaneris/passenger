using System.Runtime.CompilerServices;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Xaml.Localization;

namespace Passenger.App.Tables;

/// <summary>An open CSV, TSV, Excel or resource (.resx, .xlf) file as a table: one per open file, shared by the editors that
/// show it. A CSV's or a resource's table is read again from its text as typing pauses, so the Text view's edits show in
/// the Table's; a workbook's sheet is read as it is chosen, and again when the file changed on disk since. Its rows sort by
/// a column, in the view alone, and a table read again is sorted so too. Every read and sort is off the UI thread. A
/// resource's cells written in the table change its text through <see cref="ResourceFiles"/>, each one edit Undo takes
/// back.</summary>
public sealed class TableFile
{
    /// <summary>How long typing in a CSV pauses before its table is read again.</summary>
    private static readonly TimeSpan TypingSettle = TimeSpan.FromMilliseconds(300);

    private static readonly ConditionalWeakTable<OpenDocument, TableFile> Files = [];

    private readonly OpenDocument document;
    private readonly Debounce typing;
    private readonly Queue<Task> holds = [];
    private Workbook? book;
    private DateTime readAt;

    /// <summary>Reads begun, which numbers each, never going back: only the newest one's answer stands.</summary>
    private int reads;

    /// <summary>Reads begun that have not landed yet, the newest or not.</summary>
    private int reading;
    private bool stale = true;
    private bool header = true;
    private int sheet;

    /// <summary>The column the rows are sorted by and which way, null in the file's order.</summary>
    private (int Column, bool Descending)? sort;

    private TableFile(OpenDocument document)
    {
        this.document = document;
        IsWorkbook = Workbook.IsWorkbook(document.Path);
        IsResource = ResourceFiles.Holds(document.Path);
        // Read while the table shows; the Text view's edits are read as the Table is shown again.
        typing = new Debounce(TypingSettle, () => ShowsTable && stale ? ReadAsync() : Task.CompletedTask);
        if (!IsWorkbook)
        {
            document.Text.Changed += (_, _) =>
            {
                stale = true;
                typing.Restart();
            };
        }
    }

    /// <summary>Whether a file opens as a table: a CSV or TSV, a workbook, or a resource's .resx or XLIFF file.</summary>
    public static bool IsTable(string path) =>
        Workbook.IsWorkbook(path) || ResourceFiles.Holds(path) || Path.GetExtension(path).ToLowerInvariant() is ".csv" or ".tsv";

    /// <summary>An open file's table, made as it is first asked for; null for a file that is no table.</summary>
    public static TableFile? For(OpenDocument document) =>
        IsTable(document.Path) ? Files.GetValue(document, d => new TableFile(d)) : null;

    /// <summary>Raised on the UI thread as the table, the sheets, the problem or the choices change.</summary>
    public event EventHandler? Changed;

    public bool IsWorkbook { get; }

    /// <summary>Whether it is a resource's .resx or XLIFF file, whose cells are written in the table.</summary>
    public bool IsResource { get; }

    /// <summary>Whether its first row may name its columns: a CSV's alone.</summary>
    public bool HasHeaderChoice => !IsWorkbook && !IsResource;

    /// <summary>The table as last read; null before the first read lands, or when it failed.</summary>
    public Table? Table { get; private set; }

    /// <summary>Why the file could not be read as a table, or null.</summary>
    public string? Problem { get; private set; }

    public bool IsReading => reading > 0;

    /// <summary>A workbook's sheets' names; none for a CSV.</summary>
    public IReadOnlyList<string> Sheets => book?.Sheets.Select(s => s.Name).ToList() ?? [];

    /// <summary>Whether a CSV shows as its table, else as its text; a workbook always shows as its table.</summary>
    public bool ShowsTable { get; private set; } = true;

    /// <summary>Whether a CSV's first record names its columns.</summary>
    public bool HeaderRow
    {
        get => header;
        set
        {
            if (value != header)
            {
                header = value;
                stale = true;
                IdeErrors.Observe(ReadAsync());
            }
        }
    }

    /// <summary>Sorts the rows by a column one way, or puts them back in the file's order for null. The file is never
    /// changed.</summary>
    public void SortBy((int Column, bool Descending)? wanted)
    {
        sort = wanted;
        if (Table is not { } shown)
        {
            return;
        }
        if (reading > 0 || stale)
        {
            // What is read next is sorted so.
            stale = true;
            IdeErrors.Observe(ReadAsync());
            return;
        }
        IdeErrors.Observe(SortAsync(shown.Unsorted));
    }

    /// <summary>The table as read sorted as <see cref="sort"/> asks, off the UI thread; a sort past its columns forgotten.</summary>
    private async Task SortAsync(Table unsorted)
    {
        int read = ++reads;
        reading++;
        (int Column, bool Descending)? by = sort;
        try
        {
            Table sorted = await BackgroundWork.Run(() => Sorted(unsorted, by));
            if (read == reads)
            {
                Table = sorted;
            }
        }
        finally
        {
            reading--;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static Table Sorted(Table table, (int Column, bool Descending)? by) =>
        by is { } order && order.Column < table.ColumnCount ? TableSort.By(table, order.Column, order.Descending) : table;

    /// <summary>The workbook's sheet shown, by its place among <see cref="Sheets"/>.</summary>
    public int Sheet
    {
        get => sheet;
        set
        {
            if (value != sheet && book is not null && value >= 0 && value < book.Sheets.Count)
            {
                sheet = value;
                // Another sheet's columns are others.
                sort = null;
                stale = true;
                IdeErrors.Observe(ReadAsync());
            }
        }
    }

    public void ShowTable(bool table)
    {
        if (IsWorkbook || table == ShowsTable)
        {
            return;
        }
        ShowsTable = table;
        Changed?.Invoke(this, EventArgs.Empty);
        Follow();
    }

    /// <summary>The table is about to be shown: read now if it is not as the file stands.</summary>
    public void Follow()
    {
        if (IsWorkbook && book is not null && WrittenAt() != readAt)
        {
            book = null;
            stale = true;
        }
        if (stale && ShowsTable)
        {
            IdeErrors.Observe(ReadAsync());
        }
    }

    /// <summary>Holds each of the next reads back until its task ends, for a test to land reads out of order. A held read is
    /// no background work yet, so the window settles around it; a read past them waits for nothing.</summary>
    public void HoldNext(params Task[] until)
    {
        foreach (Task hold in until)
        {
            holds.Enqueue(hold);
        }
    }

    private DateTime WrittenAt()
    {
        try
        {
            return File.GetLastWriteTimeUtc(document.Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return readAt;
        }
    }

    /// <summary>A resource's cell written in its table as shown (<see cref="CellWrite.Table"/>): its text changed as
    /// <see cref="ResourceFiles"/> writes it, a row standing for a new key made one, the table read again at once. Null once
    /// changed (or where it needs no change); else why not, the text left as it was.</summary>
    public string? Write(CellWrite write)
    {
        if (write.Table.Unsorted is not ResourceTable read || write.Rows is not [int row])
        {
            return null;
        }
        (string text, ResourceFile now) = Now(read);
        if (read.Entry(row) is not { } was)
        {
            string key = write.Text.Trim();
            if (key.Length == 0)
            {
                return null;
            }
            return now.Entry(key) is not null ? $"'{key}' is a key of {document.Name} already"
                : Change(text, ResourceFiles.WithEntry(text, key, string.Empty), $"{document.Name} has no <root> to hold '{key}'");
        }
        if (Again(was, now) is not { } entry)
        {
            return $"'{was.Name}' is no longer in {document.Name}";
        }
        if (!read.IsResx)
        {
            return Change(text, ResourceFiles.WithTarget(text, entry.Name, write.Text, entry.Source ?? entry.Name, entry.Comment), $"'{entry.Name}' is no longer in {document.Name}");
        }
        return write.Column switch
        {
            ResourceTable.Name when write.Text.Trim().Length == 0 => $"A key of {document.Name} needs a name",
            ResourceTable.Name => Change(text, ResourceFiles.Renamed(text, entry, write.Text.Trim()), $"'{write.Text.Trim()}' is a key of {document.Name} already"),
            ResourceTable.Value => Change(text, ResourceFiles.WithValue(text, entry, write.Text), $"'{entry.Name}' is no longer in {document.Name}"),
            _ => Change(text, ResourceFiles.WithComment(text, entry, write.Text), $"'{entry.Name}' is no longer in {document.Name}"),
        };
    }

    /// <summary>A resource's keys taken out of its text, in one edit; null once taken, else why not.</summary>
    public string? Delete(CellWrite rows)
    {
        if (rows.Table.Unsorted is not ResourceTable { IsResx: true } read)
        {
            return null;
        }
        (string text, ResourceFile now) = Now(read);
        List<ResourceEntry> gone = [];
        foreach (int row in rows.Rows)
        {
            if (read.Entry(row) is { } was)
            {
                if (Again(was, now) is not { } entry)
                {
                    return $"'{was.Name}' is no longer in {document.Name}";
                }
                gone.Add(entry);
            }
        }
        return gone.Count == 0 ? null : Change(text, ResourceFiles.Without(text, gone), $"{document.Name} changed meanwhile");
    }

    /// <summary>The text as it is now and its entries, those of the table shown where it was read from this very text.</summary>
    private (string Text, ResourceFile File) Now(ResourceTable read)
    {
        TextSnapshot snapshot = document.Text.Snapshot;
        string text = snapshot.ToString();
        return (text, ReferenceEquals(snapshot, read.Text) ? read.File : ResourceFiles.Parse(document.Path, string.Empty, read.File.Format, text));
    }

    /// <summary>An entry of the table shown as the text has it now: itself, else the first of its name.</summary>
    private static ResourceEntry? Again(ResourceEntry was, ResourceFile now) =>
        now.Entries.FirstOrDefault(e => e == was) ?? now.Entry(was.Name);

    /// <summary>The text made the one given, as one edit of the open file, and the table read from it; a read-only file's
    /// refusal, or what is said when there was nothing to make.</summary>
    private string? Change(string text, string? made, string refused)
    {
        if (document.IsReadOnly)
        {
            return Refusals.ReadOnly(document.Name);
        }
        if (made is null)
        {
            return refused;
        }
        if (made == text)
        {
            return null;
        }
        IReadOnlyList<TextEdit> edits = [TextEdit.Between(text, made)];
        document.Text.Edit(edits, document.Selections, TextEditing.MapThrough(document.Selections, edits));
        stale = true;
        typing.Stop();
        IdeErrors.Observe(ReadAsync());
        return null;
    }

    private async Task ReadAsync()
    {
        stale = false;
        int read = ++reads;
        reading++;
        Changed?.Invoke(this, EventArgs.Empty);
        string path = document.Path;
        Workbook? known = book;
        int chosen = sheet;
        bool named = header;
        (int Column, bool Descending)? by = sort;
        Passenger.Text.TextSnapshot text = document.Text.Snapshot;
        try
        {
            if (holds.TryDequeue(out Task? hold))
            {
                await hold;
            }
            (Workbook? opened, Table table, DateTime at) = await BackgroundWork.Run(() =>
            {
                if (IsResource)
                {
                    return ((Workbook?)null, Sorted(ResourceTable.Read(path, text), by), default(DateTime));
                }
                if (!IsWorkbook)
                {
                    string all = text.ToString();
                    return ((Workbook?)null, Sorted(CsvTable.Parse(all, CsvTable.SeparatorFor(path, all), named), by), default(DateTime));
                }
                DateTime written = File.GetLastWriteTimeUtc(path);
                Workbook workbook = known ?? Workbook.Open(path);
                int index = Math.Clamp(chosen, 0, Math.Max(0, workbook.Sheets.Count - 1));
                Table sheetTable = workbook.Sheets.Count == 0 ? CsvTable.Parse(string.Empty, ',', false) : workbook.ReadSheet(index);
                return (workbook, Sorted(sheetTable, by), written);
            });
            // An older read landing after a later one is dropped: the later one's answer is the one that stands.
            if (read == reads)
            {
                book = opened ?? book;
                readAt = at;
                Table = table;
                Problem = null;
            }
        }
        // A file that is no workbook (or a damaged one) says why in the table's place, whatever the reader threw.
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (read == reads)
            {
                Table = null;
                Problem = $"{document.Name} could not be read as a table: {error.Message}";
            }
        }
        finally
        {
            reading--;
        }
        if (read == reads || reading == 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
