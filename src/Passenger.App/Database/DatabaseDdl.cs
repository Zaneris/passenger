using System.Data.Common;
using System.Globalization;
using System.Text;
using Passenger.Core;

namespace Passenger.App.Database;

/// <summary>An object the explorer names: a table, view, routine, trigger, index or column, by its schema and name, with a
/// trigger's, index's or column's table, a routine's kind (function or procedure) and a column's type.</summary>
public sealed record DatabaseObjectRef(DatabaseObjectKind Kind, string Schema, string Name, string Table = "", string Detail = "")
{
    public override string ToString() => Containers.ContainerSecrets.InSaid(Kind is DatabaseObjectKind.Trigger or DatabaseObjectKind.Index or DatabaseObjectKind.Column
        ? $"{Table}.{Name}" : Schema.Length == 0 ? Name : $"{Schema}.{Name}");
}

/// <summary>What a script made for an object does: make it as it stands, change it, or drop it.</summary>
public enum DdlScript
{
    Create,
    Alter,
    Drop,
}

/// <summary>Each object's definition as its database gives it (SQLite's own text, PostgreSQL's pg_get_*def, MySQL's SHOW
/// CREATE, SQL Server's OBJECT_DEFINITION), a table's made from its catalog where the database writes none, with its
/// indexes and triggers; and the scripts that make, change or drop it, written in its database's words, never run here.
/// Its tab's copy is kept in the state's cache (<see cref="Folder"/>), where the editor shows it read-only.</summary>
internal static class DatabaseDdl
{
    private static (string Root, string Folder)? known;

    /// <summary>The folder the definitions shown are copied to, as the file system names it.</summary>
    public static string Folder
    {
        get
        {
            string root = State.StateStore.DefaultRoot;
            if (known is not { } kept || kept.Root != root)
            {
                known = kept = (root, PathNames.Canonical(Path.Combine(root, "cache", "ddl")));
            }
            return kept.Folder;
        }
    }

    /// <summary>Whether a file is a definition's copy, which the editor shows read-only.</summary>
    public static bool IsCopy(string path) => PathNames.IsUnder(path, Folder);

    /// <summary>Where an object's definition is copied for its tab, named by the object.</summary>
    public static string CopyOf(DatabaseSource source, string database, DatabaseObjectRef target) => Path.Combine(Folder,
        FileNames.Safe($"{source.Name}-{source.Account[..8]}"), FileNames.Safe(database), target.Kind.ToString().ToLowerInvariant(),
        FileNames.Safe(target.Kind is DatabaseObjectKind.Trigger or DatabaseObjectKind.Index ? $"{target.Table}.{target.Name}" : target.Schema.Length == 0 ? target.Name : $"{target.Schema}.{target.Name}") + ".sql");

    /// <summary>What ends one statement of a script: a line of GO for SQL Server, whose routines must each stand alone in a batch.</summary>
    private static string End(DatabaseKind kind) => kind == DatabaseKind.SqlServer ? "\nGO\n" : ";\n";

    /// <summary>The words a trigger's definition says of when it fires ("before insert", "instead of update").</summary>
    public static string TriggerWhen(string definition, DatabaseKind kind)
    {
        List<SqlText.Token> tokens = SqlText.Tokens(definition, kind);
        string? timing = null;
        string? events = null;
        for (int i = 0; i < tokens.Count && (timing is null || events is null); i++)
        {
            string word = tokens[i].Quoted ? string.Empty : tokens[i].Word;
            if (timing is null && word is "BEFORE" or "AFTER")
            {
                timing = word.ToLowerInvariant();
            }
            else if (timing is null && word == "INSTEAD")
            {
                timing = "instead of";
            }
            else if (events is null && word is "INSERT" or "UPDATE" or "DELETE")
            {
                events = word.ToLowerInvariant();
            }
        }
        return string.Join(' ', new[] { timing, events }.OfType<string>());
    }

    /// <summary>The script that makes, changes or drops an object, reading of it only what the script needs.</summary>
    internal static async Task<string> ScriptAsync(DatabaseSource source, DatabaseObjectRef target, DdlScript script, CancellationToken cancel)
    {
        DatabaseKind kind = source.Kind;
        string name = Named(kind, target);
        string end = End(kind);
        switch (script)
        {
            case DdlScript.Create:
                return await ReadAsync(source, target, cancel);
            case DdlScript.Drop when target.Kind == DatabaseObjectKind.Routine && kind == DatabaseKind.Postgres:
                return string.Concat((await PostgresRoutinesAsync(source, target, cancel)).Select(r => $"DROP {r.Kind} {name}({r.Arguments}){end}"));
            case DdlScript.Drop:
                return Drop(kind, target, name) + end;
            case DdlScript.Alter when target.Kind == DatabaseObjectKind.Table:
                string column = DatabaseClient.Quote(kind, "new_column");
                return kind switch
                {
                    DatabaseKind.SqlServer => $"ALTER TABLE {name} ADD {column} nvarchar(100) NULL{end}",
                    DatabaseKind.MySql => $"ALTER TABLE {name} ADD COLUMN {column} varchar(100) NULL{end}",
                    DatabaseKind.ClickHouse => $"ALTER TABLE {name} ADD COLUMN {column} Nullable(String){end}",
                    DatabaseKind.DuckDb or DatabaseKind.Snowflake => $"ALTER TABLE {name} ADD COLUMN {column} varchar{end}",
                    _ => $"ALTER TABLE {name} ADD COLUMN {column} text{end}",
                };
            case DdlScript.Alter when target.Kind == DatabaseObjectKind.Column:
                string table = Named(kind, target with { Kind = DatabaseObjectKind.Table, Name = target.Table });
                string quoted = DatabaseClient.Quote(kind, target.Name);
                return kind switch
                {
                    DatabaseKind.Postgres or DatabaseKind.Redshift or DatabaseKind.DuckDb => $"ALTER TABLE {table} ALTER COLUMN {quoted} TYPE {target.Detail}{end}",
                    DatabaseKind.MySql or DatabaseKind.ClickHouse => $"ALTER TABLE {table} MODIFY COLUMN {quoted} {target.Detail}{end}",
                    DatabaseKind.Snowflake => $"ALTER TABLE {table} ALTER COLUMN {quoted} SET DATA TYPE {target.Detail}{end}",
                    DatabaseKind.SqlServer => $"ALTER TABLE {table} ALTER COLUMN {quoted} {target.Detail}{end}",
                    _ => $"ALTER TABLE {table} RENAME COLUMN {quoted} TO {quoted}{end}",
                };
            default:
                string made = (await ReadAsync(source, target, cancel)).TrimEnd();
                return Replacing(kind, target) is { } words && Replaced(made, kind, words) is { } replaced
                    ? replaced + (replaced.TrimEnd().EndsWith(';') && kind != DatabaseKind.SqlServer ? "\n" : end)
                    : Drop(kind, target, name) + end + made + (made.EndsWith(';') && kind != DatabaseKind.SqlServer ? "\n" : end);
        }
    }

    /// <summary>An object's name as its database reads it, qualified by its schema where it has one.</summary>
    private static string Named(DatabaseKind kind, DatabaseObjectRef target) => target.Kind == DatabaseObjectKind.Column
        ? DatabaseClient.Quote(kind, target.Name)
        : (target.Schema.Length > 0 ? DatabaseClient.Quote(kind, target.Schema) + "." : string.Empty) + DatabaseClient.Quote(kind, target.Name);

    private static string Drop(DatabaseKind kind, DatabaseObjectRef target, string name)
    {
        string table = target.Kind is DatabaseObjectKind.Trigger or DatabaseObjectKind.Index or DatabaseObjectKind.Column
            ? Named(kind, target with { Kind = DatabaseObjectKind.Table, Name = target.Table }) : name;
        string own = DatabaseClient.Quote(kind, target.Name);
        return target.Kind switch
        {
            DatabaseObjectKind.Table => $"DROP TABLE {name}",
            DatabaseObjectKind.View => $"DROP VIEW {name}",
            DatabaseObjectKind.Routine => $"DROP {(target.Detail.Contains("procedure", StringComparison.OrdinalIgnoreCase) ? "PROCEDURE" : "FUNCTION")} {name}",
            DatabaseObjectKind.Trigger => kind == DatabaseKind.Postgres ? $"DROP TRIGGER {own} ON {table}" : kind == DatabaseKind.Sqlite ? $"DROP TRIGGER {own}" : $"DROP TRIGGER {name}",
            DatabaseObjectKind.Index => kind switch
            {
                DatabaseKind.Postgres or DatabaseKind.DuckDb => $"DROP INDEX {name}",
                DatabaseKind.Sqlite => $"DROP INDEX {own}",
                DatabaseKind.ClickHouse => $"ALTER TABLE {table} DROP INDEX {own}",
                _ => $"DROP INDEX {own} ON {table}",
            },
            _ => $"ALTER TABLE {table} DROP COLUMN {own}",
        };
    }

    /// <summary>The words a definition's CREATE takes to replace what stands (CREATE OR REPLACE, SQL Server's CREATE OR
    /// ALTER); null where the database has none for the object, which is dropped and made again.</summary>
    private static string? Replacing(DatabaseKind kind, DatabaseObjectRef target) => (kind, target.Kind) switch
    {
        (DatabaseKind.SqlServer, DatabaseObjectKind.View or DatabaseObjectKind.Routine or DatabaseObjectKind.Trigger) => "OR ALTER",
        (DatabaseKind.Postgres, DatabaseObjectKind.View or DatabaseObjectKind.Trigger) => "OR REPLACE",
        (DatabaseKind.Postgres, DatabaseObjectKind.Routine) => string.Empty,
        (DatabaseKind.MySql or DatabaseKind.DuckDb or DatabaseKind.ClickHouse or DatabaseKind.Snowflake or DatabaseKind.Redshift, DatabaseObjectKind.View) => "OR REPLACE",
        (DatabaseKind.DuckDb or DatabaseKind.ClickHouse or DatabaseKind.Snowflake, DatabaseObjectKind.Routine) => "OR REPLACE",
        _ => null,
    };

    /// <summary>A definition with <paramref name="words"/> after its first CREATE, where none stands there already; null
    /// where it has no CREATE.</summary>
    private static string? Replaced(string made, DatabaseKind kind, string words)
    {
        List<SqlText.Token> tokens = SqlText.Tokens(made, kind);
        int create = tokens.FindIndex(t => t is { Word: "CREATE", Quoted: false });
        if (create < 0)
        {
            return null;
        }
        return words.Length == 0 || create + 1 < tokens.Count && tokens[create + 1].Word == "OR" ? made
            : made[..tokens[create].End] + " " + words + made[tokens[create].End..];
    }

    /// <summary>An object's definition as its database gives it, a table's with its indexes and triggers after it, each
    /// statement ended as a script's are; a column's is its table's.</summary>
    internal static async Task<string> ReadAsync(DatabaseSource source, DatabaseObjectRef target, CancellationToken cancel)
    {
        if (target.Kind == DatabaseObjectKind.Column)
        {
            target = target with { Kind = DatabaseObjectKind.Table, Name = target.Table, Table = string.Empty };
        }
        await using DbConnection connection = await DatabaseClient.OpenAsync(source, cancel);
        List<string> parts = source.Kind switch
        {
            DatabaseKind.Sqlite => await SqliteAsync(connection, target, cancel),
            DatabaseKind.Postgres => await PostgresAsync(connection, target, cancel),
            DatabaseKind.MySql => await MySqlAsync(connection, target, cancel),
            DatabaseKind.SqlServer => await SqlServerAsync(connection, target, cancel),
            DatabaseKind.DuckDb => await DuckDbAsync(connection, target, cancel),
            DatabaseKind.ClickHouse => await ClickHouseAsync(connection, target, cancel),
            DatabaseKind.Redshift => await RedshiftAsync(connection, target, cancel),
            _ => await SnowflakeAsync(connection, target, cancel),
        };
        if (parts.Count == 0)
        {
            throw new InvalidOperationException(Refusals.NoDefinition(target.ToString()));
        }
        string end = End(source.Kind);
        StringBuilder text = new();
        foreach (string part in parts)
        {
            string trimmed = part.Trim();
            text.Append(trimmed);
            text.Append(source.Kind != DatabaseKind.SqlServer && trimmed.EndsWith(';') ? "\n" : end);
            text.Append('\n');
        }
        return text.ToString().TrimEnd() + "\n";
    }

    private static async Task<List<string>> SqliteAsync(DbConnection connection, DatabaseObjectRef target, CancellationToken cancel)
    {
        string type = target.Kind switch
        {
            DatabaseObjectKind.Table => "table",
            DatabaseObjectKind.View => "view",
            DatabaseObjectKind.Trigger => "trigger",
            DatabaseObjectKind.Index => "index",
            _ => throw new InvalidOperationException(Refusals.NoDefinition(target.ToString())),
        };
        List<string> parts = Texts(await Read(connection, "SELECT sql FROM sqlite_schema WHERE type=@type AND name=@name AND sql IS NOT NULL",
            [("@type", type), ("@name", target.Name)], cancel));
        if (target.Kind == DatabaseObjectKind.Table && parts.Count > 0)
        {
            parts.AddRange(Texts(await DatabaseClient.ReadAsync(connection,
                "SELECT sql FROM sqlite_schema WHERE tbl_name=@name AND type IN ('index','trigger') AND sql IS NOT NULL ORDER BY type, name", [("@name", target.Name)], cancel)));
        }
        return parts;
    }

    /// <summary>DuckDB's own text of a table (with its indexes after it), a view, an index or a macro; a folder's file has none.</summary>
    private static async Task<List<string>> DuckDbAsync(DbConnection connection, DatabaseObjectRef target, CancellationToken cancel)
    {
        (string, object?)[] named = [("@schema", target.Schema), ("@name", target.Name)];
        const string Here = "database_name=current_database() AND schema_name=@schema";
        List<string> parts = target.Kind switch
        {
            DatabaseObjectKind.Table => Texts(await Read(connection, $"SELECT sql FROM duckdb_tables() WHERE {Here} AND table_name=@name", named, cancel)),
            DatabaseObjectKind.View => Texts(await Read(connection, $"SELECT sql FROM duckdb_views() WHERE {Here} AND view_name=@name", named, cancel)),
            DatabaseObjectKind.Index => Texts(await Read(connection, $"SELECT sql FROM duckdb_indexes() WHERE {Here} AND index_name=@name", named, cancel)),
            DatabaseObjectKind.Routine => [.. (await Read(connection, $"SELECT parameters, macro_definition FROM duckdb_functions() WHERE {Here} AND function_name=@name AND NOT internal", named, cancel))
                .Rows.Select(r => $"CREATE MACRO {Named(DatabaseKind.DuckDb, target)}({string.Join(", ", r[0] as System.Collections.IEnumerable is { } list ? list.Cast<object>() : [])}) AS {Text(r[1])}")],
            _ => [],
        };
        if (target.Kind == DatabaseObjectKind.Table && parts.Count > 0)
        {
            parts.AddRange(Texts(await Read(connection, $"SELECT sql FROM duckdb_indexes() WHERE {Here} AND table_name=@name AND sql IS NOT NULL ORDER BY index_name", named, cancel)));
        }
        return parts;
    }

    /// <summary>ClickHouse's own text of a table or view (SHOW CREATE TABLE), or a function of SQL.</summary>
    private static async Task<List<string>> ClickHouseAsync(DbConnection connection, DatabaseObjectRef target, CancellationToken cancel) => target.Kind switch
    {
        DatabaseObjectKind.Table or DatabaseObjectKind.View => Texts(await Read(connection, $"SHOW CREATE TABLE {Named(DatabaseKind.ClickHouse, target)}", [], cancel)),
        DatabaseObjectKind.Routine => Texts(await Read(connection, $"SHOW CREATE FUNCTION {DatabaseClient.Quote(DatabaseKind.ClickHouse, target.Name)}", [], cancel)),
        _ => [],
    };

    /// <summary>Snowflake's own text of a table or view (GET_DDL).</summary>
    private static async Task<List<string>> SnowflakeAsync(DbConnection connection, DatabaseObjectRef target, CancellationToken cancel) => target.Kind switch
    {
        DatabaseObjectKind.Table or DatabaseObjectKind.View => Texts(await Read(connection, "SELECT GET_DDL(@type, @full)",
            [("@type", target.Kind == DatabaseObjectKind.View ? "VIEW" : "TABLE"), ("@full", Named(DatabaseKind.Snowflake, target))], cancel)),
        _ => [],
    };

    /// <summary>A Redshift table's or view's definition as Redshift writes it (SHOW TABLE, SHOW VIEW), its distribution and
    /// sort keys with it; nothing else of Redshift's.</summary>
    private static async Task<List<string>> RedshiftAsync(DbConnection connection, DatabaseObjectRef target, CancellationToken cancel) => target.Kind switch
    {
        DatabaseObjectKind.Table or DatabaseObjectKind.View => Texts(await Read(connection,
            $"SHOW {(target.Kind == DatabaseObjectKind.View ? "VIEW" : "TABLE")} {Named(DatabaseKind.Redshift, target)}", [], cancel)),
        _ => [],
    };

    private static async Task<List<string>> PostgresAsync(DbConnection connection, DatabaseObjectRef target, CancellationToken cancel)
    {
        (string, object?)[] named = [("@schema", target.Schema), ("@name", target.Name), ("@table", target.Table)];
        const string Relation = "FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@name";
        switch (target.Kind)
        {
            case DatabaseObjectKind.View:
                DatabaseRows view = await Read(connection, $"SELECT c.relkind::text, pg_get_viewdef(c.oid, true) {Relation}", named, cancel);
                return [.. view.Rows.Select(r => $"CREATE {((string?)r[0] == "m" ? "MATERIALIZED VIEW" : "VIEW")} {Named(DatabaseKind.Postgres, target)} AS\n{Text(r[1]).TrimEnd()}")];
            case DatabaseObjectKind.Routine:
                return Texts(await DatabaseClient.ReadAsync(connection,
                    "SELECT pg_get_functiondef(p.oid) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname=@schema AND p.proname=@name AND p.prokind<>'a' ORDER BY p.oid",
                    named, cancel));
            case DatabaseObjectKind.Trigger:
                return Texts(await Read(connection, "SELECT pg_get_triggerdef(t.oid, true) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid "
                    + "JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@table AND t.tgname=@name", named, cancel));
            case DatabaseObjectKind.Index:
                return Texts(await Read(connection, "SELECT indexdef FROM pg_indexes WHERE schemaname=@schema AND indexname=@name", named, cancel));
        }
        DatabaseRows columns = await Read(connection, "SELECT a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull, pg_get_expr(d.adbin, d.adrelid), "
            + "a.attidentity::text, a.attgenerated::text FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace "
            + "LEFT JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum WHERE n.nspname=@schema AND c.relname=@name AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum",
            named, cancel);
        if (columns.RowCount == 0)
        {
            return [];
        }
        List<string> lines = [.. columns.Rows.Select(r =>
        {
            string line = $"{DatabaseClient.Quote(DatabaseKind.Postgres, Text(r[0]))} {Text(r[1])}";
            line += Text(r[5]) == "s" ? $" GENERATED ALWAYS AS ({Text(r[3])}) STORED"
                : Text(r[4]) is "a" ? " GENERATED ALWAYS AS IDENTITY"
                : Text(r[4]) is "d" ? " GENERATED BY DEFAULT AS IDENTITY"
                : r[3] is not null ? $" DEFAULT {Text(r[3])}" : string.Empty;
            return line + (r[2] is true ? " NOT NULL" : string.Empty);
        })];
        // NOT NULL is the column's own, though PostgreSQL 18 keeps it among the constraints too.
        lines.AddRange((await Read(connection, "SELECT k.conname, pg_get_constraintdef(k.oid, true) FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid "
            + "JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@name AND k.contype::text<>'n' ORDER BY k.contype::text<>'p', k.conname", named, cancel))
            .Rows.Select(r => $"CONSTRAINT {DatabaseClient.Quote(DatabaseKind.Postgres, Text(r[0]))} {Text(r[1])}"));
        List<string> parts = [$"CREATE TABLE {Named(DatabaseKind.Postgres, target)} (\n    {string.Join(",\n    ", lines)}\n)"];
        parts.AddRange(Texts(await Read(connection, "SELECT pg_get_indexdef(i.indexrelid) FROM pg_index i JOIN pg_class c ON c.oid=i.indrelid "
            + "JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@name AND NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid=i.indexrelid) "
            + "ORDER BY 1", named, cancel)));
        parts.AddRange(Texts(await Read(connection, "SELECT pg_get_triggerdef(t.oid, true) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid "
            + "JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@name AND NOT t.tgisinternal ORDER BY t.tgname", named, cancel)));
        return parts;
    }

    /// <summary>A PostgreSQL routine's overloads, each a function or a procedure with the arguments that name it.</summary>
    private static async Task<List<(string Kind, string Arguments)>> PostgresRoutinesAsync(DatabaseSource source, DatabaseObjectRef target, CancellationToken cancel)
    {
        await using DbConnection connection = await DatabaseClient.OpenAsync(source, cancel);
        DatabaseRows rows = await Read(connection, "SELECT p.prokind::text, pg_get_function_identity_arguments(p.oid) FROM pg_proc p "
            + "JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname=@schema AND p.proname=@name ORDER BY p.oid", [("@schema", target.Schema), ("@name", target.Name)], cancel);
        return [.. rows.Rows.Select(r => (Text(r[0]) switch { "p" => "PROCEDURE", "a" => "AGGREGATE", _ => "FUNCTION" }, Text(r[1])))];
    }

    private static async Task<List<string>> MySqlAsync(DbConnection connection, DatabaseObjectRef target, CancellationToken cancel)
    {
        string name = Named(DatabaseKind.MySql, target);
        (string Sql, string Column) shown = target.Kind switch
        {
            DatabaseObjectKind.Table => ($"SHOW CREATE TABLE {name}", "Create Table"),
            DatabaseObjectKind.View => ($"SHOW CREATE VIEW {name}", "Create View"),
            DatabaseObjectKind.Trigger => ($"SHOW CREATE TRIGGER {name}", "SQL Original Statement"),
            DatabaseObjectKind.Routine when target.Detail.Contains("procedure", StringComparison.OrdinalIgnoreCase) => ($"SHOW CREATE PROCEDURE {name}", "Create Procedure"),
            DatabaseObjectKind.Routine => ($"SHOW CREATE FUNCTION {name}", "Create Function"),
            _ => (string.Empty, string.Empty),
        };
        if (target.Kind == DatabaseObjectKind.Index)
        {
            DatabaseRows columns = await Read(connection, "SELECT column_name, non_unique FROM information_schema.statistics "
                + "WHERE table_schema=@schema AND table_name=@table AND index_name=@name ORDER BY seq_in_index", [("@schema", target.Schema), ("@table", target.Table), ("@name", target.Name)], cancel);
            return columns.RowCount == 0 ? [] : [$"CREATE {(Convert.ToInt64(columns.Rows[0][1], CultureInfo.InvariantCulture) == 0 ? "UNIQUE " : string.Empty)}INDEX "
                + $"{DatabaseClient.Quote(DatabaseKind.MySql, target.Name)} ON {Named(DatabaseKind.MySql, target with { Kind = DatabaseObjectKind.Table, Name = target.Table })} "
                + $"({string.Join(", ", columns.Rows.Select(r => DatabaseClient.Quote(DatabaseKind.MySql, Text(r[0]))))})"];
        }
        List<string> parts = [.. Column(await Read(connection, shown.Sql, [], cancel), shown.Column)];
        if (target.Kind == DatabaseObjectKind.Table && parts.Count > 0)
        {
            DatabaseRows triggers = await Read(connection, "SELECT trigger_name FROM information_schema.triggers WHERE event_object_schema=@schema "
                + "AND event_object_table=@name ORDER BY action_order", [("@schema", target.Schema), ("@name", target.Name)], cancel);
            foreach (object?[] trigger in triggers.Rows)
            {
                parts.AddRange(Column(await Read(connection, $"SHOW CREATE TRIGGER {Named(DatabaseKind.MySql, target with { Name = Text(trigger[0]) })}", [], cancel),
                    "SQL Original Statement"));
            }
        }
        return parts;
    }

    private static async Task<List<string>> SqlServerAsync(DbConnection connection, DatabaseObjectRef target, CancellationToken cancel)
    {
        string table = target.Kind is DatabaseObjectKind.Trigger or DatabaseObjectKind.Index ? target.Table : target.Name;
        (string, object?)[] named = [("@full", $"{DatabaseClient.Quote(DatabaseKind.SqlServer, target.Schema)}.{DatabaseClient.Quote(DatabaseKind.SqlServer, table)}"),
            ("@name", target.Name), ("@own", $"{DatabaseClient.Quote(DatabaseKind.SqlServer, target.Schema)}.{DatabaseClient.Quote(DatabaseKind.SqlServer, target.Name)}")];
        if (target.Kind is DatabaseObjectKind.View or DatabaseObjectKind.Routine or DatabaseObjectKind.Trigger)
        {
            return Texts(await Read(connection, "SELECT OBJECT_DEFINITION(OBJECT_ID(@own))", named, cancel));
        }
        const string IndexSql = "SELECT i.name, i.is_unique, i.type_desc, "
            + "STRING_AGG(CASE WHEN ic.is_included_column=0 THEN QUOTENAME(c.name) + CASE WHEN ic.is_descending_key=1 THEN ' DESC' ELSE '' END END, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal), "
            + "STRING_AGG(CASE WHEN ic.is_included_column=1 THEN QUOTENAME(c.name) END, ', ') "
            + "FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id "
            + "WHERE i.object_id=OBJECT_ID(@full) AND i.is_primary_key=0 AND i.is_unique_constraint=0 AND i.type>0";
        string full = Named(DatabaseKind.SqlServer, target with { Kind = DatabaseObjectKind.Table, Name = table });
        List<string> indexes = [.. (await Read(connection, IndexSql + (target.Kind == DatabaseObjectKind.Index ? " AND i.name=@name" : string.Empty)
            + " GROUP BY i.name, i.is_unique, i.type_desc ORDER BY i.name", named, cancel)).Rows.Select(r =>
            $"CREATE {(r[1] is true ? "UNIQUE " : string.Empty)}{Text(r[2])} INDEX {DatabaseClient.Quote(DatabaseKind.SqlServer, Text(r[0]))} ON {full} ({Text(r[3])})"
            + (r[4] is null ? string.Empty : $" INCLUDE ({Text(r[4])})"))];
        if (target.Kind == DatabaseObjectKind.Index)
        {
            return indexes;
        }
        DatabaseRows columns = await Read(connection, "SELECT c.name, TYPE_NAME(c.user_type_id), c.max_length, c.precision, c.scale, c.is_nullable, "
            + "c.is_identity, CAST(ic.seed_value AS bigint), CAST(ic.increment_value AS bigint), cc.definition, cc.is_persisted, dc.name, dc.definition "
            + "FROM sys.columns c LEFT JOIN sys.identity_columns ic ON ic.object_id=c.object_id AND ic.column_id=c.column_id "
            + "LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id "
            + "LEFT JOIN sys.default_constraints dc ON dc.parent_object_id=c.object_id AND dc.parent_column_id=c.column_id "
            + "WHERE c.object_id=OBJECT_ID(@full) ORDER BY c.column_id", named, cancel);
        if (columns.RowCount == 0)
        {
            return [];
        }
        List<string> lines = [.. columns.Rows.Select(r =>
        {
            string column = DatabaseClient.Quote(DatabaseKind.SqlServer, Text(r[0]));
            if (r[9] is not null)
            {
                return $"{column} AS {Text(r[9])}{(r[10] is true ? " PERSISTED" : string.Empty)}";
            }
            string line = $"{column} {SqlServerType(Text(r[1]), Convert.ToInt32(r[2], CultureInfo.InvariantCulture), Convert.ToInt32(r[3], CultureInfo.InvariantCulture), Convert.ToInt32(r[4], CultureInfo.InvariantCulture))}";
            line += r[6] is true ? $" IDENTITY({Text(r[7])}, {Text(r[8])})" : string.Empty;
            line += r[5] is true ? " NULL" : " NOT NULL";
            return line + (r[11] is null ? string.Empty : $" CONSTRAINT {DatabaseClient.Quote(DatabaseKind.SqlServer, Text(r[11]))} DEFAULT {Text(r[12])}");
        })];
        lines.AddRange((await Read(connection, "SELECT k.name, k.type, i.type_desc, STRING_AGG(QUOTENAME(c.name) + CASE WHEN ic.is_descending_key=1 THEN ' DESC' ELSE '' END, ', ') "
            + "WITHIN GROUP (ORDER BY ic.key_ordinal) FROM sys.key_constraints k JOIN sys.indexes i ON i.object_id=k.parent_object_id AND i.index_id=k.unique_index_id "
            + "JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id "
            + "WHERE k.parent_object_id=OBJECT_ID(@full) GROUP BY k.name, k.type, i.type_desc ORDER BY k.type, k.name", named, cancel)).Rows.Select(r =>
            $"CONSTRAINT {DatabaseClient.Quote(DatabaseKind.SqlServer, Text(r[0]))} {(Text(r[1]).Trim() == "PK" ? "PRIMARY KEY" : "UNIQUE")} {Text(r[2])} ({Text(r[3])})"));
        lines.AddRange((await Read(connection, "SELECT f.name, STRING_AGG(QUOTENAME(pc.name), ', ') WITHIN GROUP (ORDER BY fc.constraint_column_id), "
            + "QUOTENAME(SCHEMA_NAME(rt.schema_id)) + '.' + QUOTENAME(rt.name), STRING_AGG(QUOTENAME(rc.name), ', ') WITHIN GROUP (ORDER BY fc.constraint_column_id), "
            + "f.delete_referential_action_desc, f.update_referential_action_desc FROM sys.foreign_keys f JOIN sys.foreign_key_columns fc ON fc.constraint_object_id=f.object_id "
            + "JOIN sys.columns pc ON pc.object_id=fc.parent_object_id AND pc.column_id=fc.parent_column_id JOIN sys.tables rt ON rt.object_id=f.referenced_object_id "
            + "JOIN sys.columns rc ON rc.object_id=fc.referenced_object_id AND rc.column_id=fc.referenced_column_id WHERE f.parent_object_id=OBJECT_ID(@full) "
            + "GROUP BY f.name, rt.schema_id, rt.name, f.delete_referential_action_desc, f.update_referential_action_desc ORDER BY f.name", named, cancel)).Rows.Select(r =>
            $"CONSTRAINT {DatabaseClient.Quote(DatabaseKind.SqlServer, Text(r[0]))} FOREIGN KEY ({Text(r[1])}) REFERENCES {Text(r[2])} ({Text(r[3])})"
            + (Text(r[4]) == "NO_ACTION" ? string.Empty : $" ON DELETE {Text(r[4]).Replace('_', ' ')}")
            + (Text(r[5]) == "NO_ACTION" ? string.Empty : $" ON UPDATE {Text(r[5]).Replace('_', ' ')}")));
        lines.AddRange((await Read(connection, "SELECT name, definition FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(@full) ORDER BY name", named, cancel))
            .Rows.Select(r => $"CONSTRAINT {DatabaseClient.Quote(DatabaseKind.SqlServer, Text(r[0]))} CHECK {Text(r[1])}"));
        List<string> parts = [$"CREATE TABLE {full} (\n    {string.Join(",\n    ", lines)}\n)", .. indexes];
        parts.AddRange(Texts(await Read(connection, "SELECT OBJECT_DEFINITION(object_id) FROM sys.triggers WHERE parent_id=OBJECT_ID(@full) ORDER BY name", named, cancel)));
        return parts;
    }

    /// <summary>A SQL Server column's type as its definition writes it, with its length, precision or scale.</summary>
    private static string SqlServerType(string type, int length, int precision, int scale) => type switch
    {
        "varchar" or "char" or "varbinary" or "binary" => $"{type}({(length < 0 ? "max" : length.ToString(CultureInfo.InvariantCulture))})",
        "nvarchar" or "nchar" => $"{type}({(length < 0 ? "max" : (length / 2).ToString(CultureInfo.InvariantCulture))})",
        "decimal" or "numeric" => $"{type}({precision.ToString(CultureInfo.InvariantCulture)}, {scale.ToString(CultureInfo.InvariantCulture)})",
        "datetime2" or "time" or "datetimeoffset" => $"{type}({scale.ToString(CultureInfo.InvariantCulture)})",
        _ => type,
    };

    /// <summary>A read of the catalog given only the parameters its SQL names, as a driver may refuse one it does not.</summary>
    private static Task<DatabaseRows> Read(DbConnection connection, string sql, (string Name, object? Value)[] parameters, CancellationToken cancel) =>
        DatabaseClient.ReadAsync(connection, sql, [.. parameters.Where(p => sql.Contains(p.Name, StringComparison.Ordinal))], cancel);

    private static List<string> Texts(DatabaseRows rows) => [.. rows.Rows.Select(r => r[0]).OfType<object>().Select(Text).Where(t => t.Trim().Length > 0)];

    /// <summary>A SHOW CREATE result's definition, read by its column's name.</summary>
    private static IEnumerable<string> Column(DatabaseRows rows, string column) =>
        Array.FindIndex(rows.Columns, c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase)) is >= 0 and int at
            ? rows.Rows.Select(r => Text(r[at])).Where(t => t.Length > 0) : [];

    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
