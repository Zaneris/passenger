using System.ComponentModel;
using System.Data.Common;
using System.Globalization;
using ModelContextProtocol.Server;
using Passenger.App.Containers;
using Passenger.App.Database;
using Passenger.Core;

namespace Passenger.App.Mcp;

/// <summary>The Database window's connections for an agent, read-only by design: its connections, what one holds, a table's
/// columns and keys, and a query's rows, each read on a connection of its own held to reading (the database's read-only
/// session where it has one, in a transaction rolled back), a statement that would change anything refused before it runs.</summary>
public sealed partial class PassengerTools
{
    /// <summary>The most rows a query answers.</summary>
    private const int MostRows = 1000;

    [McpServerTool(Name = McpTools.Database, ReadOnly = true), Description(
        "The Database window's connections, read-only. Alone: each connection with its kind, database and where it was found. " +
        "With connection (its name): its databases and each schema's tables, views and routines; with table too ([schema.]name), " +
        "its columns, keys, indexes and triggers; with sql, a query's rows (at most rows), a statement that changes data or schema " +
        "or begins or ends a transaction refused. database reads another database of the connection's server.")]
    public Task<object> Database(string? connection = null, string? database = null, string? table = null, string? sql = null, int rows = 100,
        CancellationToken cancel = default) => OnUi<object>(cancel, async () =>
    {
        DatabaseController controller = window.Database;
        AgentCall.Say("Finding the folder's database connections");
        await window.DatabasesFoundAsync().WaitAsync(cancel);
        if (connection is null)
        {
            return new DatabaseConnections([.. controller.Sources.Select(s => new DatabaseConnectionInfo(Masked(s.Name), DatabaseKinds.Title(s.Kind), Masked(s.Database),
                s.Found ?? "made by hand", s.Mark == DatabaseMark.None ? null : s.Mark.ToString()))]);
        }
        DatabaseSource named = controller.Sources.FirstOrDefault(s => s.Name == connection)
            ?? controller.Sources.FirstOrDefault(s => s.Name.Equals(connection, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No connection {connection} in the Database window: {McpTools.Database} with no connection lists them");
        if (window.WhyNotOpened(named) is { } why)
        {
            throw new InvalidOperationException(why);
        }
        // Read-only on the database's side too: PostgreSQL's and MySQL's sessions, SQLite's file opened to read.
        DatabaseSource reading = (database is { Length: > 0 } other ? named.In(other) : named) with { Mark = DatabaseMark.ReadOnly };
        string? folder = Session.Folder;
        Func<string, string> mask = said => ContainerSecrets.InSaid(said, folder);
        AgentCall.Say($"Reading {named.Name}");
        // Not held: the agent's answer, which its call waits for; nothing drawn changes.
        return sql is not null ? await BackgroundWork.Unheld(() => QueryAsync(reading, sql, Math.Clamp(rows, 1, MostRows), mask, cancel), cancel)
            : table is not null ? await BackgroundWork.Unheld(() => DescribeAsync(reading, table, cancel), cancel)
            : await BackgroundWork.Unheld(() => HoldsAsync(reading, cancel), cancel);
    });

    /// <summary>A connection's databases, and each schema of the one it opens with its tables, views and routines.</summary>
    private static async Task<object> HoldsAsync(DatabaseSource source, CancellationToken cancel)
    {
        IReadOnlyList<string> databases = await Reading(() => DatabaseClient.DatabasesAsync(source, cancel), source);
        IReadOnlyList<(string Schema, IReadOnlyList<DatabaseObject> Objects)> schemas = await Reading(() => DatabaseClient.ObjectsAsync(source, cancel), source);
        return new DatabaseHolds(source.Database, databases, [.. schemas.Select(s => new DatabaseSchemaInfo(s.Schema,
            [.. s.Objects.Where(o => o.Kind == DatabaseObjectKind.Table).Select(o => o.Name)],
            [.. s.Objects.Where(o => o.Kind == DatabaseObjectKind.View).Select(o => o.Name)],
            [.. s.Objects.Where(o => o.Kind == DatabaseObjectKind.Routine).Select(o => o.Detail.Length > 0 ? $"{o.Name} ({o.Detail})" : o.Name)]))]);
    }

    /// <summary>A table's or view's columns, keys, indexes and triggers, found by its name with or without its schema.</summary>
    private static async Task<object> DescribeAsync(DatabaseSource source, string table, CancellationToken cancel)
    {
        IReadOnlyList<(string Schema, IReadOnlyList<DatabaseObject> Objects)> schemas = await Reading(() => DatabaseClient.ObjectsAsync(source, cancel), source);
        DatabaseObject[] objects = [.. schemas.SelectMany(s => s.Objects).Where(o => o.Kind is DatabaseObjectKind.Table or DatabaseObjectKind.View)];
        DatabaseObject found = objects.FirstOrDefault(o => o.Schema.Length > 0 && $"{o.Schema}.{o.Name}" == table) ?? objects.FirstOrDefault(o => o.Name == table)
            ?? objects.FirstOrDefault(o => $"{o.Schema}.{o.Name}".Equals(table, StringComparison.OrdinalIgnoreCase) || o.Name.Equals(table, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{source.Database} has no table or view {table}");
        DatabaseSchema schema = await Reading(() => DatabaseClient.SchemaAsync(source, new DatabaseTable(found.Schema, found.Name), cancel), source);
        return new DatabaseTableInfo(found.Schema.Length > 0 ? $"{found.Schema}.{found.Name}" : found.Name, found.Kind == DatabaseObjectKind.View ? "view" : "table",
            [.. schema.Columns.Select(c => new DatabaseColumnInfo(c.Name, c.Type, c.Nullable ? null : false, c.Primary ? true : null, c.Generated ? true : null))],
            schema.Keys, [.. schema.Indexes.Select(i => i.Said)], [.. schema.Triggers.Select(t => $"{t.Name} · {t.Said}")]);
    }

    /// <summary>A query's results, each statement's rows to <paramref name="most"/> within what an answer holds, a value in a
    /// column whose name says it holds a secret masked; a statement that would change anything refused before any runs.</summary>
    private static async Task<object> QueryAsync(DatabaseSource source, string sql, int most, Func<string, string> mask, CancellationToken cancel)
    {
        List<string> runs = SqlText.Runs(sql, source.Kind);
        if (runs.Count == 0)
        {
            throw new InvalidOperationException("sql holds no statement");
        }
        foreach (string run in runs)
        {
            if (SqlText.EffectOf(run, source.Kind) is var effect and > SqlEffect.Session)
            {
                throw new InvalidOperationException(Refusals.AgentsQueryWrites(mask(AgentAnswers.Line(run.ReplaceLineEndings(" "), 1)), effect switch
                {
                    SqlEffect.Transaction => "begins or ends a transaction",
                    SqlEffect.ChangesData => "changes data",
                    _ => "changes the schema",
                }));
            }
        }
        List<SqlResult> results = [];
        await Reading(async () =>
        {
            await using DbConnection held = await DatabaseClient.OpenAsync(source, cancel);
            await using DbTransaction transaction = await held.BeginTransactionAsync(cancel);
            await DatabaseClient.RunOnAsync(source, held, transaction, runs, most, results.Add, cancel);
            await transaction.RollbackAsync(cancel);
            return true;
        }, source);
        int room = AgentAnswers.MostCharacters / Math.Max(1, results.Count);
        return results.Select(r => new DatabaseQueryResult(mask(AgentAnswers.Line(r.Statement.ReplaceLineEndings(" "), 1)), r.Error is { } failed ? mask(failed) : null,
            [.. r.Sets.Select(set => Rows(set, room / Math.Max(1, r.Sets.Count), mask))])).ToArray();
    }

    /// <summary>A result set's rows as text within <paramref name="room"/> characters, a secret column's values masked, and
    /// what it left out.</summary>
    private static DatabaseRowsInfo Rows(DatabaseRows set, int room, Func<string, string> mask)
    {
        bool[] secret = [.. set.Columns.Select(c => ContainerSecrets.Secret(c))];
        string?[][] shown = [.. set.Rows.Select(row => row.Select((value, i) => value is null or DBNull ? null : secret[i] ? ContainerSecrets.Masked : Cell(value)).ToArray())];
        (IReadOnlyList<string?[]> kept, string? more) = AgentAnswers.Within(shown, row => row.Sum(v => (v?.Length ?? 4) + 3), room, "row", "ask with fewer columns or a narrower query");
        return new DatabaseRowsInfo(set.Columns, kept, more ?? (set.More ? $"More rows than {set.Rows.Length.ToString(CultureInfo.InvariantCulture)} left unread: ask with rows or a narrower query" : null));

        string Cell(object value) => mask(value switch
        {
            byte[] bytes => $"0x{Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, 64))}{(bytes.Length > 64 ? "…" : string.Empty)}",
            DateTime at => at.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset at => at.ToString("O", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) is { } text && text.Length > AgentAnswers.ValueLength ? AgentAnswers.Cut(text, AgentAnswers.ValueLength)
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        });
    }

    /// <summary>A read of a database, its driver's refusal (a bad statement, no such database, a server not there) the
    /// agent's to act on, its words masked.</summary>
    private static async Task<T> Reading<T>(Func<Task<T>> read, DatabaseSource source)
    {
        try
        {
            return await read();
        }
        catch (Exception error) when (error is DbException or System.Net.Sockets.SocketException or FormatException or ArgumentException)
        {
            throw new InvalidOperationException(ContainerSecrets.InSaid(error.Message, known: [source.ConnectionString]), error);
        }
    }
}

/// <summary>The Database window's connections as an agent reads them.</summary>
public sealed record DatabaseConnections(IReadOnlyList<DatabaseConnectionInfo> Connections);

/// <summary>A connection: its name, kind, the database it opens, where it was found (or made by hand), and its mark.</summary>
public sealed record DatabaseConnectionInfo(string Name, string Kind, string Database, string Found, string? Mark);

/// <summary>What a connection holds: the database it opened, the server's databases, and each schema's objects.</summary>
public sealed record DatabaseHolds(string Database, IReadOnlyList<string> Databases, IReadOnlyList<DatabaseSchemaInfo> Schemas);

/// <summary>A schema's tables, views and routines by name.</summary>
public sealed record DatabaseSchemaInfo(string Schema, IReadOnlyList<string> Tables, IReadOnlyList<string> Views, IReadOnlyList<string> Routines);

/// <summary>A table or view: its name, kind, columns, keys, indexes and triggers.</summary>
public sealed record DatabaseTableInfo(string Name, string Kind, IReadOnlyList<DatabaseColumnInfo> Columns, IReadOnlyList<string> Keys, IReadOnlyList<string> Indexes,
    IReadOnlyList<string> Triggers);

/// <summary>A column: its name and type, false where it takes no null, and whether it is in the primary key or generated.</summary>
public sealed record DatabaseColumnInfo(string Name, string Type, bool? Nullable, bool? Primary, bool? Generated);

/// <summary>A statement's results: the statement, its failure, and each result set's rows.</summary>
public sealed record DatabaseQueryResult(string Statement, string? Error, IReadOnlyList<DatabaseRowsInfo> Sets);

/// <summary>A result set: its columns, its rows as text (null for a null), and what it left out.</summary>
public sealed record DatabaseRowsInfo(IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows, string? More);
