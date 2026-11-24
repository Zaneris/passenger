using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Passenger.App.Tables;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.App.Database;

/// <summary>A table's or view's rows among the editors, read a page at a time, sorted and filtered on the server. Its cells
/// written, rows added (in the empty row last) and rows deleted are held as changes not yet written, marked as a diff marks
/// its lines, until Submit writes them in one transaction or Revert drops them; Undo and Redo step through them meanwhile.
/// A row is found again by its primary key, so a table without one, or a connection marked read-only, takes no change.</summary>
public sealed class DatabaseTableTab : IDisposable
{
    private readonly DatabaseController owner;
    private readonly ActionWork work;
    private readonly DatabaseSource connection;
    private readonly Action<string> request;
    private readonly Func<string, string, string, Task<bool>> ask;
    private (string Column, bool Descending)? sort;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private DatabaseSchema? schema;
    private DatabaseRows? rows;
    private PendingTable? shown;
    private DatabaseFilter? filter;
    private PendingEdits edits = PendingEdits.None;
    private readonly Stack<PendingEdits> done = new();
    private readonly Stack<PendingEdits> undone = new();
    private int added;
    private string said = "Loading table…";
    private CellGrid? grid;
    private bool disposed;

    /// <param name="request">Runs one of the window's actions, as its button asks.</param>
    /// <param name="ask">Asks the person a question (its title, words and the answer that goes on), true where they agree.</param>
    public DatabaseTableTab(DatabaseController owner, ActionWork work, DatabaseSource connection, string database, DatabaseTable table,
        Action<string> request, Func<string, string, string, Task<bool>> ask)
    {
        this.owner = owner;
        this.work = work;
        this.connection = connection;
        this.request = request;
        this.ask = ask;
        Database = database;
        Table = table;
        Document = OpenDocument.OfDatabase(this, Path.Combine(connection.Folder, $".passenger-table-{Guid.NewGuid():N}"));
        owner.Changed += OnOwnerChanged;
    }

    public OpenDocument Document { get; }

    /// <summary>The connection it reads, by id.</summary>
    public Guid Connection => connection.Id;
    public DatabaseTable Table { get; }
    public string Database { get; }
    public string Title => Containers.ContainerSecrets.InSaid($"{Table.Name} · {Now.Name}");

    /// <summary>The page last read.</summary>
    public DatabaseRows? Rows => rows;

    /// <summary>The page shown with the changes not yet written over it.</summary>
    public PendingTable? Shown => shown;

    /// <summary>The changes not yet written.</summary>
    public PendingEdits Edits => edits;

    /// <summary>Whether a read or a write of it is under way.</summary>
    public bool Reading => operation is not null;

    /// <summary>What the tab says under its rows: what was read or written, or why not.</summary>
    public string Said => operation is null ? said : "Reading table…";

    /// <summary>The connection as it stands now, its mark too, opening the tab's database.</summary>
    private DatabaseSource Now => (owner.Find(connection.Id) ?? connection).In(Database);

    public bool Matches(DatabaseSource chosen, string database, DatabaseTable table) => connection.Id == chosen.Id && Database == database && Table == table;
    private bool Valid => !disposed && owner.Find(connection.Id) is { } now && now.Kind == connection.Kind && now.ConnectionString == connection.ConnectionString;
    private event EventHandler? Changed;

    /// <summary>Why the table takes no change: its connection marked read-only, or no primary key to find a row again by.</summary>
    public string? WhyNotEdited => Now.Mark == DatabaseMark.ReadOnly ? Refusals.ReadOnlyConnection(Now.Name)
        : schema is not null && !schema.Columns.Any(c => c.Primary) ? Refusals.NoPrimaryKey(Table.ToString()) : null;

    private string? Busy => operation is not null || !Valid ? Refusals.DatabaseBusy : null;

    public string? WhyNotSubmit => Busy ?? (edits.IsEmpty ? Refusals.NoPendingChanges : WhyNotEdited);
    public string? WhyNotRevert => Busy ?? (edits.IsEmpty ? Refusals.NoPendingChanges : null);
    public string? WhyNotAddRow => Busy ?? WhyNotEdited ?? (shown is null ? Refusals.DatabaseBusy : null);
    public string? WhyNotDeleteRows => Busy ?? WhyNotEdited ?? (ChosenRows().Count == 0 ? Refusals.NoRowChosen : null);
    public string? WhyNotStep(bool redo) => Busy ?? ((redo ? undone : done).Count == 0 ? redo ? Refusals.NoPendingRedo : Refusals.NoPendingUndo : null);

    private void OnOwnerChanged(object? sender, EventArgs e)
    {
        if (!Valid)
        {
            operation?.Cancel();
            said = "The connection changed. Reopen this table from Database.";
        }
        Follow();
        Document.DatabaseTitleChanged();
    }

    public Task LoadAsync(int offset = 0, DatabaseFilter? next = null) => RunAsync(AppActions.DatabaseOpenTable, $"Reading {Table}", async cancel =>
    {
        DatabaseSource source = Now;
        DatabaseSchema read = schema ?? await BackgroundWork.Run(() => DatabaseClient.SchemaAsync(source, Table, cancel), cancel);
        (string Column, bool Descending)? order = sort;
        DatabaseRows page = await BackgroundWork.Run(() => DatabaseClient.PageAsync(source, read, offset, cancel, next, order), cancel);
        cancel.ThrowIfCancellationRequested();
        schema = read;
        Show(page);
        filter = next;
        said = $"{Table} · rows {offset + (page.RowCount > 0 ? 1 : 0)}–{offset + page.RowCount}{(page.More ? " · more available" : string.Empty)}";
    });

    /// <summary>Writes the changes in one transaction, asking first on a connection marked production, then reads the page again.</summary>
    public async Task SubmitAsync()
    {
        UiThread.Check();
        if (WhyNotSubmit is { } why)
        {
            Say(why);
            return;
        }
        DatabaseSource source = Now;
        PendingEdits writing = edits;
        if (source.Mark == DatabaseMark.Production && !await ask("Submit to Production",
            $"{source.Name} is marked production. Write {writing.Said.Replace(" pending", string.Empty, StringComparison.Ordinal)} to {Table}?", "Submit"))
        {
            return;
        }
        DatabaseSchema read = schema!;
        DatabaseRows page = rows!;
        await RunAsync(AppActions.DatabaseSubmit, $"Writing to {Table}", async cancel =>
        {
            if (!ReferenceEquals(writing, edits))
            {
                return;
            }
            await BackgroundWork.Run(() => DatabaseClient.SubmitAsync(source, read, page, writing, cancel), cancel);
            edits = PendingEdits.None;
            done.Clear();
            undone.Clear();
            said = $"{writing.Said.Replace(" pending", " written", StringComparison.Ordinal)}.";
            (string Column, bool Descending)? order = sort;
            Show(await BackgroundWork.Run(() => DatabaseClient.PageAsync(source, read, page.Offset, cancel, filter, order), cancel));
        });
    }

    /// <summary>Drops the changes not yet written, as a step Undo takes back.</summary>
    public void Revert()
    {
        if (WhyNotRevert is { } why)
        {
            Say(why);
            return;
        }
        Edit(PendingEdits.None);
        said = "Changes reverted.";
        Follow();
    }

    /// <summary>Drops the changes not yet written for good, as the tab closes without them.</summary>
    public void Discard()
    {
        edits = PendingEdits.None;
        done.Clear();
        undone.Clear();
        Follow();
    }

    /// <summary>Takes back the last change not yet written, or makes again the last taken back.</summary>
    public void Step(bool redo)
    {
        if (WhyNotStep(redo) is { } why)
        {
            Say(why);
            return;
        }
        (redo ? done : undone).Push(edits);
        edits = (redo ? undone : done).Pop();
        Follow();
    }

    /// <summary>Chooses the empty row's first cell that may be written and opens it for writing.</summary>
    public void AddRow()
    {
        if (WhyNotAddRow is { } why || grid is null || shown is null)
        {
            Say(WhyNotAddRow ?? Refusals.DatabaseBusy);
            return;
        }
        int row = shown.RowCount - 1;
        int column = Enumerable.Range(0, shown.ColumnCount).FirstOrDefault(c => shown.IsEditable(row, c));
        grid.Focus();
        grid.Choose((row, column), (row, column));
        grid.BeginWriting();
    }

    /// <summary>Deletes the rows chosen, as changes not yet written.</summary>
    public void DeleteRows()
    {
        if (WhyNotDeleteRows is { } why)
        {
            Say(why);
            return;
        }
        Delete(ChosenRows());
    }

    /// <summary>The rows chosen in the grid that a delete takes: rows read with a key and rows added.</summary>
    private List<int> ChosenRows()
    {
        if (grid is null || shown is null || !ReferenceEquals(grid.Table, shown))
        {
            return [];
        }
        (int top, _, int bottom, _) = grid.Chosen;
        return [.. Enumerable.Range(top, Math.Max(0, bottom - top + 1)).Where(r => r < shown.RowCount && !shown.IsNewRow(r) && (shown.AddedAt(r) is not null || shown.KeyOf(r) is not null))];
    }

    private void Delete(IReadOnlyList<int> chosen)
    {
        if (shown is null || chosen.Count == 0)
        {
            return;
        }
        PendingTable table = shown;
        Edit(edits.Delete([.. chosen.Where(table.IsRead).Select(r => (table.KeyOf(r)!, table.Page.Rows[r]))],
            [.. chosen.Select(table.AddedAt).OfType<AddedRow>().Select(a => a.Id)]));
    }

    private void Write(CellWrite write)
    {
        if (WhyNotEdited is { } refused)
        {
            Say(refused);
            return;
        }
        if (write.Table is not PendingTable table || !ReferenceEquals(table, shown) || write.Rows is not [int row])
        {
            return;
        }
        string? text = write.Text == "NULL" ? null : write.Text;
        if (table.IsNewRow(row))
        {
            Edit(edits.WriteAdded(++added, write.Column, text));
        }
        else if (table.AddedAt(row) is { } addedRow)
        {
            Edit(edits.WriteAdded(addedRow.Id, write.Column, text));
        }
        else if (table.KeyOf(row) is { } key)
        {
            object?[] read = table.Page.Rows[row];
            Edit(edits.Write(key, read, write.Column, text, read[write.Column] is null ? null : table.Page.Cell(row, write.Column)));
        }
    }

    /// <summary>A change made, a step Undo takes back.</summary>
    private void Edit(PendingEdits next)
    {
        if (next == edits)
        {
            return;
        }
        done.Push(edits);
        undone.Clear();
        edits = next;
        Follow();
    }

    private void Say(string words)
    {
        said = words;
        Follow();
    }

    /// <summary>A page read, shown with the changes over it.</summary>
    private void Show(DatabaseRows page)
    {
        rows = page;
        shown = new PendingTable(page, edits, editable: WhyNotEdited is null);
    }

    private void Follow()
    {
        if (shown is not null)
        {
            shown.Edits = edits;
            shown.Editable = WhyNotEdited is null;
        }
        Document.PendingChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAsync(string action, string stage, Func<CancellationToken, Task> run)
    {
        UiThread.Check();
        if (!Valid || operation is not null || work.Begin(action, stage) is not { } started)
        {
            return;
        }
        using Working working = started;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, working.Stopping);
        operation = stop;
        Follow();
        try
        {
            await run(stop.Token);
            working.Tell(true, said);
        }
        catch (OperationCanceledException)
        {
            said = "Table operation cancelled.";
            working.Tell(false, said);
        }
        catch (System.Data.Common.DbException) when (stop.IsCancellationRequested)
        {
            said = "Table operation cancelled.";
            working.Tell(false, said);
        }
        catch (Exception error) when (error is System.Data.Common.DbException or InvalidOperationException or ArgumentException or FormatException
            or OverflowException or InvalidCastException or IOException or TimeoutException)
        {
            said = Containers.ContainerSecrets.InSaid(error.Message, known: [connection.ConnectionString]);
            IdeErrors.Write($"Database table operation failed: {said}");
            working.Tell(false, said);
        }
        finally
        {
            operation = null;
            Follow();
        }
    }

    public Control Make()
    {
        CellGrid cells = new() { Name = "DatabaseTabResults" };
        grid = cells;
        ToolDropDown columns = new() { Name = "DatabaseFilterColumn" };
        ToolDropDown comparison = new() { Name = "DatabaseFilterOperator", ItemsSource = new[] { "Contains", "Equals", "Is NULL", "Is not NULL" }, SelectedItem = filter?.Operator ?? "Contains" };
        TextBox value = SearchBox.Of("Filter value", "Value to filter by");
        value.Name = "DatabaseFilterValue";
        value.Text = filter?.Value ?? string.Empty;
        TextBlock status = MutedText.Wrapped(said);
        status.Name = "DatabaseTabStatus";
        status.Margin = new Thickness(Gaps.Normal, Gaps.Tight);
        Banner marked = new() { Name = "DatabaseTabMark" };
        Button apply = ToolButtons.Of(IconKeys.Filter, "Apply Filter");
        apply.Name = "DatabaseApplyFilter";
        Button clear = ToolButtons.Of(IconKeys.Close, "Clear Filter");
        clear.Name = "DatabaseClearFilter";
        Button refresh = ToolButtons.Of(IconKeys.Rerun, "Read Again");
        Button previous = ToolButtons.Of(IconKeys.ArrowUp, "Previous Page");
        previous.Name = "DatabasePreviousPage";
        Button next = ToolButtons.Of(IconKeys.ArrowDown, "Next Page");
        next.Name = "DatabaseNextPage";
        Button cancel = ToolButtons.Of(IconKeys.Stop, "Stop");
        cancel.Name = "DatabaseTableCancel";
        Button add = ToolButtons.Of(AppActions.DatabaseAddRow, IconKeys.Add, "Add Row", request);
        Button delete = ToolButtons.Of(AppActions.DatabaseDeleteRows, IconKeys.Clear, "Delete Rows", request);
        Button submit = ToolButtons.Of(AppActions.DatabaseSubmit, IconKeys.Commit, "Submit Changes", request);
        Button revert = ToolButtons.Of(AppActions.DatabaseRevert, IconKeys.Rollback, "Revert Changes", request);
        apply.Click += (_, _) => Apply();
        value.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Apply();
                e.Handled = true;
            }
        };
        clear.Click += (_, _) =>
        {
            value.Text = string.Empty;
            IdeErrors.Observe(LoadAsync());
        };
        refresh.Click += (_, _) => IdeErrors.Observe(LoadAsync(rows?.Offset ?? 0, filter));
        previous.Click += (_, _) => IdeErrors.Observe(LoadAsync(Math.Max(0, (rows?.Offset ?? 0) - DatabaseClient.PageSize), filter));
        next.Click += (_, _) => IdeErrors.Observe(LoadAsync((rows?.Offset ?? 0) + DatabaseClient.PageSize, filter));
        cancel.Click += (_, _) => operation?.Cancel();
        cells.CellWritten += (_, write) => Write(write);
        cells.RowsDeleted += (_, write) => Delete([.. write.Rows]);
        cells.WriteRefused += (_, _) => Say(WhyNotEdited ?? Refusals.DatabaseBusy);
        cells.SortRequested += (_, wanted) =>
        {
            sort = wanted is { } by && rows is { } read ? (read.Columns[by.Column], by.Descending) : null;
            IdeErrors.Observe(LoadAsync(0, filter));
        };
        Button more = ToolButtons.Of(IconKeys.More, "More");
        more.Name = "DatabaseTableMore";
        more.Click += (_, _) => MoreMenu().ShowAt(more);
        // One row: the filter's fields narrow first, then the parts go whole from the last back, each still in More's menu.
        columns[ShrinkingRow.LeastProperty] = 64;
        value[ShrinkingRow.LeastProperty] = 80;
        ShrinkingRow.SetKeepsWidth(comparison, true);
        ShrinkingRow bar = new() { Name = "DatabaseTableBar", Margin = new Thickness(Gaps.Normal, Gaps.Hair), Spacing = Gaps.Small, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        foreach (Control control in new Control[] { refresh, previous, next, cancel, ToolBars.Rule(), add, delete, submit, revert, ToolBars.Rule(), columns, comparison, value, apply, clear })
        {
            ShrinkingRow.SetGoesWhole(control, true);
            bar.Children.Add(control);
        }
        bar.Children.Add(more);
        Border top = ToolBars.OverEditor(bar);
        DockPanel page = new();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        page.Children.Add(top);
        page.Children.Add(marked);
        page.Children.Add(status);
        page.Children.Add(new Border { Classes = { StyleClasses.Framed }, Child = cells });
        EventHandler following = (_, _) => Paint();
        page.AttachedToVisualTree += (_, _) =>
        {
            Changed += following;
            Paint();
        };
        page.DetachedFromVisualTree += (_, _) => Changed -= following;
        Paint();
        return page;

        void Apply()
        {
            if (columns.SelectedItem is string column && comparison.SelectedItem is string op)
            {
                IdeErrors.Observe(LoadAsync(0, new DatabaseFilter(column, value.Text ?? string.Empty, op)));
            }
        }

        // Every part of the bar as an item, so what a narrow bar lets go of is still a click away.
        MenuFlyout MoreMenu()
        {
            string? idle = operation is null && Valid ? null : Refusals.DatabaseBusy;
            MenuFlyout menu = new() { Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedRight };
            foreach (Control item in new Control[]
            {
                ListMenus.Item("Read Again", () => IdeErrors.Observe(LoadAsync(rows?.Offset ?? 0, filter)), idle, IconKeys.Rerun),
                ListMenus.Item("Previous Page", () => IdeErrors.Observe(LoadAsync(Math.Max(0, (rows?.Offset ?? 0) - DatabaseClient.PageSize), filter)),
                    idle ?? (rows?.Offset > 0 ? null : Refusals.NoPreviousPage), IconKeys.ArrowUp),
                ListMenus.Item("Next Page", () => IdeErrors.Observe(LoadAsync((rows?.Offset ?? 0) + DatabaseClient.PageSize, filter)),
                    idle ?? (rows?.More == true ? null : Refusals.NoNextPage), IconKeys.ArrowDown),
                ListMenus.Item("Stop", () => operation?.Cancel(), operation is null ? Refusals.DatabaseNotReading : null, IconKeys.Stop),
                new Separator(),
                ListMenus.Keyed("Add Row", AppActions.DatabaseAddRow, () => request(AppActions.DatabaseAddRow), WhyNotAddRow),
                ListMenus.Keyed("Delete Rows", AppActions.DatabaseDeleteRows, () => request(AppActions.DatabaseDeleteRows), WhyNotDeleteRows),
                ListMenus.Keyed("Submit Changes", AppActions.DatabaseSubmit, () => request(AppActions.DatabaseSubmit), WhyNotSubmit),
                ListMenus.Keyed("Revert Changes", AppActions.DatabaseRevert, () => request(AppActions.DatabaseRevert), WhyNotRevert),
                new Separator(),
                ListMenus.Item("Apply Filter", Apply, idle, IconKeys.Filter),
                ListMenus.Item("Clear Filter", () =>
                {
                    value.Text = string.Empty;
                    IdeErrors.Observe(LoadAsync());
                }, idle, IconKeys.Close),
            })
            {
                menu.Items.Add(item);
            }
            return menu;
        }

        void Paint()
        {
            bool idle = operation is null && Valid;
            string? chosen = columns.SelectedItem as string ?? filter?.Column;
            columns.ItemsSource = schema?.Columns.Select(c => c.Name).ToArray() ?? [];
            columns.SelectedItem = chosen is not null && schema?.Columns.Any(c => c.Name == chosen) == true ? chosen : schema?.Columns.FirstOrDefault()?.Name;
            foreach (Control control in new Control[] { columns, comparison, value, apply, clear, refresh, cells })
            {
                control.IsEnabled = idle;
            }
            previous.IsEnabled = idle && rows?.Offset > 0;
            next.IsEnabled = idle && rows?.More == true;
            cancel.IsEnabled = operation is not null;
            ToolButtons.Grey(add, WhyNotAddRow, ActionTips.Of(add));
            ToolButtons.Grey(delete, Busy ?? WhyNotEdited, ActionTips.Of(delete));
            ToolButtons.Grey(submit, WhyNotSubmit, ActionTips.Of(submit));
            ToolButtons.Grey(revert, WhyNotRevert, ActionTips.Of(revert));
            cells.Table = shown;
            DatabaseSource now = Now;
            marked.Say(now.Mark switch
            {
                DatabaseMark.Production => $"{now.Name} is marked production: Submit asks before it writes",
                DatabaseMark.ReadOnly => Refusals.ReadOnlyConnection(now.Name),
                _ => null,
            });
            status.Text = Said + (edits.IsEmpty || operation is not null ? string.Empty : $" · {edits.Said}");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        lifetime.Cancel();
        owner.Changed -= OnOwnerChanged;
    }
}
