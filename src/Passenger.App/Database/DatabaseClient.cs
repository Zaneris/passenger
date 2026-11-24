using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.App.Database;

public sealed record DatabaseFilter(string Column, string Value, string Operator);

internal static class DatabaseClient
{
    internal const int PageSize = 100;

    /// <summary>Opens a connection, its secret parts read from the system's store where they are kept there.</summary>
    private static async Task<DbConnection> OpenAsync(DatabaseSource source, CancellationToken cancel)
    {
        string whole = source.ConnectionString;
        if (source.SecretKept && SystemCredentials.Read(DatabaseController.CredentialService, source.Account) is { Length: > 0 } secret)
        {
            DbConnectionStringBuilder parts = new() { ConnectionString = secret };
            foreach (string key in parts.Keys)
            {
                ContainerSecrets.Mark(Convert.ToString(parts[key], CultureInfo.InvariantCulture) ?? string.Empty);
            }
            whole = KeptConnection.Joined(source.Kind, whole, secret);
        }
        DbConnection connection = source.Kind switch
        {
            DatabaseKind.Sqlite => new SqliteConnection(new SqliteConnectionStringBuilder(whole) { Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ConnectionString),
            DatabaseKind.Postgres => new NpgsqlConnection(new NpgsqlConnectionStringBuilder(whole) { Pooling = false }.ConnectionString),
            DatabaseKind.SqlServer => new SqlConnection(new SqlConnectionStringBuilder(whole) { Pooling = false }.ConnectionString),
            DatabaseKind.MySql => new MySqlConnection(new MySqlConnectionStringBuilder(whole) { Pooling = false }.ConnectionString),
            _ => throw new InvalidOperationException("Unknown database provider"),
        };
        try
        {
            await connection.OpenAsync(cancel);
            return connection;
        }
        catch (Exception)
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal static string Quote(DatabaseKind kind, string name) => kind switch
    {
        DatabaseKind.SqlServer => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]",
        DatabaseKind.MySql => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`",
        _ => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
    };

    private static string Full(DatabaseKind kind, DatabaseTable table) =>
        (table.Schema.Length > 0 ? Quote(kind, table.Schema) + "." : string.Empty) + Quote(kind, table.Name);

    internal static async Task<IReadOnlyList<DatabaseTable>> TablesAsync(DatabaseSource source, CancellationToken cancel)
    {
        await using DbConnection connection = await OpenAsync(source, cancel);
        string sql = source.Kind switch
        {
            DatabaseKind.Sqlite => "SELECT '', name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name",
            DatabaseKind.Postgres => "SELECT table_schema, table_name FROM information_schema.tables WHERE table_type='BASE TABLE' AND table_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1,2",
            DatabaseKind.SqlServer => "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE='BASE TABLE' ORDER BY 1,2",
            _ => "SELECT table_schema, table_name FROM information_schema.tables WHERE table_type='BASE TABLE' AND table_schema=DATABASE() ORDER BY 1,2",
        };
        DatabaseRows rows = await ReadAsync(connection, sql, [], cancel, limit: 10000);
        return [.. rows.Rows.Select(r => new DatabaseTable((string)r[0]!, (string)r[1]!))];
    }

    internal static async Task<DatabaseSchema> SchemaAsync(DatabaseSource source, DatabaseTable table, CancellationToken cancel)
    {
        await using DbConnection connection = await OpenAsync(source, cancel);
        List<DatabaseColumn> columns = [];
        List<string> keys = [];
        List<string> indexes = [];
        if (source.Kind == DatabaseKind.Sqlite)
        {
            DatabaseRows info = await ReadAsync(connection, "SELECT name, type, \"notnull\", pk, hidden FROM pragma_table_xinfo(@table)", [("@table", table.Name)], cancel);
            foreach (object?[] row in info.Rows)
            {
                bool primary = Convert.ToInt32(row[3], CultureInfo.InvariantCulture) > 0;
                columns.Add(new DatabaseColumn((string)row[0]!, (string)row[1]!, Convert.ToInt32(row[2], CultureInfo.InvariantCulture) == 0 && !primary,
                    primary, Convert.ToInt32(row[4], CultureInfo.InvariantCulture) > 0));
            }
            keys.AddRange(columns.Where(c => c.Primary).Select(c => $"PRIMARY KEY · {c.Name}"));
            DatabaseRows foreign = await ReadAsync(connection, "SELECT \"from\", \"table\", \"to\" FROM pragma_foreign_key_list(@table)", [("@table", table.Name)], cancel);
            keys.AddRange(foreign.Rows.Select(r => $"FOREIGN KEY · {r[0]} → {r[1]}.{r[2]}"));
            DatabaseRows indexed = await ReadAsync(connection, "SELECT name, \"unique\" FROM pragma_index_list(@table)", [("@table", table.Name)], cancel);
            foreach (object?[] index in indexed.Rows)
            {
                DatabaseRows fields = await ReadAsync(connection, "SELECT name FROM pragma_index_info(@index)", [("@index", index[0])], cancel);
                indexes.Add($"{index[0]} · {(Convert.ToInt32(index[1], CultureInfo.InvariantCulture) != 0 ? "UNIQUE · " : string.Empty)}{string.Join(", ", fields.Rows.Select(r => r[0]))}");
            }
        }
        else
        {
            (string, object?)[] parameters = [("@schema", table.Schema), ("@table", table.Name)];
            string generated = source.Kind switch
            {
                DatabaseKind.Postgres => "CASE WHEN is_identity='YES' OR is_generated='ALWAYS' THEN 1 ELSE 0 END",
                DatabaseKind.MySql => "CASE WHEN extra LIKE '%GENERATED%' OR extra LIKE '%auto_increment%' THEN 1 ELSE 0 END",
                _ => "CASE WHEN COLUMNPROPERTY(OBJECT_ID(QUOTENAME(TABLE_SCHEMA)+'.'+QUOTENAME(TABLE_NAME)), COLUMN_NAME, 'IsComputed')=1 OR COLUMNPROPERTY(OBJECT_ID(QUOTENAME(TABLE_SCHEMA)+'.'+QUOTENAME(TABLE_NAME)), COLUMN_NAME, 'IsIdentity')=1 THEN 1 ELSE 0 END",
            };
            DatabaseRows info = await ReadAsync(connection, $"SELECT column_name, data_type, is_nullable, {generated} FROM information_schema.columns WHERE table_schema=@schema AND table_name=@table ORDER BY ordinal_position", parameters, cancel);
            DatabaseRows constraints = await ReadAsync(connection,
                "SELECT tc.constraint_type, kcu.column_name, tc.constraint_name FROM information_schema.table_constraints tc JOIN information_schema.key_column_usage kcu ON tc.constraint_name=kcu.constraint_name AND tc.table_schema=kcu.table_schema AND tc.table_name=kcu.table_name WHERE tc.table_schema=@schema AND tc.table_name=@table ORDER BY tc.constraint_name,kcu.ordinal_position", parameters, cancel);
            keys.AddRange(constraints.Rows.Select(r => $"{r[2]} · {r[0]} · {r[1]}"));
            foreach (object?[] row in info.Rows)
            {
                columns.Add(new DatabaseColumn((string)row[0]!, (string)row[1]!, (string)row[2]! == "YES",
                    constraints.Rows.Any(k => (string)k[0]! == "PRIMARY KEY" && Equals(k[1], row[0])), Convert.ToInt32(row[3], CultureInfo.InvariantCulture) != 0));
            }
            string indexSql = source.Kind switch
            {
                DatabaseKind.Postgres => "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname=@schema AND tablename=@table ORDER BY indexname",
                DatabaseKind.MySql => "SELECT index_name, column_name FROM information_schema.statistics WHERE table_schema=@schema AND table_name=@table ORDER BY index_name,seq_in_index",
                _ => "SELECT i.name, c.name FROM sys.indexes i JOIN sys.index_columns ic ON i.object_id=ic.object_id AND i.index_id=ic.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id JOIN sys.tables t ON t.object_id=i.object_id JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name=@schema AND t.name=@table ORDER BY i.name,ic.key_ordinal",
            };
            DatabaseRows indexed = await ReadAsync(connection, indexSql, parameters, cancel);
            indexes.AddRange(indexed.Rows.Select(r => $"{r[0]} · {r[1]}"));
        }
        return new DatabaseSchema(table, columns, keys, indexes);
    }

    internal static async Task<DatabaseRows> PageAsync(DatabaseSource source, DatabaseSchema schema, int offset, CancellationToken cancel, DatabaseFilter? filter = null,
        (string Column, bool Descending)? sort = null)
    {
        await using DbConnection connection = await OpenAsync(source, cancel);
        if (sort is { } wanted && !schema.Columns.Any(c => c.Name == wanted.Column))
        {
            throw new InvalidOperationException("Choose a column of this table");
        }
        string order = sort is { } by ? Quote(source.Kind, by.Column) + (by.Descending ? " DESC" : string.Empty)
            : string.Join(",", schema.Columns.Where(c => c.Primary).Select(c => Quote(source.Kind, c.Name)));
        List<(string Name, object? Value)> parameters = [("@offset", offset), ("@take", PageSize + 1)];
        string where = string.Empty;
        if (filter is not null)
        {
            if (!schema.Columns.Any(c => c.Name == filter.Column)) { throw new InvalidOperationException("Choose a column of this table"); }
            string column = Quote(source.Kind, filter.Column);
            string text = source.Kind switch
            {
                DatabaseKind.SqlServer => $"CONVERT(nvarchar(max), {column})",
                DatabaseKind.MySql => $"CAST({column} AS CHAR)",
                _ => $"CAST({column} AS TEXT)",
            };
            where = filter.Operator switch
            {
                "Is NULL" => $" WHERE {column} IS NULL",
                "Is not NULL" => $" WHERE {column} IS NOT NULL",
                "Equals" => $" WHERE {text} = @filter",
                _ => $" WHERE LOWER({text}) LIKE LOWER(@filter) ESCAPE '!'",
            };
            string value = filter.Operator == "Contains" ? "%" + filter.Value.Replace("!", "!!", StringComparison.Ordinal).Replace("%", "!%", StringComparison.Ordinal)
                .Replace("_", "!_", StringComparison.Ordinal).Replace("[", "![", StringComparison.Ordinal) + "%" : filter.Value;
            parameters.Add(("@filter", value));
        }
        string sql = $"SELECT * FROM {Full(source.Kind, schema.Table)}{where} ORDER BY {(order.Length == 0 ? "1" : order)}";
        sql += source.Kind == DatabaseKind.SqlServer ? " OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY" : " LIMIT @take OFFSET @offset";
        DatabaseRows rows = await ReadAsync(connection, sql, parameters, cancel, PageSize);
        return new DatabaseRows(rows.Columns, rows.Rows, rows.More)
        {
            Schema = schema,
            Offset = offset,
            OrderedBy = sort is { } sorted ? (Array.IndexOf(rows.Columns, sorted.Column), sorted.Descending) : null,
        };
    }

    /// <summary>The databases of the connection's server; SQLite's main database alone.</summary>
    internal static async Task<IReadOnlyList<string>> DatabasesAsync(DatabaseSource source, CancellationToken cancel)
    {
        if (source.Kind == DatabaseKind.Sqlite)
        {
            return [source.Database];
        }
        await using DbConnection connection = await OpenAsync(source, cancel);
        string sql = source.Kind switch
        {
            DatabaseKind.Postgres => "SELECT datname FROM pg_database WHERE NOT datistemplate AND datallowconn ORDER BY 1",
            DatabaseKind.SqlServer => "SELECT name FROM sys.databases WHERE HAS_DBACCESS(name)=1 ORDER BY name",
            _ => "SELECT schema_name FROM information_schema.schemata WHERE schema_name NOT IN ('information_schema','performance_schema','mysql','sys') ORDER BY 1",
        };
        DatabaseRows rows = await ReadAsync(connection, sql, [], cancel);
        return [.. rows.Rows.Select(r => Convert.ToString(r[0], CultureInfo.InvariantCulture) ?? string.Empty)];
    }

    /// <summary>A database's schemas (none of the system's) with the tables, views and routines each holds; MySQL's database is
    /// its one schema, and SQLite's has none of a name.</summary>
    internal static async Task<IReadOnlyList<(string Schema, IReadOnlyList<DatabaseObject> Objects)>> ObjectsAsync(DatabaseSource source, CancellationToken cancel)
    {
        await using DbConnection connection = await OpenAsync(source, cancel);
        List<DatabaseObject> objects = [];
        List<string> schemas = [];
        if (source.Kind == DatabaseKind.Sqlite)
        {
            DatabaseRows rows = await ReadAsync(connection, "SELECT name, type FROM sqlite_schema WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY name", [], cancel);
            objects.AddRange(rows.Rows.Select(r => new DatabaseObject(string.Empty, (string)r[0]!, (string)r[1]! == "view" ? DatabaseObjectKind.View : DatabaseObjectKind.Table)));
            schemas.Add(string.Empty);
        }
        else
        {
            (string Schemas, string Tables, string Routines) sql = source.Kind switch
            {
                DatabaseKind.Postgres => (
                    "SELECT nspname FROM pg_namespace WHERE nspname NOT IN ('pg_catalog','information_schema') AND nspname NOT LIKE 'pg\\_%' ORDER BY 1",
                    "SELECT table_schema, table_name, table_type FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1,2",
                    "SELECT routine_schema, routine_name, routine_type FROM information_schema.routines WHERE routine_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1,2"),
                DatabaseKind.SqlServer => (
                    "SELECT s.name FROM sys.schemas s WHERE s.name NOT IN ('sys','INFORMATION_SCHEMA','guest') AND s.name NOT LIKE 'db\\_%' ESCAPE '\\' ORDER BY 1",
                    "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES ORDER BY 1,2",
                    "SELECT ROUTINE_SCHEMA, ROUTINE_NAME, ROUTINE_TYPE FROM INFORMATION_SCHEMA.ROUTINES ORDER BY 1,2"),
                _ => (
                    "SELECT DATABASE()",
                    "SELECT table_schema, table_name, table_type FROM information_schema.tables WHERE table_schema=DATABASE() ORDER BY 1,2",
                    "SELECT routine_schema, routine_name, routine_type FROM information_schema.routines WHERE routine_schema=DATABASE() ORDER BY 1,2"),
            };
            schemas.AddRange((await ReadAsync(connection, sql.Schemas, [], cancel)).Rows.Select(r => Convert.ToString(r[0], CultureInfo.InvariantCulture) ?? string.Empty));
            foreach (object?[] row in (await ReadAsync(connection, sql.Tables, [], cancel)).Rows)
            {
                string type = Convert.ToString(row[2], CultureInfo.InvariantCulture) ?? string.Empty;
                objects.Add(new DatabaseObject((string)row[0]!, (string)row[1]!, type.Contains("VIEW", StringComparison.OrdinalIgnoreCase) ? DatabaseObjectKind.View : DatabaseObjectKind.Table));
            }
            foreach (object?[] row in (await ReadAsync(connection, sql.Routines, [], cancel)).Rows)
            {
                objects.Add(new DatabaseObject((string)row[0]!, (string)row[1]!, DatabaseObjectKind.Routine,
                    (Convert.ToString(row[2], CultureInfo.InvariantCulture) ?? string.Empty).ToLowerInvariant()));
            }
        }
        return [.. schemas.Union(objects.Select(o => o.Schema)).Select(schema => (schema, (IReadOnlyList<DatabaseObject>)[.. objects.Where(o => o.Schema == schema)]))];
    }

    /// <summary>Runs each of <paramref name="runs"/> in turn on one connection, telling each one's result sets (each read to
    /// <paramref name="limit"/> rows), the rows it changed or its failure, after which nothing more runs.</summary>
    internal static async Task RunAsync(DatabaseSource source, IReadOnlyList<string> runs, int limit, Action<SqlResult> told, CancellationToken cancel)
    {
        await using DbConnection connection = await OpenAsync(source, cancel);
        foreach (string run in runs)
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            List<DatabaseRows> sets = [];
            try
            {
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = run;
                command.CommandTimeout = 0;
                using CancellationTokenRegistration interrupt = Interrupt(connection, cancel);
                int affected;
                bool cut = false;
                DbDataReader reader = await command.ExecuteReaderAsync(cancel);
                try
                {
                    do
                    {
                        if (reader.FieldCount > 0)
                        {
                            DatabaseRows rows = await RowsAsync(reader, limit, cancel);
                            sets.Add(rows);
                            if (rows.More)
                            {
                                // What is left of a long result is never read: the server stops sending it.
                                cut = true;
                                command.Cancel();
                                break;
                            }
                        }
                    }
                    while (await reader.NextResultAsync(cancel));
                    affected = reader.RecordsAffected;
                }
                finally
                {
                    await LetGoAsync(reader, cut);
                }
                told(new SqlResult(run, sets, affected, null, System.Diagnostics.Stopwatch.GetElapsedTime(began)));
            }
            catch (DbException failed) when (!cancel.IsCancellationRequested)
            {
                told(new SqlResult(run, sets, -1, ContainerSecrets.InSaid(failed.Message, known: [source.ConnectionString]), System.Diagnostics.Stopwatch.GetElapsedTime(began)));
                return;
            }
        }
    }

    internal static async Task UpdateAsync(DatabaseSource source, DatabaseRows rows, int row, int column, string text, CancellationToken cancel)
    {
        if (!rows.IsEditable(row, column) || rows.Schema is not { } schema)
        {
            throw new InvalidOperationException("Only a table with a primary key has editable cells");
        }
        object? original = rows.Rows[row][column];
        object? value = text == "NULL" ? null : original is null or string ? text
            : Convert.ChangeType(text, original.GetType(), CultureInfo.InvariantCulture);
        if (value is null && schema.Columns.First(c => c.Name == rows.Columns[column]) is { Nullable: false })
        {
            throw new InvalidOperationException("This column cannot hold NULL");
        }
        await using DbConnection connection = await OpenAsync(source, cancel);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancel);
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 30;
        Add(command, "@value", value);
        List<string> where = [];
        for (int i = 0; i < rows.Columns.Length; i++)
        {
            // The original values refuse a concurrent change instead of overwriting it.
            if (schema.Columns.First(c => c.Name == rows.Columns[i]) is { Generated: true, Primary: false })
            {
                continue;
            }
            string name = Quote(source.Kind, rows.Columns[i]);
            string parameter = $"@old{i}";
            where.Add(rows.Rows[row][i] is null ? $"{name} IS NULL" : $"{name}={parameter}");
            Add(command, parameter, rows.Rows[row][i]);
        }
        command.CommandText = $"UPDATE {Full(source.Kind, schema.Table)} SET {Quote(source.Kind, rows.Columns[column])}=@value WHERE {string.Join(" AND ", where)}";
        int affected = await command.ExecuteNonQueryAsync(cancel);
        if (affected != 1)
        {
            throw new InvalidOperationException($"The row changed since it was read ({affected} rows matched). Refresh before editing");
        }
        await transaction.CommitAsync(cancel);
    }

    private static async Task<DatabaseRows> ReadAsync(DbConnection connection, string sql, IReadOnlyList<(string Name, object? Value)> parameters, CancellationToken cancel, int limit = 10000)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        foreach ((string name, object? value) in parameters)
        {
            Add(command, name, value);
        }
        using CancellationTokenRegistration interrupt = Interrupt(connection, cancel);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancel);
        return await RowsAsync(reader, limit, cancel);
    }

    /// <summary>Closes a reader, whose driver reports the cancel of a result cut short as a failure of its own.</summary>
    private static async Task LetGoAsync(DbDataReader reader, bool cut)
    {
        try
        {
            await reader.DisposeAsync();
        }
        catch (DbException) when (cut)
        {
            IdeLog.Write(IdeLog.Ide, "Database: a result cut short was cancelled as asked.");
        }
    }

    /// <summary>SQLite runs in the calling thread's native code, which only its interrupt stops.</summary>
    private static CancellationTokenRegistration Interrupt(DbConnection connection, CancellationToken cancel) =>
        connection is SqliteConnection { Handle: { } handle } ? cancel.Register(() => SQLitePCL.raw.sqlite3_interrupt(handle)) : default;

    /// <summary>A reader's current result set, its first <paramref name="limit"/> rows and whether more follow.</summary>
    private static async Task<DatabaseRows> RowsAsync(DbDataReader reader, int limit, CancellationToken cancel)
    {
        string[] columns = [.. Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)];
        List<object?[]> rows = [];
        bool more = false;
        while (await reader.ReadAsync(cancel))
        {
            if (rows.Count == limit)
            {
                more = true;
                break;
            }
            object?[] values = new object?[reader.FieldCount];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = await reader.IsDBNullAsync(i, cancel) ? null : reader.GetValue(i);
            }
            rows.Add(values);
        }
        return new DatabaseRows(columns, [.. rows], more, reader.RecordsAffected);
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
