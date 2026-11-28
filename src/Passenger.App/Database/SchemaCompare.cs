using System.Text;
using Passenger.Core;

namespace Passenger.App.Database;

/// <summary>A column as a schema declares it: its type as its server writes it, whether it takes NULL, and its default.</summary>
public sealed record ColumnShape(string Name, string Type, bool Nullable, string? Default = null);

/// <summary>A foreign key: its columns and the table and columns they refer to, that table in the key's own schema, or in
/// <see cref="ToSchema"/> where it names another.</summary>
public sealed record ForeignKeyShape(string Name, IReadOnlyList<string> Columns, string Table, IReadOnlyList<string> To, string ToSchema = "")
{
    /// <summary>What it is, whatever its name: two databases name the same key apart. A table of its own schema is read as
    /// each schema's own, one of another schema as that schema's.</summary>
    public string Meaning => $"({string.Join(",", Columns)})→{(ToSchema.Length > 0 ? ToSchema + "." : string.Empty)}{Table}({string.Join(",", To)})";
}

/// <summary>An index no constraint makes: its name, whether it is unique, its columns, and what it says beyond them as its
/// server writes it (<see cref="Definition"/>).</summary>
/// <param name="Definition">PostgreSQL's from its USING on (its method, keys and expressions, INCLUDE and WHERE), SQL
/// Server's INCLUDE and WHERE, SQLite's whole statement where it holds an expression or a WHERE; empty where it is its
/// columns alone.</param>
public sealed record IndexShape(string Name, bool Unique, IReadOnlyList<string> Columns, string Definition = "");

/// <summary>A table: its columns in order, its primary key's columns, its foreign keys and its indexes; and the names its
/// server gave its primary key and (SQL Server's) its columns' defaults, which a change drops by.</summary>
public sealed record TableShape(string Name, IReadOnlyList<ColumnShape> Columns, IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<ForeignKeyShape> ForeignKeys, IReadOnlyList<IndexShape> Indexes)
{
    public string? PrimaryKeyName { get; init; }
    public IReadOnlyDictionary<string, string> DefaultNames { get; init; } = new Dictionary<string, string>();
}

/// <summary>A view and the statement it is, as its server gives it back.</summary>
public sealed record ViewShape(string Name, string Definition);

/// <summary>A trigger on a table and what its server gives back of it: PostgreSQL's, SQL Server's and SQLite's whole
/// statement, MySQL's timing, event and body.</summary>
public sealed record TriggerShape(string Name, string Table, string Definition);

/// <summary>A function or procedure (<see cref="Kind"/> FUNCTION or PROCEDURE), named with PostgreSQL's arguments, and the
/// statement that makes it as its server gives it back.</summary>
public sealed record RoutineShape(string Name, string Kind, string Definition)
{
    /// <summary>Its name alone, without PostgreSQL's arguments.</summary>
    public string Called => Name.Split('(')[0];
}

/// <summary>A sequence: its type (none for MariaDB's), its start, step, bounds and whether it cycles.</summary>
public sealed record SequenceShape(string Name, string Type, string Start, string Increment, string Minimum, string Maximum, bool Cycle);

/// <summary>What a schema holds: its tables and views, triggers, routines and sequences, as one database of
/// <see cref="Kind"/> reads them.</summary>
public sealed record SchemaShape(DatabaseKind Kind, string Schema, IReadOnlyList<TableShape> Tables, IReadOnlyList<ViewShape> Views)
{
    public IReadOnlyList<TriggerShape> Triggers { get; init; } = [];
    public IReadOnlyList<RoutineShape> Routines { get; init; } = [];
    public IReadOnlyList<SequenceShape> Sequences { get; init; } = [];
}

/// <summary>One way two schemas differ, and the statements that make the target as the source is (a comment where its
/// database cannot say it).</summary>
public sealed record SchemaDifference(string What, string Said, IReadOnlyList<string> Statements);

/// <summary>Two schemas compared: what the source holds that the target lacks, holds otherwise or holds that the source
/// does not, as tables, columns (type, NULL and default), primary keys, foreign keys (by what they mean, not their names),
/// indexes, views, triggers, routines and sequences; and the script in the target's own SQL that makes the target as the
/// source is, ordered so each statement finds what it needs: foreign keys and indexes dropped first (keys referring to a
/// primary key that changes too, added again after), then sequences, tables made (SQLite's with their foreign keys),
/// columns added, changed and dropped, primary keys, indexes made, foreign keys added, routines, views and triggers, and
/// what only the target holds dropped last, a table another refers to after it. A change a database cannot make in place
/// (SQLite's column types, keys and foreign keys) is a comment saying so.</summary>
public static class SchemaCompare
{
    public static IReadOnlyList<SchemaDifference> Differences(SchemaShape source, SchemaShape target)
    {
        DatabaseKind kind = target.Kind;
        string Name(string table) => (target.Schema.Length > 0 ? SqlText.QuotedName(kind, target.Schema) + "." : string.Empty) + SqlText.QuotedName(kind, table);
        string Q(string name) => SqlText.QuotedName(kind, name);
        List<SchemaDifference> dropKeys = [];
        List<SchemaDifference> dropIndexes = [];
        List<SchemaDifference> sequences = [];
        List<SchemaDifference> made = [];
        List<SchemaDifference> columns = [];
        List<SchemaDifference> indexes = [];
        List<SchemaDifference> addKeys = [];
        List<SchemaDifference> routines = [];
        List<SchemaDifference> views = [];
        List<SchemaDifference> triggers = [];
        List<SchemaDifference> dropped = [];
        Dictionary<string, TableShape> theirs = target.Tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
        Dictionary<string, TableShape> ours = source.Tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
        foreach (TableShape table in source.Tables)
        {
            if (!theirs.TryGetValue(table.Name, out TableShape? there))
            {
                made.Add(new SchemaDifference($"table {table.Name}", "only in the source", [Create(kind, Name(table.Name), table, Referred)]));
                foreach (IndexShape index in table.Indexes)
                {
                    indexes.Add(new SchemaDifference($"index {index.Name}", "only in the source", [CreateIndex(kind, Name(table.Name), index)]));
                }
                // SQLite's new table holds its foreign keys as it is made, which it can add no other way.
                if (kind != DatabaseKind.Sqlite)
                {
                    addKeys.AddRange(table.ForeignKeys.Select(key => new SchemaDifference($"foreign key {key.Name} of {table.Name}", "only in the source",
                        [AddKey(kind, Name(table.Name), key, Referred(key))])));
                }
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
                    columns.Add(new SchemaDifference($"column {table.Name}.{column.Name}", Otherwise(was, column), Altered(kind, Name(table.Name), was, column, there)));
                }
            }
            foreach (ColumnShape column in there.Columns.Where(c => !table.Columns.Any(o => o.Name == c.Name)))
            {
                columns.Add(new SchemaDifference($"column {table.Name}.{column.Name}", "only in the target", [$"ALTER TABLE {Name(table.Name)} DROP COLUMN {Q(column.Name)}"]));
            }
            if (!table.PrimaryKey.SequenceEqual(there.PrimaryKey))
            {
                columns.Add(new SchemaDifference($"primary key of {table.Name}", $"({string.Join(", ", there.PrimaryKey)}) here, ({string.Join(", ", table.PrimaryKey)}) in the source",
                    PrimaryKey(kind, Name(table.Name), table, there)));
                // The foreign keys referring to it go while it changes, and come again after.
                foreach ((TableShape holder, ForeignKeyShape key) in target.Tables.SelectMany(t => t.ForeignKeys.Select(k => (t, k))).Where(p => p.k.Table == table.Name
                    && ours.TryGetValue(p.t.Name, out TableShape? mine) && mine.ForeignKeys.Any(o => o.Meaning == p.k.Meaning)))
                {
                    dropKeys.Add(new SchemaDifference($"foreign key {key.Name} of {holder.Name}", $"dropped while the primary key of {table.Name} changes", [DropKey(kind, Name(holder.Name), key)]));
                    addKeys.Add(new SchemaDifference($"foreign key {key.Name} of {holder.Name}", $"made again once the primary key of {table.Name} changed",
                        [AddKey(kind, Name(holder.Name), key, Referred(key))]));
                }
            }
            foreach (ForeignKeyShape key in there.ForeignKeys.Where(k => !table.ForeignKeys.Any(o => o.Meaning == k.Meaning)))
            {
                dropKeys.Add(new SchemaDifference($"foreign key {key.Name} of {table.Name}", "only in the target", [DropKey(kind, Name(table.Name), key)]));
            }
            foreach (ForeignKeyShape key in table.ForeignKeys.Where(k => !there.ForeignKeys.Any(o => o.Meaning == k.Meaning)))
            {
                addKeys.Add(new SchemaDifference($"foreign key {key.Name} of {table.Name}", "only in the source", [AddKey(kind, Name(table.Name), key, Referred(key))]));
            }
            // An index goes before its columns change or go, which a database may refuse while one holds them.
            foreach (IndexShape index in there.Indexes.Where(i => !table.Indexes.Any(o => Same(o, i))))
            {
                dropIndexes.Add(new SchemaDifference($"index {index.Name}", table.Indexes.Any(o => o.Name == index.Name) ? "otherwise in the target" : "only in the target",
                    [DropIndex(kind, Name(table.Name), target.Schema, index)]));
            }
            foreach (IndexShape index in table.Indexes.Where(i => !there.Indexes.Any(o => Same(o, i))))
            {
                indexes.Add(new SchemaDifference($"index {index.Name}", "only in the source", [CreateIndex(kind, Name(table.Name), index)]));
            }
        }
        foreach (TableShape table in Dropping([.. target.Tables.Where(t => !ours.ContainsKey(t.Name))]))
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
        foreach (SequenceShape sequence in source.Sequences)
        {
            SequenceShape? there = target.Sequences.FirstOrDefault(s => s.Name == sequence.Name);
            if (there != sequence)
            {
                sequences.Add(new SchemaDifference($"sequence {sequence.Name}", there is null ? "only in the source" : "otherwise in the target",
                    there is null ? [MakeSequence(kind, Name(sequence.Name), sequence)] : AlterSequence(kind, Name(sequence.Name), there, sequence)));
            }
        }
        foreach (SequenceShape sequence in target.Sequences.Where(s => !source.Sequences.Any(o => o.Name == s.Name)))
        {
            dropped.Insert(0, new SchemaDifference($"sequence {sequence.Name}", "only in the target", [$"DROP SEQUENCE {Name(sequence.Name)}"]));
        }
        foreach (RoutineShape routine in source.Routines)
        {
            RoutineShape? there = target.Routines.FirstOrDefault(r => r.Name == routine.Name && r.Kind == routine.Kind);
            if (there is null || Body(kind, there.Definition, there.Kind) != Body(source.Kind, routine.Definition, routine.Kind))
            {
                routines.Add(new SchemaDifference($"{routine.Kind.ToLowerInvariant()} {routine.Name}", there is null ? "only in the source" : "otherwise in the target",
                    MakeRoutine(kind, Name(routine.Called), routine, there is not null)));
            }
        }
        foreach (RoutineShape routine in target.Routines.Where(r => !source.Routines.Any(o => o.Name == r.Name && o.Kind == r.Kind)))
        {
            dropped.Insert(0, new SchemaDifference($"{routine.Kind.ToLowerInvariant()} {routine.Name}", "only in the target",
                [$"DROP {routine.Kind} {Name(routine.Called)}{(kind == DatabaseKind.Postgres ? routine.Name[routine.Called.Length..] : string.Empty)}"]));
        }
        foreach (TriggerShape trigger in source.Triggers)
        {
            TriggerShape? there = target.Triggers.FirstOrDefault(t => t.Name == trigger.Name && t.Table == trigger.Table);
            if (there is null || Body(kind, there.Definition, "TRIGGER") != Body(source.Kind, trigger.Definition, "TRIGGER"))
            {
                triggers.Add(new SchemaDifference($"trigger {trigger.Name} of {trigger.Table}", there is null ? "only in the source" : "otherwise in the target",
                    MakeTrigger(kind, Name(trigger.Name), Name(trigger.Table), trigger, there is not null)));
            }
        }
        foreach (TriggerShape trigger in target.Triggers.Where(t => !source.Triggers.Any(o => o.Name == t.Name && o.Table == t.Table) && ours.ContainsKey(t.Table)))
        {
            triggers.Add(new SchemaDifference($"trigger {trigger.Name} of {trigger.Table}", "only in the target", [DropTrigger(kind, Name(trigger.Name), Name(trigger.Table))]));
        }
        return [.. dropKeys, .. dropIndexes, .. sequences, .. made, .. columns, .. indexes, .. addKeys, .. routines, .. views, .. triggers, .. dropped];

        // The table a key refers to, in the target's schema where it is the key's own, else in the schema it names.
        string Referred(ForeignKeyShape key) => key.ToSchema.Length > 0 ? $"{Q(key.ToSchema)}.{Q(key.Table)}" : Name(key.Table);
    }

    /// <summary>Tables to drop, each before those it refers to among them, as a table a foreign key holds cannot go first.</summary>
    private static List<TableShape> Dropping(List<TableShape> tables)
    {
        List<TableShape> ordered = [];
        List<TableShape> left = [.. tables];
        while (left.Count > 0)
        {
            // One no other left refers to goes next; where they refer to each other in a ring, the first of them.
            TableShape next = left.FirstOrDefault(t => !left.Any(o => o != t && o.ForeignKeys.Any(k => k.ToSchema.Length == 0 && k.Table == t.Name))) ?? left[0];
            ordered.Add(next);
            left.Remove(next);
        }
        return ordered;
    }

    /// <summary>The script that makes the target as the source is: a header naming both, each difference's statements under
    /// a comment saying it, each statement ending in a semicolon, and on SQL Server in a batch of its own, as a view's must be.</summary>
    public static string Script(DatabaseKind kind, IReadOnlyList<SchemaDifference> differences, string from, string to)
    {
        StringBuilder script = new();
        script.Append("-- Makes ").Append(to).Append(" as ").Append(from).Append(" is: ").Append(Plural.Of(differences.Count, "difference"))
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

    private static bool Same(IndexShape one, IndexShape other) =>
        one.Name == other.Name && one.Unique == other.Unique && one.Columns.SequenceEqual(other.Columns) && one.Definition == other.Definition;

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

    /// <summary>A table made as the source has it: its columns and primary key, and SQLite's its foreign keys too.</summary>
    private static string Create(DatabaseKind kind, string name, TableShape table, Func<ForeignKeyShape, string> referred)
    {
        List<string> parts = [.. table.Columns.Select(c => "  " + Declared(kind, c))];
        if (table.PrimaryKey.Count > 0)
        {
            parts.Add($"  PRIMARY KEY ({string.Join(", ", table.PrimaryKey.Select(c => SqlText.QuotedName(kind, c)))})");
        }
        if (kind == DatabaseKind.Sqlite)
        {
            parts.AddRange(table.ForeignKeys.Select(key => $"  {KeyClause(kind, key, referred(key))}"));
        }
        return $"CREATE TABLE {name} (\n{string.Join(",\n", parts)}\n)";
    }

    private static string KeyClause(DatabaseKind kind, ForeignKeyShape key, string to) =>
        $"FOREIGN KEY ({string.Join(", ", key.Columns.Select(c => SqlText.QuotedName(kind, c)))}) REFERENCES {to} ({string.Join(", ", key.To.Select(c => SqlText.QuotedName(kind, c)))})";

    /// <summary>A primary key made as the source's: dropped by its name and added again in one statement where the database
    /// takes both, SQLite's a comment, as it changes no key in place.</summary>
    private static IReadOnlyList<string> PrimaryKey(DatabaseKind kind, string table, TableShape now, TableShape was)
    {
        string columns = string.Join(", ", now.PrimaryKey.Select(c => SqlText.QuotedName(kind, c)));
        string? named = was.PrimaryKeyName is { Length: > 0 } name ? SqlText.QuotedName(kind, name) : null;
        switch (kind)
        {
            case DatabaseKind.Sqlite:
                return [$"-- SQLite cannot change the primary key of a table in place: make the table again with it as the source has it"];
            case DatabaseKind.MySql:
                return [$"ALTER TABLE {table} {string.Join(", ", (was.PrimaryKey.Count > 0 ? ["DROP PRIMARY KEY"] : Array.Empty<string>()).Concat(now.PrimaryKey.Count > 0 ? [$"ADD PRIMARY KEY ({columns})"] : []))}"];
            case DatabaseKind.SqlServer:
                return [.. was.PrimaryKey.Count > 0 && named is not null ? [$"ALTER TABLE {table} DROP CONSTRAINT {named}"] : Array.Empty<string>(),
                    .. now.PrimaryKey.Count > 0 ? [$"ALTER TABLE {table} ADD {(now.PrimaryKeyName is { Length: > 0 } own ? $"CONSTRAINT {SqlText.QuotedName(kind, own)} " : string.Empty)}PRIMARY KEY ({columns})"] : Array.Empty<string>()];
            default:
                return [$"ALTER TABLE {table} {string.Join(", ", (was.PrimaryKey.Count > 0 && named is not null ? [$"DROP CONSTRAINT {named}"] : Array.Empty<string>()).Concat(now.PrimaryKey.Count > 0 ? [$"ADD PRIMARY KEY ({columns})"] : []))}"];
        }
    }

    private static IReadOnlyList<string> Altered(DatabaseKind kind, string table, ColumnShape was, ColumnShape now, TableShape there)
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
                // A default is a constraint of its own, which holds the column's type: dropped first, made again after.
                List<string> statements = [];
                bool typed = was.Type != now.Type || was.Nullable != now.Nullable;
                bool defaulted = typed || was.Default != now.Default;
                if (defaulted && was.Default is not null && there.DefaultNames.TryGetValue(was.Name, out string? constraint))
                {
                    statements.Add($"ALTER TABLE {table} DROP CONSTRAINT {SqlText.QuotedName(kind, constraint)}");
                }
                if (typed)
                {
                    statements.Add($"ALTER TABLE {table} ALTER COLUMN {column} {now.Type} {(now.Nullable ? "NULL" : "NOT NULL")}");
                }
                if (defaulted && now.Default is { } given)
                {
                    statements.Add($"ALTER TABLE {table} ADD DEFAULT {given} FOR {column}");
                }
                return statements;
            default:
                return [$"-- SQLite cannot change the column {now.Name} in place: make the table again with it as the source has it"];
        }
    }

    private static string AddKey(DatabaseKind kind, string table, ForeignKeyShape key, string to) =>
        kind == DatabaseKind.Sqlite
            ? $"-- SQLite cannot add the foreign key {key.Name} to a table: make the table again with it"
            : $"ALTER TABLE {table} ADD CONSTRAINT {SqlText.QuotedName(kind, key.Name)} {KeyClause(kind, key, to)}";

    private static string DropKey(DatabaseKind kind, string table, ForeignKeyShape key) => kind switch
    {
        DatabaseKind.Sqlite => $"-- SQLite cannot drop the foreign key {key.Name} from a table: make the table again without it",
        DatabaseKind.MySql => $"ALTER TABLE {table} DROP FOREIGN KEY {SqlText.QuotedName(kind, key.Name)}",
        _ => $"ALTER TABLE {table} DROP CONSTRAINT {SqlText.QuotedName(kind, key.Name)}",
    };

    /// <summary>An index made as the source has it: PostgreSQL's from its USING on, SQL Server's with its INCLUDE and WHERE,
    /// SQLite's whole statement where it holds more than its columns.</summary>
    private static string CreateIndex(DatabaseKind kind, string table, IndexShape index)
    {
        string made = $"CREATE {(index.Unique ? "UNIQUE " : string.Empty)}INDEX {SqlText.QuotedName(kind, index.Name)} ON {table}";
        return index.Definition.Length == 0 ? $"{made} ({string.Join(", ", index.Columns.Select(c => SqlText.QuotedName(kind, c)))})" : kind switch
        {
            DatabaseKind.Sqlite => index.Definition,
            DatabaseKind.SqlServer => $"{made} ({string.Join(", ", index.Columns.Select(c => SqlText.QuotedName(kind, c)))}) {index.Definition}",
            _ => $"{made} {index.Definition}",
        };
    }

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

    private static string MakeSequence(DatabaseKind kind, string name, SequenceShape sequence) =>
        $"CREATE SEQUENCE {name}{(sequence.Type.Length > 0 ? " AS " + sequence.Type : string.Empty)} START WITH {sequence.Start} {Bounds(kind, sequence)}";

    private static IReadOnlyList<string> AlterSequence(DatabaseKind kind, string name, SequenceShape was, SequenceShape now)
    {
        if (kind == DatabaseKind.SqlServer && (was.Type != now.Type || was.Start != now.Start))
        {
            return [$"-- The sequence {now.Name}'s type or start differs: SQL Server sets them only as a sequence is made, so it is made again",
                $"DROP SEQUENCE {name}", MakeSequence(kind, name, now)];
        }
        string type = kind == DatabaseKind.Postgres && was.Type != now.Type ? " AS " + now.Type : string.Empty;
        string start = kind != DatabaseKind.SqlServer && was.Start != now.Start ? $" START WITH {now.Start}" : string.Empty;
        return [$"ALTER SEQUENCE {name}{type}{start} {Bounds(kind, now)}"];
    }

    private static string Bounds(DatabaseKind kind, SequenceShape sequence) =>
        $"INCREMENT BY {sequence.Increment} MINVALUE {sequence.Minimum} MAXVALUE {sequence.Maximum} {(sequence.Cycle ? "CYCLE" : kind == DatabaseKind.MySql ? "NOCYCLE" : "NO CYCLE")}";

    /// <summary>A routine made as the source has it: PostgreSQL's statement replaces it, MySQL's is dropped and made again,
    /// SQL Server's is made or altered under the target's name.</summary>
    private static IReadOnlyList<string> MakeRoutine(DatabaseKind kind, string name, RoutineShape routine, bool exists)
    {
        string definition = routine.Definition.Trim().TrimEnd(';').Trim();
        return kind switch
        {
            DatabaseKind.SqlServer => [$"CREATE OR ALTER {routine.Kind} {name}{definition[NameEnd(definition, routine.Kind)..]}"],
            DatabaseKind.MySql => [.. exists ? [$"DROP {routine.Kind} {name}"] : Array.Empty<string>(), definition],
            _ => [definition],
        };
    }

    /// <summary>A trigger made as the source has it: PostgreSQL's statement replaces it, MySQL's is said again from its parts
    /// under the target's names, SQL Server's is made or altered, SQLite's dropped and made again.</summary>
    private static IReadOnlyList<string> MakeTrigger(DatabaseKind kind, string name, string table, TriggerShape trigger, bool exists)
    {
        string definition = trigger.Definition.Trim().TrimEnd(';').Trim();
        return kind switch
        {
            DatabaseKind.MySql => [.. exists ? [DropTrigger(kind, name, table)] : Array.Empty<string>(),
                $"CREATE TRIGGER {name} {definition.Replace(" FOR EACH ROW ", $" ON {table} FOR EACH ROW ", StringComparison.Ordinal)}"],
            DatabaseKind.SqlServer => [$"CREATE OR ALTER TRIGGER {name}{definition[NameEnd(definition, "TRIGGER")..]}"],
            DatabaseKind.Sqlite => [.. exists ? [DropTrigger(kind, name, table)] : Array.Empty<string>(), definition],
            _ => [definition.StartsWith("CREATE TRIGGER", StringComparison.OrdinalIgnoreCase) ? "CREATE OR REPLACE TRIGGER" + definition["CREATE TRIGGER".Length..] : definition],
        };
    }

    private static string DropTrigger(DatabaseKind kind, string name, string table) =>
        kind == DatabaseKind.Postgres ? $"DROP TRIGGER {name.Split('.')[^1]} ON {table}" : $"DROP TRIGGER {name}";

    /// <summary>What a routine's or trigger's statement says past its name, which compares it whatever its name is written
    /// as (SQL Server's and SQLite's keep the statement that made it); MySQL's trigger is its parts already.</summary>
    private static string Body(DatabaseKind kind, string definition, string word) =>
        Compared(kind, kind is DatabaseKind.SqlServer or DatabaseKind.Sqlite ? definition[NameEnd(definition, word)..] : definition);

    /// <summary>SQL as compared: its white space and comments one space, its words in one letter case, but its strings,
    /// quoted names and dollar quotes as written, as 'OPEN' and 'open' are two values.</summary>
    private static string Compared(DatabaseKind kind, string sql)
    {
        string trimmed = sql.Trim().TrimEnd(';').Trim();
        StringBuilder compared = new(trimmed.Length);
        for (int i = 0; i < trimmed.Length;)
        {
            int? past = SqlText.Skipped(trimmed, i, kind);
            if (char.IsWhiteSpace(trimmed[i]) || past is not null && trimmed[i] is '-' or '/' or '#')
            {
                if (compared.Length > 0 && compared[^1] != ' ')
                {
                    compared.Append(' ');
                }
                i = past ?? i + 1;
                continue;
            }
            if (past is { } end)
            {
                compared.Append(trimmed, i, end - i);
                i = end;
                continue;
            }
            compared.Append(char.ToUpperInvariant(trimmed[i++]));
        }
        return compared.ToString().TrimEnd();
    }

    /// <summary>Where a CREATE statement's object's name ends: past its kind's word and the name after it, a schema's too.</summary>
    private static int NameEnd(string statement, string word)
    {
        List<SqlText.Token> tokens = SqlText.Tokens(statement, DatabaseKind.SqlServer);
        int at = tokens.FindIndex(t => t.Word == word && !t.Quoted);
        if (at < 0 || at + 1 >= tokens.Count)
        {
            return 0;
        }
        int last = at + 1;
        while (last + 2 < tokens.Count && tokens[last + 1].Word == ".")
        {
            last += 2;
        }
        return tokens[last].End;
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

    /// <summary>A view's query as compared (<see cref="Compared"/>): SQL Server's and SQLite's after the statement's AS, as
    /// they keep the statement that made the view.</summary>
    private static string Normal(DatabaseKind kind, string definition) =>
        Compared(kind, kind is DatabaseKind.SqlServer or DatabaseKind.Sqlite ? definition[AsAt(definition)..] : definition);
}
