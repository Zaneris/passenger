using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Tables;
using Passenger.App.ToolWindows;

namespace Passenger.App.Database;

public sealed class DatabaseView : DockPanel
{
    private readonly DatabaseController controller;
    private readonly ComboBox sources = new() { Name = "DatabaseSources", MinWidth = 180, MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox tables = new() { Name = "DatabaseTables" };
    private readonly ListBox details = new() { Name = "DatabaseDetails" };
    private readonly TextBox sql = new() { Name = "DatabaseSql", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxHeight = 180, PlaceholderText = "SQL to run against the selected database" };
    private readonly ComboBox history = new() { Name = "DatabaseHistory", PlaceholderText = "Query history", MinWidth = 140, MaxWidth = 320 };
    private readonly CellGrid grid = new() { Name = "DatabaseResults" };
    private readonly TextBlock said = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4) };
    private bool showing;

    public DatabaseView(DatabaseController controller)
    {
        this.controller = controller;
        ReusedRows.Use(tables, TableRow);
        ReusedRows.Use(details, DetailRow);
        sources.SelectionChanged += (_, _) => { if (!showing) { controller.Choose(sources.SelectedItem as DatabaseSource); } };
        tables.SelectionChanged += (_, _) => { if (!showing) { controller.ChooseTable(tables.SelectedItem as DatabaseTable); } };
        tables.DoubleTapped += (_, _) => ActionRequested?.Invoke(this, AppActions.DatabaseBrowse);
        sql.TextChanged += (_, _) => { if (!showing) { controller.Sql = sql.Text ?? string.Empty; } };
        history.SelectionChanged += (_, _) => { if (!showing && history.SelectedItem is string text) { sql.Text = text; } };
        grid.CellWritten += (_, write) =>
        {
            PendingWrite = write;
            ActionRequested?.Invoke(this, AppActions.DatabaseEdit);
            PendingWrite = null;
        };
        WrapPanel toolbar = new() { Margin = new Thickness(8, 4), Orientation = Orientation.Horizontal };
        toolbar.Children.Add(sources);
        foreach ((string action, string icon, string title) in new (string, string, string)[]
        {
            (AppActions.DatabaseRefresh, IconKeys.Rerun, "Refresh Database Connections"), (AppActions.DatabaseConnect, IconKeys.Database, "Connect to Database"),
            (AppActions.DatabaseBrowse, IconKeys.DocumentText, "Browse Database Rows"), (AppActions.DatabasePrevious, IconKeys.ArrowUp, "Previous Database Page"),
            (AppActions.DatabaseNext, IconKeys.ArrowDown, "Next Database Page"), (AppActions.DatabaseQuery, IconKeys.Run, "Run Database SQL…"),
        })
        {
            toolbar.Children.Add(ToolButtons.Of(action, icon, title, requested => ActionRequested?.Invoke(this, requested)));
        }
        toolbar.Children.Add(history);
        Border top = ToolBars.Top(toolbar);
        SetDock(top, Dock.Top);
        SetDock(said, Dock.Bottom);
        Grid body = new() { ColumnDefinitions = new ColumnDefinitions("180,*") };
        Grid schema = new() { RowDefinitions = new RowDefinitions("*,*") };
        schema.Children.Add(new Border { Classes = { StyleClasses.Framed }, Child = tables });
        schema.Children.Add(new Border { Classes = { StyleClasses.Framed }, Child = details, [Grid.RowProperty] = 1 });
        DockPanel console = new() { Margin = new Thickness(6, 0, 0, 0), [Grid.ColumnProperty] = 1 };
        SetDock(sql, Dock.Top);
        console.Children.Add(sql);
        console.Children.Add(new Border { Classes = { StyleClasses.Framed }, Margin = new Thickness(0, 6, 0, 0), Child = grid });
        body.Children.Add(schema);
        body.Children.Add(console);
        Children.Add(top);
        Children.Add(said);
        Children.Add(body);
        controller.Changed += (_, _) => Show();
        Show();
    }

    public event EventHandler<string>? ActionRequested;

    public CellWrite? PendingWrite { get; private set; }

    public void BeginEditing() => grid.BeginWriting();

    private static Control TableRow()
    {
        TextBlock text = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), Height = ListRow.Height };
        return text.Showing<DatabaseTable>(table => text.Text = table.ToString());
    }

    private static Control DetailRow()
    {
        TextBlock text = new() { Margin = new Thickness(8, 2), TextWrapping = TextWrapping.Wrap };
        return text.Showing<string>(value => text.Text = Containers.ContainerSecrets.InSaid(value));
    }

    private void Show()
    {
        showing = true;
        try
        {
            sources.ItemsSource = controller.Sources;
            sources.SelectedItem = controller.Source;
            sources.IsEnabled = !controller.Busy;
            tables.ItemsSource = controller.Tables;
            tables.SelectedItem = controller.Table;
            tables.IsEnabled = !controller.Busy;
            sql.Text = controller.Sql;
            sql.IsEnabled = !controller.Busy;
            history.ItemsSource = controller.History;
            history.IsEnabled = !controller.Busy;
            grid.IsEnabled = !controller.Busy;
            if (!ReferenceEquals(grid.Table, controller.Rows))
            {
                grid.Table = controller.Rows;
            }
            details.ItemsSource = controller.Schema is { } schema
                ? new[] { schema.Table.ToString(), "Columns" }.Concat(schema.Columns.Select(c => $"{c.Name} · {c.Type}{(c.Nullable ? " · NULL" : string.Empty)}{(c.Primary ? " · PK" : string.Empty)}"))
                    .Concat(["Keys"]).Concat(schema.Keys).Concat(["Indexes"]).Concat(schema.Indexes).ToArray() : [];
            said.Text = controller.Said;
        }
        finally
        {
            showing = false;
        }
    }
}
