using System.Data.Common;
using System.Text.Json;
using Passenger.App.Containers;
using Passenger.App.State;
using Passenger.App.Tables;
using Passenger.Core;

namespace Passenger.App.Database;

public sealed class DatabaseController(ActionWork work, Func<string, string, Task<bool>> confirm, Action<string> say)
{
    private ProjectSession? session;
    private Working? running;
    private StateDocument<DatabaseHistory>? kept;
    private CancellationTokenSource lifetime = new();
    private DatabaseSource? connected;

    public event EventHandler? Changed;
    public IReadOnlyList<DatabaseSource> Sources { get; private set; } = [];
    public DatabaseSource? Source { get; private set; }
    public IReadOnlyList<DatabaseTable> Tables { get; private set; } = [];
    public DatabaseTable? Table { get; private set; }
    public DatabaseSchema? Schema { get; private set; }
    public DatabaseRows? Rows { get; private set; }
    public string Sql { get; set; } = "SELECT ";
    public IReadOnlyList<string> History { get; private set; } = [];
    public string Said { get; private set; } = "Refresh to find this folder's database connections. Nothing connects until you choose Connect.";
    public bool Busy => running is not null;
    public bool Discovered { get; private set; }
    public bool Connected => Source is not null && ReferenceEquals(Source, connected);

    public void Keep(StateDocument<DatabaseHistory> history)
    {
        UiThread.Check();
        kept = history;
        History = history.Value.Queries;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        UiThread.Check();
        lifetime.Cancel();
    }

    public void Bind(ProjectSession next)
    {
        UiThread.Check();
        lifetime.Cancel();
        lifetime = new CancellationTokenSource();
        session = next;
        kept = next.State?.Database;
        History = kept?.Value.Queries ?? [];
        Sources = [];
        Discovered = false;
        Source = null;
        Forget();
        Sql = "SELECT ";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Choose(DatabaseSource? source)
    {
        UiThread.Check();
        if (Busy || ReferenceEquals(Source, source))
        {
            return;
        }
        Source = source;
        Forget();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ChooseTable(DatabaseTable? table)
    {
        UiThread.Check();
        if (Busy || Table == table)
        {
            return;
        }
        Table = table;
        Schema = null;
        Rows = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Forget()
    {
        connected = null;
        Tables = [];
        Table = null;
        Schema = null;
        Rows = null;
        Said = "Choose a connection and Connect to browse its tables.";
    }

    public Task RefreshAsync() => RunAsync(AppActions.DatabaseRefresh, "Finding database connections…", async cancel =>
    {
        if (session is null)
        {
            return;
        }
        Forget();
        Sources = [];
        Source = null;
        Sources = await CurrentAsync(DatabaseDiscovery.ReadAsync(session, cancel), cancel);
        Discovered = true;
        Source = Sources.FirstOrDefault();
        Forget();
        Said = Sources.Count == 0 ? "No supported connection strings found in this folder's runs." : "Connections found. Choose one and Connect.";
    });

    public Task ConnectAsync() => RunAsync(AppActions.DatabaseConnect, "Connecting to the database…", async cancel =>
    {
        DatabaseSource source = Source ?? throw new InvalidOperationException("Choose a database connection first");
        Forget();
        Tables = await CurrentAsync(BackgroundWork.Run(() => DatabaseClient.TablesAsync(source, cancel), cancel), cancel);
        connected = source;
        Said = $"{Tables.Count} tables. Choose a table and Browse Rows, or run SQL.";
    });

    public Task BrowseAsync(int offset = 0) => RunAsync(AppActions.DatabaseBrowse, "Reading the table…", async cancel =>
    {
        DatabaseSource source = RequireConnection();
        DatabaseTable table = Table ?? throw new InvalidOperationException("Choose a table first");
        DatabaseSchema schema = await CurrentAsync(BackgroundWork.Run(() => DatabaseClient.SchemaAsync(source, table, cancel), cancel), cancel);
        DatabaseRows rows = await CurrentAsync(BackgroundWork.Run(() => DatabaseClient.PageAsync(source, schema, offset, cancel), cancel), cancel);
        Schema = schema;
        Rows = rows;
        Said = $"{table} · rows {offset + (Rows.RowCount > 0 ? 1 : 0)}–{offset + Rows.RowCount}{(Rows.More ? " · more available" : string.Empty)}"
            + (Schema.Columns.Any(c => c.Primary) ? " · Enter or double-click a cell to edit; writes ask first. NULL clears a nullable cell." : " · no primary key; rows are read-only.");
    });

    public Task QueryAsync() => RunAsync(AppActions.DatabaseQuery, "Running SQL…", async cancel =>
    {
        DatabaseSource source = RequireConnection();
        string sql = Sql;
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new InvalidOperationException("Enter SQL first");
        }
        if (!await confirm("Run SQL", $"Run this SQL against {source}? SQL can change data and schema.\n\n{ContainerSecrets.InSaid(sql)}"))
        {
            Said = "SQL cancelled; nothing was sent.";
            return;
        }
        cancel.ThrowIfCancellationRequested();
        Rows = await CurrentAsync(BackgroundWork.Run(() => DatabaseClient.QueryAsync(source, sql, cancel), cancel), cancel);
        string masked = ContainerSecrets.InSaid(sql);
        History = [.. new[] { masked }.Concat(History).Distinct().Take(100)];
        kept?.Set(new DatabaseHistory { Queries = [.. History] }.Clamped());
        Said = $"{Rows.RowCount} result rows{(Rows.More ? " · first 1000 shown" : string.Empty)}"
            + (Rows.Affected >= 0 ? $" · {Rows.Affected} affected rows" : string.Empty);
    });

    public Task EditAsync(CellWrite write) => RunAsync(AppActions.DatabaseEdit, "Updating the cell…", async cancel =>
    {
        DatabaseSource source = RequireConnection();
        if (write.Table is not DatabaseRows rows || !ReferenceEquals(rows, Rows) || write.Rows is not [int row])
        {
            throw new InvalidOperationException("Edit one cell in the current table page");
        }
        if (!await confirm("Update Database Cell", $"Write {ContainerSecrets.InSaid(write.Text)} to {rows.Schema?.Table}.{rows.Columns[write.Column]}, row {rows.RowNumber(row)} in {source}?"))
        {
            Said = "Cell update cancelled; nothing was written.";
            return;
        }
        cancel.ThrowIfCancellationRequested();
        await BackgroundWork.Run(() => DatabaseClient.UpdateAsync(source, rows, row, write.Column, write.Text, cancel), cancel);
        cancel.ThrowIfCancellationRequested();
        Rows = await CurrentAsync(BackgroundWork.Run(() => DatabaseClient.PageAsync(source, rows.Schema!, rows.Offset, cancel), cancel), cancel);
        Said = "One cell updated; the table page was read again.";
    });

    private DatabaseSource RequireConnection() => Connected ? Source! : throw new InvalidOperationException("Choose a connection and Connect first");

    private static async Task<T> CurrentAsync<T>(Task<T> pending, CancellationToken cancel)
    {
        T value = await pending;
        cancel.ThrowIfCancellationRequested();
        return value;
    }

    private async Task RunAsync(string action, string stage, Func<CancellationToken, Task> run)
    {
        UiThread.Check();
        if (Busy || work.Begin(action, stage) is not { } started)
        {
            return;
        }
        running = started;
        using Working working = started;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(working.Stopping, lifetime.Token);
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await run(stop.Token);
            working.Tell(true, Said);
        }
        catch (OperationCanceledException)
        {
            Said = "Database operation cancelled.";
            working.Tell(false, Said);
        }
        catch (Exception error) when (error is DbException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException or TimeoutException)
        {
            Said = ContainerSecrets.InSaid(error.Message, known: Source is { } source ? [source.ConnectionString] : []);
            IdeErrors.Write($"Database operation failed ({stage}): {Said}");
            working.Tell(false, Said);
        }
        finally
        {
            running = null;
            say(Said);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
