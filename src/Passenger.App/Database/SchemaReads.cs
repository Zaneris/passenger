using System.Data.Common;
using System.Globalization;

namespace Passenger.App.Database;

/// <summary>A schema's shape read from its server's own catalog (PostgreSQL's pg_catalog, MySQL's information_schema,
/// SQL Server's sys views, SQLite's schema table and pragmas): every table's columns with their full types, NULL and
/// defaults, its primary key, its foreign keys and the indexes no constraint makes, and every view's definition.</summary>
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
                    + "c.is_nullable, dc.definition FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.columns c ON c.object_id=t.object_id "
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
                [.. indexRows.Rows.Where(i => Text(i[0]) == table.Key).Select(i => new IndexShape(Text(i[1]), Truth(i[2]), Listed(i[3])))]));
        }
        string qualifier = kind == DatabaseKind.MySql ? SqlText.QuotedName(kind, schema) + "." : string.Empty;
        List<ViewShape> views = [.. viewRows.Rows.Select(r => new ViewShape(Text(r[0]),
            qualifier.Length > 0 ? Text(r[1]).Replace(qualifier, string.Empty, StringComparison.Ordinal) : Text(r[1])))];
        return new SchemaShape(kind, kind == DatabaseKind.Sqlite ? string.Empty : schema, tables, views);
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
