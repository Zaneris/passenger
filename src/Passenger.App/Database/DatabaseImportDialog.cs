using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Passenger.App.Tables;
using Passenger.Core;

namespace Passenger.App.Database;

/// <summary>Import Data: a CSV's or JSON file's rows into a new table or one of the database's, the file read again as its
/// format, separator or header row changes. Its columns go into the table's of their names, or each into one chosen (a new
/// table's named and typed as its first rows propose), its first rows shown as read, and how its text becomes values said.
/// Import closes with the choice made (<see cref="ImportChoice"/>).</summary>
public sealed class DatabaseImportDialog : IdeWindow
{
    /// <summary>The choice of a table to make, first among the tables offered.</summary>
    public const string NewTable = "A new table";

    /// <summary>How many of the file's rows it reads to show them and propose a new table's types.</summary>
    public const int Previewed = 100;

    private readonly DatabaseKind kind;
    private readonly string folder;
    private readonly string schema;
    private readonly IReadOnlyList<DatabaseTable> tables;
    private readonly Func<DatabaseTable, CancellationToken, Task<DatabaseSchema>> columnsOf;
    private readonly TextBox file = new() { Name = "ImportFile" };
    private readonly FormDropDown format = new() { Name = "ImportFormat", ItemsSource = new[] { "CSV", "JSON" }, SelectedIndex = 0 };
    private readonly FormDropDown separator = new() { Name = "ImportSeparator", ItemsSource = DatabaseExportDialog.Separators.Select(s => s.Title).ToArray(), SelectedIndex = 0 };
    private readonly CheckBox header = new() { Name = "ImportHeader", Content = "First row names the columns", IsChecked = true };
    private readonly FormDropDown into = new() { Name = "ImportInto" };
    private readonly TextBox name = new() { Name = "ImportTableName" };
    private readonly ImportColumnsTable columns = new() { Name = "ImportColumns" };
    private readonly CellGrid preview = new() { Name = "ImportPreview", Height = 6 * ListRow.Height };
    private readonly TextBlock holds = FormRows.Note();
    private readonly TextBlock problem = new() { Name = "ImportProblem", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid separatorRow;
    private readonly Grid headerRow;
    private readonly Grid nameRow;
    private readonly CancellationTokenSource closing = new();
    private CancellationTokenSource? reading;
    private ImportPreview? read;
    private DatabaseSchema? target;

    /// <summary>The read of the table chosen last, whose answer alone is taken: an earlier choice's read is cancelled.</summary>
    private CancellationTokenSource? targeting;
    private bool following;
    private bool fileReading;
    private bool tableReading;
    private string? lastNamed;

    /// <param name="schema">The schema a new table is made in (empty for none).</param>
    /// <param name="tables">The tables of the database a file's rows may go into.</param>
    /// <param name="chosen">The table offered first, null for a new one.</param>
    /// <param name="columnsOf">Reads a table's columns.</param>
    public DatabaseImportDialog(DatabaseKind kind, string folder, string schema, IReadOnlyList<DatabaseTable> tables, DatabaseTable? chosen,
        Func<DatabaseTable, CancellationToken, Task<DatabaseSchema>> columnsOf)
    {
        this.kind = kind;
        this.folder = folder;
        this.schema = schema;
        this.tables = tables;
        this.columnsOf = columnsOf;
        Title = "Import Data";
        Width = DialogWidths.Wide;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FormRows.Scope(this);
        problem.Classes.Add(StyleClasses.Error);
        into.ItemsSource = (string[])[NewTable, .. tables.Select(t => t.ToString())];
        into.SelectedIndex = chosen is null ? 0 : Math.Max(0, tables.ToList().IndexOf(chosen) + 1);
        separatorRow = FormRows.Of("Separator", separator);
        headerRow = FormRows.Of(null, header);
        nameRow = FormRows.Of("Table name", name);
        holds.Text = "An empty field of a CSV is NULL and \"\" empty text; JSON's null is NULL. A binary column takes Base64 or 0x-hex; "
            + "dates and times are read as ISO 8601 (2024-05-01 13:45:00). The rows are written in one transaction: the first the database refuses stops "
            + "the import, and nothing is written.";
        Button import = DialogFooter.Default("Import");
        import.Name = "ImportConfirm";
        import.Click += (_, _) => Import();
        file.LostFocus += (_, _) => Reread(fromName: false);
        file.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                Reread(fromName: false);
                e.Handled = true;
            }
        };
        format.SelectionChanged += (_, _) => Reread(fromName: false);
        separator.SelectionChanged += (_, _) => Reread(fromName: false);
        header.IsCheckedChanged += (_, _) => Reread(fromName: false);
        into.SelectionChanged += (_, _) => IdeErrors.Observe(TargetAsync());
        Closed += (_, _) => closing.Cancel();
        StackPanel fields = new()
        {
            Spacing = FormRows.Between,
            Children =
            {
                FormRows.Of("File", BrowseRow.Of(file, BrowseAsync)),
                FormRows.Of("Format", format),
                separatorRow,
                headerRow,
                FormRows.Of("Into", into),
                nameRow,
                FormRows.Of("Columns", columns, top: true),
                FormRows.Of("First rows", new Border { Classes = { StyleClasses.Framed }, Child = preview }, [holds], top: true),
            },
        };
        TextBlock about = MutedText.Wrapped("Reads the rows of a CSV or JSON file into a new table or one of this database's, matching columns by name.");
        Content = DialogFooter.Headed("Import Data", about, fields, DialogFooter.OkCancel(import, () => Close(null), problem));
        columns.Say("Choose a file to read its columns");
        Follow();
        IdeErrors.Observe(TargetAsync());
    }

    /// <summary>The file's path as typed; set, the file is read again.</summary>
    public string File
    {
        get => file.Text ?? string.Empty;
        set
        {
            file.Text = value;
            Reread(fromName: true);
        }
    }

    /// <summary>Whether the file or the table chosen is being read.</summary>
    public bool IsReading => fileReading || tableReading;

    /// <summary>The file's first rows as read, null until they are.</summary>
    public ImportPreview? Preview => read;

    /// <summary>The columns as the table of them stands.</summary>
    public IReadOnlyList<ImportColumn> Columns => columns.Columns;

    /// <summary>Why it cannot import as it stands; empty when it can.</summary>
    public string Problem => problem.Text ?? string.Empty;

    private bool IsNew => into.SelectedIndex <= 0;

    private void Follow()
    {
        bool csv = format.SelectedIndex == 0;
        separatorRow.IsVisible = csv;
        headerRow.IsVisible = csv;
        nameRow.IsVisible = IsNew;
    }

    /// <summary>The file read again as it is named or read otherwise; a name given sets the format, the separator and the new
    /// table's name as the file's own say.</summary>
    private void Reread(bool fromName)
    {
        if (!following && File.Trim() is { Length: > 0 } path)
        {
            string full = Path.GetFullPath(path, folder);
            // A file named anew is read as its own name says, whether set or typed.
            bool named = fromName || !PathNames.Same(full, lastNamed ?? string.Empty);
            lastNamed = full;
            IdeErrors.Observe(ReadAsync(full, named));
        }
    }

    /// <summary>The separator a CSV's first line has most of, as its viewer reads it.</summary>
    private static char SeparatorOf(string path)
    {
        try
        {
            using StreamReader reader = new(Text.FileText.OpenRead(path));
            char[] start = new char[4096];
            int length = reader.Read(start, 0, start.Length);
            return CsvTable.SeparatorFor(path, start.AsSpan(0, length));
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return ',';
        }
    }

    private async Task ReadAsync(string full, bool fromName)
    {
        reading?.Cancel();
        CancellationTokenSource now = reading = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        fileReading = true;
        columns.Say("Reading the file…");
        try
        {
            if (fromName)
            {
                ImportFile named = ImportFile.Of(full, ',');
                char found = named.Json ? ',' : await BackgroundWork.Run(() => SeparatorOf(full), now.Token);
                following = true;
                format.SelectedIndex = named.Json ? 1 : 0;
                separator.SelectedIndex = Math.Max(0, Array.FindIndex(DatabaseExportDialog.Separators, s => s.Separator == found));
                if (string.IsNullOrWhiteSpace(name.Text))
                {
                    name.Text = Path.GetFileNameWithoutExtension(full);
                }
                following = false;
            }
            Follow();
            ImportFile chosen = new(full, format.SelectedIndex == 1, DatabaseExportDialog.Separators[Math.Max(0, separator.SelectedIndex)].Separator, header.IsChecked == true);
            ImportPreview first = await BackgroundWork.Run(() => RowImport.PreviewAsync(chosen, Previewed, now.Token), now.Token);
            if (now != reading)
            {
                return;
            }
            read = first;
            problem.Text = string.Empty;
            preview.Table = new TextRows(first.Columns, first.Rows);
            Fill();
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or NotSupportedException
            or OperationCanceledException)
        {
            following = false;
            if (now == reading && unread is not OperationCanceledException)
            {
                read = null;
                preview.Table = null;
                columns.Say(Containers.ContainerSecrets.InSaid(unread.Message));
            }
        }
        finally
        {
            if (now == reading)
            {
                fileReading = false;
            }
        }
    }

    /// <summary>The table chosen read for its columns, a new one needing none; a read an earlier choice began is let go of,
    /// its answer, however late it comes, never taken for the table chosen since.</summary>
    private async Task TargetAsync()
    {
        Follow();
        target = null;
        targeting?.Cancel();
        CancellationTokenSource now = targeting = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        tableReading = false;
        if (!IsNew && into.SelectedIndex - 1 < tables.Count)
        {
            DatabaseTable table = tables[into.SelectedIndex - 1];
            tableReading = true;
            try
            {
                DatabaseSchema columnsRead = await columnsOf(table, now.Token);
                if (now != targeting)
                {
                    return;
                }
                target = columnsRead;
            }
            catch (Exception unread) when (unread is System.Data.Common.DbException or IOException or InvalidOperationException or OperationCanceledException
                or TimeoutException or System.Net.Sockets.SocketException)
            {
                if (now != targeting)
                {
                    return;
                }
                problem.Text = Containers.ContainerSecrets.InSaid(unread.Message);
            }
            finally
            {
                if (now == targeting)
                {
                    tableReading = false;
                }
            }
        }
        Fill();
    }

    /// <summary>The columns' rows for the file read and the table chosen.</summary>
    private void Fill()
    {
        if (read is not { } first)
        {
            return;
        }
        if (IsNew)
        {
            columns.SetNew(first.Columns, [.. first.Samples.Select(values => ImportTypes.Proposed(values, kind))]);
        }
        else if (target is { } table)
        {
            string[] names = [.. table.Columns.Select(c => c.Name)];
            columns.SetExisting(first.Columns, names, ImportTypes.Mapped(first.Columns, names, header.IsChecked == true || format.SelectedIndex == 1));
        }
    }

    /// <summary>Import: the dialog closed with the choice made; null once closed, else why not, said in it.</summary>
    public string? Import()
    {
        string? why = File.Trim().Length == 0 ? Refusals.NoImportFile
            : read is null || !IsNew && target?.Table != tables[into.SelectedIndex - 1] ? Refusals.ImportNotRead
            : IsNew && string.IsNullOrWhiteSpace(name.Text) ? Refusals.NoNewTableNamed
            : columns.Columns.All(c => c.Into is null) ? Refusals.NoColumnImported : null;
        if (why is not null)
        {
            problem.Text = why;
            return why;
        }
        DatabaseTable table = IsNew ? new DatabaseTable(schema, name.Text!.Trim()) : tables[into.SelectedIndex - 1];
        ImportFile chosen = new(Path.GetFullPath(File.Trim(), folder), format.SelectedIndex == 1, DatabaseExportDialog.Separators[Math.Max(0, separator.SelectedIndex)].Separator,
            header.IsChecked == true);
        Close(new ImportChoice(chosen, table, IsNew, columns.Columns));
        return null;
    }

    private async Task BrowseAsync()
    {
        IReadOnlyList<IStorageFile> picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Data",
            FileTypeFilter = [new FilePickerFileType("CSV and JSON files") { Patterns = ["*.csv", "*.tsv", "*.txt", "*.json", "*.jsonl", "*.ndjson"] }],
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(folder),
        });
        if (picked.Count == 1 && picked[0].TryGetLocalPath() is { } path)
        {
            File = path;
        }
    }
}
