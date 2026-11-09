using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace Passenger.App.Database;

internal static class DatabaseClient
{
    internal const int PageSize = 100;

    private static async Task<DbConnection> OpenAsync(DatabaseSource source, CancellationToken cancel)
    {
        DbConnection connection = source.Kind switch
        {
            DatabaseKind.Sqlite => new SqliteConnection(new SqliteConnectionStringBuilder(source.ConnectionString) { Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ConnectionString),
            DatabaseKind.Postgres => new NpgsqlConnection(new NpgsqlConnectionStringBuilder(source.ConnectionString) { Pooling = false }.ConnectionString),
            DatabaseKind.SqlServer => new SqlConnection(new SqlConnectionStringBuilder(source.ConnectionString) { Pooling = false }.ConnectionString),
            DatabaseKind.MySql => new MySqlConnection(new MySqlConnectionStringBuilder(source.ConnectionString) { Pooling = false }.ConnectionString),
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

    internal static async Task<DatabaseRows> PageAsync(DatabaseSource source, DatabaseSchema schema, int offset, CancellationToken cancel)
    {
        await using DbConnection connection = await OpenAsync(source, cancel);
        string order = string.Join(",", schema.Columns.Where(c => c.Primary).Select(c => Quote(source.Kind, c.Name)));
        string sql = $"SELECT * FROM {Full(source.Kind, schema.Table)} ORDER BY {(order.Length == 0 ? "1" : order)}";
        sql += source.Kind == DatabaseKind.SqlServer ? " OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY" : " LIMIT @take OFFSET @offset";
        DatabaseRows rows = await ReadAsync(connection, sql, [("@offset", offset), ("@take", PageSize + 1)], cancel, PageSize);
        return new DatabaseRows(rows.Columns, rows.Rows, rows.More) { Schema = schema, Offset = offset };
    }

    internal static async Task<DatabaseRows> QueryAsync(DatabaseSource source, string sql, CancellationToken cancel)
    {
        await using DbConnection connection = await OpenAsync(source, cancel);
        return await ReadAsync(connection, sql, [], cancel, 1000);
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
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancel);
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
