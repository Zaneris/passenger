using System.Collections;
using System.Globalization;
using System.Text.Json;
using Cassandra;
using Passenger.App.Tables;

namespace Passenger.App.Database;

/// <summary>Cassandra as the Database window reads it, through DataStax's driver: a cluster's keyspaces (none of the
/// system's), a keyspace's tables and materialized views, a table's columns with their kinds (its partition key's, its
/// clustering columns' with their order, the rest), its keys and indexes as its schema tables say them; a table's rows
/// read a page at a time by the driver to <see cref="MostRows"/>, filtered, sorted and paged here; and a console's CQL.
/// Every failure of the driver is said as an <see cref="IOException"/>, as a relational driver's is.</summary>
internal static class CassandraClient
{
    /// <summary>The most rows a tab reads.</summary>
    public const int MostRows = 10000;

    /// <summary>The keyspaces, the connection's own first where it names one.</summary>
    internal static async Task<IReadOnlyList<string>> DatabasesAsync(DatabaseSource source, CancellationToken cancel) => await Session<IReadOnlyList<string>>(source, null, async session =>
    {
        List<string> names = [.. (await ReadAsync(session, new SimpleStatement("SELECT keyspace_name FROM system_schema.keyspaces"), MostRows, cancel)).Rows
            .Select(r => (string)r[0]!).Where(name => !name.StartsWith("system", StringComparison.Ordinal))];
        return [.. names.Append(source.Database).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(n => n != source.Database).ThenBy(n => n, StringComparer.Ordinal)];
    });

    /// <summary>A keyspace's tables and materialized views.</summary>
    internal static async Task<IReadOnlyList<(string Schema, IReadOnlyList<DatabaseObject> Objects)>> TablesAsync(DatabaseSource source, CancellationToken cancel) =>
        await Session<IReadOnlyList<(string, IReadOnlyList<DatabaseObject>)>>(source, null, async session =>
        {
            List<DatabaseObject> objects = [];
            objects.AddRange((await ReadAsync(session, new SimpleStatement("SELECT table_name FROM system_schema.tables WHERE keyspace_name = ?", source.Database), MostRows, cancel))
                .Rows.Select(r => new DatabaseObject(string.Empty, (string)r[0]!, DatabaseObjectKind.Table)));
            objects.AddRange((await ReadAsync(session, new SimpleStatement("SELECT view_name FROM system_schema.views WHERE keyspace_name = ?", source.Database), MostRows, cancel))
                .Rows.Select(r => new DatabaseObject(string.Empty, (string)r[0]!, DatabaseObjectKind.View)));
            return [(string.Empty, [.. objects.OrderBy(o => o.Name, StringComparer.Ordinal)])];
        });

    /// <summary>A table's columns as its schema says them: its partition key's in their order, its clustering columns' with
    /// their order, then the rest by name; its keys said, and its secondary indexes.</summary>
    internal static async Task<DatabaseSchema> SchemaAsync(DatabaseSource source, DatabaseTable table, CancellationToken cancel) => await Session(source, null, async session =>
    {
        DatabaseRows read = await ReadAsync(session, new SimpleStatement(
            "SELECT column_name, kind, position, type, clustering_order FROM system_schema.columns WHERE keyspace_name = ? AND table_name = ?", source.Database, table.Name),
            MostRows, cancel);
        List<(string Name, string Kind, int Position, string Type, string Order)> columns = [.. read.Rows.Select(r => ((string)r[0]!, (string)r[1]!,
            Convert.ToInt32(r[2], CultureInfo.InvariantCulture), (string)r[3]!, r[4] as string ?? "none"))];
        if (columns.Count == 0)
        {
            throw new InvalidOperationException($"{source.Database} has no table or view {table.Name}: read the keyspace again");
        }
        int Rank(string kind) => kind switch { "partition_key" => 0, "clustering" => 1, "static" => 2, _ => 3 };
        columns = [.. columns.OrderBy(c => Rank(c.Kind)).ThenBy(c => Rank(c.Kind) < 2 ? c.Position : 0).ThenBy(c => c.Name, StringComparer.Ordinal)];
        List<string> keys = [$"partition key ({string.Join(", ", columns.Where(c => c.Kind == "partition_key").Select(c => c.Name))})"];
        if (columns.Any(c => c.Kind == "clustering"))
        {
            keys.Add($"clustering key ({string.Join(", ", columns.Where(c => c.Kind == "clustering").Select(c => $"{c.Name} {c.Order.ToUpperInvariant()}"))})");
        }
        DatabaseRows indexes = await ReadAsync(session, new SimpleStatement("SELECT index_name, options FROM system_schema.indexes WHERE keyspace_name = ? AND table_name = ?",
            source.Database, table.Name), MostRows, cancel);
        return new DatabaseSchema(table, [.. columns.Select(c => new DatabaseColumn(c.Name, c.Kind == "static" ? c.Type + " static" : c.Type, Rank(c.Kind) > 1, Rank(c.Kind) < 2, false))], keys,
            [.. indexes.Rows.Select(r => new DatabasePart((string)r[0]!, $"{r[0]} on {(r[1] is JsonText options ? System.Text.Json.Nodes.JsonNode.Parse(options.Json)?["target"]?.GetValue<string>() : null) ?? "?"}"))]);
    });

    /// <summary>A page of a table's rows, read to as many as the page needs (to <see cref="MostRows"/> where it is filtered or
    /// sorted, which is done here).</summary>
    internal static async Task<DatabaseRows> PageAsync(DatabaseSource source, DatabaseSchema schema, int offset, CancellationToken cancel, DatabaseFilter? filter,
        (string Column, bool Descending)? sort) => await Session(source, null, async session =>
    {
        int wanted = filter is null && sort is null ? offset + DatabaseClient.PageSize + 1 : MostRows;
        DatabaseRows read = await ReadAsync(session, new SimpleStatement($"SELECT * FROM {Quoted(source.Database)}.{Quoted(schema.Table.Name)}"), wanted, cancel);
        // The table's columns in its schema's order, whatever order the driver gives them.
        string[] columns = [.. schema.Columns.Select(c => c.Name)];
        int[] at = [.. columns.Select(c => Array.IndexOf(read.Columns, c))];
        DatabaseRows all = new(columns, [.. read.Rows.Select(r => at.Select(i => i >= 0 ? r[i] : null).ToArray())]);
        IEnumerable<int> kept = Enumerable.Range(0, all.RowCount);
        if (filter is not null && Array.IndexOf(columns, filter.Column) is >= 0 and int by)
        {
            kept = kept.Where(r => filter.Operator switch
            {
                "Is NULL" => all.Rows[r][by] is null,
                "Is not NULL" => all.Rows[r][by] is not null,
                "Equals" => all.Cell(r, by) == filter.Value,
                _ => all.Cell(r, by).Contains(filter.Value, StringComparison.OrdinalIgnoreCase),
            });
        }
        if (sort is { } order && Array.IndexOf(columns, order.Column) is >= 0 and int sorted)
        {
            int[] ranked = TableSort.Order(all, sorted, order.Descending);
            int[] place = new int[ranked.Length];
            for (int i = 0; i < ranked.Length; i++)
            {
                place[ranked[i]] = i;
            }
            kept = kept.OrderBy(r => place[r]);
        }
        int[] chosen = [.. kept];
        return new DatabaseRows(columns, [.. chosen.Skip(offset).Take(DatabaseClient.PageSize).Select(r => all.Rows[r])],
            chosen.Length > offset + DatabaseClient.PageSize || read.More)
        {
            Schema = schema,
            Offset = offset,
            Types = [.. schema.Columns.Select(c => c.Type)],
            OrderedBy = sort is { } ordered && Array.IndexOf(columns, ordered.Column) is >= 0 and int column ? (column, ordered.Descending) : null,
        };
    });

    /// <summary>Runs each of a console's CQL statements in turn as <see cref="DatabaseClient.RunAsync"/> does, in the
    /// connection's keyspace, its rows (at most <paramref name="limit"/>) read a page at a time; a failure ends the run.</summary>
    internal static async Task RunAsync(DatabaseSource source, IReadOnlyList<string> runs, int limit, Action<SqlResult> told, CancellationToken cancel) =>
        await Session(source, source.Database, async session =>
        {
            foreach (string run in runs)
            {
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    DatabaseRows rows = await ReadAsync(session, new SimpleStatement(run), limit, cancel);
                    told(new SqlResult(run, rows.ColumnCount > 0 ? [rows] : [], -1, null, System.Diagnostics.Stopwatch.GetElapsedTime(began)));
                }
                catch (DriverException failed)
                {
                    told(new SqlResult(run, [], -1, failed.Message, System.Diagnostics.Stopwatch.GetElapsedTime(began)));
                    break;
                }
            }
            return true;
        });

    /// <summary>The keyspace's tables and their columns, for a console's completion.</summary>
    internal static async Task<SqlCatalog> CatalogAsync(DatabaseSource source, CancellationToken cancel) => await Session(source, null, async session =>
    {
        DatabaseRows read = await ReadAsync(session, new SimpleStatement(
            "SELECT table_name, column_name, type FROM system_schema.columns WHERE keyspace_name = ?", source.Database), 50000, cancel);
        return new SqlCatalog([.. read.Rows.GroupBy(r => (string)r[0]!).Select(t => new SqlTableInfo(string.Empty, t.Key, [.. t.Select(r => ((string)r[1]!, (string)r[2]!))]))]);
    });

    /// <summary>A statement's rows, read a page at a time to <paramref name="most"/>, each value as a row's cell holds it.</summary>
    private static async Task<DatabaseRows> ReadAsync(ISession session, IStatement statement, int most, CancellationToken cancel)
    {
        statement.SetPageSize(Math.Clamp(most + 1, 1, 5000)).SetAutoPage(false);
        List<object?[]> rows = [];
        string[] columns = [];
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            RowSet page = await session.ExecuteAsync(statement);
            columns = [.. (page.Columns ?? []).Select(c => c.Name)];
            foreach (Row row in page.GetRows())
            {
                rows.Add([.. Enumerable.Range(0, columns.Length).Select(i => Cell(row.GetValue<object>(i)))]);
            }
            if (page.PagingState is not { } next || rows.Count > most)
            {
                break;
            }
            statement.SetPagingState(next);
        }
        return new DatabaseRows(columns, [.. rows.Take(most)], rows.Count > most);
    }

    /// <summary>A value as a row's cell holds it: plain values as themselves, a date as its moment, a collection or a map as
    /// its JSON, any other as its words.</summary>
    private static object? Cell(object? value) => value switch
    {
        null => null,
        string or bool or int or long or short or sbyte or float or double or decimal => value,
        DateTimeOffset at => at.UtcDateTime,
        byte[] bytes => bytes,
        IDictionary or IEnumerable and not string => Json(value),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static object Json(object value)
    {
        try
        {
            return new JsonText(JsonSerializer.Serialize(value));
        }
        catch (NotSupportedException)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    /// <summary>A keyspace's or table's name in CQL's quotes, as written.</summary>
    private static string Quoted(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Runs work on a session of the connection's cluster, in a keyspace where one is named, the cluster shut after;
    /// the driver's failures said as an <see cref="IOException"/> holding no secret.</summary>
    private static async Task<T> Session<T>(DatabaseSource source, string? keyspace, Func<ISession, Task<T>> work)
    {
        string connection = source.ConnectionString;
        (string host, int port) = DatabaseKinds.Endpoint(DatabaseKind.Cassandra, connection);
        Builder builder = Cluster.Builder()
            .AddContactPoints((DatabaseKinds.Value(connection, "Contact Points", "ContactPoints", "Host") ?? host).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .WithPort(port)
            .WithSocketOptions(new SocketOptions().SetConnectTimeoutMillis(10000).SetReadTimeoutMillis(30000))
            .WithQueryTimeout(30000)
            // The explorer reads the schema's tables itself, so the driver needn't read every keyspace's at each connection.
            .WithMetadataSyncOptions(new MetadataSyncOptions().SetMetadataSyncEnabled(false));
        if (DatabaseKinds.Value(connection, "Username", "User") is { } user)
        {
            builder = builder.WithCredentials(user, DatabaseKinds.Value(connection, "Password") ?? string.Empty);
        }
        Cluster cluster = builder.Build();
        try
        {
            ISession session = keyspace is { Length: > 0 } named ? await cluster.ConnectAsync(named) : await cluster.ConnectAsync();
            return await work(session);
        }
        catch (Exception failed) when (failed is DriverException or ArgumentException)
        {
            throw new IOException(Containers.ContainerSecrets.InSaid(failed.Message, known: [source.ConnectionString]), failed);
        }
        finally
        {
            await cluster.ShutdownAsync();
        }
    }
}
