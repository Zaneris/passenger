using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.App.Tables;
using Passenger.Core;
using Passenger.Dap;

namespace Passenger.App.Debugging;

internal sealed class VariableTableWindow : IdeWindow
{
    private const int PageSize = 12;
    private readonly CellGrid grid = new() { Name = "DebugValueGrid" };
    private readonly ComboBox tables = new() { Name = "DebugValueTables", IsVisible = false };
    private readonly Button previous = new() { Name = "PreviousDebugRows", Content = "Previous", IsEnabled = false };
    private readonly Button next = new() { Name = "NextDebugRows", Content = "Next", IsEnabled = false };
    private readonly Button detail = new() { Name = "InspectDebugCell", Content = "View Cell", IsEnabled = false };
    private readonly TextBlock status = MutedText.Caption(string.Empty);
    private readonly Func<int, int, int, Task<VariableTableResponse>> read;
    private VariableTextWindow? cell;
    private int start;
    private int totalRows;
    private int selectedTable = -1;
    private bool reading;
    private bool fitted;

    public VariableTableWindow(string name, Func<int, int, int, Task<VariableTableResponse>> read)
    {
        this.read = read;
        Title = $"Table · {name}";
        Name = "DebugTableViewer";
        Width = DialogWidths.Wide;
        Height = 432;
        MinWidth = PopupFrame.Medium;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AutomationProperties.SetName(tables, "DataSet table");
        StackPanel toolbar = new()
        {
            [DockPanel.DockProperty] = Dock.Top,
            Orientation = Orientation.Horizontal,
            Spacing = Gaps.Normal,
            Margin = new Thickness(Gaps.Normal, Gaps.Normal, Gaps.Normal, 0),
            Children = { tables, previous, next, detail },
        };
        Border footer = DialogFooter.Bar(DialogFooter.Listed(DialogFooter.Cancel(Close, "Close")), status);
        DockPanel.SetDock(footer, Dock.Bottom);
        Content = new DockPanel
        {
            Children =
            {
                toolbar,
                footer,
                new Border { Classes = { StyleClasses.Framed }, Margin = new Thickness(Gaps.Normal), Child = grid },
            },
        };
        Opened += (_, _) => IdeErrors.Observe(ReadAsync(0, 0));
        previous.Click += (_, _) => GoTo(start - PageSize);
        next.Click += (_, _) => GoTo(start + PageSize);
        tables.SelectionChanged += (_, _) => GoTo(0);
        detail.Click += (_, _) => ShowCell();
        grid.CellReadRequested += (_, _) => ShowCell();
        grid.SortRequested += (_, order) =>
        {
            if (grid.Table is { } current)
            {
                grid.Table = order is { } sort ? TableSort.By(current, sort.Column, sort.Descending) : current.Unsorted;
            }
        };
    }

    private void GoTo(int at)
    {
        if (!reading && at >= 0)
        {
            IdeErrors.Observe(ReadAsync(at, Math.Max(tables.SelectedIndex, 0)));
        }
    }

    private async Task ReadAsync(int at, int tableIndex)
    {
        using IDisposable held = BackgroundWork.Scope();
        reading = true;
        previous.IsEnabled = next.IsEnabled = detail.IsEnabled = tables.IsEnabled = false;
        cell?.Close();
        status.Text = "Reading values…";
        try
        {
            VariableTableResponse page = await read(at, PageSize, tableIndex);
            if (!IsVisible)
            {
                return;
            }
            start = at;
            totalRows = page.RowCount;
            selectedTable = Math.Min(tableIndex, page.Tables.Count - 1);
            tables.ItemsSource = page.Tables;
            tables.IsVisible = page.Tables.Count > 0;
            tables.SelectedIndex = selectedTable;
            grid.Table = new DebugRows(page, at);
            if (!fitted)
            {
                fitted = true;
                if (page.Columns.Count <= 4)
                {
                    Width = Math.Min(Width, DialogWidths.Form);
                }
                Height = Math.Min(Height, Math.Max(MinHeight, 144 + page.Rows.Count * ListRow.Height));
            }
            status.Text = page.Rows.Count == 0 ? "No rows" : $"Rows {Numbers.Said(at + 1)}–{Numbers.Said(at + page.Rows.Count)} of {Numbers.Said(page.RowCount)}";
        }
        catch (DapException error)
        {
            tables.SelectedIndex = selectedTable;
            status.Text = error.Reason;
        }
        finally
        {
            reading = false;
            tables.IsEnabled = true;
            detail.IsEnabled = grid.Table is { RowCount: > 0, ColumnCount: > 0 };
            previous.IsEnabled = start > 0;
            next.IsEnabled = grid.Table is { RowCount: > 0 } shown && start + shown.RowCount < totalRows;
        }
    }

    private void ShowCell()
    {
        if (reading || grid.Table is not { RowCount: > 0, ColumnCount: > 0 } table)
        {
            return;
        }
        cell?.Close();
        (int row, int column) = grid.Caret;
        VariableTextWindow viewer = new($"{table.Header(column)} · row {table.RowLabel(row)}",
            table.ValueAt(row, column)?.ToString() ?? string.Empty);
        cell = viewer;
        viewer.Closed += (_, _) => cell = null;
        viewer.Show(this);
    }

    private sealed class DebugRows(VariableTableResponse page, int start) : Table
    {
        private readonly TextRows rows = new(page.Columns,
            [.. page.Rows.Select(row => row.Select(value => DebugController.StringValue(value) ?? value).ToArray())]);

        public override int RowCount => rows.RowCount;
        public override int ColumnCount => rows.ColumnCount;
        public override string Header(int column) => rows.Header(column);
        public override int RowNumber(int row) => start + row + 1;
        public override string Cell(int row, int column) => VariableNode.Cut(rows.Cell(row, column)).Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        public override bool IsNumber(int row, int column) => rows.IsNumber(row, column);
        public override CellValue ValueOf(int row, int column) => rows.ValueOf(row, column);
        public override object? ValueAt(int row, int column) => rows.ValueAt(row, column);
    }
}
