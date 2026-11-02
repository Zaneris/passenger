using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App.Tables;

/// <summary>An editor group's table: a bar over the editor (a CSV's or a resource's Table and Text, or a workbook's sheets,
/// and how large the table is) and the <see cref="CellGrid"/> laid over the editor while the table shows, a resource's
/// cells written through its file. Each file's chosen cells and scroll are kept for when its tab is shown again.</summary>
public sealed class TableViewer
{
    private readonly StackPanel choices = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox header = new() { Content = "First row is header", FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock size = new() { Classes = { StyleClasses.Muted }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock message = new() { Classes = { StyleClasses.EmptyText }, IsVisible = false };
    private readonly Dictionary<TableFile, ((int, int) Anchor, (int, int) Caret, Vector Scroll)> kept = [];
    private TableFile? file;
    private bool painting;

    /// <summary>A place in a resource's text gone to, whose key's row the table chooses once it is read; null for none.</summary>
    private int? goneTo;

    public TableViewer()
    {
        Bar = ToolWindows.ToolBars.OverEditor(new DockPanel
        {
            Margin = new Thickness(8, 0),
            Children =
            {
                new Border { [DockPanel.DockProperty] = Dock.Right, Child = size, Margin = new Thickness(12, 0, 0, 0) },
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { choices, header } },
                },
            },
        });
        Bar.IsVisible = false;
        header.IsCheckedChanged += (_, _) =>
        {
            if (!painting && file is not null)
            {
                file.HeaderRow = header.IsChecked == true;
            }
        };
        Body = new Panel { IsVisible = false, Children = { View, message } };
        View.SortRequested += (_, wanted) => file?.SortBy(wanted);
        View.CellWritten += (_, write) => Said(file?.Write(write));
        View.RowsDeleted += (_, rows) => Said(file?.Delete(rows));
    }

    /// <summary>Raised with why a cell written or a row taken out did not change the file.</summary>
    public event EventHandler<string>? Refused;

    private void Said(string? refused)
    {
        if (refused is not null)
        {
            Refused?.Invoke(this, refused);
        }
    }

    /// <summary>The bar over the editor, shown for a table file whether its table or its text shows.</summary>
    public Border Bar { get; }

    /// <summary>What lies over the editor while the table shows: the grid, or why there is none yet.</summary>
    public Panel Body { get; }

    public CellGrid View { get; } = new();

    /// <summary>The file shown, null while the group shows no table file.</summary>
    public TableFile? File => file;

    /// <summary>Whether the table, not the editor under it, is what the group shows.</summary>
    public bool ShowsTable => file is { ShowsTable: true };

    /// <summary>Raised as a CSV switches between its Table and its Text, for the group to move the focus.</summary>
    public event EventHandler? Switched;

    public void Show(TableFile shown)
    {
        if (shown == file)
        {
            file.Follow();
            Paint();
            return;
        }
        Hide();
        file = shown;
        file.Changed += OnChanged;
        choices.Children.Clear();
        Paint();
        if (kept.TryGetValue(shown, out ((int, int) Anchor, (int, int) Caret, Vector Scroll) view))
        {
            View.Choose(view.Anchor, view.Caret);
            View.ScrollTo(view.Scroll);
        }
        else
        {
            View.Choose((0, 0), (0, 0));
            View.ScrollTo(default);
        }
        file.Follow();
    }

    /// <summary>Shows no table: the file's view kept, the bar and grid hidden.</summary>
    public void Hide()
    {
        if (file is null)
        {
            return;
        }
        kept[file] = (View.Anchor, View.Caret, View.ScrollOffset);
        file.Changed -= OnChanged;
        file = null;
        View.Table = null;
        Bar.IsVisible = false;
        Body.IsVisible = false;
    }

    /// <summary>The text's caret taken to a place in it (Go to Declaration to a resource's key): a resource's table chooses
    /// the value of the key written there, now or once the table is read.</summary>
    public void Reveal(int offset)
    {
        goneTo = file is { IsResource: true } ? offset : null;
        Chase();
    }

    private void Chase()
    {
        if (goneTo is not int offset || View.Table is not { Unsorted: ResourceTable read } shown)
        {
            return;
        }
        goneTo = null;
        int column = read.IsResx ? ResourceTable.Value : ResourceTable.Target;
        for (int row = 0; row < shown.RowCount; row++)
        {
            if (read.Entry(shown.FileRow(row)) is { } entry && entry.Start <= offset && offset <= entry.End)
            {
                View.Choose((row, column), (row, column));
                return;
            }
        }
    }

    /// <summary>A file closed: nothing of it is kept.</summary>
    public void Forget(TableFile closed) => kept.Remove(closed);

    private void OnChanged(object? sender, EventArgs e) => Paint();

    private void Paint()
    {
        if (file is null)
        {
            return;
        }
        painting = true;
        Bar.IsVisible = true;
        Body.IsVisible = file.ShowsTable;
        if (!ReferenceEquals(View.Table, file.Table))
        {
            View.Table = file.Table;
            Chase();
        }
        PaintChoices();
        header.IsVisible = file.HasHeaderChoice && file.ShowsTable;
        header.IsChecked = file.HeaderRow;
        message.IsVisible = file.Table is null;
        message.Text = file.Problem ?? (file.IsReading ? "Reading…" : string.Empty);
        View.IsVisible = file.Table is not null;
        size.Text = file.Table is { } table && file.ShowsTable
            ? $"{table.RowCount.ToString("N0", CultureInfo.CurrentCulture)} {Core.Plural.Noun(table.RowCount, "row")} × {table.ColumnCount.ToString("N0", CultureInfo.CurrentCulture)} {Core.Plural.Noun(table.ColumnCount, "column")}"
            : file.IsReading ? "Reading…" : string.Empty;
        painting = false;
    }

    /// <summary>The segments: a workbook's sheets, or a CSV's Table and Text, the one in force checked.</summary>
    private void PaintChoices()
    {
        TableFile shown = file!;
        string[] names = shown.IsWorkbook ? [.. shown.Sheets] : ["Table", "Text"];
        int chosen = shown.IsWorkbook ? shown.Sheet : shown.ShowsTable ? 0 : 1;
        if (choices.Children.Count != names.Length || names.Where((name, i) => (string?)((ToggleButton)choices.Children[i]).Content != name).Any())
        {
            choices.Children.Clear();
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                ToggleButton choice = new() { Classes = { StyleClasses.Segment }, Content = names[i], FontSize = 12, Name = shown.IsWorkbook ? null : $"Show{names[i]}" };
                choice.Click += (_, _) => Choose(index);
                choices.Children.Add(choice);
            }
        }
        for (int i = 0; i < choices.Children.Count; i++)
        {
            ((ToggleButton)choices.Children[i]).IsChecked = i == chosen;
        }
        // A sheet alone needs no choosing.
        choices.IsVisible = names.Length > 1;
    }

    private void Choose(int index)
    {
        if (file is null)
        {
            return;
        }
        if (file.IsWorkbook)
        {
            View.Choose((0, 0), (0, 0));
            View.ScrollTo(default);
            file.Sheet = index;
            Paint();
            return;
        }
        file.ShowTable(index == 0);
        Paint();
        Switched?.Invoke(this, EventArgs.Empty);
    }
}
