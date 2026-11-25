namespace Passenger.App.Database;

/// <summary>What a statement does to its database, from least to most: reads, sets the session's own (or runs a
/// batch's flow), begins or ends a transaction, changes rows (or may: a routine called, a statement of a kind not known),
/// or changes the schema, rights or the server's settings.</summary>
public enum SqlEffect
{
    Reads,
    Session,
    Transaction,
    ChangesData,
    ChangesSchema,
}

/// <summary>A transaction statement a console in manual commit takes as its own.</summary>
public enum SqlTransaction
{
    Begin,
    Commit,
    Rollback,
}

/// <summary>SQL read as each database's lexer reads it, far enough to tell its statements apart: strings, quoted names,
/// comments and PostgreSQL's dollar quotes are passed over whole, so a semicolon inside one ends nothing. A routine's body
/// (CREATE PROCEDURE, FUNCTION or TRIGGER, its BEGIN … END) is one statement, and SQL Server's GO lines end batches.</summary>
public static class SqlText
{
    /// <summary>A word (upper-cased), a symbol or a number outside strings and comments, with where it stands and its brackets'
    /// depth; or a quoted name, its words as written inside its quotes (<see cref="Quoted"/>).</summary>
    public readonly record struct Token(string Word, int Start, int End, int Depth, bool Quoted = false);

    /// <summary>The words and symbols of <paramref name="sql"/>, upper-cased, outside its strings, quoted names and comments.</summary>
    public static List<Token> Tokens(string sql, DatabaseKind kind)
    {
        List<Token> tokens = [];
        int depth = 0;
        for (int i = 0; i < sql.Length;)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (Skipped(sql, i, kind) is { } past)
            {
                if (QuotesName(c, kind) && past - i >= 2)
                {
                    char close = c == '[' ? ']' : c;
                    tokens.Add(new Token(sql[(i + 1)..(past - 1)].Replace($"{close}{close}", close.ToString(), StringComparison.Ordinal), i, past, depth, Quoted: true));
                }
                i = past;
                continue;
            }
            if (c == '(')
            {
                depth++;
            }
            int start = i++;
            if (char.IsLetter(c) || c == '_')
            {
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$'))
                {
                    i++;
                }
            }
            tokens.Add(new Token(sql[start..i].ToUpperInvariant(), start, i, depth));
            if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
        }
        return tokens;
    }

    /// <summary>Where a string, quoted name or comment starting at <paramref name="at"/> ends, or null where none starts there.</summary>
    public static int? Skipped(string sql, int at, DatabaseKind kind)
    {
        if (kind == DatabaseKind.MongoDb)
        {
            return DocumentShell.Skipped(sql, at);
        }
        if (kind == DatabaseKind.Redis)
        {
            return RedisCommands.Skipped(sql, at);
        }
        char c = sql[at];
        char next = at + 1 < sql.Length ? sql[at + 1] : '\0';
        if (c == '-' && next == '-' || c == '#' && kind is DatabaseKind.MySql or DatabaseKind.ClickHouse || c == '/' && next == '/' && kind == DatabaseKind.Snowflake)
        {
            int end = sql.IndexOfAny(['\r', '\n'], at);
            return end < 0 ? sql.Length : end;
        }
        if (c == '/' && next == '*')
        {
            // PostgreSQL, SQL Server and DuckDB nest block comments; the others end one at its first close.
            bool nests = kind is DatabaseKind.Postgres or DatabaseKind.SqlServer or DatabaseKind.DuckDb;
            int open = 1;
            int i = at + 2;
            while (i < sql.Length && open > 0)
            {
                if (nests && sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                {
                    open++;
                    i += 2;
                }
                else if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
                {
                    open--;
                    i += 2;
                }
                else
                {
                    i++;
                }
            }
            return i;
        }
        if (c == '$' && kind is DatabaseKind.Postgres or DatabaseKind.DuckDb or DatabaseKind.Snowflake && DollarTag(sql, at) is { } tag)
        {
            int end = sql.IndexOf(tag, at + tag.Length, StringComparison.Ordinal);
            return end < 0 ? sql.Length : end + tag.Length;
        }
        bool escapes = kind is DatabaseKind.MySql or DatabaseKind.ClickHouse or DatabaseKind.Snowflake or DatabaseKind.CosmosDb || kind == DatabaseKind.Postgres && at > 0 && sql[at - 1] is 'E' or 'e'
            && (at < 2 || !char.IsLetterOrDigit(sql[at - 2]));
        return c switch
        {
            '\'' => Quoted(sql, at, '\'', escapes),
            '"' => Quoted(sql, at, '"', kind is DatabaseKind.MySql or DatabaseKind.ClickHouse or DatabaseKind.CosmosDb),
            '`' when kind is DatabaseKind.MySql or DatabaseKind.Sqlite or DatabaseKind.ClickHouse => Quoted(sql, at, '`', kind == DatabaseKind.ClickHouse),
            '[' when kind is DatabaseKind.SqlServer or DatabaseKind.Sqlite => Quoted(sql, at, ']', false),
            _ => null,
        };
    }

    /// <summary>Whether a quote starts a name rather than a string, as the database reads it.</summary>
    private static bool QuotesName(char quote, DatabaseKind kind) => quote switch
    {
        '"' => kind is not (DatabaseKind.MySql or DatabaseKind.CosmosDb),
        '`' => kind is DatabaseKind.MySql or DatabaseKind.Sqlite or DatabaseKind.ClickHouse,
        '[' => kind is DatabaseKind.SqlServer or DatabaseKind.Sqlite,
        _ => false,
    };

    /// <summary>A dollar quote's tag ($$ or $name$) starting at <paramref name="at"/>; null where a parameter ($1) stands.</summary>
    private static string? DollarTag(string sql, int at)
    {
        int i = at + 1;
        if (i < sql.Length && char.IsDigit(sql[i]))
        {
            return null;
        }
        while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
        {
            i++;
        }
        return i < sql.Length && sql[i] == '$' ? sql[at..(i + 1)] : null;
    }

    /// <summary>Past a run quoted by <paramref name="end"/>, its quote doubled inside it, or after a backslash where those escape.</summary>
    private static int Quoted(string sql, int at, char end, bool backslashes)
    {
        int i = at + 1;
        while (i < sql.Length)
        {
            char c = sql[i++];
            if (backslashes && c == '\\')
            {
                i++;
            }
            else if (c == end)
            {
                if (i < sql.Length && sql[i] == end)
                {
                    i++;
                    continue;
                }
                return i;
            }
        }
        return sql.Length;
    }

    /// <summary>Each statement of <paramref name="sql"/>, from its first word to its semicolon (or its text's end), passing
    /// over what is only white space and comments.</summary>
    public static List<(int Start, int End)> Statements(string sql, DatabaseKind kind)
    {
        if (kind == DatabaseKind.MongoDb)
        {
            return DocumentShell.Statements(sql);
        }
        if (kind == DatabaseKind.Redis)
        {
            return RedisCommands.Statements(sql);
        }
        List<(int Start, int End)> statements = [];
        List<Token> tokens = Tokens(sql, kind);
        int first = -1;
        int blocks = 0;
        bool routine = false;
        for (int t = 0; t < tokens.Count; t++)
        {
            Token token = tokens[t];
            if (kind == DatabaseKind.SqlServer && token.Word == "GO" && AloneOnLine(sql, token))
            {
                Close(t - 1);
                continue;
            }
            if (first < 0)
            {
                if (token.Word == ";")
                {
                    continue;
                }
                first = t;
                blocks = 0;
                routine = Routine(tokens, t);
            }
            if (routine)
            {
                blocks += Block(tokens, t);
            }
            if (token.Word == ";" && blocks <= 0)
            {
                Close(t);
            }
        }
        Close(tokens.Count - 1);
        return statements;

        void Close(int last)
        {
            if (first >= 0 && last >= first)
            {
                statements.Add((tokens[first].Start, Through(tokens[last].End, last + 1 < tokens.Count ? tokens[last + 1].Start : sql.Length)));
            }
            first = -1;
        }

        // The strings after a statement's last word are its own (WHERE a = 'x'), the comments after them not.
        int Through(int end, int before)
        {
            int through = end;
            for (int i = end; i < before;)
            {
                if (char.IsWhiteSpace(sql[i]))
                {
                    i++;
                    continue;
                }
                if (Skipped(sql, i, kind) is not { } past)
                {
                    break;
                }
                through = sql[i] is '-' or '/' or '#' ? through : past;
                i = past;
            }
            return through;
        }
    }

    /// <summary>The statement the caret stands in, or the one it follows on its line, else the next; null where there is none.</summary>
    public static (int Start, int End)? At(string sql, DatabaseKind kind, int caret)
    {
        List<(int Start, int End)> statements = Statements(sql, kind);
        (int Start, int End)? before = null;
        foreach ((int start, int end) in statements)
        {
            if (caret >= start && caret <= end)
            {
                return (start, end);
            }
            if (end < caret)
            {
                before = (start, end);
            }
            else
            {
                bool sameLine = before is { } last && sql.AsSpan(last.End, caret - last.End).IndexOfAny('\r', '\n') < 0;
                return sameLine ? before : (start, end);
            }
        }
        return before;
    }

    /// <summary>What runs as one: SQL Server's batches between GO lines, each whole, as its variables live through a batch;
    /// every other database's statements, one at a time.</summary>
    public static List<string> Runs(string sql, DatabaseKind kind)
    {
        if (kind != DatabaseKind.SqlServer)
        {
            return [.. Statements(sql, kind).Select(s => Trimmed(sql[s.Start..s.End]))];
        }
        List<string> batches = [];
        int from = 0;
        foreach (Token token in Tokens(sql, kind))
        {
            if (token.Word == "GO" && AloneOnLine(sql, token))
            {
                batches.Add(sql[from..token.Start]);
                from = token.End;
            }
        }
        batches.Add(sql[from..]);
        return [.. batches.Where(b => Tokens(b, kind).Any(t => t.Word != ";")).Select(b => b.Trim())];
    }

    /// <summary>The one table or view a plain SELECT reads, its name (a schema's too) as written; null where it reads none, or
    /// more than one (a join, a list, a union's).</summary>
    public static string? OnlyTable(string statement, DatabaseKind kind)
    {
        List<Token> tokens = Tokens(statement, kind);
        int from = tokens.FindIndex(t => t is { Word: "FROM", Quoted: false, Depth: 0 });
        if (tokens is not [{ Word: "SELECT", Quoted: false }, ..] || from < 0 || from + 1 >= tokens.Count
            || tokens[from + 1] is { Quoted: false, Word: var word } && !char.IsLetter(word[0]) && word[0] != '_')
        {
            return null;
        }
        int last = from + 1;
        while (last + 2 < tokens.Count && tokens[last + 1].Word == ".")
        {
            last += 2;
        }
        bool more = tokens.Skip(last + 1).Any(t => t is { Depth: 0, Quoted: false, Word: "JOIN" or "," or "FROM" or "UNION" or "INTERSECT" or "EXCEPT" });
        return more ? null : statement[tokens[from + 1].Start..tokens[last].End];
    }

    /// <summary>A name quoted as the database quotes one: SQL Server's brackets, MySQL's backticks, ClickHouse's backticks
    /// escaped by backslashes, else double quotes.</summary>
    public static string QuotedName(DatabaseKind kind, string name) => kind switch
    {
        DatabaseKind.SqlServer => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]",
        DatabaseKind.MySql => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`",
        DatabaseKind.ClickHouse => "`" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal) + "`",
        _ => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
    };

    /// <summary>A statement without its closing semicolon, which some drivers refuse alone.</summary>
    private static string Trimmed(string statement) => statement.TrimEnd().TrimEnd(';').TrimEnd();

    private static bool AloneOnLine(string sql, Token token)
    {
        int start = token.Start;
        while (start > 0 && sql[start - 1] is ' ' or '\t')
        {
            start--;
        }
        int end = token.End;
        while (end < sql.Length && sql[end] is ' ' or '\t')
        {
            end++;
        }
        return (start == 0 || sql[start - 1] is '\n' or '\r') && (end == sql.Length || sql[end] is '\n' or '\r');
    }

    /// <summary>What a run (a statement, or SQL Server's batch) does to its database: the most any of its statements does, a
    /// statement of a kind not known taken as changing data. A word that begins a statement counts wherever one stands
    /// (a data-modifying CTE, a batch's statements without semicolons), but not as a name: after a dot or AS, before a comma,
    /// FROM or an operator. EXPLAIN reads unless it ANALYZEs, which runs what it explains.</summary>
    public static SqlEffect EffectOf(string run, DatabaseKind kind)
    {
        // Cosmos DB's SQL only reads; MongoDB's shell and Redis's commands say what each does.
        if (!DatabaseKinds.SpeaksSql(kind))
        {
            return kind switch { DatabaseKind.CosmosDb => SqlEffect.Reads, DatabaseKind.Redis => RedisCommands.EffectOf(run), _ => DocumentShell.EffectOf(run) };
        }
        List<Token> tokens = Tokens(run, kind);
        SqlEffect effect = SqlEffect.Reads;
        int first = 0;
        int blocks = 0;
        bool routine = false;
        for (int t = 0; t <= tokens.Count; t++)
        {
            if (t < tokens.Count)
            {
                if (t == first)
                {
                    blocks = 0;
                    routine = Routine(tokens, t);
                }
                if (routine)
                {
                    blocks += Block(tokens, t);
                }
                if (tokens[t].Word != ";" || blocks > 0)
                {
                    continue;
                }
            }
            if (t > first)
            {
                effect = (SqlEffect)Math.Max((int)effect, (int)StatementEffect(tokens, first, t, kind));
            }
            first = t + 1;
        }
        return effect;
    }

    /// <summary>The one transaction statement a run is (BEGIN, COMMIT, ROLLBACK, as each database writes them), which a
    /// console in manual commit takes as its own; null for any other run, a partial ROLLBACK TO among them.</summary>
    public static SqlTransaction? TransactionOf(string run, DatabaseKind kind)
    {
        if (!DatabaseKinds.SpeaksSql(kind))
        {
            return null;
        }
        List<Token> tokens = Tokens(run, kind);
        while (tokens.Count > 0 && tokens[^1].Word == ";")
        {
            tokens.RemoveAt(tokens.Count - 1);
        }
        if (tokens.Count == 0 || tokens.Any(t => t.Word == ";" || t.Quoted))
        {
            return null;
        }
        string next = tokens.Count > 1 ? tokens[1].Word : string.Empty;
        bool Rest(params string[] words) => tokens.Skip(1).All(t => words.Contains(t.Word));
        return tokens[0].Word switch
        {
            "COMMIT" when Rest("WORK", "TRAN", "TRANSACTION") => SqlTransaction.Commit,
            "END" when kind is DatabaseKind.Postgres or DatabaseKind.Sqlite && Rest("WORK", "TRANSACTION") => SqlTransaction.Commit,
            "ROLLBACK" when Rest("WORK", "TRAN", "TRANSACTION") => SqlTransaction.Rollback,
            "ABORT" when kind == DatabaseKind.Postgres && Rest("WORK", "TRANSACTION") => SqlTransaction.Rollback,
            "START" when next == "TRANSACTION" => SqlTransaction.Begin,
            "BEGIN" when kind != DatabaseKind.SqlServer || next is "TRAN" or "TRANSACTION" => SqlTransaction.Begin,
            _ => null,
        };
    }

    private static readonly HashSet<string> ReadingWords = ["(", "SELECT", "WITH", "VALUES", "TABLE", "SHOW", "DESCRIBE", "DESC", "EXPLAIN", "PRAGMA", "HELP"];

    private static readonly HashSet<string> SessionWords =
    [
        "SET", "USE", "RESET", "DECLARE", "PRINT", "DISCARD", "LISTEN", "UNLISTEN", "PREPARE", "DEALLOCATE", "FETCH", "CLOSE", "OPEN",
        "MOVE", "IF", "WHILE", "ELSE", "RETURN", "RAISERROR", "THROW", "WAITFOR", "GOTO", "BREAK", "CONTINUE",
    ];

    private static readonly HashSet<string> TransactionWords = ["BEGIN", "START", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE", "END", "ABORT", "SAVE", "XA"];

    private static readonly HashSet<string> DataWords =
        ["INSERT", "UPDATE", "DELETE", "MERGE", "REPLACE", "UPSERT", "COPY", "LOAD", "IMPORT", "CALL", "EXEC", "EXECUTE", "DO", "HANDLER"];

    private static readonly HashSet<string> SchemaWords =
    [
        "CREATE", "ALTER", "DROP", "TRUNCATE", "RENAME", "GRANT", "REVOKE", "DENY", "COMMENT", "SECURITY", "REINDEX", "CLUSTER", "VACUUM",
        "ANALYZE", "ANALYSE", "OPTIMIZE", "REPAIR", "REFRESH", "ATTACH", "DETACH", "BACKUP", "RESTORE", "DBCC", "KILL", "SHUTDOWN", "FLUSH",
        "PURGE", "INSTALL", "UNINSTALL", "LOCK", "UNLOCK", "CHECKPOINT", "RECONFIGURE",
    ];

    /// <summary>The words that end a name's use rather than follow a statement's first word.</summary>
    private static readonly HashSet<string> AfterAName =
        ["FROM", "AS", "AND", "OR", "IS", "IN", "NOT", "LIKE", "BETWEEN", "THEN", "ELSE", "END", "WHEN", "ASC", "DESC", "OVER", "COLLATE", "ESCAPE"];

    /// <summary>The effect of the statement of tokens <paramref name="first"/> to <paramref name="end"/>, by its first word and
    /// the statements standing inside it.</summary>
    private static SqlEffect StatementEffect(List<Token> tokens, int first, int end, DatabaseKind kind)
    {
        Token lead = tokens[first];
        string word = lead.Quoted ? string.Empty : lead.Word;
        string next = first + 1 < end ? tokens[first + 1].Word : string.Empty;
        if (DataWords.Contains(word))
        {
            return SqlEffect.ChangesData;
        }
        if (SchemaWords.Contains(word))
        {
            return SqlEffect.ChangesSchema;
        }
        SqlEffect own;
        switch (word)
        {
            case "EXPLAIN" or "DESCRIBE" or "DESC":
                int inner = first + 1;
                bool runs = false;
                while (inner < end && (tokens[inner].Word is "ANALYZE" or "ANALYSE" or "(" || !ReadingWords.Contains(tokens[inner].Word)
                    && !DataWords.Contains(tokens[inner].Word) && !SchemaWords.Contains(tokens[inner].Word)))
                {
                    runs |= tokens[inner].Word is "ANALYZE" or "ANALYSE";
                    inner++;
                }
                return runs && inner < end ? StatementEffect(tokens, inner, end, kind) : SqlEffect.Reads;
            case "PRAGMA":
                return tokens.Skip(first).Take(end - first).Any(t => t.Word is "=" or "OPTIMIZE" or "INCREMENTAL_VACUUM" or "WAL_CHECKPOINT") ? SqlEffect.ChangesSchema : SqlEffect.Reads;
            case "SET" when next is "GLOBAL" or "PERSIST" or "PERSIST_ONLY":
                return SqlEffect.ChangesSchema;
            case "BEGIN" when kind == DatabaseKind.SqlServer && next is not ("TRAN" or "TRANSACTION" or "DISTRIBUTED"):
                own = SqlEffect.Session;
                break;
            default:
                own = ReadingWords.Contains(word) ? SqlEffect.Reads : SessionWords.Contains(word) ? SqlEffect.Session
                    : TransactionWords.Contains(word) ? SqlEffect.Transaction : SqlEffect.ChangesData;
                break;
        }
        for (int t = first + 1; t < end; t++)
        {
            SqlEffect? inside = StartsStatement(tokens, t, end) ? SchemaWords.Contains(tokens[t].Word) ? SqlEffect.ChangesSchema : SqlEffect.ChangesData
                : SelectsInto(tokens, t, end);
            own = (SqlEffect)Math.Max((int)own, (int)(inside ?? own));
        }
        return own;
    }

    /// <summary>What a SELECT's INTO at token <paramref name="t"/> makes: a table (SQL Server's and PostgreSQL's), or a file on
    /// the server (MySQL's OUTFILE); null for an INSERT's INTO or one into a variable.</summary>
    private static SqlEffect? SelectsInto(List<Token> tokens, int t, int end)
    {
        if (tokens[t] is not { Word: "INTO", Quoted: false } || tokens[t - 1].Word is "INSERT" or "MERGE" or "REPLACE" or "UPSERT" or "IGNORE")
        {
            return null;
        }
        string target = t + 1 < end ? tokens[t + 1].Word : string.Empty;
        return target is "@" or ":" ? null : target is "OUTFILE" or "DUMPFILE" ? SqlEffect.ChangesData : SqlEffect.ChangesSchema;
    }

    /// <summary>Whether a word changing data or schema inside a statement begins one of its own rather than names something:
    /// not after a dot, AS or a row lock's FOR, and followed by a name (a call's bracket only for EXEC and CALL).</summary>
    private static bool StartsStatement(List<Token> tokens, int t, int end)
    {
        Token token = tokens[t];
        if (token.Quoted || !DataWords.Contains(token.Word) && !SchemaWords.Contains(token.Word))
        {
            return false;
        }
        string before = tokens[t - 1].Quoted ? string.Empty : tokens[t - 1].Word;
        if (before is "." or "AS" or "FOR" or "ON" or "KEY" or "," || t + 1 >= end)
        {
            return false;
        }
        Token after = tokens[t + 1];
        if (after.Quoted)
        {
            return true;
        }
        if (after.Word == "FROM")
        {
            return token.Word == "DELETE";
        }
        if (after.Word is "(" or "@")
        {
            return token.Word is "EXEC" or "EXECUTE" or "CALL";
        }
        return after.Word.Length > 0 && (char.IsLetter(after.Word[0]) || after.Word[0] == '_') && !AfterAName.Contains(after.Word);
    }

    /// <summary>Whether the statement starting at token <paramref name="t"/> makes a routine, whose body's semicolons end nothing.</summary>
    private static bool Routine(List<Token> tokens, int t)
    {
        if (tokens[t].Word is not ("CREATE" or "ALTER"))
        {
            return false;
        }
        for (int i = t + 1; i < Math.Min(tokens.Count, t + 12); i++)
        {
            if (tokens[i].Word is "PROCEDURE" or "PROC" or "FUNCTION" or "TRIGGER" or "EVENT")
            {
                return true;
            }
            if (tokens[i].Word is "TABLE" or "VIEW" or "INDEX" or "AS" or ";")
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>How a routine's word changes its blocks' depth: BEGIN (not of a transaction) and CASE open one, END closes
    /// one unless it ends an IF, a loop or a WHILE, which opened none.</summary>
    private static int Block(List<Token> tokens, int t)
    {
        string next = t + 1 < tokens.Count ? tokens[t + 1].Word : string.Empty;
        return tokens[t].Word switch
        {
            "BEGIN" => next is "TRAN" or "TRANSACTION" or "WORK" or "DISTRIBUTED" or ";" ? 0 : 1,
            "CASE" when t > 0 && tokens[t - 1].Word == "END" => 0,
            "CASE" => 1,
            "END" => next is "IF" or "LOOP" or "WHILE" or "REPEAT" or "FOR" ? 0 : -1,
            _ => 0,
        };
    }
}
