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

    /// <summary>The history and parameters of a window keeping no state, for its session alone.</summary>
    private DatabaseState unkept = new();

    private DatabaseState State => kept?.Value ?? unkept;

    private void SetState(DatabaseState state)
    {
        if (kept is null)
        {
            unkept = state;
        }
        else
        {
            kept.Set(state);
        }
    }
    private CancellationTokenSource lifetime = new();
    private readonly List<DatabaseSource> made = [];
    private IReadOnlyList<DatabaseSource> found = [];

    /// <summary>Connections the IDE's other windows asked for that the folder's files do not name (a context's database written
    /// in its code), listed with those found until the folder's end.</summary>
    private readonly List<DatabaseSource> asked = [];
    private Task? under;
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
        SshTunnels.Close([.. Sources.Select(s => s.Id)]);
        foreach (SqlConsole console in consoles.Values)
        {
            LetGo(console);
        }
    }

    /// <summary>Closes a console's held connection, an open transaction rolled back as it closes.</summary>
    private static void LetGo(SqlConsole console)
    {
        if (console.Session is { } session)
        {
            console.Session = null;
            UnawaitedWork.Dismiss(BackgroundWork.Run(async () => await session.DisposeAsync()));
        }
    }

    public void Bind(ProjectSession next)
    {
        UiThread.Check();
        lifetime.Cancel();
        SshTunnels.Close([.. Sources.Select(s => s.Id)]);
        lifetime = new CancellationTokenSource();
        session = next;
        kept = null;
        unkept = new DatabaseState();
        made.Clear();
        found = [];
        asked.Clear();
        connected.Clear();
        foreach (SqlConsole console in consoles.Values)
        {
            console.Stop();
            LetGo(console);
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
            Mark = connection.Mark,
            Tunnel = connection.Tunnel,
            SignIn = connection.SignIn,
        }));
        foreach ((string path, Guid id) in state.Value.Consoles)
        {
            consoles.TryAdd(path, new SqlConsole(path) { Connection = id });
        }
        foreach (string path in state.Value.ManualCommit)
        {
            if (!consoles.TryGetValue(path, out SqlConsole? console))
            {
                console = consoles[path] = new SqlConsole(path);
            }
            console.Manual = true;
        }
        found = [.. found.Select(Marked)];
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

    /// <summary>What ran on a connection, newest first, as kept for the folder.</summary>
    public IReadOnlyList<SqlRan> HistoryOf(DatabaseSource source) => State.History.GetValueOrDefault(source.HistoryKey) ?? [];

    /// <summary>The values a console's parameters were last given, by name.</summary>
    public IReadOnlyDictionary<string, string> ParametersOf(SqlConsole console) => State.Parameters.GetValueOrDefault(console.Path) ?? [];

    /// <summary>Keeps the values a console's parameters were given, for the next time it asks.</summary>
    public void KeepParameters(SqlConsole console, IReadOnlyDictionary<string, string> values)
    {
        UiThread.Check();
        Dictionary<string, Dictionary<string, string>> all = new(State.Parameters);
        all[console.Path] = new Dictionary<string, string>(all.GetValueOrDefault(console.Path) ?? []).Concat(values)
            .GroupBy(v => v.Key).ToDictionary(g => g.Key, g => g.Last().Value);
        SetState((State with { Parameters = all }).Clamped());
    }

    /// <summary>Keeps what a run came to in its connection's history, each statement newest first, one run again taking
    /// its earlier place's.</summary>
    private void Remember(DatabaseSource source, IReadOnlyList<SqlResult> results)
    {
        if (results.Count == 0)
        {
            return;
        }
        List<SqlRan> history = [.. State.History.GetValueOrDefault(source.HistoryKey) ?? []];
        foreach (SqlResult result in results)
        {
            string statement = result.Statement.Length > DatabaseState.LongestRun ? result.Statement[..DatabaseState.LongestRun] : result.Statement;
            history.RemoveAll(r => r.Statement == statement);
            history.Insert(0, new SqlRan(statement, DateTime.UtcNow, SqlConsole.Outcome(result), result.Error is not null));
        }
        Dictionary<string, List<SqlRan>> all = new(State.History) { [source.HistoryKey] = history };
        SetState((State with { History = all }).Clamped());
    }

    /// <summary>Runs each of <paramref name="runs"/> in a console against its connection, one after another on one
    /// connection, under a <see cref="Working"/> of its own beside the explorer's reads, its parameters given the
    /// <paramref name="values"/> asked; false where one runs there already.</summary>
    public async Task<bool> RunAsync(SqlConsole console, DatabaseSource source, IReadOnlyList<string> runs, IReadOnlyDictionary<string, string>? values = null)
    {
        UiThread.Check();
        if (console.Manual && runs is [string alone] && SqlText.TransactionOf(alone, source.Kind) is { } transaction)
        {
            // Manual commit's transaction is the console's own: its COMMIT and ROLLBACK end it, and its BEGIN has nothing to open.
            if (transaction == SqlTransaction.Begin || !console.InTransaction)
            {
                console.Ran(console.Results, console.InTransaction ? "A transaction is open already." : Refusals.NoTransaction);
                return true;
            }
            await EndTransactionAsync(console, transaction == SqlTransaction.Commit);
            return true;
        }
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
            Action<SqlResult> told = result =>
            {
                lock (results)
                {
                    results.Add(result);
                }
            };
            if (console.Manual)
            {
                if (console.Session is { } held && held.Source.Id != source.Id)
                {
                    LetGo(console);
                }
                SqlSession session = console.Session ??= await BackgroundWork.Run(() => SqlSession.OpenAsync(source, stop.Token), stop.Token);
                await BackgroundWork.Run(() => session.RunAsync(runs, SqlConsole.RowLimit, told, stop.Token, values), stop.Token);
            }
            else
            {
                await BackgroundWork.Run(() => DatabaseClient.RunAsync(source, runs, SqlConsole.RowLimit, told, stop.Token, values), stop.Token);
            }
            said = SqlConsole.Summary(results, System.Diagnostics.Stopwatch.GetElapsedTime(began));
            if (console.Session is { InTransaction: true } open)
            {
                said += $" · transaction open: {Plural.Of(open.Statements, "statement")}, {Plural.Of(open.Changed, "row")} changed";
            }
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
        List<SqlResult> ran;
        lock (results)
        {
            ran = [.. results];
        }
        Remember(source, ran);
        console.Ran(ran, said);
        say(said);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Explains a console's statement against its connection, under a <see cref="Working"/> of its own, never
    /// running it; false where one runs there already.</summary>
    public async Task<bool> ExplainAsync(SqlConsole console, DatabaseSource source, string statement, IReadOnlyDictionary<string, string>? values)
    {
        UiThread.Check();
        if (console.Running || work.Begin(AppActions.DatabaseExplain, $"Explaining on {source.Name}…") is not { } started)
        {
            return false;
        }
        using Working working = started;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(working.Stopping, lifetime.Token);
        console.Run(working);
        PlanStep? plan = null;
        string said;
        try
        {
            plan = await BackgroundWork.Run(() => DatabaseClient.ExplainAsync(source, statement, values, stop.Token), stop.Token);
            connected.Add(source.Id);
            PlanStep? costliest = SqlPlans.Costliest(plan);
            said = $"Plan of {Plural.Of(SqlPlans.Each(plan).Count() - 1, "step")}" + (costliest is null ? ", its costs not given" : $", the costliest {costliest.Operation}");
            working.Tell(true, said);
        }
        catch (Exception stopped) when (stopped is OperationCanceledException || stopped is DbException && stop.IsCancellationRequested)
        {
            said = "Stopped.";
            working.Tell(false, said);
        }
        catch (Exception error) when (error is DbException or IOException or InvalidOperationException or FormatException or TimeoutException or JsonException
            or System.Xml.XmlException or System.Net.Sockets.SocketException or KeyNotFoundException)
        {
            said = ContainerSecrets.InSaid(error.Message, known: [source.ConnectionString]);
            IdeErrors.Write($"Database explain failed: {said}");
            working.Tell(false, said);
        }
        console.Explained(statement, plan, said);
        say(said);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The next export held by a test (<see cref="HoldExport"/>): after how many rows, what it waits for, and told
    /// of the export once it waits.</summary>
    private (long At, Task Until, TaskCompletionSource<RowExport> Holding)? exportHeld;

    /// <summary>Holds the next export once it has written <paramref name="rows"/> rows until <paramref name="until"/> ends,
    /// for a test of what it holds meanwhile; done with the export once it is held.</summary>
    public Task<RowExport> HoldExport(long rows, Task until)
    {
        TaskCompletionSource<RowExport> holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        exportHeld = (rows, until, holding);
        return holding.Task;
    }

    /// <summary>Writes rows to a file as <paramref name="choice"/> says, read from the server as they come, under a
    /// <see cref="Working"/> that says how many it has written and that Stop ends, the file left as it was then; what it
    /// came to is said. Null once written, else why not.</summary>
    public async Task<string?> ExportAsync(ExportSource source, ExportChoice choice)
    {
        UiThread.Check();
        if (work.Begin(AppActions.DatabaseExport, $"Exporting {source.Title}…") is not { } started)
        {
            return ActionWork.Busy(work.Of(AppActions.DatabaseExport)!);
        }
        using Working working = started;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(working.Stopping, lifetime.Token);
        string file = Path.GetFileName(choice.Path);
        bool ended = false;
        (long At, Func<RowExport, Task> Hold)? hold = null;
        if (exportHeld is { } held)
        {
            exportHeld = null;
            hold = (held.At, export =>
            {
                held.Holding.TrySetResult(export);
                return held.Until;
            });
        }
        string said;
        bool done = false;
        try
        {
            long rows = await BackgroundWork.Run(() => RowExport.RunAsync(source, choice, written => UiThread.Post(() =>
            {
                if (!ended)
                {
                    working.Say($"Exporting {source.Title}: {Plural.Of(written, "row")} written…");
                }
            }), stop.Token, hold), stop.Token);
            if (source.Held is null)
            {
                connected.Add(source.Connection.Id);
            }
            long size = await BackgroundWork.Run(() => new FileInfo(choice.Path).Length);
            said = $"Exported {Plural.Of(rows, "row")} of {source.Title} to {file} ({Sizes.Said(size)})";
            done = true;
        }
        catch (Exception stopped) when (stopped is OperationCanceledException || stopped is DbException && stop.IsCancellationRequested)
        {
            said = $"Export stopped: {file} is as it was";
        }
        catch (Exception error) when (error is DbException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException
            or InvalidCastException or NotSupportedException or TimeoutException or System.Net.Sockets.SocketException)
        {
            said = $"{file} was not written: {ContainerSecrets.InSaid(error.Message, known: [source.Connection.ConnectionString])}";
            IdeErrors.Write($"Database export failed: {said}");
        }
        ended = true;
        working.Tell(done, said);
        say(said);
        return done ? null : said;
    }

    /// <summary>Writes a file's rows into a table as <paramref name="choice"/> maps them, in one transaction, under a
    /// <see cref="Working"/> that says how many it has written and that Stop ends, nothing written then; what it came to is
    /// said, a row refused by its number. Null once written, else why not.</summary>
    public async Task<string?> ImportAsync(DatabaseSource source, ImportChoice choice)
    {
        UiThread.Check();
        string file = Path.GetFileName(choice.File.Path);
        if (work.Begin(AppActions.DatabaseImport, $"Importing {file} into {choice.Table}…") is not { } started)
        {
            return ActionWork.Busy(work.Of(AppActions.DatabaseImport)!);
        }
        using Working working = started;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(working.Stopping, lifetime.Token);
        bool ended = false;
        string said;
        bool done = false;
        try
        {
            long rows = await BackgroundWork.Run(() => RowImport.RunAsync(source, choice, written => UiThread.Post(() =>
            {
                if (!ended)
                {
                    working.Say($"Importing {file} into {choice.Table}: {Plural.Of(written, "row")} written…");
                }
            }), stop.Token), stop.Token);
            said = $"Imported {Plural.Of(rows, "row")} of {file} into {(choice.Made ? "the new table " : string.Empty)}{choice.Table}";
            done = true;
        }
        catch (ImportRefused refused)
        {
            said = Refusals.RowRefused(refused.Row, file, ContainerSecrets.InSaid(refused.Message, known: [source.ConnectionString]));
        }
        catch (Exception stopped) when (stopped is OperationCanceledException || stopped is DbException && stop.IsCancellationRequested)
        {
            said = $"Import stopped: nothing of {file} was written";
        }
        catch (Exception error) when (error is DbException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException
            or JsonException or TimeoutException or System.Net.Sockets.SocketException)
        {
            said = $"{file} was not imported: {ContainerSecrets.InSaid(error.Message, known: [source.ConnectionString])}";
            IdeErrors.Write($"Database import failed: {said}");
        }
        ended = true;
        connected.Add(source.Id);
        catalogs.Remove(source.Id);
        working.Tell(done, said);
        say(said);
        Changed?.Invoke(this, EventArgs.Empty);
        return done ? null : said;
    }

    /// <summary>The connections kept for the folder, written as they change; a window keeping no state keeps them for its
    /// session alone, so a found connection's settings stand through a refresh there too.</summary>
    private void Keep()
    {
        SetState((State with
        {
            Connections = [.. made.Select(source => new KeptConnection(source.Id, source.Name, source.Kind, source.ConnectionString, source.Mark, source.Tunnel, source.SignIn))],
            Consoles = consoles.Values.Where(c => c.Connection is { } id && made.Any(m => m.Id == id)).ToDictionary(c => c.Path, c => c.Connection!.Value),
            ManualCommit = [.. consoles.Values.Where(c => c.Manual).Select(c => c.Path)],
            FoundMarks = new Dictionary<string, DatabaseMark>(State.FoundMarks.Where(m => !found.Any(f => f.FoundKey == m.Key))
                .Concat(found.Where(f => f.Mark != DatabaseMark.None).Select(f => KeyValuePair.Create(f.FoundKey, f.Mark)))),
            FoundSettings = new Dictionary<string, FoundSetting>(State.FoundSettings.Where(m => !found.Any(f => f.FoundKey == m.Key))
                .Concat(found.Where(f => f.Over.Length > 0 || f.Tunnel is not null || f.SignIn is not null)
                    .Select(f => KeyValuePair.Create(f.FoundKey, new FoundSetting(f.Over, f.Tunnel, f.SignIn))))),
        }).Clamped());
    }

    /// <summary>A found connection with the mark, SSL and tunnel kept for it.</summary>
    private DatabaseSource Marked(DatabaseSource source) =>
        source with
        {
            Mark = State.FoundMarks.GetValueOrDefault(source.FoundKey),
            Over = State.FoundSettings.GetValueOrDefault(source.FoundKey)?.Over ?? string.Empty,
            Tunnel = State.FoundSettings.GetValueOrDefault(source.FoundKey)?.Tunnel,
            SignIn = State.FoundSettings.GetValueOrDefault(source.FoundKey)?.SignIn,
        };

    /// <summary>Marks a connection read-only, production or neither, kept for the folder, found or made by hand.</summary>
    public void SetMark(DatabaseSource source, DatabaseMark mark)
    {
        UiThread.Check();
        int at = made.FindIndex(s => s.Id == source.Id);
        if (at >= 0)
        {
            made[at] = made[at] with { Mark = mark };
        }
        else
        {
            found = [.. found.Select(s => s.Id == source.Id ? s with { Mark = mark } : s)];
        }
        Keep();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The consoles holding a transaction open.</summary>
    public IReadOnlyList<SqlConsole> OpenTransactions => [.. consoles.Values.Where(c => c.InTransaction)];

    /// <summary>Whether a connection's consoles hold a transaction open.</summary>
    public bool InTransaction(DatabaseSource source) => consoles.Values.Any(c => c.Connection == source.Id && c.InTransaction);

    /// <summary>Has a console run in manual commit, its statements in a transaction until Commit or Rollback, or commit
    /// each as it runs; refused while its transaction is open.</summary>
    public string? SetManual(SqlConsole console, bool manual)
    {
        UiThread.Check();
        if (console.Manual == manual)
        {
            return null;
        }
        if (console.InTransaction || console.Running)
        {
            console.Raise();
            return console.Running ? Refusals.SqlRunning : Refusals.TransactionOpen;
        }
        if (manual && console.Connection is { } id && Find(id) is { } source && !DatabaseKinds.HasTransactions(source.Kind))
        {
            console.Raise();
            return Refusals.NoTransactions(DatabaseKinds.Title(source.Kind));
        }
        console.Manual = manual;
        if (!manual)
        {
            LetGo(console);
        }
        Keep();
        console.Raise();
        return null;
    }

    /// <summary>Commits a console's open transaction, or rolls it back, under a <see cref="Working"/> of its own.</summary>
    public async Task EndTransactionAsync(SqlConsole console, bool commit)
    {
        UiThread.Check();
        string action = commit ? AppActions.DatabaseCommit : AppActions.DatabaseRollback;
        if (console.Session is not { InTransaction: true } session || console.Running || work.Begin(action, commit ? "Committing…" : "Rolling back…") is not { } started)
        {
            return;
        }
        using Working working = started;
        console.Run(working);
        string said;
        try
        {
            int statements = session.Statements;
            int changed = session.Changed;
            await BackgroundWork.Run(() => session.EndAsync(commit, lifetime.Token), lifetime.Token);
            said = $"{(commit ? "Committed" : "Rolled back")}: {Plural.Of(statements, "statement")}, {Plural.Of(changed, "row")} changed.";
            working.Tell(true, said);
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or IOException or OperationCanceledException or TimeoutException
            or System.Net.Sockets.SocketException)
        {
            said = ContainerSecrets.InSaid(error.Message, known: [session.Source.ConnectionString]);
            IdeErrors.Write($"Database {(commit ? "commit" : "rollback")} failed: {said}");
            working.Tell(false, said);
            LetGo(console);
        }
        catalogs.Remove(session.Source.Id);
        console.Ran(console.Results, said);
        say(said);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lets go of a console as its tab closes: its connection closed, a transaction left open rolled back.</summary>
    public void Close(SqlConsole console)
    {
        UiThread.Check();
        console.Stop();
        LetGo(console);
        console.Raise();
    }

    /// <summary>Adds a connection made by hand, kept for the folder, its secrets in the system's store (said where they are
    /// kept for this session alone).</summary>
    public void Add(DatabaseSource source)
    {
        UiThread.Check();
        (DatabaseSource kept, bool lasting) = Kept(source with { Found = null }, keepsOld: false);
        made.Add(kept);
        Keep();
        TellWhereSecretsLast(kept, lasting);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Says that a connection's secrets are kept for this session alone, where the system's store refused them or
    /// there is none: after Passenger restarts the connection has none.</summary>
    private void TellWhereSecretsLast(DatabaseSource source, bool lasting)
    {
        if (!lasting)
        {
            Tell($"{source.Name}'s secrets are kept for this session alone: {(SystemCredentials.Kept ? "the system's credential store refused them"
                : "this system has no credential store Passenger reaches")}, so give them again after Passenger restarts");
        }
    }

    /// <summary>Puts <paramref name="replacement"/> in a connection's place; a found one keeps what its files say and takes
    /// only its mark, SSL and tunnel, kept for the folder.</summary>
    public void Replace(DatabaseSource original, DatabaseSource replacement)
    {
        UiThread.Check();
        if (original.Found is not null)
        {
            (DatabaseSource given, bool overLasting) = KeptOver(original with
            {
                Mark = replacement.Mark, Over = replacement.Over, Tunnel = replacement.Tunnel, TunnelSecret = replacement.TunnelSecret, SignIn = replacement.SignIn,
            });
            found = [.. found.Select(s => s.Id == original.Id ? given : s)];
            connected.Remove(original.Id);
            SshTunnels.Close([original.Id]);
            Keep();
            TellWhereSecretsLast(given, overLasting);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        (DatabaseSource changed, bool lasting) = Kept(replacement with { Id = original.Id, Found = null }, keepsOld: original.SecretKept);
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
        if (original.ConnectionString != changed.ConnectionString || original.Kind != changed.Kind || original.Tunnel != changed.Tunnel || original.SignIn != changed.SignIn)
        {
            connected.Remove(original.Id);
            SshTunnels.Close([original.Id]);
        }
        Keep();
        TellWhereSecretsLast(changed, lasting);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets a connection made by hand and its secrets; a found one comes back at the next refresh.</summary>
    public void Remove(DatabaseSource source)
    {
        UiThread.Check();
        made.RemoveAll(s => s.Id == source.Id);
        found = [.. found.Where(s => s.Id != source.Id)];
        asked.RemoveAll(s => s.Id == source.Id);
        connected.Remove(source.Id);
        if (source.Found is null)
        {
            SystemCredentials.Forget(CredentialService, source.Account);
            SystemCredentials.Forget(CredentialService, source.TunnelAccount);
        }
        SshTunnels.Close([source.Id]);
        Keep();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A connection as kept: its secret parts, and its tunnel's password or passphrase, put in the system's store
    /// and taken out of it, and whether those given will last past this session. Where <paramref name="keepsOld"/> (an edit),
    /// a part not given again keeps the one stored.</summary>
    private static (DatabaseSource Kept, bool Lasting) Kept(DatabaseSource source, bool keepsOld)
    {
        (string rest, string secret) = KeptConnection.Split(source.Kind, source.ConnectionString);
        bool lasting = KeepSecret(source.Account, secret, keepsOld) & KeepTunnelSecret(source, keepsOld);
        return (source with { ConnectionString = rest, SecretKept = true, TunnelSecret = null }, lasting);
    }

    /// <summary>A found connection's own settings as kept: the secret parts of its SSL and its tunnel's password or
    /// passphrase put in the system's store, each not given again keeping the one stored, and whether those given last.</summary>
    private static (DatabaseSource Kept, bool Lasting) KeptOver(DatabaseSource source)
    {
        (string rest, string secret) = source.Over.Length > 0 ? KeptConnection.Split(source.Kind, source.Over) : (string.Empty, string.Empty);
        bool lasting = KeepSecret(source.OverAccount, rest.Length > 0 ? secret : null, keepsOld: rest.Length > 0) & KeepTunnelSecret(source, keepsOld: true);
        return (source with { Over = rest, TunnelSecret = null }, lasting);
    }

    /// <summary>Stores secret parts under an account, laid over those stored where <paramref name="keepsOld"/>; none given
    /// and none kept forgets them. False where those given are kept for this session alone.</summary>
    private static bool KeepSecret(string account, string? secret, bool keepsOld)
    {
        string? old = keepsOld ? SystemCredentials.Read(CredentialService, account) : null;
        if (secret is { Length: > 0 })
        {
            DbConnectionStringBuilder merged = new() { ConnectionString = old ?? string.Empty };
            DbConnectionStringBuilder given = new() { ConnectionString = secret };
            foreach (string key in given.Keys)
            {
                merged[key] = given[key];
            }
            return SystemCredentials.Store(CredentialService, account, merged.ConnectionString) && SystemCredentials.Kept;
        }
        if (!keepsOld)
        {
            SystemCredentials.Forget(CredentialService, account);
        }
        return true;
    }

    /// <summary>Stores a tunnel's password or passphrase given, or forgets it; false where it is kept for this session alone.</summary>
    private static bool KeepTunnelSecret(DatabaseSource source, bool keepsOld)
    {
        if (source is { Tunnel: not null, TunnelSecret.Length: > 0 })
        {
            return SystemCredentials.Store(CredentialService, source.TunnelAccount, source.TunnelSecret) && SystemCredentials.Kept;
        }
        if (source.Tunnel is null || !keepsOld)
        {
            SystemCredentials.Forget(CredentialService, source.TunnelAccount);
        }
        return true;
    }

    /// <summary>Forgets that a connection was opened, so its explorer reads it again when next opened.</summary>
    public void Disconnect(DatabaseSource source)
    {
        UiThread.Check();
        connected.Remove(source.Id);
        SshTunnels.Close([source.Id]);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Says what an action the person asked came to.</summary>
    public void Tell(string said)
    {
        UiThread.Check();
        Said = said;
        say(said);
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
        IReadOnlyList<DatabaseSource> taken = [.. read.Where(s => !made.Any(m => m.Kind == s.Kind && m.ConnectionString == s.ConnectionString))
            .Select(s => before.FirstOrDefault(old => old.Name == s.Name && old.Kind == s.Kind && old.ConnectionString == s.ConnectionString) is { } old ? s with { Id = old.Id } : s)];
        found = [.. taken.Concat(asked.Where(a => !made.Concat(taken).Any(s => s.Kind == a.Kind && s.ConnectionString == a.ConnectionString))).Select(Marked)];
        connected.RemoveWhere(id => Find(id) is null);
        catalogs.Clear();
        Discovered = true;
        Said = Sources.Count == 0 ? "No database connections found in this folder's settings or runs. Add one with New Connection…"
            : $"{Plural.Of(Sources.Count, "connection")}. Open one to read its databases.";
        return true;
    }) ?? Task.CompletedTask;

    /// <summary>The connection reaching a database: one listed with its kind and connection string, else <paramref name="wanted"/>
    /// listed with those found, kept until the folder's end where <paramref name="lasting"/> (a running service's goes with
    /// its run at the next refresh).</summary>
    public DatabaseSource Use(DatabaseSource wanted, bool lasting)
    {
        UiThread.Check();
        if (Sources.FirstOrDefault(s => s.Kind == wanted.Kind && s.ConnectionString == wanted.ConnectionString) is { } listed)
        {
            return listed;
        }
        if (lasting)
        {
            asked.Add(wanted);
        }
        DatabaseSource added = Marked(wanted);
        found = [.. found, added];
        Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    /// <summary>The connection reaching the database a running program's command ran on, as its connection told it: one at
    /// its host and port (SQLite's at its file), else the one found for the program's project <paramref name="folder"/> that
    /// opens a database of that name (a container's program names its server as only the containers reach it); null with none.</summary>
    public DatabaseSource? Reaching(DatabaseKind kind, string source, string database, string? folder)
    {
        UiThread.Check();
        DatabaseSource[] kin = [.. Sources.Where(s => s.Kind == kind)];
        if (DatabaseKinds.IsFile(kind))
        {
            return kin.FirstOrDefault(s => PathNames.Same(DatabaseKinds.FileOf(kind, s.ConnectionString), source));
        }
        (string host, int? port) = DatabaseKinds.EndpointOfSource(kind, source);
        return kin.FirstOrDefault(s => DatabaseKinds.Endpoint(kind, s.ConnectionString) is var at && DatabaseKinds.SameHost(at.Host, host) && (port is null || at.Port == port)
                && (s.Database.Length == 0 || s.Database.Equals(database, StringComparison.OrdinalIgnoreCase)))
            ?? kin.FirstOrDefault(s => DatabaseKinds.Endpoint(kind, s.ConnectionString) is var at && DatabaseKinds.SameHost(at.Host, host) && (port is null || at.Port == port))
            ?? (folder is null ? null : kin.Where(s => s.Found is not null && PathNames.Same(s.Folder, folder) && s.Database.Equals(database, StringComparison.OrdinalIgnoreCase))
                .ToArray() is [var only] ? only : null);
    }

    /// <summary>Ends once the operation under way has, at once with none.</summary>
    public Task Idle => under is { IsCompleted: false } pending ? pending : Task.CompletedTask;

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

    /// <summary>What a diagram's places are kept by: its connection's history key, its database and its schema.</summary>
    public static string DiagramKey(DatabaseSource source, string database, string schema) => $"{source.HistoryKey}|{database}|{schema}";

    /// <summary>Where the person left a diagram's boxes, by table.</summary>
    public IReadOnlyDictionary<string, (double X, double Y)> DiagramPlaces(string key) =>
        State.Diagrams.GetValueOrDefault(key) is { } spots ? spots.ToDictionary(s => s.Key, s => (s.Value.X, s.Value.Y)) : new Dictionary<string, (double X, double Y)>();

    /// <summary>Keeps where the person left a diagram's boxes, none forgetting the diagram's.</summary>
    public void KeepDiagramPlaces(string key, IReadOnlyDictionary<string, (double X, double Y)> places)
    {
        UiThread.Check();
        Dictionary<string, Dictionary<string, DiagramSpot>> diagrams = new(State.Diagrams);
        if (places.Count == 0)
        {
            diagrams.Remove(key);
        }
        else
        {
            diagrams[key] = places.ToDictionary(p => p.Key, p => new DiagramSpot(p.Value.X, p.Value.Y));
        }
        SetState((State with { Diagrams = diagrams }).Clamped());
    }

    /// <summary>Reads two schemas, the source's and the target's, and what makes the target as the source is: none where
    /// they hold the same; null where a read failed or was stopped, said in <see cref="Said"/>.</summary>
    public async Task<IReadOnlyList<SchemaDifference>?> CompareAsync(DatabaseSource source, string sourceSchema, DatabaseSource target, string targetSchema)
    {
        IReadOnlyList<SchemaDifference>? found = null;
        await (RunAsync(AppActions.DatabaseCompare, $"Comparing {source.Name} with {target.Name}…", async cancel =>
        {
            SchemaShape from = await BackgroundWork.Run(() => SchemaReads.ReadAsync(source, sourceSchema, cancel), cancel);
            SchemaShape to = await BackgroundWork.Run(() => SchemaReads.ReadAsync(target, targetSchema, cancel), cancel);
            cancel.ThrowIfCancellationRequested();
            connected.Add(source.Id);
            connected.Add(target.Id);
            found = SchemaCompare.Differences(from, to);
            Said = found.Count == 0 ? $"{Named(target, targetSchema)} holds the same tables, keys, indexes and views as {Named(source, sourceSchema)}."
                : $"{Plural.Of(found.Count, "difference")} from {Named(source, sourceSchema)}: {target.Name}'s console holds the script that makes {Named(target, targetSchema)} the same.";
            return true;
        }, source) ?? Task.CompletedTask);
        return found;

        static string Named(DatabaseSource connection, string schema) =>
            ContainerSecrets.InSaid(schema.Length > 0 && schema != connection.Database ? $"{connection.Database} · {schema} of {connection.Name}" : $"{connection.Database} of {connection.Name}");
    }

    /// <summary>Runs one read at a time under a <see cref="Working"/>, saying what it came to; null where another runs.</summary>
    private Task? RunAsync(string action, string stage, Func<CancellationToken, Task<bool>> run, DatabaseSource? source = null)
    {
        UiThread.Check();
        if (Busy || work.Begin(action, stage) is not { } started)
        {
            return null;
        }
        return under = RunAsync(started, run, source);
    }

    private async Task RunAsync(Working started, Func<CancellationToken, Task<bool>> run, DatabaseSource? source)
    {
        running = started;
        using Working working = started;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(working.Stopping, lifetime.Token);
        // A DuckDB connection's first open may download its engine, whose stages the work says.
        using IDisposable telling = DuckDbNative.Tell(stage => UiThread.Post(() =>
        {
            if (running == working)
            {
                working.Say(stage);
            }
        }));
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
