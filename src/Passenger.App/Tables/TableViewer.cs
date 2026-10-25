using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App.Tables;

/// <summary>An editor group's table: a bar over the editor (a CSV's Table and Text, or a workbook's sheets, and how large
/// the table is) and the <see cref="CellGrid"/> laid over the editor while the table shows. Each file's chosen cells and
/// scroll are kept for when its tab is shown again.</summary>
public sealed class TableViewer
{
    private readonly StackPanel choices = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox header = new() { Content = "First row is header", FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock size = new() { Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock message = new() { Classes = { "emptyText" }, IsVisible = false };
    private readonly Dictionary<TableFile, ((int, int) Anchor, (int, int) Caret, Vector Scroll)> kept = [];
    private TableFile? file;
    private bool painting;

    public TableViewer()
    {
        Bar = new Border
        {
            [DockPanel.DockProperty] = Dock.Top,
            Height = 32,
            Padding = new Thickness(8, 0),
            IsVisible = false,
            Child = new DockPanel
            {
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
            },
        };
        Bar.Bind(Border.BackgroundProperty, Bar.GetResourceObservable(TokenKeys.Panel));
        Bar.Bind(Border.BorderBrushProperty, Bar.GetResourceObservable(TokenKeys.Divider));
        Bar.BorderThickness = new Thickness(0, 0, 0, 1);
        header.IsCheckedChanged += (_, _) =>
        {
            if (!painting && file is not null)
            {
                file.HeaderRow = header.IsChecked == true;
            }
        };
        Body = new Panel { IsVisible = false, Children = { View, message } };
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
        }
        PaintChoices();
        header.IsVisible = !file.IsWorkbook && file.ShowsTable;
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
                ToggleButton choice = new() { Classes = { "segment" }, Content = names[i], FontSize = 12, Name = shown.IsWorkbook ? null : $"Show{names[i]}" };
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
