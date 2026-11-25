namespace Passenger.App.Database;

/// <summary>A table or view of a connection's database, with its columns and their types, as completion offers them.</summary>
public sealed record SqlTableInfo(string Schema, string Name, IReadOnlyList<(string Name, string Type)> Columns);

/// <summary>What a connection's database holds for completion: its tables and views with their columns, read once.</summary>
public sealed record SqlCatalog(IReadOnlyList<SqlTableInfo> Tables)
{
    /// <summary>The schema a name needs no prefix in: the one most of the tables stand in (public, dbo, the database's own).</summary>
    public string Usual { get; } = Tables.GroupBy(t => t.Schema).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? string.Empty;
}

/// <summary>What completion offers at a caret in a console's SQL, from the live schema: after a name and a dot, a table's
/// (or an alias's) columns or a schema's tables; else the columns of the tables the statement names, every table and
/// view, the schemas, and the database's keywords. Each name is written as the database reads it, quoted where it must be.</summary>
public static class SqlCompletions
{
    public enum Kind
    {
        Column,
        Table,
        Schema,
        Keyword,
    }

    public readonly record struct Candidate(string Label, string Insert, Kind Kind, string Detail);

    private static readonly string[] Common =
    [
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "NULL", "IS", "IN", "LIKE", "BETWEEN", "EXISTS", "JOIN", "INNER", "LEFT", "RIGHT", "FULL",
        "OUTER", "CROSS", "ON", "AS", "GROUP BY", "HAVING", "ORDER BY", "ASC", "DESC", "DISTINCT", "UNION", "ALL", "EXCEPT", "INTERSECT", "CASE",
        "WHEN", "THEN", "ELSE", "END", "WITH", "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "CREATE TABLE", "CREATE VIEW",
        "CREATE INDEX", "ALTER TABLE", "DROP TABLE", "ADD", "COLUMN", "PRIMARY KEY", "FOREIGN KEY", "REFERENCES", "UNIQUE", "DEFAULT", "CHECK",
        "BEGIN", "COMMIT", "ROLLBACK", "COUNT", "SUM", "AVG", "MIN", "MAX", "CAST", "COALESCE",
    ];

    /// <summary>Each database's own words beyond those all of them read.</summary>
    public static IReadOnlyList<string> Keywords(DatabaseKind kind) => [.. Common, .. kind switch
    {
        DatabaseKind.SqlServer => (string[])["TOP", "OFFSET", "FETCH NEXT", "ROWS ONLY", "DECLARE", "EXEC", "OUTPUT", "MERGE", "IDENTITY", "NVARCHAR", "GO"],
        DatabaseKind.Postgres => ["LIMIT", "OFFSET", "RETURNING", "ILIKE", "ON CONFLICT", "DO NOTHING", "SERIAL", "JSONB", "EXPLAIN", "ANALYZE"],
        DatabaseKind.MySql => ["LIMIT", "OFFSET", "AUTO_INCREMENT", "SHOW TABLES", "DESCRIBE", "REPLACE INTO", "ENGINE", "EXPLAIN"],
        DatabaseKind.DuckDb => ["LIMIT", "OFFSET", "QUALIFY", "PIVOT", "UNPIVOT", "EXCLUDE", "REPLACE", "SUMMARIZE", "DESCRIBE", "ATTACH", "COPY", "EXPLAIN",
            "read_parquet", "read_csv", "read_json", "GROUP BY ALL", "ORDER BY ALL", "ASOF JOIN"],
        DatabaseKind.ClickHouse => ["LIMIT", "OFFSET", "FINAL", "SAMPLE", "PREWHERE", "ARRAY JOIN", "LIMIT BY", "WITH TOTALS", "SETTINGS", "FORMAT",
            "ENGINE", "MergeTree", "PARTITION BY", "TTL", "SHOW TABLES", "DESCRIBE", "EXPLAIN", "OPTIMIZE TABLE", "ALTER TABLE"],
        DatabaseKind.Snowflake => ["LIMIT", "OFFSET", "QUALIFY", "ILIKE", "FLATTEN", "LATERAL", "PIVOT", "UNPIVOT", "MATCH_RECOGNIZE", "SAMPLE",
            "SHOW TABLES", "DESCRIBE", "USE WAREHOUSE", "USE ROLE", "CLONE", "VARIANT", "EXPLAIN"],
        _ => ["LIMIT", "OFFSET", "AUTOINCREMENT", "PRAGMA", "REPLACE INTO", "GLOB", "WITHOUT ROWID", "EXPLAIN QUERY PLAN"],
    }];

    /// <summary>Where the name being typed at <paramref name="caret"/> starts, and what may stand there.</summary>
    public static (int Start, IReadOnlyList<Candidate> Items) At(string text, int caret, DatabaseKind kind, SqlCatalog? catalog)
    {
        int start = caret;
        while (start > 0 && IsNamePart(text[start - 1]))
        {
            start--;
        }
        List<Candidate> items = [];
        if (catalog is null)
        {
            items.AddRange(Keywords(kind).Select(k => new Candidate(k, k, Kind.Keyword, "keyword")));
            return (start, items);
        }
        Dictionary<string, SqlTableInfo> named = Named(text, caret, kind, catalog);
        if (start > 0 && text[start - 1] == '.' && QualifierBefore(text, start - 1) is { } qualifier)
        {
            if (named.TryGetValue(qualifier, out SqlTableInfo? table) || Find(catalog, qualifier) is { } found && (table = found) is not null)
            {
                items.AddRange(table.Columns.Select(c => new Candidate(c.Name, Quoted(c.Name, kind), Kind.Column, c.Type)));
            }
            else
            {
                items.AddRange(catalog.Tables.Where(t => string.Equals(t.Schema, qualifier, StringComparison.OrdinalIgnoreCase))
                    .Select(t => new Candidate(t.Name, Quoted(t.Name, kind), Kind.Table, t.Schema)));
            }
            return (start, items);
        }
        foreach (SqlTableInfo table in named.Values.Distinct())
        {
            items.AddRange(table.Columns.Select(c => new Candidate(c.Name, Quoted(c.Name, kind), Kind.Column, $"{table.Name}, {c.Type}")));
        }
        items.AddRange(catalog.Tables.Select(t => new Candidate(t.Name, t.Schema == catalog.Usual ? Quoted(t.Name, kind) : $"{Quoted(t.Schema, kind)}.{Quoted(t.Name, kind)}",
            Kind.Table, t.Schema.Length > 0 ? t.Schema : "table")));
        items.AddRange(catalog.Tables.Select(t => t.Schema).Where(s => s.Length > 0 && s != catalog.Usual).Distinct()
            .Select(s => new Candidate(s, Quoted(s, kind), Kind.Schema, "schema")));
        items.AddRange(Keywords(kind).Select(k => new Candidate(k, k, Kind.Keyword, "keyword")));
        return (start, [.. items.DistinctBy(i => (i.Label, i.Kind))]);
    }

    /// <summary>A name as the database reads it: as written where it is a plain name (lower case alone for PostgreSQL, which
    /// folds the rest, upper case alone for Snowflake, which folds it the other way), else in the database's quotes.</summary>
    public static string Quoted(string name, DatabaseKind kind)
    {
        bool plain = name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_')
            && (kind != DatabaseKind.Postgres || !name.Any(char.IsUpper))
            && (kind != DatabaseKind.Snowflake || !name.Any(char.IsLower))
            && !Common.Contains(name, StringComparer.OrdinalIgnoreCase);
        return plain ? name : SqlText.QuotedName(kind, name);
    }

    private static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>The name before a dot, unquoted: plain, or in any of the databases' quotes.</summary>
    private static string? QualifierBefore(string text, int dot)
    {
        int end = dot;
        if (end > 0 && text[end - 1] is '"' or ']' or '`')
        {
            char close = text[end - 1];
            char open = close == ']' ? '[' : close;
            int begin = text.LastIndexOf(open, end - 2);
            return begin < 0 ? null : text[(begin + 1)..(end - 1)];
        }
        int start = end;
        while (start > 0 && IsNamePart(text[start - 1]))
        {
            start--;
        }
        return start == end ? null : text[start..end];
    }

    private static SqlTableInfo? Find(SqlCatalog catalog, string name) =>
        catalog.Tables.FirstOrDefault(t => t.Name == name) ?? catalog.Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The tables the statement at the caret names after FROM, JOIN, UPDATE or INTO, by their names and aliases.</summary>
    private static Dictionary<string, SqlTableInfo> Named(string text, int caret, DatabaseKind kind, SqlCatalog catalog)
    {
        Dictionary<string, SqlTableInfo> named = new(StringComparer.OrdinalIgnoreCase);
        (int Start, int End) statement = SqlText.At(text, kind, caret) is { } around && caret >= around.Start ? around : (0, text.Length);
        string within = text[statement.Start..Math.Max(statement.Start, Math.Min(text.Length, Math.Max(statement.End, caret)))];
        List<SqlText.Token> tokens = SqlText.Tokens(within, kind);
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Word is not ("FROM" or "JOIN" or "UPDATE" or "INTO" or ","))
            {
                continue;
            }
            int at = i + 1;
            if (at >= tokens.Count || NameOf(within, tokens[at]) is not { } name)
            {
                continue;
            }
            while (at + 2 < tokens.Count && tokens[at + 1].Word == "." && NameOf(within, tokens[at + 2]) is { } part)
            {
                name = part;
                at += 2;
            }
            if (Find(catalog, name) is not { } table)
            {
                continue;
            }
            named[name] = table;
            int alias = at + 1 < tokens.Count && tokens[at + 1].Word == "AS" ? at + 2 : at + 1;
            if (alias < tokens.Count && NameOf(within, tokens[alias]) is { } given && (tokens[alias].Quoted || !Reserved(tokens[alias].Word)))
            {
                named[given] = table;
            }
        }
        return named;
    }

    /// <summary>Whether a word is the statement's own rather than an alias's: a keyword, or a join's or clause's word.</summary>
    private static bool Reserved(string word) => Common.Any(k => k.Split(' ')[0] == word)
        || word is "LEFT" or "RIGHT" or "INNER" or "OUTER" or "CROSS" or "FULL" or "WHERE" or "ON" or "SET" or "VALUES" or "ORDER" or "GROUP" or "LIMIT";

    /// <summary>A token's name as written, a quoted one's within its quotes; null for a symbol.</summary>
    private static string? NameOf(string text, SqlText.Token token)
    {
        if (token.Quoted)
        {
            return token.Word;
        }
        if (token.Word.Length > 0 && (char.IsLetter(token.Word[0]) || token.Word[0] == '_'))
        {
            return text[token.Start..token.End];
        }
        return null;
    }
}
