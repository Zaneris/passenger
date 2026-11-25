using System.Globalization;

namespace Passenger.App.Database;

/// <summary>A statement's parameter where it stands: named as written (@id, :id, $id), or by its place ($1, and ?1, ?2 …
/// for each bare ? in turn across what runs together).</summary>
public readonly record struct SqlParameter(string Name, int Start, int End);

/// <summary>The parameters of what runs, as each database's driver takes them: SQL Server's @name (one its batch DECLAREs
/// being its own variable), PostgreSQL's $1 and @name, MySQL's ? and @name (a user variable its statements assign passed
/// over) and SQLite's ?, ?NNN, :name, @name and $name; none inside strings, quoted names or comments, nor in a routine's
/// definition, which declares its own. A value is the person's text: NULL, a number, 'quoted' text or text.</summary>
public static class SqlParameters
{
    /// <summary>Each run's parameters, in turn, a bare ? numbered across all of them.</summary>
    public static List<List<SqlParameter>> Of(IReadOnlyList<string> runs, DatabaseKind kind)
    {
        int unnamed = 0;
        // A variable one statement assigns is the session's for those after it.
        HashSet<string> own = [.. runs.SelectMany(run => Variables(run, kind))];
        return [.. runs.Select(run => Of(run, kind, own, ref unnamed))];
    }

    /// <summary>The names asked for, each once, in the order they first stand (a place's in its number's order).</summary>
    public static List<string> Asked(IEnumerable<IEnumerable<SqlParameter>> found) =>
        [.. found.SelectMany(p => p).Select(p => p.Name).Distinct(StringComparer.Ordinal)
            .OrderBy(n => n.Length > 1 && n[0] is '$' or '?' && char.IsAsciiDigit(n[1]) ? int.Parse(n[1..], CultureInfo.InvariantCulture) : 0)];

    private static List<SqlParameter> Of(string run, DatabaseKind kind, HashSet<string> own, ref int unnamed)
    {
        List<SqlParameter> found = [];
        // DuckDB's, ClickHouse's, Snowflake's Redis's and the document databases' statements run as written, their own placeholders theirs to read.
        if (DefinesRoutine(run, kind) || kind is DatabaseKind.DuckDb or DatabaseKind.ClickHouse or DatabaseKind.Snowflake || !DatabaseKinds.SpeaksSql(kind))
        {
            return found;
        }
        for (int i = 0; i < run.Length;)
        {
            if (SqlText.Skipped(run, i, kind) is { } past)
            {
                i = past;
                continue;
            }
            char c = run[i];
            char before = i > 0 ? run[i - 1] : '\0';
            int end = i + 1;
            if (c == '?' && kind is DatabaseKind.MySql or DatabaseKind.Sqlite)
            {
                while (kind == DatabaseKind.Sqlite && end < run.Length && char.IsAsciiDigit(run[end]))
                {
                    end++;
                }
                found.Add(new SqlParameter(end > i + 1 ? run[i..end] : $"?{(++unnamed).ToString(CultureInfo.InvariantCulture)}", i, end));
                i = end;
                continue;
            }
            if (c == '$' && kind == DatabaseKind.Postgres && end < run.Length && char.IsAsciiDigit(run[end]) && !NamePart(before))
            {
                while (end < run.Length && char.IsAsciiDigit(run[end]))
                {
                    end++;
                }
                found.Add(new SqlParameter(run[i..end], i, end));
                i = end;
                continue;
            }
            bool prefix = c == '@' || kind == DatabaseKind.Sqlite && c is ':' or '$';
            if (prefix && end < run.Length && (char.IsLetter(run[end]) || run[end] == '_') && !NamePart(before) && before is not ('@' or '<' or ':'))
            {
                while (end < run.Length && NamePart(run[end]))
                {
                    end++;
                }
                string name = run[i..end];
                if (!own.Contains(name.ToUpperInvariant()))
                {
                    found.Add(new SqlParameter(name, i, end));
                }
                i = end;
                continue;
            }
            i++;
        }
        return found;
    }

    private static bool NamePart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>Whether a run makes a routine (a procedure, function or trigger), whose @names and $1s are its own.</summary>
    private static bool DefinesRoutine(string run, DatabaseKind kind)
    {
        List<SqlText.Token> tokens = SqlText.Tokens(run, kind);
        for (int t = 0; t + 1 < tokens.Count; t++)
        {
            if (tokens[t].Word is "CREATE" or "ALTER" && tokens.Skip(t + 1).Take(6).Any(w => w.Word is "PROCEDURE" or "PROC" or "FUNCTION" or "TRIGGER"))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The variables a run makes its own, upper-cased with their @: SQL Server's DECLAREd, and MySQL's user
    /// variables its statements assign (SET @a = …, @a := …, INTO @a).</summary>
    private static HashSet<string> Variables(string run, DatabaseKind kind)
    {
        HashSet<string> own = [];
        if (kind is not (DatabaseKind.SqlServer or DatabaseKind.MySql))
        {
            return own;
        }
        List<SqlText.Token> tokens = SqlText.Tokens(run, kind);
        string? clause = null;
        int depth = 0;
        for (int t = 0; t < tokens.Count; t++)
        {
            SqlText.Token token = tokens[t];
            if (token is { Word: "DECLARE" or "SET" or "INTO", Quoted: false })
            {
                clause = token.Word;
                depth = token.Depth;
                continue;
            }
            if (token.Word == ";" || token is { Quoted: false, Word: "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "IF" or "WHILE" or "EXEC" or "EXECUTE" or "PRINT"
                or "RETURN" or "BEGIN" or "WITH" or "MERGE" or "VALUES" or "FROM" or "WHERE" } && token.Depth <= depth)
            {
                clause = null;
                continue;
            }
            if (token.Word != "@" || t + 1 >= tokens.Count || tokens[t + 1].Start != token.End)
            {
                continue;
            }
            string name = "@" + tokens[t + 1].Word;
            string previous = t > 0 ? tokens[t - 1].Word : string.Empty;
            string next = t + 2 < tokens.Count ? tokens[t + 2].Word : string.Empty;
            bool assigned = next == ":" && t + 3 < tokens.Count && tokens[t + 3].Word == "=";
            bool listed = token.Depth == depth && previous is "DECLARE" or "SET" or "INTO" or ",";
            if (assigned || (kind == DatabaseKind.SqlServer ? clause == "DECLARE" && listed : clause is "SET" or "INTO" && listed && (clause == "INTO" || next is "=" or ":")))
            {
                own.Add(name);
            }
        }
        return own;
    }

    /// <summary>A value as the person wrote it, given to the driver: NULL, a whole or decimal number, text in single quotes
    /// (its doubled quotes one), or else the text itself; PostgreSQL takes every value as text it reads as a literal of the
    /// type its place needs (<see cref="DatabaseClient.Bind"/>).</summary>
    public static object Value(string written, DatabaseKind kind)
    {
        string text = written.Trim();
        if (text.Equals("NULL", StringComparison.OrdinalIgnoreCase))
        {
            return DBNull.Value;
        }
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
        {
            return text[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }
        if (kind == DatabaseKind.Postgres)
        {
            return written;
        }
        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole) ? whole
            : text.Contains('.', StringComparison.Ordinal) && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number) ? number
            : written;
    }
}
