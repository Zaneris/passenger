namespace Passenger.App.Database;

/// <summary>A name a console's SQL writes that its connection's schema does not hold, where it stands.</summary>
public readonly record struct SqlProblem(int Start, int End, string Message);

/// <summary>A console's SQL read against its connection's schema as last read (<see cref="SqlCatalog"/>): each table or
/// view it names after FROM, JOIN, UPDATE or an INSERT's INTO, and each column it names after a table's name or alias and
/// a dot, or alone where the statement's tables are all known (a SELECT's item, a condition's left side, a SET's target, an
/// ORDER or GROUP BY's item, an INSERT's column list). The SQL's earlier statements are read as the schema they leave: a
/// table made with its columns, a column added, renamed or dropped, a table renamed or dropped. Names it can't be sure of are
/// passed over: a CTE's, a view the SQL makes, a table made after, a derived table's, a function's, a variable's, another
/// database's or schema's the schema read holds none of, and the system's own; each database's case read as it reads it
/// (PostgreSQL folding a name not quoted to lower case).</summary>
public static class SqlChecks
{
    /// <summary>The words that end a table's name where an alias would stand.</summary>
    private static readonly HashSet<string> NoAlias =
    [
        "WHERE", "ON", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS", "FULL", "NATURAL", "SET", "GROUP", "ORDER", "LIMIT", "USING", "UNION", "WINDOW",
        "HAVING", "RETURNING", "VALUES", "SELECT", "OFFSET", "FETCH", "FOR", "WITH", "EXCEPT", "INTERSECT", "TABLESAMPLE", "DEFAULT", "OUTPUT", "LATERAL",
    ];

    /// <summary>Words that stand alone where a column could, none of them one.</summary>
    private static readonly HashSet<string> NoColumn =
    [
        "NULL", "TRUE", "FALSE", "DEFAULT", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "SESSION_USER", "USER", "LOCALTIME",
        "LOCALTIMESTAMP", "ROWID", "_ROWID_", "OID", "ROWNUM", "NOT", "DISTINCT", "ALL", "CASE", "EXISTS", "INTERVAL", "DATE", "TIME", "TIMESTAMP", "ARRAY", "ROW",
    ];

    public static List<SqlProblem> Of(string text, DatabaseKind kind, SqlCatalog catalog)
    {
        List<SqlProblem> found = [];
        // A document's fields are what each document holds, never a schema's, and Redis's keys have none.
        if (!DatabaseKinds.SpeaksSql(kind))
        {
            return found;
        }
        HashSet<string> made = Made(text, kind);
        SqlCatalog now = catalog;
        foreach ((int start, int end) in SqlText.Statements(text, kind))
        {
            string statement = text[start..end];
            foreach (SqlProblem problem in Statement(statement, kind, now, made))
            {
                found.Add(problem with { Start = problem.Start + start, End = problem.End + start });
            }
            now = After(statement, kind, now);
        }
        return [.. found.OrderBy(p => p.Start)];
    }

    /// <summary>The schema as a statement leaves it: a table made with its columns (those of its list that are no key or
    /// constraint), a column added, renamed or dropped, a table renamed or dropped; any other statement leaves it as it was.</summary>
    private static SqlCatalog After(string sql, DatabaseKind kind, SqlCatalog catalog)
    {
        List<SqlText.Token> tokens = SqlText.Tokens(sql, kind);
        if (tokens.Count < 3 || tokens[0].Quoted)
        {
            return catalog;
        }
        List<SqlTableInfo> tables = [.. catalog.Tables];
        int at = 1;
        while (at < tokens.Count && !tokens[at].Quoted && tokens[at].Word is "OR" or "REPLACE" or "TEMP" or "TEMPORARY" or "UNLOGGED" or "GLOBAL" or "LOCAL")
        {
            at++;
        }
        if (at >= tokens.Count || tokens[at].Word != "TABLE" || tokens[0].Word is not ("CREATE" or "ALTER" or "DROP"))
        {
            return catalog;
        }
        at++;
        while (at < tokens.Count && !tokens[at].Quoted && tokens[at].Word is "IF" or "NOT" or "EXISTS" or "ONLY")
        {
            at++;
        }
        if (Named(sql, tokens, kind, ref at, catalog) is not { } name)
        {
            return catalog;
        }
        int index = tables.FindIndex(t => Same(name.Schema, t.Schema, kind) && Same(name.Name, t.Name, kind));
        switch (tokens[0].Word)
        {
            case "CREATE" when at < tokens.Count && tokens[at].Word == "(":
                int depth = tokens[at].Depth;
                List<(string Name, string Type)> columns = [];
                for (int item = at + 1; item < tokens.Count && tokens[item].Depth >= depth; item++)
                {
                    bool starts = tokens[item - 1].Word == "(" && item - 1 == at || tokens[item - 1].Word == "," && tokens[item - 1].Depth == depth;
                    if (starts && IsName(sql, tokens, item) && (tokens[item].Quoted || tokens[item].Word is not ("PRIMARY" or "FOREIGN" or "UNIQUE" or "CHECK"
                        or "CONSTRAINT" or "KEY" or "INDEX" or "EXCLUDE" or "PERIOD" or "FULLTEXT" or "SPATIAL")))
                    {
                        columns.Add((Folded(sql, tokens[item], kind, keep: true), item + 1 < tokens.Count ? tokens[item + 1].Word.ToLowerInvariant() : string.Empty));
                    }
                }
                if (index >= 0)
                {
                    tables.RemoveAt(index);
                }
                tables.Add(new SqlTableInfo(name.Schema, name.Name, columns));
                break;
            case "DROP":
                if (index >= 0)
                {
                    tables.RemoveAt(index);
                }
                break;
            case "ALTER" when index >= 0:
                SqlTableInfo table = tables[index];
                List<(string Name, string Type)> now = [.. table.Columns];
                string renamed = table.Name;
                for (int t = at; t < tokens.Count; t++)
                {
                    if (tokens[t].Depth != tokens[at].Depth || tokens[t].Quoted || t != at && tokens[t - 1].Word != ",")
                    {
                        continue;
                    }
                    int c = t + 1;
                    while (c < tokens.Count && !tokens[c].Quoted && tokens[c].Word is "COLUMN" or "IF" or "NOT" or "EXISTS")
                    {
                        c++;
                    }
                    bool column = c < tokens.Count && IsName(sql, tokens, c) && (tokens[c].Quoted || tokens[c].Word is not ("CONSTRAINT" or "PRIMARY" or "FOREIGN" or "UNIQUE"
                        or "CHECK" or "INDEX" or "KEY" or "DEFAULT" or "TO"));
                    if (tokens[t].Word == "ADD" && column)
                    {
                        now.Add((Folded(sql, tokens[c], kind, keep: true), c + 1 < tokens.Count ? tokens[c + 1].Word.ToLowerInvariant() : string.Empty));
                    }
                    else if (tokens[t].Word == "DROP" && column)
                    {
                        now.RemoveAll(o => Same(sql, tokens[c], o.Name, kind));
                    }
                    else if (tokens[t].Word == "RENAME" && column && c + 2 < tokens.Count && tokens[c + 1].Word == "TO" && IsName(sql, tokens, c + 2))
                    {
                        int was = now.FindIndex(o => Same(sql, tokens[c], o.Name, kind));
                        if (was >= 0)
                        {
                            now[was] = (Folded(sql, tokens[c + 2], kind, keep: true), now[was].Type);
                        }
                    }
                    else if (tokens[t].Word == "RENAME" && c < tokens.Count && tokens[c].Word == "TO" && IsName(sql, tokens, c + 1))
                    {
                        renamed = Folded(sql, tokens[c + 1], kind, keep: true);
                    }
                }
                tables[index] = new SqlTableInfo(table.Schema, renamed, now);
                break;
        }
        return new SqlCatalog(tables);
    }

    /// <summary>A table's schema and name written from <paramref name="at"/>, moving past them; the catalog's usual schema
    /// where it names none.</summary>
    private static (string Schema, string Name)? Named(string sql, List<SqlText.Token> tokens, DatabaseKind kind, ref int at, SqlCatalog catalog)
    {
        if (at >= tokens.Count || !IsName(sql, tokens, at))
        {
            return null;
        }
        string first = Folded(sql, tokens[at], kind, keep: true);
        if (at + 2 < tokens.Count && tokens[at + 1].Word == "." && IsName(sql, tokens, at + 2))
        {
            string second = Folded(sql, tokens[at + 2], kind, keep: true);
            at += 3;
            return (first, second);
        }
        at++;
        return (catalog.Usual ?? string.Empty, first);
    }

    private static bool Same(string one, string other, DatabaseKind kind) =>
        string.Equals(one, other, DatabaseKinds.SpeaksPostgres(kind) || kind == DatabaseKind.ClickHouse ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>The tables and views the SQL makes (CREATE TABLE, VIEW), by their names folded as the database folds them.</summary>
    private static HashSet<string> Made(string text, DatabaseKind kind)
    {
        HashSet<string> made = [];
        List<SqlText.Token> tokens = SqlText.Tokens(text, kind);
        for (int t = 0; t + 2 < tokens.Count; t++)
        {
            if (tokens[t] is { Word: "CREATE", Quoted: false })
            {
                int at = t + 1;
                while (at < tokens.Count && tokens[at].Word is "OR" or "REPLACE" or "TEMP" or "TEMPORARY" or "UNLOGGED" or "MATERIALIZED" or "GLOBAL" or "LOCAL")
                {
                    at++;
                }
                if (at + 1 < tokens.Count && tokens[at].Word is "TABLE" or "VIEW")
                {
                    at++;
                    while (at + 2 < tokens.Count && tokens[at].Word is "IF" or "NOT" or "EXISTS")
                    {
                        at++;
                    }
                    while (at + 2 < tokens.Count && tokens[at + 1].Word == ".")
                    {
                        at += 2;
                    }
                    made.Add(Folded(text, tokens[at], kind));
                }
            }
        }
        return made;
    }

    private sealed record Reference(int Token, SqlTableInfo? Table);

    private static IEnumerable<SqlProblem> Statement(string sql, DatabaseKind kind, SqlCatalog catalog, HashSet<string> made)
    {
        List<SqlText.Token> tokens = SqlText.Tokens(sql, kind);
        if (tokens.Count == 0 || tokens.Take(8).Any(t => t.Word is "PROCEDURE" or "PROC" or "FUNCTION" or "TRIGGER" or "EVENT") && tokens[0].Word is "CREATE" or "ALTER")
        {
            yield break;
        }
        HashSet<string> named = [.. Ctes(sql, tokens, kind)];
        Dictionary<string, SqlTableInfo?> aliases = new(StringComparer.Ordinal);
        List<SqlTableInfo> tables = [];
        HashSet<int> consumed = [];
        bool unsure = false;
        bool[] query = QueryDepths(tokens);
        string first = tokens[0].Quoted ? string.Empty : tokens[0].Word;
        int fromDepth = -1;
        for (int t = 0; t < tokens.Count; t++)
        {
            SqlText.Token token = tokens[t];
            string word = token.Quoted ? string.Empty : token.Word;
            if (fromDepth >= 0 && token.Depth == fromDepth && word is "WHERE" or "GROUP" or "ORDER" or "HAVING" or "LIMIT" or "UNION" or "SET" or "RETURNING" or "WINDOW" or ";" or "OFFSET")
            {
                fromDepth = -1;
            }
            bool refers = word switch
            {
                "FROM" => query[t] && (t == 0 || tokens[t - 1].Word != "DISTINCT"),
                "JOIN" => query[t],
                "UPDATE" => t == 0 || tokens[t - 1].Word == ")",
                "INTO" => first is "INSERT" or "REPLACE" or "MERGE",
                "," => fromDepth >= 0 && token.Depth == fromDepth,
                _ => false,
            };
            if (word == "FROM" && query[t])
            {
                fromDepth = token.Depth;
            }
            if (!refers || t + 1 >= tokens.Count)
            {
                continue;
            }
            int at = t + 1;
            while (at < tokens.Count && !tokens[at].Quoted && tokens[at].Word is "ONLY" or "LATERAL")
            {
                at++;
            }
            if (at >= tokens.Count || !IsName(sql, tokens, at))
            {
                unsure = true;
                continue;
            }
            List<int> parts = [at];
            while (parts[^1] + 2 < tokens.Count && tokens[parts[^1] + 1].Word == "." && IsName(sql, tokens, parts[^1] + 2))
            {
                parts.Add(parts[^1] + 2);
            }
            int last = parts[^1];
            consumed.UnionWith(Enumerable.Range(at, last - at + 1));
            if (last + 1 < tokens.Count && tokens[last + 1].Word == "(")
            {
                // A function's rows, or a table's column list after INTO.
                if (word != "INTO")
                {
                    unsure = true;
                    continue;
                }
            }
            SqlTableInfo? table = null;
            string name = Folded(sql, tokens[last], kind);
            if (parts.Count == 1 && named.Contains(name) || parts.Count > 2 || Systems(name))
            {
                unsure = true;
            }
            else if (parts.Count == 2 && !catalog.Tables.Any(c => Same(sql, tokens[parts[0]], c.Schema, kind)))
            {
                unsure = true;
            }
            else if (catalog.Tables.FirstOrDefault(c => Same(sql, tokens[last], c.Name, kind) && (parts.Count == 1 || Same(sql, tokens[parts[0]], c.Schema, kind))) is not { } known
                && parts.Count == 1 && made.Contains(name))
            {
                unsure = true;
            }
            else
            {
                table = catalog.Tables.FirstOrDefault(c => Same(sql, tokens[last], c.Name, kind) && (parts.Count == 1 || Same(sql, tokens[parts[0]], c.Schema, kind)));
                if (table is null)
                {
                    yield return new SqlProblem(tokens[at].Start, tokens[last].End, $"Unknown table or view {Written(sql, tokens, at, last)}");
                    unsure = true;
                }
                else
                {
                    tables.Add(table);
                    if (word == "INTO" && last + 1 < tokens.Count && tokens[last + 1].Word == "(")
                    {
                        // An INSERT's columns, each the table's.
                        int depth = tokens[last + 1].Depth;
                        for (int c = last + 2; c < tokens.Count && !(tokens[c].Word == ")" && tokens[c].Depth == depth); c++)
                        {
                            consumed.Add(c);
                            if (tokens[c].Depth == depth && IsName(sql, tokens, c) && !table.Columns.Any(column => Same(sql, tokens[c], column.Name, kind)))
                            {
                                yield return new SqlProblem(tokens[c].Start, tokens[c].End, $"Unknown column {Written(sql, tokens, c, c)} of {table.Name}");
                            }
                        }
                    }
                }
            }
            aliases[name] = table;
            int alias = last + 1 < tokens.Count && tokens[last + 1].Word == "AS" ? last + 2 : last + 1;
            if (alias < tokens.Count && IsName(sql, tokens, alias) && (tokens[alias].Quoted || !NoAlias.Contains(tokens[alias].Word)) && word != "INTO")
            {
                aliases[Folded(sql, tokens[alias], kind)] = table;
                consumed.Add(alias);
            }
        }
        HashSet<string> said = [.. Enumerable.Range(1, Math.Max(0, tokens.Count - 1)).Where(t => tokens[t - 1].Word == "AS" && IsName(sql, tokens, t)).Select(t => Folded(sql, tokens[t], kind))];
        for (int t = 0; t < tokens.Count; t++)
        {
            if (consumed.Contains(t) || !IsName(sql, tokens, t) || t > 0 && tokens[t - 1].Word == "." || Prefixed(sql, tokens, t))
            {
                continue;
            }
            if (t + 2 < tokens.Count && tokens[t + 1].Word == "." && IsName(sql, tokens, t + 2) && (t + 3 >= tokens.Count || tokens[t + 3].Word is not ("." or "(")))
            {
                if (aliases.TryGetValue(Folded(sql, tokens[t], kind), out SqlTableInfo? owner) && owner is not null
                    && !owner.Columns.Any(c => Same(sql, tokens[t + 2], c.Name, kind)))
                {
                    yield return new SqlProblem(tokens[t + 2].Start, tokens[t + 2].End, $"Unknown column {Written(sql, tokens, t + 2, t + 2)} of {owner.Name}");
                }
                t += 2;
                continue;
            }
            if (unsure || tables.Count == 0 || !Alone(tokens, t) || !tokens[t].Quoted && NoColumn.Contains(tokens[t].Word)
                || SqlCompletions.Keywords(kind).Contains(tokens[t].Word) && !tokens[t].Quoted || said.Contains(Folded(sql, tokens[t], kind))
                || aliases.ContainsKey(Folded(sql, tokens[t], kind)))
            {
                continue;
            }
            if (!tables.Any(table => table.Columns.Any(c => Same(sql, tokens[t], c.Name, kind))))
            {
                yield return new SqlProblem(tokens[t].Start, tokens[t].End,
                    $"Unknown column {Written(sql, tokens, t, t)}: {(tables.Count == 1 ? $"{tables[0].Name} has" : "none of the tables here has")} no column of this name");
            }
        }
    }

    /// <summary>Whether a name stands where only a column can: a SELECT's item alone, a condition's or a SET's left side, or
    /// an ORDER or GROUP BY's item alone.</summary>
    private static bool Alone(List<SqlText.Token> tokens, int t)
    {
        string before = tokens[t - 1 < 0 ? 0 : t - 1].Word;
        string after = t + 1 < tokens.Count ? tokens[t + 1].Word : ";";
        if (t == 0 || after is "(" or ".")
        {
            return false;
        }
        bool listed = before is "SELECT" or "," or "BY" or "DISTINCT" && after is "," or "FROM" or ";" or ")" or "ASC" or "DESC" or "LIMIT" or "NULLS" or "HAVING" or "OFFSET"
            && Clause(tokens, t) is "SELECT" or "BY";
        bool compared = before is "WHERE" or "AND" or "OR" or "ON" or "NOT" or "HAVING" or "(" or "SET" or "," && after is "=" or "<" or ">" or "!" or "LIKE" or "ILIKE" or "IN" or "IS" or "BETWEEN"
            && Clause(tokens, t) is "WHERE" or "ON" or "HAVING" or "SET" or "JOIN";
        return listed || compared;
    }

    /// <summary>The clause a token stands in: the last clause word before it at its depth, a condition's bracket going on
    /// in its condition's clause; none in another bracket's (a call's, an IN's list).</summary>
    private static string Clause(List<SqlText.Token> tokens, int t)
    {
        int depth = tokens[t].Depth;
        for (int i = t - 1; i >= 0; i--)
        {
            SqlText.Token token = tokens[i];
            if (token.Quoted || token.Depth > depth || token.Word == "(" && token.Depth == depth)
            {
                continue;
            }
            if (token.Depth < depth)
            {
                if (token.Word is not ("WHERE" or "AND" or "OR" or "NOT" or "ON" or "HAVING" or "("))
                {
                    return string.Empty;
                }
                depth = token.Depth;
            }
            if (token.Word is "SELECT" or "WHERE" or "ON" or "HAVING" or "SET" or "BY" or "FROM" or "JOIN" or "VALUES" or "RETURNING" or "LIMIT" or "INTO" or "USING")
            {
                return token.Word;
            }
        }
        return string.Empty;
    }

    /// <summary>For each token, whether a FROM there would read a query's tables: outside brackets, or in one a query opens.</summary>
    private static bool[] QueryDepths(List<SqlText.Token> tokens)
    {
        bool[] query = new bool[tokens.Count];
        Stack<bool> open = new([true]);
        for (int t = 0; t < tokens.Count; t++)
        {
            query[t] = open.Peek();
            if (tokens[t].Word == "(" && !tokens[t].Quoted)
            {
                open.Push(t + 1 < tokens.Count && tokens[t + 1].Word is "SELECT" or "WITH" or "VALUES");
            }
            else if (tokens[t].Word == ")" && !tokens[t].Quoted && open.Count > 1)
            {
                open.Pop();
            }
        }
        return query;
    }

    /// <summary>The names a statement's WITH gives its queries.</summary>
    private static IEnumerable<string> Ctes(string sql, List<SqlText.Token> tokens, DatabaseKind kind)
    {
        for (int t = 1; t < tokens.Count; t++)
        {
            if (tokens[t].Word == "AS" && t + 1 < tokens.Count && tokens[t + 1].Word == "(" && IsName(sql, tokens, t - 1)
                || tokens[t].Word == "(" && t + 1 < tokens.Count && IsName(sql, tokens, t - 1) && tokens.Take(t).Any(p => p.Word == "WITH") && tokens[t - 1].Depth == 0
                    && t >= 2 && tokens[t - 2].Word is "WITH" or "RECURSIVE" or ",")
            {
                yield return Folded(sql, tokens[t - 1], kind);
            }
        }
    }

    private static bool IsName(string sql, List<SqlText.Token> tokens, int t) => t < tokens.Count && (tokens[t].Quoted
        || tokens[t].Word.Length > 0 && (char.IsLetter(tokens[t].Word[0]) || tokens[t].Word[0] == '_') && sql[tokens[t].Start] is not '$');

    /// <summary>Whether a name is a variable's or parameter's (@x, :x, $x) or a SQL Server temporary table's (#x).</summary>
    private static bool Prefixed(string sql, List<SqlText.Token> tokens, int t) =>
        t > 0 && tokens[t - 1].End == tokens[t].Start && tokens[t - 1].Word is "@" or ":" or "$" or "#" or "?";

    /// <summary>The system's own tables and views, which a schema's read leaves out.</summary>
    private static bool Systems(string name) => name.StartsWith("pg_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("pragma_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("sys", StringComparison.OrdinalIgnoreCase)
        || name.Equals("dual", StringComparison.OrdinalIgnoreCase) || name.StartsWith("information_schema", StringComparison.OrdinalIgnoreCase);

    /// <summary>A name as the database keeps it: a quoted one as written, one not quoted folded as the database folds it (none
    /// by ClickHouse, whose names are as written), or with <paramref name="keep"/> as written where the database keeps a
    /// name's case as written (all but PostgreSQL).</summary>
    private static string Folded(string sql, SqlText.Token token, DatabaseKind kind, bool keep = false) =>
        token.Quoted ? token.Word : DatabaseKinds.SpeaksPostgres(kind) ? sql[token.Start..token.End].ToLowerInvariant()
            : keep || kind == DatabaseKind.ClickHouse ? sql[token.Start..token.End] : sql[token.Start..token.End].ToUpperInvariant();

    /// <summary>Whether a name written is the one the schema holds, as the database compares them.</summary>
    private static bool Same(string sql, SqlText.Token token, string actual, DatabaseKind kind) => DatabaseKinds.SpeaksPostgres(kind) || kind == DatabaseKind.ClickHouse
        ? Folded(sql, token, kind) == actual
        : string.Equals(token.Quoted ? token.Word : sql[token.Start..token.End], actual, StringComparison.OrdinalIgnoreCase);

    private static string Written(string sql, List<SqlText.Token> tokens, int first, int last) => sql[tokens[first].Start..tokens[last].End];
}
