namespace Passenger.App.Database;

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
        char c = sql[at];
        char next = at + 1 < sql.Length ? sql[at + 1] : '\0';
        if (c == '-' && next == '-' || c == '#' && kind == DatabaseKind.MySql)
        {
            int end = sql.IndexOfAny(['\r', '\n'], at);
            return end < 0 ? sql.Length : end;
        }
        if (c == '/' && next == '*')
        {
            // PostgreSQL and SQL Server nest block comments; MySQL and SQLite end one at its first close.
            bool nests = kind is DatabaseKind.Postgres or DatabaseKind.SqlServer;
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
        if (c == '$' && kind == DatabaseKind.Postgres && DollarTag(sql, at) is { } tag)
        {
            int end = sql.IndexOf(tag, at + tag.Length, StringComparison.Ordinal);
            return end < 0 ? sql.Length : end + tag.Length;
        }
        bool escapes = kind == DatabaseKind.MySql || kind == DatabaseKind.Postgres && at > 0 && sql[at - 1] is 'E' or 'e'
            && (at < 2 || !char.IsLetterOrDigit(sql[at - 2]));
        return c switch
        {
            '\'' => Quoted(sql, at, '\'', escapes),
            '"' => Quoted(sql, at, '"', kind == DatabaseKind.MySql),
            '`' when kind is DatabaseKind.MySql or DatabaseKind.Sqlite => Quoted(sql, at, '`', false),
            '[' when kind is DatabaseKind.SqlServer or DatabaseKind.Sqlite => Quoted(sql, at, ']', false),
            _ => null,
        };
    }

    /// <summary>Whether a quote starts a name rather than a string, as the database reads it.</summary>
    private static bool QuotesName(char quote, DatabaseKind kind) => quote switch
    {
        '"' => kind != DatabaseKind.MySql,
        '`' => kind is DatabaseKind.MySql or DatabaseKind.Sqlite,
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
                statements.Add((tokens[first].Start, tokens[last].End));
            }
            first = -1;
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
