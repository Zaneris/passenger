using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Passenger.App.Tables;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.App.Database;

public sealed class DatabaseTableTab : IDisposable
{
    private readonly DatabaseController owner;
    private readonly ActionWork work;
    private readonly DatabaseSource source;
    private readonly Func<string, string, Task<bool>> confirm;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private DatabaseSchema? schema;
    private DatabaseRows? rows;
    private DatabaseFilter? filter;
    private string said = "Loading table…";
    private bool disposed;

    public DatabaseTableTab(DatabaseController owner, ActionWork work, DatabaseSource source, DatabaseTable table, Func<string, string, Task<bool>> confirm)
    {
        this.owner = owner; this.work = work; this.source = source; Table = table; this.confirm = confirm;
        Document = OpenDocument.OfDatabase(this, Path.Combine(source.Folder, $".passenger-table-{Guid.NewGuid():N}"));
        owner.Changed += OnOwnerChanged;
    }

    public OpenDocument Document { get; }
    public DatabaseTable Table { get; }
    public string Title => Containers.ContainerSecrets.InSaid($"{Table.Name} · {owner.Sources.FirstOrDefault(s => s.Id == source.Id)?.Name ?? source.Name}");
    public bool Matches(DatabaseSource chosen, DatabaseTable table) => source.Id == chosen.Id && source.Kind == chosen.Kind && source.ConnectionString == chosen.ConnectionString && Table == table;
    private bool Valid => !disposed && owner.HasConnection(source);
    private event EventHandler? Changed;

    private void OnOwnerChanged(object? sender, EventArgs e)
    {
        if (!Valid) { operation?.Cancel(); said = "The connection changed. Reopen this table from Database."; }
        Document.DatabaseTitleChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task LoadAsync(int offset = 0, DatabaseFilter? next = null) => RunAsync(async cancel =>
    {
        DatabaseSchema read = schema ?? await BackgroundWork.Run(() => DatabaseClient.SchemaAsync(source, Table, cancel), cancel);
        DatabaseRows page = await BackgroundWork.Run(() => DatabaseClient.PageAsync(source, read, offset, cancel, next), cancel);
        cancel.ThrowIfCancellationRequested();
        schema = read; rows = page; filter = next;
        said = $"{Table} · rows {offset + (page.RowCount > 0 ? 1 : 0)}–{offset + page.RowCount}{(page.More ? " · more available" : string.Empty)}";
    });

    private async Task RunAsync(Func<CancellationToken, Task> run)
    {
        UiThread.Check();
        if (!Valid || operation is not null || work.Begin(AppActions.DatabaseOpenTable, $"Reading {Table}") is not { } started) { return; }
        using Working working = started;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, working.Stopping);
        operation = stop;
        Changed?.Invoke(this, EventArgs.Empty);
        try { await run(stop.Token); working.Tell(true, said); }
        catch (OperationCanceledException) { said = "Table operation cancelled."; working.Tell(false, said); }
        catch (System.Data.Common.DbException) when (stop.IsCancellationRequested) { said = "Table operation cancelled."; working.Tell(false, said); }
        catch (Exception error) when (error is System.Data.Common.DbException or InvalidOperationException or ArgumentException or FormatException or OverflowException or IOException or TimeoutException)
        {
            said = Containers.ContainerSecrets.InSaid(error.Message, known: [source.ConnectionString]);
            IdeErrors.Write($"Database table operation failed: {said}");
            working.Tell(false, said);
        }
        finally { operation = null; Changed?.Invoke(this, EventArgs.Empty); }
    }

    public Control Make()
    {
        CellGrid grid = new() { Name = "DatabaseTabResults" };
        ComboBox columns = new() { Name = "DatabaseFilterColumn", MinWidth = 120 };
        ComboBox comparison = new() { Name = "DatabaseFilterOperator", ItemsSource = new[] { "Contains", "Equals", "Is NULL", "Is not NULL" }, SelectedItem = filter?.Operator ?? "Contains", MinWidth = 110 };
        TextBox value = new() { Name = "DatabaseFilterValue", Text = filter?.Value ?? string.Empty, PlaceholderText = "Filter value", MinWidth = 160 };
        TextBlock status = MutedText.Wrapped(said);
        status.Name = "DatabaseTabStatus";
        status.Margin = new Thickness(8, 4);
        Button apply = new() { Name = "DatabaseApplyFilter", Content = "Apply Filter" };
        Button clear = new() { Name = "DatabaseClearFilter", Content = "Clear" };
        Button refresh = new() { Content = "Refresh" };
        Button previous = new() { Content = "Previous Page" };
        Button next = new() { Content = "Next Page" };
        Button cancel = new() { Content = "Cancel Operation", Name = "DatabaseTableCancel" };
        apply.Click += (_, _) => Apply();
        value.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } };
        clear.Click += (_, _) => { value.Text = string.Empty; IdeErrors.Observe(LoadAsync()); };
        refresh.Click += (_, _) => IdeErrors.Observe(LoadAsync(rows?.Offset ?? 0, filter));
        previous.Click += (_, _) => IdeErrors.Observe(LoadAsync(Math.Max(0, (rows?.Offset ?? 0) - DatabaseClient.PageSize), filter));
        next.Click += (_, _) => IdeErrors.Observe(LoadAsync((rows?.Offset ?? 0) + DatabaseClient.PageSize, filter));
        cancel.Click += (_, _) => operation?.Cancel();
        grid.CellWritten += (_, write) => IdeErrors.Observe(EditAsync(write));
        WrapPanel bar = new() { Margin = new Thickness(8, 4) };
        foreach (Control control in new Control[] { columns, comparison, value, apply, clear, refresh, previous, next, cancel })
        {
            control.Margin = new Thickness(0, 0, 6, 4);
            bar.Children.Add(control);
        }
        Border top = ToolBars.Top(bar);
        DockPanel page = new();
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(status, Dock.Bottom);
        page.Children.Add(top); page.Children.Add(status);
        page.Children.Add(new Border { Classes = { StyleClasses.Framed }, Child = grid });
        EventHandler show = (_, _) => Show();
        page.AttachedToVisualTree += (_, _) => { Changed += show; Show(); };
        page.DetachedFromVisualTree += (_, _) => Changed -= show;
        Show();
        return page;

        void Apply()
        {
            if (columns.SelectedItem is string column && comparison.SelectedItem is string op)
            {
                IdeErrors.Observe(LoadAsync(0, new DatabaseFilter(column, value.Text ?? string.Empty, op)));
            }
        }

        void Show()
        {
            bool idle = operation is null && Valid;
            string? chosen = columns.SelectedItem as string ?? filter?.Column;
            columns.ItemsSource = schema?.Columns.Select(c => c.Name).ToArray() ?? [];
            columns.SelectedItem = chosen is not null && schema?.Columns.Any(c => c.Name == chosen) == true ? chosen : schema?.Columns.FirstOrDefault()?.Name;
            foreach (Control control in new Control[] { columns, comparison, value, apply, clear, refresh, grid }) { control.IsEnabled = idle; }
            previous.IsEnabled = idle && rows?.Offset > 0;
            next.IsEnabled = idle && rows?.More == true;
            cancel.IsEnabled = operation is not null;
            if (!ReferenceEquals(grid.Table, rows)) { grid.Table = rows; }
            status.Text = operation is null ? said : "Reading table…";
        }
    }

    private async Task EditAsync(CellWrite write)
    {
        if (!Valid || operation is not null || write.Table is not DatabaseRows page || !ReferenceEquals(page, rows) || write.Rows is not [int row]) { return; }
        if (!await confirm("Update Database Cell", $"Write {Containers.ContainerSecrets.InSaid(write.Text)} to {Table}.{page.Columns[write.Column]}, row {page.RowNumber(row)}?")) { return; }
        await RunAsync(async cancel =>
        {
            await BackgroundWork.Run(() => DatabaseClient.UpdateAsync(source, page, row, write.Column, write.Text, cancel), cancel);
            rows = await BackgroundWork.Run(() => DatabaseClient.PageAsync(source, schema!, page.Offset, cancel, filter), cancel);
            said = "Cell updated.";
        });
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        lifetime.Cancel();
        owner.Changed -= OnOwnerChanged;
    }
}
