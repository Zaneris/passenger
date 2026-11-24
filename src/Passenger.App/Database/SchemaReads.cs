using System.Data.Common;
using System.Globalization;

namespace Passenger.App.Database;

/// <summary>A schema's shape read from its server's own catalog (PostgreSQL's pg_catalog, MySQL's information_schema and
/// SHOW CREATE, SQL Server's sys views, SQLite's schema table and pragmas): every table's columns with their full types,
/// NULL and defaults, its primary key, its foreign keys and the indexes no constraint makes, and every view's, trigger's
/// and routine's definition and sequence's settings (an extension's routines and a column's own sequence left out).</summary>
internal static class SchemaReads
{
    public static async Task<SchemaShape> ReadAsync(DatabaseSource source, string schema, CancellationToken cancel)
    {
        await using DbConnection connection = await DatabaseClient.OpenAsync(source, cancel);
        (string, object?)[] named = [("@schema", schema)];
        DatabaseKind kind = source.Kind;
        (string Columns, string Keys, string Indexes, string Views) sql = kind switch
        {
            DatabaseKind.Postgres => (
                "SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull, pg_get_expr(d.adbin, d.adrelid) FROM pg_class c "
                    + "JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped "
                    + "LEFT JOIN pg_attrdef d ON d.adrelid=c.oid AND d.adnum=a.attnum WHERE n.nspname=@schema AND c.relkind IN ('r','p') ORDER BY c.relname, a.attnum",
                "SELECT c.relname, con.conname, CAST(con.contype AS text), "
                    + "array_to_string(ARRAY(SELECT a.attname FROM unnest(con.conkey) WITH ORDINALITY k(n, o) JOIN pg_attribute a ON a.attrelid=con.conrelid AND a.attnum=k.n ORDER BY k.o), ','), "
                    + "r.relname, array_to_string(ARRAY(SELECT a.attname FROM unnest(con.confkey) WITH ORDINALITY k(n, o) JOIN pg_attribute a ON a.attrelid=con.confrelid AND a.attnum=k.n ORDER BY k.o), ',') "
                    + "FROM pg_constraint con JOIN pg_class c ON c.oid=con.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace LEFT JOIN pg_class r ON r.oid=con.confrelid "
                    + "WHERE n.nspname=@schema AND con.contype IN ('p','f') ORDER BY 1, 2",
                "SELECT t.relname, i.relname, ix.indisunique, array_to_string(ARRAY(SELECT a.attname FROM unnest(ix.indkey) WITH ORDINALITY k(n, o) "
                    + "JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=k.n ORDER BY k.o), ',') FROM pg_index ix JOIN pg_class i ON i.oid=ix.indexrelid "
                    + "JOIN pg_class t ON t.oid=ix.indrelid JOIN pg_namespace n ON n.oid=t.relnamespace "
                    + "WHERE n.nspname=@schema AND NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid=ix.indexrelid) ORDER BY 1, 2",
                "SELECT c.relname, pg_get_viewdef(c.oid, true) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relkind='v' ORDER BY 1"),
            DatabaseKind.MySql => (
                "SELECT c.TABLE_NAME, c.COLUMN_NAME, c.COLUMN_TYPE, c.IS_NULLABLE='YES', c.COLUMN_DEFAULT FROM information_schema.COLUMNS c "
                    + "JOIN information_schema.TABLES t ON t.TABLE_SCHEMA=c.TABLE_SCHEMA AND t.TABLE_NAME=c.TABLE_NAME AND t.TABLE_TYPE='BASE TABLE' "
                    + "WHERE c.TABLE_SCHEMA=@schema ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION",
                "SELECT k.TABLE_NAME, k.CONSTRAINT_NAME, CASE t.CONSTRAINT_TYPE WHEN 'PRIMARY KEY' THEN 'p' ELSE 'f' END, "
                    + "GROUP_CONCAT(k.COLUMN_NAME ORDER BY k.ORDINAL_POSITION), MAX(k.REFERENCED_TABLE_NAME), GROUP_CONCAT(k.REFERENCED_COLUMN_NAME ORDER BY k.ORDINAL_POSITION) "
                    + "FROM information_schema.KEY_COLUMN_USAGE k JOIN information_schema.TABLE_CONSTRAINTS t ON t.CONSTRAINT_SCHEMA=k.CONSTRAINT_SCHEMA "
                    + "AND t.TABLE_NAME=k.TABLE_NAME AND t.CONSTRAINT_NAME=k.CONSTRAINT_NAME WHERE k.TABLE_SCHEMA=@schema AND t.CONSTRAINT_TYPE IN ('PRIMARY KEY','FOREIGN KEY') "
                    + "GROUP BY k.TABLE_NAME, k.CONSTRAINT_NAME, t.CONSTRAINT_TYPE ORDER BY 1, 2",
                "SELECT s.TABLE_NAME, s.INDEX_NAME, MAX(s.NON_UNIQUE)=0, GROUP_CONCAT(s.COLUMN_NAME ORDER BY s.SEQ_IN_INDEX) FROM information_schema.STATISTICS s "
                    + "WHERE s.TABLE_SCHEMA=@schema AND s.INDEX_NAME<>'PRIMARY' AND NOT EXISTS (SELECT 1 FROM information_schema.TABLE_CONSTRAINTS t "
                    + "WHERE t.CONSTRAINT_SCHEMA=s.TABLE_SCHEMA AND t.TABLE_NAME=s.TABLE_NAME AND t.CONSTRAINT_NAME=s.INDEX_NAME) GROUP BY s.TABLE_NAME, s.INDEX_NAME ORDER BY 1, 2",
                "SELECT TABLE_NAME, VIEW_DEFINITION FROM information_schema.VIEWS WHERE TABLE_SCHEMA=@schema ORDER BY 1"),
            DatabaseKind.SqlServer => (
                "SELECT t.name, c.name, ty.name + CASE WHEN ty.name IN ('varchar','char','varbinary','binary') THEN '(' + CASE WHEN c.max_length=-1 THEN 'max' "
                    + "ELSE CAST(c.max_length AS varchar(10)) END + ')' WHEN ty.name IN ('nvarchar','nchar') THEN '(' + CASE WHEN c.max_length=-1 THEN 'max' "
                    + "ELSE CAST(c.max_length/2 AS varchar(10)) END + ')' WHEN ty.name IN ('decimal','numeric') THEN '(' + CAST(c.precision AS varchar(10)) + ',' "
                    + "+ CAST(c.scale AS varchar(10)) + ')' WHEN ty.name IN ('datetime2','time','datetimeoffset') THEN '(' + CAST(c.scale AS varchar(10)) + ')' ELSE '' END, "
                    + "c.is_nullable, dc.definition, dc.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.columns c ON c.object_id=t.object_id "
                    + "JOIN sys.types ty ON ty.user_type_id=c.user_type_id LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id "
                    + "WHERE s.name=@schema ORDER BY t.name, c.column_id",
                "SELECT t.name, kc.name, 'p', STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal), NULL, NULL FROM sys.key_constraints kc "
                    + "JOIN sys.tables t ON t.object_id=kc.parent_object_id JOIN sys.schemas s ON s.schema_id=t.schema_id "
                    + "JOIN sys.index_columns ic ON ic.object_id=kc.parent_object_id AND ic.index_id=kc.unique_index_id "
                    + "JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE kc.type='PK' AND s.name=@schema GROUP BY t.name, kc.name "
                    + "UNION ALL SELECT t.name, fk.name, 'f', STRING_AGG(pc.name, ',') WITHIN GROUP (ORDER BY fkc.constraint_column_id), MAX(rt.name), "
                    + "STRING_AGG(rc.name, ',') WITHIN GROUP (ORDER BY fkc.constraint_column_id) FROM sys.foreign_keys fk JOIN sys.tables t ON t.object_id=fk.parent_object_id "
                    + "JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id "
                    + "JOIN sys.columns pc ON pc.object_id=fkc.parent_object_id AND pc.column_id=fkc.parent_column_id JOIN sys.tables rt ON rt.object_id=fkc.referenced_object_id "
                    + "JOIN sys.columns rc ON rc.object_id=fkc.referenced_object_id AND rc.column_id=fkc.referenced_column_id WHERE s.name=@schema GROUP BY t.name, fk.name ORDER BY 1, 2",
                "SELECT t.name, i.name, i.is_unique, STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) FROM sys.indexes i "
                    + "JOIN sys.tables t ON t.object_id=i.object_id JOIN sys.schemas s ON s.schema_id=t.schema_id "
                    + "JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.is_included_column=0 "
                    + "JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id "
                    + "WHERE s.name=@schema AND i.is_primary_key=0 AND i.is_unique_constraint=0 AND i.type>0 GROUP BY t.name, i.name, i.is_unique ORDER BY 1, 2",
                "SELECT v.name, m.definition FROM sys.views v JOIN sys.schemas s ON s.schema_id=v.schema_id JOIN sys.sql_modules m ON m.object_id=v.object_id "
                    + "WHERE s.name=@schema ORDER BY 1"),
            _ => (
                "SELECT m.name, p.name, p.type, p.\"notnull\" = 0 AND p.pk = 0, p.dflt_value FROM sqlite_schema m JOIN pragma_table_info(m.name) p "
                    + "WHERE m.type='table' AND m.name NOT LIKE 'sqlite_%' ORDER BY m.name, p.cid",
                string.Empty,
                "SELECT m.name, l.name, l.\"unique\", (SELECT group_concat(i.name, ',') FROM (SELECT name FROM pragma_index_info(l.name) ORDER BY seqno) i) "
                    + "FROM sqlite_schema m JOIN pragma_index_list(m.name) l WHERE m.type='table' AND l.origin='c' ORDER BY 1, 2",
                "SELECT name, sql FROM sqlite_schema WHERE type='view' ORDER BY 1"),
        };
        DatabaseRows columnRows = await DatabaseClient.ReadAsync(connection, sql.Columns, named, cancel, limit: 100_000);
        List<object?[]> keyRows = kind == DatabaseKind.Sqlite ? await SqliteKeysAsync(connection, cancel) : [.. (await DatabaseClient.ReadAsync(connection, sql.Keys, named, cancel, limit: 100_000)).Rows];
        DatabaseRows indexRows = await DatabaseClient.ReadAsync(connection, sql.Indexes, named, cancel, limit: 100_000);
        DatabaseRows viewRows = await DatabaseClient.ReadAsync(connection, sql.Views, named, cancel, limit: 10_000);
        List<TableShape> tables = [];
        foreach (IGrouping<string, object?[]> table in columnRows.Rows.GroupBy(r => Text(r[0])))
        {
            List<ColumnShape> columns = [.. table.Select(r => new ColumnShape(Text(r[1]), Text(r[2]), Truth(r[3]), Default(kind, r[4])))];
            IEnumerable<object?[]> keys = keyRows.Where(k => Text(k[0]) == table.Key);
            tables.Add(new TableShape(table.Key, columns,
                keys.Where(k => Text(k[2]) == "p").Select(k => Listed(k[3])).FirstOrDefault() ?? [],
                [.. keys.Where(k => Text(k[2]) == "f").Select(k => new ForeignKeyShape(Text(k[1]), Listed(k[3]), Text(k[4]), Listed(k[5])))],
                [.. indexRows.Rows.Where(i => Text(i[0]) == table.Key).Select(i => new IndexShape(Text(i[1]), Truth(i[2]), Listed(i[3])))])
            {
                PrimaryKeyName = keys.Where(k => Text(k[2]) == "p").Select(k => Text(k[1])).FirstOrDefault(),
                DefaultNames = table.Where(r => r.Length > 5 && r[5] is not (null or DBNull)).ToDictionary(r => Text(r[1]), r => Text(r[5])),
            });
        }
        string qualifier = kind == DatabaseKind.MySql ? SqlText.QuotedName(kind, schema) + "." : string.Empty;
        List<ViewShape> views = [.. viewRows.Rows.Select(r => new ViewShape(Text(r[0]),
            qualifier.Length > 0 ? Text(r[1]).Replace(qualifier, string.Empty, StringComparison.Ordinal) : Text(r[1])))];
        return new SchemaShape(kind, kind == DatabaseKind.Sqlite ? string.Empty : schema, tables, views)
        {
            Triggers = [.. (await ReadAsync(connection, Triggers(kind), named, cancel)).Select(r => new TriggerShape(Text(r[0]), Text(r[1]), Text(r[2])))],
            Routines = await RoutinesAsync(connection, kind, schema, named, cancel),
            Sequences = await SequencesAsync(connection, kind, schema, named, cancel),
        };
    }

    private static string Triggers(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Postgres => "SELECT t.tgname, c.relname, pg_get_triggerdef(t.oid) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid "
            + "JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND NOT t.tgisinternal ORDER BY 2, 1",
        DatabaseKind.MySql => "SELECT TRIGGER_NAME, EVENT_OBJECT_TABLE, CONCAT(ACTION_TIMING, ' ', EVENT_MANIPULATION, ' FOR EACH ROW ', ACTION_STATEMENT) "
            + "FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA=@schema ORDER BY 2, 1",
        DatabaseKind.SqlServer => "SELECT tr.name, t.name, m.definition FROM sys.triggers tr JOIN sys.tables t ON t.object_id=tr.parent_id "
            + "JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.sql_modules m ON m.object_id=tr.object_id WHERE s.name=@schema ORDER BY 2, 1",
        _ => "SELECT name, tbl_name, sql FROM sqlite_schema WHERE type='trigger' ORDER BY 2, 1",
    };

    /// <summary>The schema's functions and procedures: MySQL's each by its SHOW CREATE, without its definer, which only the
    /// server it was made on knows.</summary>
    private static async Task<IReadOnlyList<RoutineShape>> RoutinesAsync(DbConnection connection, DatabaseKind kind, string schema, (string, object?)[] named, CancellationToken cancel)
    {
        switch (kind)
        {
            case DatabaseKind.Postgres:
                return [.. (await ReadAsync(connection, "SELECT p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')', "
                    + "CASE p.prokind WHEN 'p' THEN 'PROCEDURE' ELSE 'FUNCTION' END, pg_get_functiondef(p.oid) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace "
                    + "WHERE n.nspname=@schema AND p.prokind IN ('f','p') AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid=p.oid AND d.deptype='e') ORDER BY 1",
                    named, cancel)).Select(r => new RoutineShape(Text(r[0]), Text(r[1]), Text(r[2])))];
            case DatabaseKind.SqlServer:
                return [.. (await ReadAsync(connection, "SELECT o.name, CASE o.type WHEN 'P' THEN 'PROCEDURE' ELSE 'FUNCTION' END, m.definition FROM sys.objects o "
                    + "JOIN sys.schemas s ON s.schema_id=o.schema_id JOIN sys.sql_modules m ON m.object_id=o.object_id WHERE s.name=@schema AND o.type IN ('P','FN','IF','TF') ORDER BY 1",
                    named, cancel)).Select(r => new RoutineShape(Text(r[0]), Text(r[1]), Text(r[2])))];
            case DatabaseKind.MySql:
                List<RoutineShape> routines = [];
                foreach (object?[] row in await ReadAsync(connection, "SELECT ROUTINE_NAME, ROUTINE_TYPE FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA=@schema ORDER BY 1", named, cancel))
                {
                    string type = Text(row[1]) == "PROCEDURE" ? "PROCEDURE" : "FUNCTION";
                    object?[][] made = await ReadAsync(connection, $"SHOW CREATE {type} {SqlText.QuotedName(kind, schema)}.{SqlText.QuotedName(kind, Text(row[0]))}", [], cancel);
                    if (made is [{ Length: > 2 } shown, ..])
                    {
                        routines.Add(new RoutineShape(Text(row[0]), type, WithoutDefiner(Text(shown[2]))));
                    }
                }
                return routines;
            default:
                return [];
        }
    }

    /// <summary>The schema's sequences but those a column owns: MariaDB's each read from itself, as its catalog lists its
    /// sequences as tables.</summary>
    private static async Task<IReadOnlyList<SequenceShape>> SequencesAsync(DbConnection connection, DatabaseKind kind, string schema, (string, object?)[] named, CancellationToken cancel)
    {
        switch (kind)
        {
            case DatabaseKind.Postgres:
                return [.. (await ReadAsync(connection, "SELECT s.sequencename, CAST(s.data_type AS text), CAST(s.start_value AS text), CAST(s.increment_by AS text), "
                    + "CAST(s.min_value AS text), CAST(s.max_value AS text), s.cycle FROM pg_sequences s WHERE s.schemaname=@schema AND NOT EXISTS (SELECT 1 FROM pg_class c "
                    + "JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_depend d ON d.objid=c.oid WHERE n.nspname=s.schemaname AND c.relname=s.sequencename "
                    + "AND d.deptype IN ('a','i')) ORDER BY 1", named, cancel)).Select(Sequence)];
            case DatabaseKind.SqlServer:
                return [.. (await ReadAsync(connection, "SELECT q.name, TYPE_NAME(q.user_type_id), CAST(q.start_value AS varchar(40)), CAST(q.increment AS varchar(40)), "
                    + "CAST(q.minimum_value AS varchar(40)), CAST(q.maximum_value AS varchar(40)), q.is_cycling FROM sys.sequences q "
                    + "JOIN sys.schemas s ON s.schema_id=q.schema_id WHERE s.name=@schema ORDER BY 1", named, cancel)).Select(Sequence)];
            case DatabaseKind.MySql:
                List<SequenceShape> sequences = [];
                foreach (object?[] row in await ReadAsync(connection, "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=@schema AND TABLE_TYPE='SEQUENCE' ORDER BY 1", named, cancel))
                {
                    object?[][] read = await ReadAsync(connection, "SELECT start_value, increment, minimum_value, maximum_value, cycle_option FROM "
                        + $"{SqlText.QuotedName(kind, schema)}.{SqlText.QuotedName(kind, Text(row[0]))}", [], cancel);
                    if (read is [var settings, ..])
                    {
                        sequences.Add(Sequence([row[0], string.Empty, .. settings]));
                    }
                }
                return sequences;
            default:
                return [];
        }
    }

    private static SequenceShape Sequence(object?[] row) => new(Text(row[0]), Text(row[1]), Text(row[2]), Text(row[3]), Text(row[4]), Text(row[5]), Truth(row[6]));

    private static async Task<object?[][]> ReadAsync(DbConnection connection, string sql, (string, object?)[] named, CancellationToken cancel) =>
        (await DatabaseClient.ReadAsync(connection, sql, named, cancel, limit: 10_000)).Rows;

    /// <summary>A MySQL statement without its DEFINER clause.</summary>
    private static string WithoutDefiner(string statement)
    {
        int at = statement.IndexOf("DEFINER=", StringComparison.OrdinalIgnoreCase);
        int end = at < 0 ? -1 : statement.IndexOf(' ', at);
        return at < 0 || end < 0 ? statement : statement[..at] + statement[(end + 1)..];
    }

    /// <summary>SQLite's primary keys and foreign keys, each table's read by its own pragmas.</summary>
    private static async Task<List<object?[]>> SqliteKeysAsync(DbConnection connection, CancellationToken cancel)
    {
        List<object?[]> keys = [];
        DatabaseRows tables = await DatabaseClient.ReadAsync(connection, "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY 1", [], cancel);
        foreach (string table in tables.Rows.Select(r => Text(r[0])))
        {
            DatabaseRows primary = await DatabaseClient.ReadAsync(connection, "SELECT name FROM pragma_table_info(@table) WHERE pk > 0 ORDER BY pk", [("@table", table)], cancel);
            if (primary.Rows.Length > 0)
            {
                keys.Add([table, "primary", "p", string.Join(',', primary.Rows.Select(r => Text(r[0]))), null, null]);
            }
            DatabaseRows foreign = await DatabaseClient.ReadAsync(connection, "SELECT id, \"from\", \"table\", \"to\" FROM pragma_foreign_key_list(@table) ORDER BY id, seq", [("@table", table)], cancel);
            foreach (IGrouping<string, object?[]> key in foreign.Rows.GroupBy(r => Text(r[0])))
            {
                keys.Add([table, $"{table}_fk{key.Key}", "f", string.Join(',', key.Select(r => Text(r[1]))), Text(key.First()[2]), string.Join(',', key.Select(r => Text(r[3])))]);
            }
        }
        return keys;
    }

    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static bool Truth(object? value) => value switch
    {
        bool truth => truth,
        null => false,
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
    };

    private static IReadOnlyList<string> Listed(object? value) => Text(value) is { Length: > 0 } list ? list.Split(',') : [];

    /// <summary>A column's default as its server writes it, none where it has none (MariaDB writes none as NULL).</summary>
    private static string? Default(DatabaseKind kind, object? value) =>
        value is null or DBNull || Text(value) is "" || kind == DatabaseKind.MySql && Text(value) == "NULL" ? null : Text(value);
}
