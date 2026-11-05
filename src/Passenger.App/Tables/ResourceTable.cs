using Passenger.Text;
using Passenger.Xaml.Localization;

namespace Passenger.App.Tables;

/// <summary>A language's file of strings as a table, read by <see cref="ResourceFiles"/> from one version of its text: a
/// .resx's keys with their values and comments, written in place, and a row below them standing empty for a new key; an
/// XLIFF file's units with their sources, targets, states and notes, of which the target is written.</summary>
public sealed class ResourceTable : Table
{
    /// <summary>A .resx's columns.</summary>
    public const int Name = 0;
    public const int Value = 1;
    public const int Comment = 2;

    /// <summary>An XLIFF file's column of its targets.</summary>
    public const int Target = 2;

    private static readonly string[] ResxColumns = ["Name", "Value", "Comment"];
    private static readonly string[] XliffColumns = ["ID", "Source", "Target", "State", "Note"];

    private ResourceTable(ResourceFile file, TextSnapshot text)
    {
        File = file;
        Text = text;
    }

    /// <summary>The file as read.</summary>
    public ResourceFile File { get; }

    /// <summary>The version of the text it was read from.</summary>
    public TextSnapshot Text { get; }

    public bool IsResx => File.Format == ResourceFormat.Resx;

    /// <summary>A .resx's or an XLIFF file's table read from a version of its text.</summary>
    public static ResourceTable Read(string path, TextSnapshot text) =>
        new(ResourceFiles.Parse(path, string.Empty, path.EndsWith(".xlf", StringComparison.OrdinalIgnoreCase) ? ResourceFormat.Xliff : ResourceFormat.Resx, text.ToString()), text);

    public override int RowCount => File.Entries.Count + (IsResx ? 1 : 0);

    public override int ColumnCount => IsResx ? ResxColumns.Length : XliffColumns.Length;

    public override string Header(int column) => IsResx ? ResxColumns[column] : XliffColumns[column];

    /// <summary>The entry a row of the file's order is, null for the row standing for a new key.</summary>
    public ResourceEntry? Entry(int row) => row < File.Entries.Count ? File.Entries[row] : null;

    public override string Cell(int row, int column)
    {
        if (Entry(row) is not { } entry)
        {
            return string.Empty;
        }
        return (IsResx ? column switch
        {
            Name => entry.Name,
            Value => entry.Value,
            _ => entry.Comment,
        } : column switch
        {
            0 => entry.Name,
            1 => entry.Source,
            Target => entry.Value,
            3 => entry.State,
            _ => entry.Comment,
        }) ?? string.Empty;
    }

    public override bool IsNumber(int row, int column) => false;

    public override CellValue ValueOf(int row, int column) => new(Cell(row, column).Length == 0 ? CellKind.Empty : CellKind.Text);

    public override string RowLabel(int row) => IsNewRow(row) ? "+" : base.RowLabel(row);

    public override bool IsNewRow(int row) => IsResx && row == File.Entries.Count;

    public override string? DeleteTitle(int rows) => IsResx ? rows == 1 ? "Delete Key" : "Delete Keys" : null;

    /// <summary>A .resx's every cell, but the new row's value and comment until it has its name; an XLIFF unit's target.</summary>
    public override bool IsEditable(int row, int column) => IsResx ? !IsNewRow(row) || column == Name : column == Target;
}
