using System.Runtime.CompilerServices;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Tables;

/// <summary>An open CSV, TSV or Excel file as a table: one per open file, shared by the editors that show it. A CSV's table
/// is read again from its text as typing pauses, so the Text view's edits show in the Table's; a workbook's sheet is read
/// as it is chosen, and again when the file changed on disk since. Every read is off the UI thread.</summary>
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

    private TableFile(OpenDocument document)
    {
        this.document = document;
        IsWorkbook = Workbook.IsWorkbook(document.Path);
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

    /// <summary>Whether a file opens as a table: a CSV or TSV, or a workbook.</summary>
    public static bool IsTable(string path) =>
        Workbook.IsWorkbook(path) || Path.GetExtension(path).ToLowerInvariant() is ".csv" or ".tsv";

    /// <summary>An open file's table, made as it is first asked for; null for a file that is no table.</summary>
    public static TableFile? For(OpenDocument document) =>
        IsTable(document.Path) ? Files.GetValue(document, d => new TableFile(d)) : null;

    /// <summary>Raised on the UI thread as the table, the sheets, the problem or the choices change.</summary>
    public event EventHandler? Changed;

    public bool IsWorkbook { get; }

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

    /// <summary>The workbook's sheet shown, by its place among <see cref="Sheets"/>.</summary>
    public int Sheet
    {
        get => sheet;
        set
        {
            if (value != sheet && book is not null && value >= 0 && value < book.Sheets.Count)
            {
                sheet = value;
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
        Passenger.Text.TextSnapshot text = document.Text.Snapshot;
        try
        {
            if (holds.TryDequeue(out Task? hold))
            {
                await hold;
            }
            (Workbook? opened, Table table, DateTime at) = await BackgroundWork.Run(() =>
            {
                if (!IsWorkbook)
                {
                    string all = text.ToString();
                    return ((Workbook?)null, (Table)CsvTable.Parse(all, CsvTable.SeparatorFor(path, all), named), default(DateTime));
                }
                DateTime written = File.GetLastWriteTimeUtc(path);
                Workbook workbook = known ?? Workbook.Open(path);
                int index = Math.Clamp(chosen, 0, Math.Max(0, workbook.Sheets.Count - 1));
                Table sheetTable = workbook.Sheets.Count == 0 ? CsvTable.Parse(string.Empty, ',', false) : workbook.ReadSheet(index);
                return (workbook, sheetTable, written);
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
