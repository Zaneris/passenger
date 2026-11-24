using System.Data.Common;
using System.Text.Json;
using Passenger.App.Containers;
using Passenger.App.State;
using Passenger.Core;

namespace Passenger.App.Database;

/// <summary>A window's database connections, those found in the open folder and those made by hand (kept for the folder,
/// their secrets in the system's credential store), and the reads the explorer asks of them, one at a time, each holding
/// a <see cref="Working"/>. Nothing connects until the person asks: finding connections reads only the folder's files.</summary>
public sealed class DatabaseController(ActionWork work, Action<string> say)
{
    /// <summary>The service the system's credential store keeps each connection's secret parts under, by its id.</summary>
    public const string CredentialService = "Passenger database connection";

    private ProjectSession? session;
    private Working? running;
    private StateDocument<DatabaseState>? kept;
    private CancellationTokenSource lifetime = new();
    private readonly List<DatabaseSource> made = [];
    private IReadOnlyList<DatabaseSource> found = [];
    private readonly HashSet<Guid> connected = [];
    private readonly Dictionary<string, SqlConsole> consoles = new(PathNames.Comparer);
    private readonly Dictionary<Guid, Task<SqlCatalog?>> catalogs = [];

    public event EventHandler? Changed;

    /// <summary>Every connection: those made by hand, then those found.</summary>
    public IReadOnlyList<DatabaseSource> Sources => [.. made, .. found];

    public string Said { get; private set; } = "Refresh to find this folder's database connections. Nothing connects until you open one.";
    public bool Busy => running is not null;
    public bool Discovered { get; private set; }

    /// <summary>Whether a connection has been opened this session, its explorer read.</summary>
    public bool IsConnected(DatabaseSource source) => connected.Contains(source.Id);

    public DatabaseSource? Find(Guid id) => Sources.FirstOrDefault(s => s.Id == id);

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
        kept = null;
        made.Clear();
        found = [];
        connected.Clear();
        foreach (SqlConsole console in consoles.Values)
        {
            console.Stop();
        }
        consoles.Clear();
        Discovered = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes in the connections kept for the folder; their secrets stay in the system's store until one is opened.</summary>
    public void Keep(StateDocument<DatabaseState> state)
    {
        UiThread.Check();
        kept = state;
        if (session?.Folder is not { } folder)
        {
            return;
        }
        made.Clear();
        made.AddRange(state.Value.Connections.Select(connection => new DatabaseSource(connection.Name, connection.Kind, folder)
        {
            Id = connection.Id,
            ConnectionString = connection.ConnectionString,
            SecretKept = true,
        }));
        foreach ((string path, Guid id) in state.Value.Consoles)
        {
            consoles.TryAdd(path, new SqlConsole(path) { Connection = id });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What a connection's database holds, for a console's completion: read once the connection was opened this
    /// session (completion never opens one), kept until a run or a refresh may have changed it; null where it is not.</summary>
    public Task<SqlCatalog?> CatalogOf(DatabaseSource source)
    {
        UiThread.Check();
        if (!connected.Contains(source.Id))
        {
            return Task.FromResult<SqlCatalog?>(null);
        }
        if (!catalogs.TryGetValue(source.Id, out Task<SqlCatalog?>? catalog))
        {
            CancellationToken cancel = lifetime.Token;
            catalog = catalogs[source.Id] = BackgroundWork.Run(async () =>
            {
                try
                {
                    return await DatabaseClient.CatalogAsync(source, cancel);
                }
                catch (Exception failed) when (failed is DbException or IOException or InvalidOperationException or OperationCanceledException or TimeoutException
                    or System.Net.Sockets.SocketException)
                {
                    IdeErrors.Write($"Database: {source.Name}'s tables were not read for completion: {ContainerSecrets.InSaid(failed.Message, known: [source.ConnectionString])}");
                    return null;
                }
            });
        }
        return catalog;
    }

    /// <summary>The console a .sql file is, where a connection was chosen for it.</summary>
    public SqlConsole? ConsoleOf(string path) => consoles.GetValueOrDefault(path);

    /// <summary>Makes a .sql file a console of <paramref name="source"/>, kept for the folder.</summary>
    public SqlConsole Bind(string path, DatabaseSource source)
    {
        UiThread.Check();
        if (!consoles.TryGetValue(path, out SqlConsole? console))
        {
            console = consoles[path] = new SqlConsole(path);
        }
        console.Connection = source.Id;
        console.Raise();
        Keep();
        Changed?.Invoke(this, EventArgs.Empty);
        return console;
    }

    /// <summary>Runs each of <paramref name="runs"/> in a console against its connection, one after another on one
    /// connection, under a <see cref="Working"/> of its own beside the explorer's reads; false where one runs there already.</summary>
    public async Task<bool> RunAsync(SqlConsole console, DatabaseSource source, IReadOnlyList<string> runs)
    {
        UiThread.Check();
        if (console.Running || work.Begin(AppActions.DatabaseRun, $"Running on {source.Name}…") is not { } started)
        {
            return false;
        }
        using Working working = started;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(working.Stopping, lifetime.Token);
        console.Run(working);
        catalogs.Remove(source.Id);
        List<SqlResult> results = [];
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        string said;
        try
        {
            await BackgroundWork.Run(() => DatabaseClient.RunAsync(source, runs, SqlConsole.RowLimit, result =>
            {
                lock (results)
                {
                    results.Add(result);
                }
            }, stop.Token), stop.Token);
            said = SqlConsole.Summary(results, System.Diagnostics.Stopwatch.GetElapsedTime(began));
            connected.Add(source.Id);
            working.Tell(results is not [.., { Error: not null }], said);
        }
        catch (Exception stopped) when (stopped is OperationCanceledException || stopped is DbException && stop.IsCancellationRequested)
        {
            said = "Stopped.";
            working.Tell(false, said);
        }
        catch (Exception error) when (error is DbException or IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or FormatException or TimeoutException or System.Net.Sockets.SocketException)
        {
            said = ContainerSecrets.InSaid(error.Message, known: [source.ConnectionString]);
            IdeErrors.Write($"Database console failed: {said}");
            working.Tell(false, said);
        }
        lock (results)
        {
            console.Ran([.. results], said);
        }
        say(said);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The connections kept for the folder, written as they change.</summary>
    private void Keep()
    {
        if (kept is null)
        {
            return;
        }
        kept.Set((kept.Value with
        {
            Connections = [.. made.Select(source => new KeptConnection(source.Id, source.Name, source.Kind, source.ConnectionString))],
            Consoles = consoles.Values.Where(c => c.Connection is { } id && made.Any(m => m.Id == id)).ToDictionary(c => c.Path, c => c.Connection!.Value),
        }).Clamped());
    }

    /// <summary>Adds a connection made by hand, kept for the folder, its secrets in the system's store.</summary>
    public void Add(DatabaseSource source)
    {
        UiThread.Check();
        made.Add(Kept(source with { Found = null }, keepsOld: false));
        Keep();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Puts <paramref name="replacement"/> in a connection's place; a found one changed becomes one made by hand.</summary>
    public void Replace(DatabaseSource original, DatabaseSource replacement)
    {
        UiThread.Check();
        DatabaseSource changed = Kept(replacement with { Id = original.Id, Found = null }, keepsOld: original.SecretKept);
        int at = made.FindIndex(s => s.Id == original.Id);
        if (at >= 0)
        {
            made[at] = changed;
        }
        else
        {
            found = [.. found.Where(s => s.Id != original.Id)];
            made.Add(changed);
        }
        if (original.ConnectionString != changed.ConnectionString || original.Kind != changed.Kind)
        {
            connected.Remove(original.Id);
        }
        Keep();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets a connection made by hand and its secrets; a found one comes back at the next refresh.</summary>
    public void Remove(DatabaseSource source)
    {
        UiThread.Check();
        made.RemoveAll(s => s.Id == source.Id);
        found = [.. found.Where(s => s.Id != source.Id)];
        connected.Remove(source.Id);
        SystemCredentials.Forget(CredentialService, source.Account);
        Keep();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A connection as kept: its secret parts put in the system's store and taken out of its string. One given with
    /// none keeps those stored already where <paramref name="keepsOld"/> (an edit that left the password as it was).</summary>
    private static DatabaseSource Kept(DatabaseSource source, bool keepsOld)
    {
        (string rest, string secret) = KeptConnection.Split(source.Kind, source.ConnectionString);
        if (secret.Length > 0)
        {
            SystemCredentials.Store(CredentialService, source.Account, secret);
        }
        else if (!keepsOld)
        {
            SystemCredentials.Forget(CredentialService, source.Account);
        }
        return source with { ConnectionString = rest, SecretKept = true };
    }

    /// <summary>Forgets that a connection was opened, so its explorer reads it again when next opened.</summary>
    public void Disconnect(DatabaseSource source)
    {
        UiThread.Check();
        connected.Remove(source.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Says why what the person asked was refused.</summary>
    public void Refuse(string why)
    {
        UiThread.Check();
        Said = why;
        say(why);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void CancelOperation()
    {
        UiThread.Check();
        running?.Cancel();
    }

    /// <summary>Finds the folder's connections again, in its settings, run environments and running services, opening none.</summary>
    public Task RefreshAsync() => RunAsync(AppActions.DatabaseRefresh, "Finding database connections…", async cancel =>
    {
        if (session is null)
        {
            return true;
        }
        IReadOnlyList<DatabaseSource> before = found;
        IReadOnlyList<DatabaseSource> read = await DatabaseDiscovery.ReadAsync(session, cancel);
        cancel.ThrowIfCancellationRequested();
        found = [.. read.Where(s => !made.Any(m => m.Kind == s.Kind && m.ConnectionString == s.ConnectionString))
            .Select(s => before.FirstOrDefault(old => old.Name == s.Name && old.Kind == s.Kind && old.ConnectionString == s.ConnectionString) is { } old ? s with { Id = old.Id } : s)];
        connected.RemoveWhere(id => Find(id) is null);
        catalogs.Clear();
        Discovered = true;
        Said = Sources.Count == 0 ? "No database connections found in this folder's settings or runs. Add one with New Connection…"
            : $"{Plural.Of(Sources.Count, "connection")}. Open one to read its databases.";
        return true;
    }) ?? Task.CompletedTask;

    /// <summary>Reads what the explorer shows under a connection, a database or a table, opening the connection as the person
    /// asked; null where it failed or was stopped, said in <see cref="Said"/>.</summary>
    public async Task<T?> ReadAsync<T>(DatabaseSource source, string stage, Func<CancellationToken, Task<T>> read)
        where T : class
    {
        T? result = null;
        await (RunAsync(AppActions.DatabaseConnect, stage, async cancel =>
        {
            result = await BackgroundWork.Run(() => read(cancel), cancel);
            cancel.ThrowIfCancellationRequested();
            connected.Add(source.Id);
            Said = $"{source.Name}: read.";
            return true;
        }, source) ?? Task.CompletedTask);
        return result;
    }

    /// <summary>Runs one read at a time under a <see cref="Working"/>, saying what it came to; null where another runs.</summary>
    private Task? RunAsync(string action, string stage, Func<CancellationToken, Task<bool>> run, DatabaseSource? source = null)
    {
        UiThread.Check();
        if (Busy || work.Begin(action, stage) is not { } started)
        {
            return null;
        }
        return RunAsync(started, run, source);
    }

    private async Task RunAsync(Working started, Func<CancellationToken, Task<bool>> run, DatabaseSource? source)
    {
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
            Said = "Database operation stopped.";
            working.Tell(false, Said);
        }
        catch (DbException) when (stop.IsCancellationRequested)
        {
            Said = "Database operation stopped.";
            working.Tell(false, Said);
        }
        catch (Exception error) when (error is DbException or IOException or UnauthorizedAccessException or JsonException or ArgumentException
            or InvalidOperationException or FormatException or OverflowException or TimeoutException or System.Net.Sockets.SocketException)
        {
            Said = ContainerSecrets.InSaid(error.Message, known: source is null ? [] : [source.ConnectionString]);
            IdeErrors.Write($"Database operation failed: {Said}");
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
