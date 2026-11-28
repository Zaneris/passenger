using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Passenger.App.Containers;
using Passenger.App.Tables;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.App.Database;

/// <summary>A table's or view's rows among the editors, read a page at a time, sorted and filtered on the server. Its cells
/// written, rows added (in the empty row last) and rows deleted are held as changes not yet written, marked as a diff marks
/// its lines, until Submit writes them in one transaction or Revert drops them; Undo and Redo step through them meanwhile.
/// A row is found again by its primary key, so a table without one, or a connection marked read-only, takes no change.</summary>
public sealed class DatabaseTableTab : IDatabaseTab
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

    public string ShownAs => "a database table";

    /// <summary>The page last read.</summary>
    public DatabaseRows? Rows => rows;

    /// <summary>What its database said of the page last read beside its rows (a Redis key's type and time to live), which its
    /// header line shows; null for nothing.</summary>
    public string? Header => rows?.About;

    /// <summary>The page shown with the changes not yet written over it.</summary>
    public PendingTable? Shown => shown;

    /// <summary>The changes not yet written.</summary>
    public PendingEdits Edits => edits;

    /// <summary>Whether it shows a collection's documents rather than a table's rows.</summary>
    public bool HoldsDocuments => DatabaseKinds.HoldsDocuments(connection.Kind);

    /// <summary>What a row is, as its bar and menus name it.</summary>
    public string Noun => connection.Kind == DatabaseKind.DynamoDb ? "Item" : HoldsDocuments ? "Document" : "Row";

    /// <summary>How many rows (a collection's documents) its changes change, delete and add, said in a line.</summary>
    public string EditsSaid => edits.SaidOf(Noun.ToLowerInvariant());

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
    public Words? WhyNotEdited => Now.Mark == DatabaseMark.ReadOnly ? Refusals.ReadOnlyConnection(Now.Name)
        : !DatabaseKinds.EditsRows(Now.Kind) ? Refusals.NoRowEdits(DatabaseKinds.Title(Now.Kind))
        : schema is not null && !schema.Columns.Any(c => c.Primary) ? Refusals.NoPrimaryKey(Table.ToString()) : null;

    private Words? Busy => operation is not null || !Valid ? Refusals.DatabaseBusy : null;

    public Words? WhyNotSubmit => Busy ?? (edits.IsEmpty ? Refusals.NoPendingChanges : WhyNotEdited);
    public Words? WhyNotRevert => Busy ?? (edits.IsEmpty ? Refusals.NoPendingChanges : null);
    public Words? WhyNotAddRow => Busy ?? WhyNotEdited ?? (schema is { RowsFixed: true } ? Refusals.RowsFixed : shown is null ? Refusals.DatabaseBusy : null);
    public Words? WhyNotDeleteRows => Busy ?? WhyNotEdited ?? (schema is { RowsFixed: true } ? Refusals.RowsFixed : ChosenRows().Count == 0 ? Refusals.NoRowChosen : null);
    /// <summary>Why a file's rows may not be imported into the table: its connection marked read-only, or its columns not read yet.</summary>
    public Words? WhyNotImport => DatabaseController.WhyNotImport(Now) ?? (schema is null ? Refusals.DatabaseBusy : null);

    /// <summary>The table's columns as last read, null until they are.</summary>
    public DatabaseSchema? Schema => schema;

    /// <summary>The connection as it stands now, opening the tab's database.</summary>
    public DatabaseSource Source => Now;

    public Words? WhyNotStep(bool redo) => Busy ?? ((redo ? undone : done).Count == 0 ? redo ? Refusals.NoPendingRedo : Refusals.NoPendingUndo : null);

    private void OnOwnerChanged(object? sender, EventArgs e)
    {
        if (!Valid)
        {
            operation?.Cancel();
            said = "The connection changed. Reopen this table from Database.";
        }
        Follow();
        Document.TitleChanged();
    }

    /// <summary>Why another page may not be read now: a document database's changes not written, which stand by the page's
    /// own fields, as another page of its documents may hold others in another order.</summary>
    private Words? WhyNotLeavePage => !edits.IsEmpty && DatabaseKinds.HoldsDocuments(Now.Kind) ? Refusals.DocumentChangesPending : null;

    /// <summary>Reads a page of the table, filtered and sorted as asked; refused where its changes may not leave the page shown.</summary>
    public Task LoadAsync(int offset = 0, DatabaseFilter? next = null)
    {
        if (WhyNotLeavePage is { } why)
        {
            Say(why.Shown);
            return Task.CompletedTask;
        }
        return Read(offset, next);
    }

    private Task Read(int offset, DatabaseFilter? next) => RunAsync(AppActions.DatabaseOpenTable, $"Reading {Table}", async cancel =>
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

    /// <summary>Reads the page shown again, as its filter and sort read it.</summary>
    public Task ReadAgainAsync() => LoadAsync(rows?.Offset ?? 0, filter);

    /// <summary>The rows an export of the tab writes, every one of its table's as its filter and sort read them; null until
    /// its columns are read.</summary>
    public ExportSource? Exported()
    {
        if (schema is not { } read)
        {
            return null;
        }
        DatabaseSource source = Now;
        (string sql, List<(string Name, object? Value)> parameters) = DatabaseClient.Selecting(source.Kind, read, filter, sort);
        string which = filter switch
        {
            null => string.Empty,
            { Operator: "Is NULL" } => $" whose {filter.Column} is NULL",
            { Operator: "Is not NULL" } => $" whose {filter.Column} is not NULL",
            { Operator: "Equals" } => $" whose {filter.Column} is '{filter.Value}'",
            _ => $" whose {filter.Column} holds '{filter.Value}'",
        };
        string order = sort is { } by ? $" in {by.Column}'s order{(by.Descending ? ", descending" : string.Empty)}" : string.Empty;
        return new ExportSource(source, Table.ToString(), new InsertTarget(source.Kind, DatabaseClient.Full(source.Kind, Table)))
        {
            Sql = sql,
            Parameters = parameters,
            Said = $"Writes {(filter is null ? "every row" : "the rows")} of {Table}{which}{order} to a file, reading them from {source.Name} a row at a time.",
        };
    }

    /// <summary>Writes the changes in one transaction, asking first on a connection marked production, then reads the page again.</summary>
    public async Task SubmitAsync()
    {
        UiThread.Check();
        if (WhyNotSubmit is { } why)
        {
            Say(why.Shown);
            return;
        }
        DatabaseSource source = Now;
        PendingEdits writing = edits;
        if (source.Mark == DatabaseMark.Production && !await ask("Submit to Production",
            $"{source.Name} is marked production. Write {writing.SaidOf(Noun.ToLowerInvariant()).Replace(" pending", string.Empty, StringComparison.Ordinal)} to {Table}?", "Submit"))
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
            try
            {
                await BackgroundWork.Run(() => DatabaseClient.SubmitAsync(source, read, page, writing, cancel), cancel);
            }
            catch (PartlyWritten partly)
            {
                // Those written are the database's now: the rest wait on the page as it was read, none written twice.
                edits = partly.Left;
                done.Clear();
                undone.Clear();
                said = $"{Containers.ContainerSecrets.InSaid(partly.Message)} Written before it: {writing.Besides(partly.Left, Noun.ToLowerInvariant())}; "
                    + $"{partly.Left.SaidOf(Noun.ToLowerInvariant())}.";
                Show(page);
                return;
            }
            edits = PendingEdits.None;
            done.Clear();
            undone.Clear();
            string written = writing.SaidOf(Noun.ToLowerInvariant()).Replace(" pending", " written", StringComparison.Ordinal);
            said = $"{written}.";
            (string Column, bool Descending)? order = sort;
            try
            {
                Show(await BackgroundWork.Run(() => DatabaseClient.PageAsync(source, read, page.Offset, cancel, filter, order), cancel));
            }
            catch (Exception stopped) when (cancel.IsCancellationRequested && stopped is OperationCanceledException or System.Data.Common.DbException)
            {
                // Stop came once the changes were the database's: they stay said written, the page as it was read until read again.
                said = $"{written}; stopped before the table was read again.";
                Show(page);
            }
        });
    }

    /// <summary>Drops the changes not yet written, as a step Undo takes back.</summary>
    public void Revert()
    {
        if (WhyNotRevert is { } why)
        {
            Say(why.Shown);
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
            Say(why.Shown);
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
            Say((WhyNotAddRow ?? Refusals.DatabaseBusy).Shown);
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
            Say(why.Shown);
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
            Say(refused.Shown);
            return;
        }
        if (write.Table is not PendingTable table || !ReferenceEquals(table, shown) || write.Rows is not [int row])
        {
            return;
        }
        // The box held the cell as shown, a secret in it masked: what the masks hid goes back where they still stand.
        if (ContainerSecrets.Unmasked(table.Whole(row, write.Column), write.Text) is not { } unmasked)
        {
            Say(Refusals.MaskedPartChanged.Shown);
            return;
        }
        string? text = unmasked == "NULL" ? null : unmasked;
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
            // A field the document lacks is written whatever is written in it, NULL adding it as null.
            Edit(edits.Write(key, read, write.Column, text, read[write.Column] is not AbsentField && text == (read[write.Column] is null ? null : table.Page.Text(row, write.Column))));
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
        shown = new PendingTable(page, edits, editable: WhyNotEdited is null) { Noun = Noun };
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
        TextBlock header = MutedText.Wrapped(string.Empty);
        header.Name = "DatabaseTabHeader";
        header.Margin = new Thickness(Gaps.Normal, Gaps.Tight);
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
        Button add = ToolButtons.Of(AppActions.DatabaseAddRow, IconKeys.Add, $"Add {Noun}", request);
        Button delete = ToolButtons.Of(AppActions.DatabaseDeleteRows, IconKeys.Clear, $"Delete {Noun}s", request);
        Button submit = ToolButtons.Of(AppActions.DatabaseSubmit, IconKeys.Commit, "Submit Changes", request);
        Button revert = ToolButtons.Of(AppActions.DatabaseRevert, IconKeys.Rollback, "Revert Changes", request);
        Button export = ToolButtons.Of(AppActions.DatabaseExport, IconKeys.Export, "Export Data…", request);
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
        cells.WriteRefused += (_, _) => Say((WhyNotEdited ?? Refusals.DatabaseBusy).Shown);
        cells.SortRequested += (_, wanted) =>
        {
            if (WhyNotLeavePage is { } why)
            {
                Say(why.Shown);
                return;
            }
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
        foreach (Control control in new Control[] { refresh, previous, next, cancel, export, ToolBars.Rule(), add, delete, submit, revert, ToolBars.Rule(), columns, comparison, value, apply, clear })
        {
            ShrinkingRow.SetGoesWhole(control, true);
            bar.Children.Add(control);
        }
        bar.Children.Add(more);
        Border top = ToolBars.OverEditor(bar);
        DockPanel page = new();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        DockPanel.SetDock(header, Dock.Top);
        page.Children.Add(top);
        page.Children.Add(header);
        page.Children.Add(marked);
        page.Children.Add(status);
        Border framed = new() { Classes = { StyleClasses.Framed }, Child = cells };
        if (DatabaseKinds.HoldsDocuments(connection.Kind))
        {
            // A collection's documents: the grid of their fields, and the chosen one whole beside it.
            DocumentTree tree = new() { Name = "DatabaseTabDocument" };
            void FollowChoice() => tree.Show(shown is { } now && now.IsRead(cells.Caret.Row) && cells.Caret.Row < now.Page.Documents.Length
                ? now.Page.Documents[cells.Caret.Row] : null);
            cells.CaretMoved += (_, _) => FollowChoice();
            cells.TableChanged += (_, _) => FollowChoice();
            page.Children.Add(new PaneSplit(framed, tree, "3*,1,2*"));
        }
        else
        {
            page.Children.Add(framed);
        }
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
            Words? idle = operation is null && Valid ? null : Refusals.DatabaseBusy;
            Words? leaving = idle ?? WhyNotLeavePage;
            MenuFlyout menu = new() { Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedRight };
            foreach (Control item in new Control[]
            {
                ListMenus.Item("Read Again", () => IdeErrors.Observe(LoadAsync(rows?.Offset ?? 0, filter)), leaving, IconKeys.Rerun),
                ListMenus.Item("Previous Page", () => IdeErrors.Observe(LoadAsync(Math.Max(0, (rows?.Offset ?? 0) - DatabaseClient.PageSize), filter)),
                    leaving ?? (rows?.Offset > 0 ? null : Refusals.NoPreviousPage), IconKeys.ArrowUp),
                ListMenus.Item("Next Page", () => IdeErrors.Observe(LoadAsync((rows?.Offset ?? 0) + DatabaseClient.PageSize, filter)),
                    leaving ?? (rows?.More == true ? null : Refusals.NoNextPage), IconKeys.ArrowDown),
                ListMenus.Item("Stop", () => operation?.Cancel(), operation is null ? Refusals.DatabaseNotReading : null, IconKeys.Stop),
                ListMenus.Keyed("Export Data…", AppActions.DatabaseExport, () => request(AppActions.DatabaseExport), schema is null ? Refusals.DatabaseBusy : null),
                ListMenus.Keyed("Import Data…", AppActions.DatabaseImport, () => request(AppActions.DatabaseImport), WhyNotImport),
                new Separator(),
                ListMenus.Keyed($"Add {Noun}", AppActions.DatabaseAddRow, () => request(AppActions.DatabaseAddRow), WhyNotAddRow),
                ListMenus.Keyed($"Delete {Noun}s", AppActions.DatabaseDeleteRows, () => request(AppActions.DatabaseDeleteRows), WhyNotDeleteRows),
                ListMenus.Keyed("Submit Changes", AppActions.DatabaseSubmit, () => request(AppActions.DatabaseSubmit), WhyNotSubmit),
                ListMenus.Keyed("Revert Changes", AppActions.DatabaseRevert, () => request(AppActions.DatabaseRevert), WhyNotRevert),
                new Separator(),
                ListMenus.Item("Apply Filter", Apply, leaving, IconKeys.Filter),
                ListMenus.Item("Clear Filter", () =>
                {
                    value.Text = string.Empty;
                    IdeErrors.Observe(LoadAsync());
                }, leaving, IconKeys.Close),
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
            marked.Say((now.Mark switch
            {
                DatabaseMark.Production => $"{now.Name} is marked production: Submit asks before it writes",
                DatabaseMark.ReadOnly => Refusals.ReadOnlyConnection(now.Name),
                _ => null,
            })?.Shown);
            status.Text = Said + (edits.IsEmpty || operation is not null ? string.Empty : $" · {EditsSaid}");
            header.Text = Header is { } read ? Containers.ContainerSecrets.InSaid(read) : string.Empty;
            header.IsVisible = Header is not null;
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
