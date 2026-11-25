using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.App.Database;

public sealed record DatabaseFilter(string Column, string Value, string Operator);

internal static partial class DatabaseClient
{
    internal const int PageSize = 100;

    /// <summary>Opens a connection, its secret parts read from the system's store where they are kept there, through its SSH
    /// tunnel where it has one (its certificate still checked against its server's own name). A read-only connection is
    /// one the server holds read-only too where it can: SQLite's file opened read-only, PostgreSQL's and MySQL's sessions'
    /// transactions read-only, SQL Server's asked for a read-only session.</summary>
    internal static async Task<DbConnection> OpenAsync(DatabaseSource source, CancellationToken cancel)
    {
        if (DatabaseKinds.HoldsDocuments(source.Kind))
        {
            throw new InvalidOperationException(Refusals.NoSqlOnDocuments(DatabaseKinds.Title(source.Kind)));
        }
        if (source.Kind == DatabaseKind.DuckDb)
        {
            try
            {
                await DuckDbNative.Current.ReadyAsync(cancel);
            }
            catch (Updates.ReleaseAssetException failed)
            {
                throw new IOException(Refusals.NoDuckDbEngine(failed.Message), failed);
            }
        }
        string whole = source.ConnectionString;
        if (source.SecretKept && SystemCredentials.Read(DatabaseController.CredentialService, source.Account) is { Length: > 0 } secret)
        {
            MarkSecrets(secret);
            whole = KeptConnection.Joined(source.Kind, whole, secret);
        }
        if (source.Over.Length > 0)
        {
            string? overSecret = SystemCredentials.Read(DatabaseController.CredentialService, source.OverAccount);
            MarkSecrets(overSecret);
            whole = DatabaseKinds.LaidOver(source.Kind, whole, KeptConnection.Joined(source.Kind, source.Over, overSecret));
        }
        string? through = null;
        if (source.Tunnel is { } tunnel && !DatabaseKinds.IsFile(source.Kind) && source.Kind != DatabaseKind.Snowflake)
        {
            (string host, int port) = DatabaseKinds.Endpoint(source.Kind, whole);
            whole = DatabaseKinds.AtLoopback(source.Kind, whole, await SshTunnels.PortAsync(source, tunnel, host, port, cancel), host);
            through = host;
        }
        bool readOnly = source.Mark == DatabaseMark.ReadOnly;
        NpgsqlConnectionStringBuilder? postgres = source.Kind == DatabaseKind.Postgres ? new NpgsqlConnectionStringBuilder(whole) { Pooling = false } : null;
        if (postgres is not null && readOnly)
        {
            postgres.Options = $"{postgres.Options} -c default_transaction_read_only=on".Trim();
        }
        DbConnection connection = source.Kind switch
        {
            DatabaseKind.Sqlite => new SqliteConnection(new SqliteConnectionStringBuilder(whole) { Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false }.ConnectionString),
            DatabaseKind.Postgres => new NpgsqlConnection(postgres!.ConnectionString),
            DatabaseKind.SqlServer => new SqlConnection(SqlServer(whole, readOnly)),
            DatabaseKind.MySql => MySql(whole, through),
            DatabaseKind.DuckDb => DuckDb(whole, readOnly),
            DatabaseKind.ClickHouse => new ClickHouse.Driver.ADO.ClickHouseConnection(whole),
            DatabaseKind.Snowflake => new Snowflake.Data.Client.SnowflakeDbConnection { ConnectionString = whole },
            _ => throw new InvalidOperationException("Unknown database provider"),
        };
        if (readOnly && connection is ClickHouse.Driver.ADO.ClickHouseConnection clickHouse)
        {
            // 2 refuses every change of data or schema and still takes the settings a query sends.
            clickHouse.CustomSettings["readonly"] = 2;
        }
        if (through is not null && connection is NpgsqlConnection npgsql)
        {
            npgsql.SslClientAuthenticationOptionsCallback = options => options.TargetHost = through;
        }
        try
        {
            await connection.OpenAsync(cancel);
            if (readOnly && source.Kind == DatabaseKind.MySql)
            {
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = "SET SESSION TRANSACTION READ ONLY";
                await command.ExecuteNonQueryAsync(cancel);
            }
            if (source.Kind == DatabaseKind.DuckDb && Directory.Exists(source.Folder))
            {
                // A relative path in a query is read from the folder, whose files the explorer lists.
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = $"SET file_search_path = {DuckDbFiles.Read(source.Folder)}";
                await command.ExecuteNonQueryAsync(cancel);
            }
            return connection;
        }
        catch (Exception)
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>A MySQL connection, its console's SET @a and SELECT @a the session's own variables, its parameters bound by
    /// name over them; through a tunnel, its certificate verified here against its authority and its server's own name,
    /// as the driver would check the tunnel's address.</summary>
    private static MySqlConnection MySql(string whole, string? through)
    {
        MySqlConnectionStringBuilder my = new(whole) { Pooling = false, AllowUserVariables = true };
        X509Certificate2? client = null;
        if (my is { SslCert.Length: > 0, SslKey.Length: > 0, CertificatePassword.Length: > 0, CertificateFile.Length: 0 })
        {
            try
            {
                client = DatabaseSsl.ClientCertificate(my.SslCert, my.SslKey, my.CertificatePassword);
            }
            catch (CryptographicException unread)
            {
                throw new IOException($"The client certificate {Path.GetFileName(my.SslCert)} could not be read with its key: {unread.Message}", unread);
            }
            my.SslCert = string.Empty;
            my.SslKey = string.Empty;
            my.CertificatePassword = string.Empty;
        }
        string? authority = null;
        string? named = null;
        if (through is not null && my.SslMode is MySqlSslMode.VerifyCA or MySqlSslMode.VerifyFull)
        {
            authority = my.SslCa;
            named = my.SslMode == MySqlSslMode.VerifyFull ? through : null;
            my.SslMode = MySqlSslMode.Required;
            my.SslCa = string.Empty;
        }
        MySqlConnection connection = new(my.ConnectionString);
        if (authority is not null)
        {
            connection.RemoteCertificateValidationCallback = (_, certificate, _, _) => DatabaseSsl.Verified(certificate, authority, named);
        }
        if (client is not null)
        {
            connection.ProvideClientCertificatesCallback = certificates =>
            {
                certificates.Add(client);
                return ValueTask.CompletedTask;
            };
        }
        return connection;
    }

    /// <summary>A SQL Server connection's string, a read-only one asking for a read-only session, which an availability
    /// group's listener gives on a readable secondary that holds every change away (a server standing alone has none).</summary>
    private static string SqlServer(string whole, bool readOnly)
    {
        SqlConnectionStringBuilder sql = new(whole) { Pooling = false };
        if (readOnly)
        {
            sql.ApplicationIntent = ApplicationIntent.ReadOnly;
        }
        return sql.ConnectionString;
    }

    /// <summary>A DuckDB connection: its file opened as it is, never made where it is not there (as DuckDB would), read-only
    /// where the connection is; one in memory takes the folder's files alone.</summary>
    private static DuckDB.NET.Data.DuckDBConnection DuckDb(string whole, bool readOnly)
    {
        string file = DatabaseKinds.FileOf(DatabaseKind.DuckDb, whole);
        if (file.Length > 0 && !File.Exists(file))
        {
            throw new IOException($"No DuckDB database file {file}");
        }
        return new DuckDB.NET.Data.DuckDBConnection(readOnly && file.Length > 0 ? DatabaseKinds.With(whole, "ACCESS_MODE", "READ_ONLY") : whole);
    }

    /// <summary>Marks each part of a connection string's secret parts, so no message says them.</summary>
    private static void MarkSecrets(string? secret)
    {
        if (secret is not { Length: > 0 })
        {
            return;
        }
        DbConnectionStringBuilder parts = new() { ConnectionString = secret };
        foreach (string key in parts.Keys)
        {
            ContainerSecrets.Mark(Convert.ToString(parts[key], CultureInfo.InvariantCulture) ?? string.Empty);
        }
    }

    internal static string Quote(DatabaseKind kind, string name) => SqlText.QuotedName(kind, name);

    internal static string Full(DatabaseKind kind, DatabaseTable table) =>
        kind == DatabaseKind.DuckDb && table.Schema == DuckDbFiles.Schema ? DuckDbFiles.Read(table.Name)
        : (table.Schema.Length > 0 ? Quote(kind, table.Schema) + "." : string.Empty) + Quote(kind, table.Name);

    internal static async Task<IReadOnlyList<DatabaseTable>> TablesAsync(DatabaseSource source, CancellationToken cancel)
    {
        await using DbConnection connection = await OpenAsync(source, cancel);
        string sql = source.Kind switch
        {
            DatabaseKind.Sqlite => "SELECT '', name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name",
            DatabaseKind.Postgres => "SELECT table_schema, table_name FROM information_schema.tables WHERE table_type='BASE TABLE' AND table_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1,2",
            DatabaseKind.SqlServer => "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE='BASE TABLE' ORDER BY 1,2",
            DatabaseKind.MySql => "SELECT table_schema, table_name FROM information_schema.tables WHERE table_type='BASE TABLE' AND table_schema=DATABASE() ORDER BY 1,2",
            DatabaseKind.DuckDb => "SELECT table_schema, table_name FROM information_schema.tables WHERE table_type='BASE TABLE' AND table_catalog=current_database() ORDER BY 1,2",
            DatabaseKind.ClickHouse => "SELECT database, name FROM system.tables WHERE database=currentDatabase() AND engine NOT LIKE '%View' ORDER BY name",
            _ => "SELECT table_schema, table_name FROM information_schema.tables WHERE table_type='BASE TABLE' AND table_schema<>'INFORMATION_SCHEMA' ORDER BY 1,2",
        };
        DatabaseRows rows = await ReadAsync(connection, sql, [], cancel, limit: 10000);
        return [.. rows.Rows.Select(r => new DatabaseTable((string)r[0]!, (string)r[1]!))];
    }

    internal static async Task<DatabaseSchema> SchemaAsync(DatabaseSource source, DatabaseTable table, CancellationToken cancel)
    {
        if (DatabaseKinds.HoldsDocuments(source.Kind))
        {
            return await DocumentClient.SchemaAsync(source, table, cancel);
        }
        await using DbConnection connection = await OpenAsync(source, cancel);
        if (source.Kind is DatabaseKind.DuckDb or DatabaseKind.ClickHouse or DatabaseKind.Snowflake)
        {
            return await AnalyticalSchemaAsync(connection, source.Kind, table, cancel);
        }
        List<DatabaseColumn> columns = [];
        List<string> keys = [];
        List<DatabasePart> indexes = [];
        List<DatabasePart> triggers = [];
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
                indexes.Add(new DatabasePart((string)index[0]!,
                    $"{index[0]} · {(Convert.ToInt32(index[1], CultureInfo.InvariantCulture) != 0 ? "UNIQUE · " : string.Empty)}{string.Join(", ", fields.Rows.Select(r => r[0]))}"));
            }
            DatabaseRows made = await ReadAsync(connection, "SELECT name, sql FROM sqlite_schema WHERE type='trigger' AND tbl_name=@table ORDER BY name", [("@table", table.Name)], cancel);
            triggers.AddRange(made.Rows.Select(r => new DatabasePart((string)r[0]!, DatabaseDdl.TriggerWhen(Convert.ToString(r[1], CultureInfo.InvariantCulture) ?? string.Empty, source.Kind))));
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
            indexes.AddRange(indexed.Rows.Select(r => new DatabasePart(Convert.ToString(r[0], CultureInfo.InvariantCulture) ?? string.Empty, $"{r[0]} · {r[1]}")));
            string triggerSql = source.Kind switch
            {
                DatabaseKind.Postgres => "SELECT trigger_name, string_agg(DISTINCT lower(action_timing || ' ' || event_manipulation), ', ') FROM information_schema.triggers "
                    + "WHERE event_object_schema=@schema AND event_object_table=@table GROUP BY trigger_name ORDER BY 1",
                DatabaseKind.MySql => "SELECT trigger_name, lower(concat(action_timing, ' ', event_manipulation)) FROM information_schema.triggers "
                    + "WHERE event_object_schema=@schema AND event_object_table=@table ORDER BY 1",
                _ => "SELECT tr.name, CASE WHEN tr.is_instead_of_trigger=1 THEN 'instead of ' ELSE 'after ' END + LOWER(STRING_AGG(te.type_desc, ', ')) FROM sys.triggers tr "
                    + "JOIN sys.trigger_events te ON te.object_id=tr.object_id JOIN sys.objects t ON t.object_id=tr.parent_id JOIN sys.schemas s ON s.schema_id=t.schema_id "
                    + "WHERE s.name=@schema AND t.name=@table GROUP BY tr.name, tr.is_instead_of_trigger ORDER BY 1",
            };
            triggers.AddRange((await ReadAsync(connection, triggerSql, parameters, cancel)).Rows.Select(r =>
                new DatabasePart(Convert.ToString(r[0], CultureInfo.InvariantCulture) ?? string.Empty, Convert.ToString(r[1], CultureInfo.InvariantCulture) ?? string.Empty)));
        }
        return new DatabaseSchema(table, columns, keys, indexes) { Triggers = triggers };
    }

    /// <summary>A DuckDB, ClickHouse or Snowflake table's or view's columns, keys and indexes as its catalog gives them: a
    /// folder's file's as DuckDB reads it; ClickHouse's keys its primary, sorting and partition keys, its indexes the ones
    /// that skip data; Snowflake's none, as it enforces no key and holds no index.</summary>
    private static async Task<DatabaseSchema> AnalyticalSchemaAsync(DbConnection connection, DatabaseKind kind, DatabaseTable table, CancellationToken cancel)
    {
        (string, object?)[] parameters = [("@schema", table.Schema), ("@table", table.Name)];
        List<DatabaseColumn> columns = [];
        List<string> keys = [];
        List<DatabasePart> indexes = [];
        if (kind == DatabaseKind.DuckDb && table.Schema == DuckDbFiles.Schema)
        {
            DatabaseRows described = await ReadAsync(connection, $"DESCRIBE SELECT * FROM {DuckDbFiles.Read(table.Name)}", [], cancel);
            columns.AddRange(described.Rows.Select(r => new DatabaseColumn(Text(r[0]), Text(r[1]), Text(r[2]) != "NO", false, false)));
            return new DatabaseSchema(table, columns, keys, indexes);
        }
        if (kind == DatabaseKind.ClickHouse)
        {
            DatabaseRows info = await ReadAsync(connection, "SELECT name, type, is_in_primary_key, default_kind FROM system.columns WHERE database=@schema AND table=@table ORDER BY position", parameters, cancel);
            columns.AddRange(info.Rows.Select(r => new DatabaseColumn(Text(r[0]), Text(r[1]), Text(r[1]).StartsWith("Nullable(", StringComparison.Ordinal),
                Convert.ToInt32(r[2], CultureInfo.InvariantCulture) != 0, Text(r[3]) is "MATERIALIZED" or "ALIAS")));
            DatabaseRows told = await ReadAsync(connection, "SELECT primary_key, sorting_key, partition_key FROM system.tables WHERE database=@schema AND name=@table", parameters, cancel);
            foreach (object?[] row in told.Rows)
            {
                keys.AddRange(new[] { ("PRIMARY KEY", Text(row[0])), ("ORDER BY", Text(row[1])), ("PARTITION BY", Text(row[2])) }.Where(k => k.Item2.Length > 0).Select(k => $"{k.Item1} · {k.Item2}"));
            }
            DatabaseRows skipping = await ReadAsync(connection, "SELECT name, type, expr FROM system.data_skipping_indices WHERE database=@schema AND table=@table ORDER BY name", parameters, cancel);
            indexes.AddRange(skipping.Rows.Select(r => new DatabasePart(Text(r[0]), $"{Text(r[0])} · {Text(r[1])} · {Text(r[2])}")));
            return new DatabaseSchema(table, columns, keys, indexes);
        }
        string catalog = kind == DatabaseKind.DuckDb ? " AND table_catalog=current_database()" : string.Empty;
        DatabaseRows read = await ReadAsync(connection, $"SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_schema=@schema AND table_name=@table{catalog} ORDER BY ordinal_position", parameters, cancel);
        HashSet<string> primary = [];
        if (kind == DatabaseKind.DuckDb)
        {
            DatabaseRows constraints = await ReadAsync(connection, "SELECT constraint_type, array_to_string(constraint_column_names, ', ') FROM duckdb_constraints() "
                + "WHERE database_name=current_database() AND schema_name=@schema AND table_name=@table AND constraint_type IN ('PRIMARY KEY', 'UNIQUE', 'FOREIGN KEY') ORDER BY 1", parameters, cancel);
            keys.AddRange(constraints.Rows.Select(r => $"{Text(r[0])} · {Text(r[1])}"));
            primary.UnionWith(constraints.Rows.Where(r => Text(r[0]) == "PRIMARY KEY").SelectMany(r => Text(r[1]).Split(", ")));
            DatabaseRows indexed = await ReadAsync(connection, "SELECT index_name, sql FROM duckdb_indexes() WHERE database_name=current_database() AND schema_name=@schema AND table_name=@table ORDER BY 1", parameters, cancel);
            indexes.AddRange(indexed.Rows.Select(r => new DatabasePart(Text(r[0]), $"{Text(r[0])} · {Text(r[1])}")));
        }
        columns.AddRange(read.Rows.Select(r => new DatabaseColumn(Text(r[0]), Text(r[1]), Text(r[2]) == "YES", primary.Contains(Text(r[0])), false)));
        return new DatabaseSchema(table, columns, keys, indexes);

        static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    internal static async Task<DatabaseRows> PageAsync(DatabaseSource source, DatabaseSchema schema, int offset, CancellationToken cancel, DatabaseFilter? filter = null,
        (string Column, bool Descending)? sort = null)
    {
        if (DatabaseKinds.HoldsDocuments(source.Kind))
        {
            return await DocumentClient.PageAsync(source, schema, offset, cancel, filter, sort);
        }
        await using DbConnection connection = await OpenAsync(source, cancel);
        (string sql, List<(string Name, object? Value)> parameters) = Selecting(source.Kind, schema, filter, sort);
        parameters.Add(("@offset", offset));
        parameters.Add(("@take", PageSize + 1));
        sql += source.Kind == DatabaseKind.SqlServer ? " OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY" : " LIMIT @take OFFSET @offset";
        DatabaseRows rows = await ReadAsync(connection, sql, parameters, cancel, PageSize);
        return new DatabaseRows(rows.Columns, rows.Rows, rows.More)
        {
            Schema = schema,
            Offset = offset,
            Types = rows.Types,
            Into = new InsertTarget(source.Kind, Full(source.Kind, schema.Table)),
            OrderedBy = sort is { } sorted ? (Array.IndexOf(rows.Columns, sorted.Column), sorted.Descending) : null,
        };
    }

    /// <summary>A table's rows as a query reads them, filtered and sorted on the server, else in its primary key's order,
    /// with the parameters it takes.</summary>
    internal static (string Sql, List<(string Name, object? Value)> Parameters) Selecting(DatabaseKind kind, DatabaseSchema schema, DatabaseFilter? filter,
        (string Column, bool Descending)? sort)
    {
        if (sort is { } wanted && !schema.Columns.Any(c => c.Name == wanted.Column))
        {
            throw new InvalidOperationException("Choose a column of this table");
        }
        string order = sort is { } by ? Quote(kind, by.Column) + (by.Descending ? " DESC" : string.Empty)
            : string.Join(",", schema.Columns.Where(c => c.Primary).Select(c => Quote(kind, c.Name)));
        List<(string Name, object? Value)> parameters = [];
        string where = string.Empty;
        if (filter is not null)
        {
            if (!schema.Columns.Any(c => c.Name == filter.Column)) { throw new InvalidOperationException("Choose a column of this table"); }
            string column = Quote(kind, filter.Column);
            string text = kind switch
            {
                DatabaseKind.SqlServer => $"CONVERT(nvarchar(max), {column})",
                DatabaseKind.MySql => $"CAST({column} AS CHAR)",
                DatabaseKind.ClickHouse => $"toString({column})",
                DatabaseKind.Snowflake => $"TO_VARCHAR({column})",
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
        return ($"SELECT * FROM {Full(kind, schema.Table)}{where} ORDER BY {(order.Length == 0 ? "1" : order)}", parameters);
    }

    /// <summary>The databases of the connection's server; SQLite's main database alone.</summary>
    internal static async Task<IReadOnlyList<string>> DatabasesAsync(DatabaseSource source, CancellationToken cancel)
    {
        if (DatabaseKinds.IsFile(source.Kind))
        {
            return [source.Database];
        }
        if (DatabaseKinds.HoldsDocuments(source.Kind))
        {
            return await DocumentClient.DatabasesAsync(source, cancel);
        }
        await using DbConnection connection = await OpenAsync(source, cancel);
        string sql = source.Kind switch
        {
            DatabaseKind.Postgres => "SELECT datname FROM pg_database WHERE NOT datistemplate AND datallowconn ORDER BY 1",
            DatabaseKind.SqlServer => "SELECT name FROM sys.databases WHERE HAS_DBACCESS(name)=1 ORDER BY name",
            DatabaseKind.ClickHouse => "SELECT name FROM system.databases WHERE name NOT IN ('system','information_schema','INFORMATION_SCHEMA') ORDER BY name",
            DatabaseKind.Snowflake => "SHOW TERSE DATABASES",
            _ => "SELECT schema_name FROM information_schema.schemata WHERE schema_name NOT IN ('information_schema','performance_schema','mysql','sys') ORDER BY 1",
        };
        DatabaseRows rows = await ReadAsync(connection, sql, [], cancel);
        int named = source.Kind == DatabaseKind.Snowflake ? Math.Max(0, Array.FindIndex(rows.Columns, c => c.Equals("name", StringComparison.OrdinalIgnoreCase))) : 0;
        return [.. rows.Rows.Select(r => Convert.ToString(r[named], CultureInfo.InvariantCulture) ?? string.Empty)];
    }

    /// <summary>A database's schemas (none of the system's) with the tables, views and routines each holds; MySQL's database is
    /// its one schema, and SQLite's has none of a name.</summary>
    internal static async Task<IReadOnlyList<(string Schema, IReadOnlyList<DatabaseObject> Objects)>> ObjectsAsync(DatabaseSource source, CancellationToken cancel)
    {
        if (DatabaseKinds.HoldsDocuments(source.Kind))
        {
            return await DocumentClient.CollectionsAsync(source, cancel);
        }
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
                DatabaseKind.DuckDb => (
                    "SELECT schema_name FROM information_schema.schemata WHERE catalog_name=current_database() AND schema_name NOT IN ('information_schema','pg_catalog') ORDER BY 1",
                    "SELECT table_schema, table_name, table_type FROM information_schema.tables WHERE table_catalog=current_database() AND table_schema NOT IN ('information_schema','pg_catalog') ORDER BY 1,2",
                    "SELECT schema_name, function_name, function_type FROM duckdb_functions() WHERE NOT internal AND database_name=current_database() ORDER BY 1,2"),
                DatabaseKind.ClickHouse => (
                    "SELECT currentDatabase()",
                    "SELECT database, name, engine FROM system.tables WHERE database=currentDatabase() ORDER BY name",
                    "SELECT currentDatabase(), name, 'function' FROM system.functions WHERE origin='SQLUserDefined' ORDER BY name"),
                DatabaseKind.Snowflake => (
                    "SELECT schema_name FROM information_schema.schemata WHERE schema_name<>'INFORMATION_SCHEMA' ORDER BY 1",
                    "SELECT table_schema, table_name, table_type FROM information_schema.tables WHERE table_schema<>'INFORMATION_SCHEMA' ORDER BY 1,2",
                    "SELECT function_schema, function_name, 'function' FROM information_schema.functions UNION ALL SELECT procedure_schema, procedure_name, 'procedure' FROM information_schema.procedures ORDER BY 1,2"),
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
        if (source.Kind == DatabaseKind.DuckDb && DuckDbFiles.Under(source.Folder) is { Count: > 0 } files)
        {
            schemas.Add(DuckDbFiles.Schema);
            objects.AddRange(files.Select(f => new DatabaseObject(DuckDbFiles.Schema, f, DatabaseObjectKind.File)));
        }
        return [.. schemas.Union(objects.Select(o => o.Schema)).Select(schema => (schema, (IReadOnlyList<DatabaseObject>)[.. objects.Where(o => o.Schema == schema)]))];
    }

    /// <summary>The tables and views of the connection's database with their columns, for completion.</summary>
    internal static async Task<SqlCatalog> CatalogAsync(DatabaseSource source, CancellationToken cancel)
    {
        if (DatabaseKinds.HoldsDocuments(source.Kind))
        {
            return await DocumentClient.CatalogAsync(source, cancel);
        }
        await using DbConnection connection = await OpenAsync(source, cancel);
        string sql = source.Kind switch
        {
            DatabaseKind.Sqlite => "SELECT '', m.name, p.name, p.type FROM sqlite_schema m JOIN pragma_table_info(m.name) p WHERE m.type IN ('table','view') AND m.name NOT LIKE 'sqlite_%' ORDER BY m.name, p.cid",
            DatabaseKind.Postgres => "SELECT table_schema, table_name, column_name, data_type FROM information_schema.columns WHERE table_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1, 2, ordinal_position",
            DatabaseKind.SqlServer => "SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS ORDER BY 1, 2, ORDINAL_POSITION",
            DatabaseKind.MySql => "SELECT table_schema, table_name, column_name, data_type FROM information_schema.columns WHERE table_schema = DATABASE() ORDER BY 1, 2, ordinal_position",
            DatabaseKind.DuckDb => "SELECT table_schema, table_name, column_name, data_type FROM information_schema.columns WHERE table_catalog = current_database() "
                + "AND table_schema NOT IN ('information_schema','pg_catalog') ORDER BY 1, 2, ordinal_position",
            DatabaseKind.ClickHouse => "SELECT database, table, name, type FROM system.columns WHERE database = currentDatabase() ORDER BY 1, 2, position",
            _ => "SELECT table_schema, table_name, column_name, data_type FROM information_schema.columns WHERE table_schema <> 'INFORMATION_SCHEMA' ORDER BY 1, 2, ordinal_position",
        };
        DatabaseRows rows = await ReadAsync(connection, sql, [], cancel, limit: 50000);
        return new SqlCatalog([.. rows.Rows.GroupBy(r => (Schema: Text(r[0]), Table: Text(r[1])))
            .Select(g => new SqlTableInfo(g.Key.Schema, g.Key.Table, [.. g.Select(r => (Text(r[2]), Text(r[3])))]))]);

        static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>Runs each of <paramref name="runs"/> in turn on one connection, telling each one's result sets (each read to
    /// <paramref name="limit"/> rows), the rows it changed or its failure, after which nothing more runs; its parameters
    /// given the <paramref name="values"/> asked by name, where they were.</summary>
    internal static async Task RunAsync(DatabaseSource source, IReadOnlyList<string> runs, int limit, Action<SqlResult> told, CancellationToken cancel,
        IReadOnlyDictionary<string, string>? values = null)
    {
        if (DatabaseKinds.HoldsDocuments(source.Kind))
        {
            await DocumentClient.RunAsync(source, runs, limit, told, cancel);
            return;
        }
        await using DbConnection connection = await OpenAsync(source, cancel);
        await RunOnAsync(source, connection, null, runs, limit, told, cancel, values);
    }

    /// <summary>Runs each of <paramref name="runs"/> in turn on a connection held open, in its <paramref name="transaction"/>
    /// where it has one, as <see cref="RunAsync"/> does; false where one failed.</summary>
    internal static async Task<bool> RunOnAsync(DatabaseSource source, DbConnection connection, DbTransaction? transaction, IReadOnlyList<string> runs, int limit,
        Action<SqlResult> told, CancellationToken cancel, IReadOnlyDictionary<string, string>? values = null)
    {
        List<List<SqlParameter>> parameters = SqlParameters.Of(runs, source.Kind);
        for (int at = 0; at < runs.Count; at++)
        {
            string run = runs[at];
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            List<DatabaseRows> sets = [];
            try
            {
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = values is null ? run : Bind(command, run, parameters[at], values, source.Kind);
                command.Transaction = transaction;
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
                            DatabaseRows rows = await RowsAsync(reader, limit, cancel, new InsertTarget(source.Kind, SqlInserts.TableOf(run, source.Kind)));
                            sets.Add(rows);
                            if (rows.More)
                            {
                                // The rest of a long result is cancelled unread, but passed over in PostgreSQL's transaction, which a cancel ends.
                                cut = true;
                                if (transaction is null || source.Kind != DatabaseKind.Postgres)
                                {
                                    command.Cancel();
                                }
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
                return false;
            }
        }
        return true;
    }

    /// <summary>A run with its parameters given the values asked, as its driver binds them: by name, a place's in its
    /// order with no name (PostgreSQL's $n, MySQL's ?), SQLite's bare ? written as its number, which SQLite binds by.</summary>
    internal static string Bind(DbCommand command, string run, IReadOnlyList<SqlParameter> found, IReadOnlyDictionary<string, string> values, DatabaseKind kind)
    {
        if (found.Count == 0)
        {
            return run;
        }
        bool positional = kind == DatabaseKind.Postgres && found.Any(p => p.Name[0] == '$') || kind == DatabaseKind.MySql && found.Any(p => p.Name[0] == '?');
        if (positional)
        {
            IEnumerable<string> places = kind == DatabaseKind.Postgres
                ? Enumerable.Range(1, found.Where(p => p.Name[0] == '$').Max(p => int.Parse(p.Name[1..], CultureInfo.InvariantCulture))).Select(n => $"${n.ToString(CultureInfo.InvariantCulture)}")
                : found.Select(p => p.Name);
            foreach (string place in places)
            {
                command.Parameters.Add(Made(command, string.Empty, values.GetValueOrDefault(place, "NULL"), kind));
            }
            return run;
        }
        string sql = run;
        foreach (SqlParameter parameter in found.Where(p => p.Name.Length > 1 && p.Name[0] == '?').Reverse())
        {
            sql = sql[..parameter.Start] + parameter.Name + sql[parameter.End..];
        }
        foreach (string name in found.Select(p => p.Name).Distinct(StringComparer.Ordinal))
        {
            command.Parameters.Add(Made(command, name, values.GetValueOrDefault(name, "NULL"), kind));
        }
        return sql;
    }

    private static DbParameter Made(DbCommand command, string name, string text, DatabaseKind kind)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = SqlParameters.Value(text, kind);
        if (parameter is Npgsql.NpgsqlParameter literal && parameter.Value is string)
        {
            // PostgreSQL reads the text as it reads a literal of the type its place needs.
            literal.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Unknown;
        }
        return parameter;
    }

    /// <summary>A statement's plan as its database explains it without running it (<see cref="SqlPlans"/>), its parameters
    /// given the <paramref name="values"/> asked; SQL Server's asked of a session that only plans.</summary>
    internal static async Task<PlanStep> ExplainAsync(DatabaseSource source, string statement, IReadOnlyDictionary<string, string>? values, CancellationToken cancel)
    {
        await using DbConnection connection = await OpenAsync(source, cancel);
        using CancellationTokenRegistration interrupt = Interrupt(connection, cancel);
        if (source.Kind == DatabaseKind.SqlServer)
        {
            await using DbCommand planning = connection.CreateCommand();
            planning.CommandText = "SET SHOWPLAN_XML ON";
            await planning.ExecuteNonQueryAsync(cancel);
        }
        string asked = SqlPlans.Asked(statement, source.Kind);
        await using DbCommand command = connection.CreateCommand();
        command.CommandTimeout = 60;
        command.CommandText = values is null ? asked : Bind(command, asked, SqlParameters.Of([asked], source.Kind)[0], values, source.Kind);
        DatabaseRows rows;
        await using (DbDataReader reader = await command.ExecuteReaderAsync(cancel))
        {
            rows = await RowsAsync(reader, 10000, cancel);
        }
        string Said(int row, int column) => Convert.ToString(rows.Rows[row][column], CultureInfo.InvariantCulture) ?? string.Empty;
        if (rows.RowCount == 0)
        {
            throw new InvalidOperationException(Refusals.NoPlan);
        }
        return source.Kind switch
        {
            DatabaseKind.DuckDb => SqlPlans.FromDuckDb(Said(0, rows.ColumnCount - 1)),
            DatabaseKind.ClickHouse => SqlPlans.FromPostgres(string.Join('\n', Enumerable.Range(0, rows.RowCount).Select(r => Said(r, 0)))),
            DatabaseKind.Snowflake => SqlPlans.FromSnowflake(Said(0, 0)),
            DatabaseKind.Postgres => SqlPlans.FromPostgres(Said(0, 0)),
            DatabaseKind.MySql => SqlPlans.FromMySql(Said(0, 0)),
            DatabaseKind.SqlServer => SqlPlans.FromSqlServer(Said(0, 0)),
            _ => SqlPlans.FromSqlite([.. Enumerable.Range(0, rows.RowCount).Select(r => (Convert.ToInt64(rows.Rows[r][0], CultureInfo.InvariantCulture),
                Convert.ToInt64(rows.Rows[r][1], CultureInfo.InvariantCulture), Said(r, rows.ColumnCount - 1)))]),
        };
    }

    /// <summary>Writes a table's changes in one transaction: each row deleted or changed found by its primary key, locked,
    /// and refused where it no longer holds what was read (deleted or changed elsewhere since), then each row added; nothing
    /// is written unless all of it is. The text written in a cell is read by the server as it reads a value of its column's
    /// type, or converted to the type its column was read as.</summary>
    internal static async Task SubmitAsync(DatabaseSource source, DatabaseSchema schema, DatabaseRows page, PendingEdits edits, CancellationToken cancel)
    {
        if (DatabaseKinds.HoldsDocuments(source.Kind))
        {
            await DocumentClient.SubmitAsync(source, schema, page, edits, cancel);
            return;
        }
        string[] columns = page.Columns;
        string[] keys = [.. schema.Columns.Where(c => c.Primary).Select(c => c.Name)];
        if (keys.Length == 0 && !(edits.Changed.IsEmpty && edits.Deleted.IsEmpty))
        {
            throw new InvalidOperationException(Refusals.NoPrimaryKey(schema.Table.ToString()));
        }
        string table = Full(source.Kind, schema.Table);
        await using DbConnection connection = await OpenAsync(source, cancel);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancel);
        using CancellationTokenRegistration interrupt = Interrupt(connection, cancel);
        foreach ((RowKey key, object?[] read) in edits.Deleted)
        {
            await HeldAsync(key, read);
            await WriteAsync($"DELETE FROM {table} WHERE {Where(keys)}", key, []);
        }
        foreach ((RowKey key, ChangedRow changed) in edits.Changed)
        {
            await HeldAsync(key, changed.Read);
            (string Column, object? Read, string? Text)[] set = [.. changed.Written.OrderBy(w => w.Key).Select(w => (columns[w.Key], changed.Read[w.Key], w.Value))];
            await WriteAsync($"UPDATE {table} SET {string.Join(", ", set.Select((c, i) => $"{Quote(source.Kind, c.Column)}=@v{i}"))} WHERE {Where(keys)}", key, set);
        }
        foreach (AddedRow added in edits.Added)
        {
            (string Column, object? Read, string? Text)[] given = [.. added.Written.OrderBy(w => w.Key).Select(w => (columns[w.Key], Like(w.Key), w.Value))];
            await using DbCommand command = Command(given.Length == 0
                ? source.Kind == DatabaseKind.MySql ? $"INSERT INTO {table} () VALUES ()" : $"INSERT INTO {table} DEFAULT VALUES"
                : $"INSERT INTO {table} ({string.Join(", ", given.Select(g => Quote(source.Kind, g.Column)))}) VALUES ({string.Join(", ", given.Select((_, i) => $"@v{i}"))})");
            for (int i = 0; i < given.Length; i++)
            {
                Written(command, $"@v{i}", given[i].Read, given[i].Text);
            }
            Place(command);
            await command.ExecuteNonQueryAsync(cancel);
        }
        await transaction.CommitAsync(cancel);

        string Where(string[] names) => string.Join(" AND ", names.Select((name, i) => $"{Quote(source.Kind, name)}=@k{i}"));

        DbCommand Command(string sql)
        {
            DbCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 30;
            command.CommandText = sql;
            return command;
        }

        void Keyed(DbCommand command, RowKey key)
        {
            for (int i = 0; i < key.Values.Length; i++)
            {
                Add(command, $"@k{i}", key.Values[i]);
            }
        }

        // The row as read, locked until the transaction ends, or why it may not be written.
        async Task HeldAsync(RowKey key, object?[] read)
        {
            string locked = source.Kind switch
            {
                DatabaseKind.Postgres or DatabaseKind.MySql => $"SELECT * FROM {table} WHERE {Where(keys)} FOR UPDATE",
                DatabaseKind.SqlServer => $"SELECT * FROM {table} WITH (UPDLOCK, HOLDLOCK) WHERE {Where(keys)}",
                _ => $"SELECT * FROM {table} WHERE {Where(keys)}",
            };
            await using DbCommand command = Command(locked);
            Keyed(command, key);
            Place(command);
            DatabaseRows now;
            await using (DbDataReader reader = await command.ExecuteReaderAsync(cancel))
            {
                now = await RowsAsync(reader, 2, cancel);
            }
            if (now.RowCount != 1)
            {
                throw new InvalidOperationException(Refusals.RowGone(schema.Table.ToString(), key.ToString()));
            }
            for (int i = 0; i < columns.Length; i++)
            {
                if (Array.IndexOf(now.Columns, columns[i]) is not (>= 0 and int at) || !PendingEdits.Same(now.Rows[0][at], read[i]))
                {
                    throw new InvalidOperationException(Refusals.RowChanged(schema.Table.ToString(), key.ToString()));
                }
            }
        }

        async Task WriteAsync(string sql, RowKey key, (string Column, object? Read, string? Text)[] set)
        {
            await using DbCommand command = Command(sql);
            for (int i = 0; i < set.Length; i++)
            {
                Written(command, $"@v{i}", set[i].Read, set[i].Text);
            }
            Keyed(command, key);
            Place(command);
            int affected = await command.ExecuteNonQueryAsync(cancel);
            if (affected != 1)
            {
                throw new InvalidOperationException(Refusals.RowChanged(schema.Table.ToString(), key.ToString()));
            }
        }

        // A value of the column as read on the page, whose type a new row's text is converted to.
        object? Like(int column) => page.Rows.Select(r => r[column]).FirstOrDefault(v => v is not null);

        void Written(DbCommand command, string name, object? like, string? text)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = text is null ? DBNull.Value : Typed(like, text);
            if (parameter is NpgsqlParameter literal && text is not null)
            {
                // PostgreSQL reads the text as it reads a literal of the column's type.
                literal.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Unknown;
            }
            command.Parameters.Add(parameter);
        }

        object Typed(object? like, string text) => source.Kind == DatabaseKind.Postgres ? text : like switch
        {
            bool => text.Trim() switch
            {
                "1" => true,
                "0" => false,
                string word => bool.TryParse(word, out bool truth) ? truth : throw new FormatException(Refusals.NotTrueOrFalse(text)),
            },
            IConvertible and not string => Convert.ChangeType(text, like.GetType(), CultureInfo.InvariantCulture),
            _ => text,
        };
    }

    internal static async Task<DatabaseRows> ReadAsync(DbConnection connection, string sql, IReadOnlyList<(string Name, object? Value)> parameters, CancellationToken cancel, int limit = 10000)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        foreach ((string name, object? value) in parameters)
        {
            Add(command, name, value);
        }
        Place(command);
        using CancellationTokenRegistration interrupt = Interrupt(connection, cancel);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancel);
        return await RowsAsync(reader, limit, cancel);
    }

    /// <summary>A command's @name parameters as its driver binds them: DuckDB's $name, ClickHouse's {name:Type} of the value's
    /// type, Snowflake's ? in the order they stand; the others' as written.</summary>
    internal static void Place(DbCommand command)
    {
        if (command.Connection is not (DuckDB.NET.Data.DuckDBConnection or ClickHouse.Driver.ADO.ClickHouseConnection or Snowflake.Data.Client.SnowflakeDbConnection)
            || command.Parameters.Count == 0)
        {
            return;
        }
        Dictionary<string, object?> given = command.Parameters.Cast<DbParameter>().ToDictionary(p => p.ParameterName.TrimStart('@'), p => p.Value is DBNull ? null : p.Value, StringComparer.Ordinal);
        command.Parameters.Clear();
        List<(string Name, object? Value)> placed = [];
        command.CommandText = Parameter().Replace(command.CommandText, match =>
        {
            string name = match.Groups[1].Value;
            if (!given.TryGetValue(name, out object? value))
            {
                return match.Value;
            }
            switch (command.Connection)
            {
                case DuckDB.NET.Data.DuckDBConnection:
                    placed.Add((name, value));
                    return "$" + name;
                case ClickHouse.Driver.ADO.ClickHouseConnection:
                    placed.Add((name, value));
                    string type = value switch { null => "Nullable(String)", bool => "Bool", int => "Int32", long => "Int64", double => "Float64", decimal => "Decimal(38, 10)", _ => "String" };
                    return "{" + name + ":" + type + "}";
                default:
                    placed.Add(((placed.Count + 1).ToString(CultureInfo.InvariantCulture), value));
                    return "?";
            }
        });
        foreach ((string name, object? value) in placed.DistinctBy(p => p.Name))
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            parameter.DbType = value switch { int => DbType.Int32, long => DbType.Int64, double => DbType.Double, decimal => DbType.Decimal, bool => DbType.Boolean, _ => DbType.String };
            command.Parameters.Add(parameter);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\w@])@(\w+)")]
    private static partial System.Text.RegularExpressions.Regex Parameter();

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
    internal static CancellationTokenRegistration Interrupt(DbConnection connection, CancellationToken cancel) =>
        connection is SqliteConnection { Handle: { } handle } ? cancel.Register(() => SQLitePCL.raw.sqlite3_interrupt(handle)) : default;

    /// <summary>A reader's current result set, its first <paramref name="limit"/> rows and whether more follow.</summary>
    private static async Task<DatabaseRows> RowsAsync(DbDataReader reader, int limit, CancellationToken cancel, InsertTarget? into = null)
    {
        string[] columns = [.. Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)];
        string[] types = Types(reader);
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
        return new DatabaseRows(columns, [.. rows], more, reader.RecordsAffected) { Types = types, Into = into };
    }

    /// <summary>Each of a reader's columns' types as its database names them, empty where it names none.</summary>
    internal static string[] Types(DbDataReader reader) => [.. Enumerable.Range(0, reader.FieldCount).Select(i =>
    {
        try
        {
            return reader.GetDataTypeName(i);
        }
        catch (Exception unnamed) when (unnamed is InvalidOperationException or NotSupportedException or IndexOutOfRangeException)
        {
            return string.Empty;
        }
    })];

    private static void Add(DbCommand command, string name, object? value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
