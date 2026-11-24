using System.Text;

namespace Passenger.App.Database;

/// <summary>A column as a schema declares it: its type as its server writes it, whether it takes NULL, and its default.</summary>
public sealed record ColumnShape(string Name, string Type, bool Nullable, string? Default = null);

/// <summary>A foreign key: its columns and the table and columns they refer to, in the same schema.</summary>
public sealed record ForeignKeyShape(string Name, IReadOnlyList<string> Columns, string Table, IReadOnlyList<string> To)
{
    /// <summary>What it is, whatever its name: two databases name the same key apart.</summary>
    public string Meaning => $"({string.Join(",", Columns)})→{Table}({string.Join(",", To)})";
}

/// <summary>An index no constraint makes: its name, whether it is unique, and its columns.</summary>
public sealed record IndexShape(string Name, bool Unique, IReadOnlyList<string> Columns);

/// <summary>A table: its columns in order, its primary key's columns, its foreign keys and its indexes.</summary>
public sealed record TableShape(string Name, IReadOnlyList<ColumnShape> Columns, IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<ForeignKeyShape> ForeignKeys, IReadOnlyList<IndexShape> Indexes);

/// <summary>A view and the statement it is, as its server gives it back.</summary>
public sealed record ViewShape(string Name, string Definition);

/// <summary>What a schema holds: its tables and views, as one database of <see cref="Kind"/> reads them.</summary>
public sealed record SchemaShape(DatabaseKind Kind, string Schema, IReadOnlyList<TableShape> Tables, IReadOnlyList<ViewShape> Views);

/// <summary>One way two schemas differ, and the statements that make the target as the source is (a comment where its
/// database cannot say it).</summary>
public sealed record SchemaDifference(string What, string Said, IReadOnlyList<string> Statements);

/// <summary>Two schemas compared: what the source holds that the target lacks, holds otherwise or holds that the source
/// does not, as tables, columns (type, NULL and default), primary keys, foreign keys (by what they mean, not their names),
/// indexes and views; and the script in the target's own SQL that makes the target as the source is, ordered so each
/// statement finds what it needs: foreign keys dropped first, then tables made, columns added, changed and dropped,
/// indexes, foreign keys added, views, and tables dropped last. A change a database cannot make in place (SQLite's
/// column types, keys and foreign keys; any primary key) is a comment saying so.</summary>
public static class SchemaCompare
{
    public static IReadOnlyList<SchemaDifference> Differences(SchemaShape source, SchemaShape target)
    {
        DatabaseKind kind = target.Kind;
        string Name(string table) => (target.Schema.Length > 0 ? SqlText.QuotedName(kind, target.Schema) + "." : string.Empty) + SqlText.QuotedName(kind, table);
        string Q(string name) => SqlText.QuotedName(kind, name);
        List<SchemaDifference> dropKeys = [];
        List<SchemaDifference> made = [];
        List<SchemaDifference> columns = [];
        List<SchemaDifference> indexes = [];
        List<SchemaDifference> addKeys = [];
        List<SchemaDifference> views = [];
        List<SchemaDifference> dropped = [];
        Dictionary<string, TableShape> theirs = target.Tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
        Dictionary<string, TableShape> ours = source.Tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
        foreach (TableShape table in source.Tables)
        {
            if (!theirs.TryGetValue(table.Name, out TableShape? there))
            {
                made.Add(new SchemaDifference($"table {table.Name}", "only in the source", [Create(kind, Name(table.Name), table)]));
                foreach (IndexShape index in table.Indexes)
                {
                    indexes.Add(new SchemaDifference($"index {index.Name}", "only in the source", [CreateIndex(kind, Name(table.Name), index)]));
                }
                addKeys.AddRange(table.ForeignKeys.Select(key => new SchemaDifference($"foreign key {key.Name} of {table.Name}", "only in the source",
                    [AddKey(kind, Name(table.Name), key, Name(key.Table))])));
                continue;
            }
            Dictionary<string, ColumnShape> had = there.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
            foreach (ColumnShape column in table.Columns)
            {
                if (!had.TryGetValue(column.Name, out ColumnShape? was))
                {
                    columns.Add(new SchemaDifference($"column {table.Name}.{column.Name}", "only in the source",
                        [$"ALTER TABLE {Name(table.Name)} ADD {(kind == DatabaseKind.SqlServer ? string.Empty : "COLUMN ")}{Declared(kind, column)}"]));
                }
                else if (was != column)
                {
                    columns.Add(new SchemaDifference($"column {table.Name}.{column.Name}", Otherwise(was, column), Altered(kind, Name(table.Name), was, column)));
                }
            }
            foreach (ColumnShape column in there.Columns.Where(c => !table.Columns.Any(o => o.Name == c.Name)))
            {
                columns.Add(new SchemaDifference($"column {table.Name}.{column.Name}", "only in the target", [$"ALTER TABLE {Name(table.Name)} DROP COLUMN {Q(column.Name)}"]));
            }
            if (!table.PrimaryKey.SequenceEqual(there.PrimaryKey))
            {
                columns.Add(new SchemaDifference($"primary key of {table.Name}", $"({string.Join(", ", there.PrimaryKey)}) here, ({string.Join(", ", table.PrimaryKey)}) in the source",
                    [$"-- The primary key of {table.Name} differs: change it by hand, as its constraint and what refers to it move with it"]));
            }
            foreach (ForeignKeyShape key in there.ForeignKeys.Where(k => !table.ForeignKeys.Any(o => o.Meaning == k.Meaning)))
            {
                dropKeys.Add(new SchemaDifference($"foreign key {key.Name} of {table.Name}", "only in the target", [DropKey(kind, Name(table.Name), key)]));
            }
            foreach (ForeignKeyShape key in table.ForeignKeys.Where(k => !there.ForeignKeys.Any(o => o.Meaning == k.Meaning)))
            {
                addKeys.Add(new SchemaDifference($"foreign key {key.Name} of {table.Name}", "only in the source", [AddKey(kind, Name(table.Name), key, Name(key.Table))]));
            }
            foreach (IndexShape index in there.Indexes.Where(i => !table.Indexes.Any(o => Same(o, i))))
            {
                indexes.Add(new SchemaDifference($"index {index.Name}", table.Indexes.Any(o => o.Name == index.Name) ? "otherwise in the target" : "only in the target",
                    [DropIndex(kind, Name(table.Name), target.Schema, index)]));
            }
            foreach (IndexShape index in table.Indexes.Where(i => !there.Indexes.Any(o => Same(o, i))))
            {
                indexes.Add(new SchemaDifference($"index {index.Name}", "only in the source", [CreateIndex(kind, Name(table.Name), index)]));
            }
        }
        foreach (TableShape table in target.Tables.Where(t => !ours.ContainsKey(t.Name)))
        {
            dropped.Add(new SchemaDifference($"table {table.Name}", "only in the target: dropping it drops its rows", [$"DROP TABLE {Name(table.Name)}"]));
        }
        Dictionary<string, ViewShape> seen = target.Views.ToDictionary(v => v.Name, StringComparer.Ordinal);
        foreach (ViewShape view in source.Views)
        {
            if (!seen.TryGetValue(view.Name, out ViewShape? there) || Normal(kind, there.Definition) != Normal(source.Kind, view.Definition))
            {
                views.Add(new SchemaDifference($"view {view.Name}", there is null ? "only in the source" : "otherwise in the target", MakeView(kind, Name(view.Name), view, there is not null)));
            }
        }
        foreach (ViewShape view in target.Views.Where(v => !source.Views.Any(o => o.Name == v.Name)))
        {
            views.Add(new SchemaDifference($"view {view.Name}", "only in the target", [$"DROP VIEW {Name(view.Name)}"]));
        }
        return [.. dropKeys, .. made, .. columns, .. indexes, .. addKeys, .. views, .. dropped];
    }

    /// <summary>The script that makes the target as the source is: a header naming both, each difference's statements under
    /// a comment saying it, each statement ending in a semicolon, and on SQL Server in a batch of its own, as a view's must be.</summary>
    public static string Script(DatabaseKind kind, IReadOnlyList<SchemaDifference> differences, string from, string to)
    {
        StringBuilder script = new();
        script.Append("-- Makes ").Append(to).Append(" as ").Append(from).Append(" is: ").Append(differences.Count).Append(differences.Count == 1 ? " difference" : " differences")
            .Append(". Read it through before running it.\n");
        foreach (SchemaDifference difference in differences)
        {
            script.Append("\n-- ").Append(difference.What).Append(": ").Append(difference.Said).Append('\n');
            foreach (string statement in difference.Statements)
            {
                script.Append(statement).Append(statement.StartsWith("--", StringComparison.Ordinal) ? "\n" : kind == DatabaseKind.SqlServer ? ";\nGO\n" : ";\n");
            }
        }
        return script.ToString();
    }

    private static bool Same(IndexShape one, IndexShape other) => one.Name == other.Name && one.Unique == other.Unique && one.Columns.SequenceEqual(other.Columns);

    private static string Otherwise(ColumnShape was, ColumnShape now)
    {
        List<string> said = [];
        if (was.Type != now.Type)
        {
            said.Add($"{was.Type} here, {now.Type} in the source");
        }
        if (was.Nullable != now.Nullable)
        {
            said.Add(now.Nullable ? "takes NULL in the source" : "takes no NULL in the source");
        }
        if (was.Default != now.Default)
        {
            said.Add($"defaults to {now.Default ?? "nothing"} in the source");
        }
        return string.Join("; ", said);
    }

    private static string Declared(DatabaseKind kind, ColumnShape column) =>
        $"{SqlText.QuotedName(kind, column.Name)} {column.Type}{(column.Default is { } value ? " DEFAULT " + value : string.Empty)}{(column.Nullable ? string.Empty : " NOT NULL")}";

    private static string Create(DatabaseKind kind, string name, TableShape table)
    {
        List<string> parts = [.. table.Columns.Select(c => "  " + Declared(kind, c))];
        if (table.PrimaryKey.Count > 0)
        {
            parts.Add($"  PRIMARY KEY ({string.Join(", ", table.PrimaryKey.Select(c => SqlText.QuotedName(kind, c)))})");
        }
        return $"CREATE TABLE {name} (\n{string.Join(",\n", parts)}\n)";
    }

    private static IReadOnlyList<string> Altered(DatabaseKind kind, string table, ColumnShape was, ColumnShape now)
    {
        string column = SqlText.QuotedName(kind, now.Name);
        switch (kind)
        {
            case DatabaseKind.Postgres:
                List<string> changes = [];
                if (was.Type != now.Type)
                {
                    changes.Add($"ALTER COLUMN {column} TYPE {now.Type}");
                }
                if (was.Nullable != now.Nullable)
                {
                    changes.Add($"ALTER COLUMN {column} {(now.Nullable ? "DROP" : "SET")} NOT NULL");
                }
                if (was.Default != now.Default)
                {
                    changes.Add(now.Default is { } value ? $"ALTER COLUMN {column} SET DEFAULT {value}" : $"ALTER COLUMN {column} DROP DEFAULT");
                }
                return [$"ALTER TABLE {table} {string.Join(", ", changes)}"];
            case DatabaseKind.MySql:
                return [$"ALTER TABLE {table} MODIFY COLUMN {Declared(kind, now)}"];
            case DatabaseKind.SqlServer:
                List<string> statements = [];
                if (was.Type != now.Type || was.Nullable != now.Nullable)
                {
                    statements.Add($"ALTER TABLE {table} ALTER COLUMN {column} {now.Type} {(now.Nullable ? "NULL" : "NOT NULL")}");
                }
                if (was.Default != now.Default)
                {
                    statements.Add($"-- The default of {now.Name} differs: SQL Server keeps it as a constraint of its own, changed by hand");
                }
                return statements;
            default:
                return [$"-- SQLite cannot change the column {now.Name} in place: make the table again with it as the source has it"];
        }
    }

    private static string AddKey(DatabaseKind kind, string table, ForeignKeyShape key, string to) =>
        kind == DatabaseKind.Sqlite
            ? $"-- SQLite cannot add the foreign key {key.Name} to a table: make the table again with it"
            : $"ALTER TABLE {table} ADD CONSTRAINT {SqlText.QuotedName(kind, key.Name)} FOREIGN KEY ({string.Join(", ", key.Columns.Select(c => SqlText.QuotedName(kind, c)))}) "
                + $"REFERENCES {to} ({string.Join(", ", key.To.Select(c => SqlText.QuotedName(kind, c)))})";

    private static string DropKey(DatabaseKind kind, string table, ForeignKeyShape key) => kind switch
    {
        DatabaseKind.Sqlite => $"-- SQLite cannot drop the foreign key {key.Name} from a table: make the table again without it",
        DatabaseKind.MySql => $"ALTER TABLE {table} DROP FOREIGN KEY {SqlText.QuotedName(kind, key.Name)}",
        _ => $"ALTER TABLE {table} DROP CONSTRAINT {SqlText.QuotedName(kind, key.Name)}",
    };

    private static string CreateIndex(DatabaseKind kind, string table, IndexShape index) =>
        $"CREATE {(index.Unique ? "UNIQUE " : string.Empty)}INDEX {SqlText.QuotedName(kind, index.Name)} ON {table} ({string.Join(", ", index.Columns.Select(c => SqlText.QuotedName(kind, c)))})";

    private static string DropIndex(DatabaseKind kind, string table, string schema, IndexShape index) => kind switch
    {
        DatabaseKind.Postgres => $"DROP INDEX {(schema.Length > 0 ? SqlText.QuotedName(kind, schema) + "." : string.Empty)}{SqlText.QuotedName(kind, index.Name)}",
        DatabaseKind.Sqlite => $"DROP INDEX {SqlText.QuotedName(kind, index.Name)}",
        _ => $"DROP INDEX {SqlText.QuotedName(kind, index.Name)} ON {table}",
    };

    /// <summary>A view made as the source has it: SQL Server's and SQLite's definitions are whole statements, made again
    /// under the target's name; PostgreSQL's and MySQL's are its query.</summary>
    private static IReadOnlyList<string> MakeView(DatabaseKind kind, string name, ViewShape view, bool exists)
    {
        string body = view.Definition.Trim().TrimEnd(';').Trim();
        switch (kind)
        {
            case DatabaseKind.SqlServer:
                int at = AsAt(body);
                return [$"CREATE OR ALTER VIEW {name} AS{body[at..]}"];
            case DatabaseKind.Sqlite:
                return [.. exists ? [$"DROP VIEW {name}"] : Array.Empty<string>(), $"CREATE VIEW {name} AS{body[AsAt(body)..]}"];
            case DatabaseKind.MySql:
                return [$"CREATE OR REPLACE VIEW {name} AS {body}"];
            default:
                return [.. exists ? [$"DROP VIEW {name}"] : Array.Empty<string>(), $"CREATE VIEW {name} AS {body}"];
        }
    }

    /// <summary>Where a whole CREATE VIEW statement's query begins, after its first AS word.</summary>
    private static int AsAt(string statement)
    {
        foreach (SqlText.Token token in SqlText.Tokens(statement, DatabaseKind.SqlServer))
        {
            if (token.Word == "AS" && !token.Quoted)
            {
                return token.End;
            }
        }
        return 0;
    }

    /// <summary>A view's query as compared, its white space and letter case aside: SQL Server's and SQLite's after the
    /// statement's AS, as they keep the statement that made the view.</summary>
    private static string Normal(DatabaseKind kind, string definition)
    {
        string query = kind is DatabaseKind.SqlServer or DatabaseKind.Sqlite ? definition[AsAt(definition)..] : definition;
        return string.Join(' ', query.Trim().TrimEnd(';').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }
}
