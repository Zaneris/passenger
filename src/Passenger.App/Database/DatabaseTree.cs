using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Containers;
using Passenger.App.ToolWindows;

namespace Passenger.App.Database;

public sealed class DatabaseTreeRow(DatabaseTable table, string title, int depth, bool expands) : Observable, ITreeRow
{
    private bool expanded;
    public DatabaseTable Table { get; } = table;
    public string Title { get; } = title;
    public int Depth { get; } = depth;
    public bool Expands { get; } = expands;
    public List<DatabaseTreeRow>? Children { get; set; }
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
}

public sealed class DatabaseTree : DockPanel
{
    private readonly DatabaseController controller;
    private readonly TextBox search = FilterBox.Of();
    private readonly ListBox list = new() { Name = "DatabaseTables", Classes = { StyleClasses.Tree } };
    private readonly TreeRows<DatabaseTreeRow> rows = [];
    private readonly TextBlock empty = new() { Text = "No matching tables", Classes = { StyleClasses.EmptyText } };
    private IReadOnlyList<DatabaseTable>? known;
    private List<DatabaseTreeRow> roots = [];
    private bool showing;

    public DatabaseTree(DatabaseController controller)
    {
        this.controller = controller;
        search.Name = "DatabaseTableSearch";
        search.PlaceholderText = "Type to find tables";
        search.Margin = new Thickness(4);
        SetDock(search, Dock.Top);
        Children.Add(search);
        Children.Add(new Panel { Children = { list, empty } });
        list.ItemsSource = rows;
        ReusedRows.Use(list, Row);
        TreeKeys.Attach<DatabaseTreeRow>(list, row => IdeErrors.Observe(ToggleAsync(row)), row =>
        {
            if (row.Depth != 0) { return false; }
            OpenRequested?.Invoke(this, row.Table);
            return true;
        });
        list.SelectionChanged += (_, _) =>
        {
            if (!showing) { controller.ChooseTable((list.SelectedItem as DatabaseTreeRow)?.Table); }
        };
        list.DoubleTapped += (_, e) =>
        {
            if (e.Source is RowGlyphs) { return; }
            if (list.SelectedItem is DatabaseTreeRow row)
            {
                if (row.Depth == 0) { OpenRequested?.Invoke(this, row.Table); }
                else if (row.Expands) { IdeErrors.Observe(ToggleAsync(row)); }
            }
        };
        list.AddHandler(TextInputEvent, (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Text) && controller.Connected && !controller.Busy && !e.Text.Any(char.IsControl))
            {
                search.Focus();
                search.Text += e.Text;
                search.CaretIndex = search.Text?.Length ?? 0;
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        list.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control && list.SelectedItem is DatabaseTreeRow row)
            {
                OpenRequested?.Invoke(this, row.Table);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape) { ClearSearch(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        search.TextChanged += (_, _) => Filter();
        search.KeyDown += (_, e) =>
        {
            if (ListKeys.Move(list, e)) { e.Handled = true; }
            else if (e.Key == Key.Escape) { ClearSearch(); e.Handled = true; }
            else if (e.Key == Key.Enter && list.SelectedItem is DatabaseTreeRow row)
            {
                OpenRequested?.Invoke(this, row.Table);
                e.Handled = true;
            }
        };
        controller.Changed += (_, _) => Show();
        Show();
    }

    public event EventHandler<DatabaseTable>? OpenRequested;

    private void ClearSearch()
    {
        search.Text = string.Empty;
        ListKeys.Hold(list, chooseFirst: true);
    }

    private void Show()
    {
        list.IsEnabled = controller.Connected;
        search.IsEnabled = controller.Connected;
        if (!ReferenceEquals(known, controller.Tables))
        {
            known = controller.Tables;
            roots = [.. known.Select(t => new DatabaseTreeRow(t, t.ToString(), 0, true))];
            Filter();
        }
        if (!controller.Busy) { controller.ChooseTable((list.SelectedItem as DatabaseTreeRow)?.Table); }
    }

    private void Filter()
    {
        string query = search.Text?.Trim() ?? string.Empty;
        List<DatabaseTreeRow> matches = [.. roots.Where(r => r.Table.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || $"{r.Table.Schema}.{r.Table.Name}".Contains(query, StringComparison.OrdinalIgnoreCase))];
        DatabaseTreeRow? previous = list.SelectedItem as DatabaseTreeRow;
        showing = true;
        try
        {
            rows.Show([.. TreeRows<DatabaseTreeRow>.Shown(matches, r => r.Children)]);
            if (previous is not null && rows.Contains(previous)) { list.SelectedItem = previous; }
            else { list.SelectedItem = query.Length > 0 ? matches.FirstOrDefault() : matches.FirstOrDefault(r => r.Table == controller.Table); }
            empty.IsVisible = controller.Connected && matches.Count == 0;
        }
        finally { showing = false; }
        controller.ChooseTable((list.SelectedItem as DatabaseTreeRow)?.Table);
    }

    private async Task ToggleAsync(DatabaseTreeRow row)
    {
        if (!row.Expands || controller.Busy) { return; }
        list.SelectedItem = row;
        if (row.IsExpanded)
        {
            row.IsExpanded = false;
            rows.Close(row);
            return;
        }
        if (row.Children is null)
        {
            await controller.ReadSchemaAsync(row.Table);
            if (!roots.Contains(row) || controller.SchemaOf(row.Table) is not { } schema) { return; }
            row.Children =
            [
                Group("Columns", schema.Columns.Select(c => $"{c.Name} · {c.Type}{(c.Nullable ? " · NULL" : string.Empty)}{(c.Primary ? " · PK" : string.Empty)}")),
                Group("Keys", schema.Keys),
                Group("Indexes", schema.Indexes),
            ];
        }
        row.IsExpanded = true;
        rows.Open(row, TreeRows<DatabaseTreeRow>.Shown(row.Children, r => r.Children));

        DatabaseTreeRow Group(string title, IEnumerable<string> items)
        {
            List<DatabaseTreeRow> children = [.. items.Select(t => new DatabaseTreeRow(row.Table, t, 2, false))];
            return new DatabaseTreeRow(row.Table, title, 1, true)
            {
                Children = children.Count > 0 ? children : [new DatabaseTreeRow(row.Table, $"No {title.ToLowerInvariant()}", 2, false)],
            };
        }
    }

    private Control Row()
    {
        RowGlyphs glyphs = new() { Width = 18 };
        TextBlock text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel line = new() { Height = ListRow.Height, Children = { glyphs, text } };
        SetDock(glyphs, Dock.Left);
        glyphs.ChevronPressed += (_, e) =>
        {
            if (line.DataContext is DatabaseTreeRow row) { IdeErrors.Observe(ToggleAsync(row)); e.Handled = true; }
        };
        return line.Showing<DatabaseTreeRow>(row =>
        {
            line.Margin = new Thickness(4 + row.Depth * 16, 0, 4, 0);
            glyphs.HasChevron = row.Expands;
            glyphs.Turned = row.IsExpanded;
            glyphs.Icon = null;
            text.Text = ContainerSecrets.InSaid(row.Title);
            ToolTip.SetTip(line, text.Text);
        });
    }
}
