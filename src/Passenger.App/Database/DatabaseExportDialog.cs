using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Passenger.Core;

namespace Passenger.App.Database;

/// <summary>Export Data: what rows it writes, then the format (CSV with its separator and header row, JSON, or SQL inserts
/// into a table named here, in the connection's SQL), how each format holds what the rows hold, and the file. Export closes
/// with the choice made (<see cref="ExportChoice"/>).</summary>
public sealed class DatabaseExportDialog : IdeWindow
{
    /// <summary>The formats offered, in <see cref="ExportFormat"/>'s order.</summary>
    public static readonly string[] Formats = ["CSV", "JSON", "SQL inserts"];

    /// <summary>A CSV's separators offered, with the character each is.</summary>
    public static readonly (string Title, char Separator)[] Separators = [("Comma", ','), ("Semicolon", ';'), ("Tab", '\t'), ("Vertical bar", '|')];

    private readonly FormDropDown format = new() { Name = "ExportFormat", ItemsSource = Formats, SelectedIndex = 0 };
    private readonly FormDropDown separator = new() { Name = "ExportSeparator", ItemsSource = Separators.Select(s => s.Title).ToArray(), SelectedIndex = 0 };
    private readonly CheckBox header = new() { Name = "ExportHeader", Content = "First row names the columns", IsChecked = true };
    private readonly TextBox table = new() { Name = "ExportTable" };
    private readonly TextBox file = new() { Name = "ExportFile" };
    private readonly TextBlock holds = FormRows.Note();
    private readonly TextBlock problem = new() { Name = "ExportProblem", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid separatorRow;
    private readonly Grid headerRow;
    private readonly Grid tableRow;
    private readonly DatabaseKind kind;
    private readonly string folder;

    /// <param name="stem">The file's name before its extension, as first offered.</param>
    /// <param name="last">The choice the last export made, whose format, separator, header and folder it offers again.</param>
    public DatabaseExportDialog(ExportSource source, string folder, string stem, ExportChoice? last)
    {
        kind = source.Connection.Kind;
        this.folder = folder;
        Title = "Export Data";
        Width = DialogWidths.Form;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FormRows.Scope(this);
        problem.Classes.Add(StyleClasses.Error);
        table.Text = source.Into.Table;
        separatorRow = FormRows.Of("Separator", separator);
        headerRow = FormRows.Of(null, header);
        tableRow = FormRows.Of("Into table", table);
        if (last is not null)
        {
            format.SelectedIndex = (int)last.Format;
            separator.SelectedIndex = Math.Max(0, Array.FindIndex(Separators, s => s.Separator == last.Separator));
            header.IsChecked = last.Header;
        }
        // Offered where the last export went, as a person exporting again most often wants.
        file.Text = Shown(Path.Combine(last is null ? folder : Path.GetDirectoryName(last.Path) ?? folder, FileNames.Safe(stem) + Extension));
        format.SelectionChanged += (_, _) => Follow(changed: true);
        Button export = DialogFooter.Default("Export");
        export.Name = "ExportConfirm";
        export.Click += (_, _) => Export();
        StackPanel fields = new()
        {
            Spacing = FormRows.Between,
            Children =
            {
                FormRows.Of("Format", format, [holds]),
                separatorRow,
                headerRow,
                tableRow,
                FormRows.Of("File", BrowseRow.Of(file, BrowseAsync)),
            },
        };
        TextBlock what = MutedText.Wrapped(Containers.ContainerSecrets.InSaid(source.Said));
        Content = DialogFooter.Headed("Export Data", what, fields, DialogFooter.OkCancel(export, () => Close(null), problem));
        Follow(changed: false);
    }

    /// <summary>The format chosen.</summary>
    public ExportFormat Format => (ExportFormat)Math.Max(0, format.SelectedIndex);

    /// <summary>What the dialog says the format chosen makes of the rows' values.</summary>
    public string Holds => holds.Text ?? string.Empty;

    /// <summary>Why it cannot export as it stands; empty when it can.</summary>
    public string Problem => problem.Text ?? string.Empty;

    /// <summary>A file as the field shows it: from the open folder where it is under it, else whole.</summary>
    private string Shown(string path) => PathNames.Relative(path, folder) ?? path;

    private string Extension => Format switch
    {
        ExportFormat.Csv => ".csv",
        ExportFormat.Json => ".json",
        _ => ".sql",
    };

    /// <summary>The rows shown for the format chosen and what it makes of the values; the file's extension the format's,
    /// where it was one of the formats'.</summary>
    private void Follow(bool changed)
    {
        bool csv = Format == ExportFormat.Csv;
        separatorRow.IsVisible = csv;
        headerRow.IsVisible = csv;
        tableRow.IsVisible = Format == ExportFormat.Inserts;
        holds.Text = Format switch
        {
            ExportFormat.Csv => "UTF-8, a row a line as RFC 4180 has it, a field holding the separator, a quote or a line break quoted. "
                + "NULL is an empty field and empty text \"\"; binary is Base64; dates and times are ISO 8601 (2024-05-01 13:45:00).",
            ExportFormat.Json => "UTF-8, an array of one object a row. NULL is null; numbers, true and false are JSON's own; a JSON column's value is "
                + "itself; binary is Base64 text; dates and times are ISO 8601 text (2024-05-01T13:45:00).",
            _ => $"An INSERT a row in {DatabaseKinds.Title(kind)}'s SQL, which its console runs. NULL stays NULL; text is quoted as it reads it; "
                + "binary is its byte literal; dates and times are text it reads (2024-05-01 13:45:00).",
        };
        if (changed && file.Text is { Length: > 0 } typed && Path.GetExtension(typed).ToLowerInvariant() is ".csv" or ".json" or ".sql")
        {
            file.Text = Path.ChangeExtension(typed, Extension);
        }
    }

    /// <summary>Export: the dialog closed with the choice made; null once closed, else why not, said in it.</summary>
    public string? Export()
    {
        string path = file.Text?.Trim() ?? string.Empty;
        string? why = path.Length == 0 ? Refusals.NoExportFile
            : Format == ExportFormat.Inserts && string.IsNullOrWhiteSpace(table.Text) ? Refusals.NoInsertTableNamed : null;
        if (why is not null)
        {
            problem.Text = why;
            return why;
        }
        Close(new ExportChoice(Format, Separators[Math.Max(0, separator.SelectedIndex)].Separator, header.IsChecked == true, (table.Text ?? string.Empty).Trim(),
            Path.GetFullPath(path, folder)));
        return null;
    }

    private async Task BrowseAsync()
    {
        IStorageFile? picked = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Data",
            SuggestedFileName = Path.GetFileName(file.Text ?? string.Empty),
            DefaultExtension = Extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType(Formats[(int)Format]) { Patterns = ["*" + Extension] }],
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(Path.GetFullPath(file.Text ?? string.Empty, folder)) ?? folder),
        });
        if (picked?.TryGetLocalPath() is { } path)
        {
            file.Text = Shown(PathNames.Canonical(path));
        }
    }
}
