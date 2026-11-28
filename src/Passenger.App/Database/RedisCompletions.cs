namespace Passenger.App.Database;

/// <summary>What completion offers at a caret in a Redis console: a line's first word its commands, each with its arguments
/// and what it does; any word after it the database's keys, each with its type, and the options commands take.</summary>
public static class RedisCompletions
{
    private static readonly string[] Options = ["EX", "PX", "NX", "XX", "KEEPTTL", "MATCH", "COUNT", "TYPE", "WITHSCORES", "LIMIT"];

    /// <summary>Where the word being typed at <paramref name="caret"/> starts, and what may stand there.</summary>
    public static (int Start, IReadOnlyList<SqlCompletions.Candidate> Items) At(string text, int caret, SqlCatalog? catalog)
    {
        int start = RedisCommands.WordAt(text, caret).Start;
        int line = start;
        while (line > 0 && text[line - 1] != '\n')
        {
            line--;
        }
        if (text.AsSpan(line, start - line).Trim().Length == 0)
        {
            return (start, [.. RedisCommands.Known.Select(c => new SqlCompletions.Candidate(c.Name, c.Name + " ", SqlCompletions.Kind.Keyword,
                c.Arguments.Length > 0 ? $"{c.Arguments}: {c.Said}" : c.Said))]);
        }
        return (start, [.. (catalog?.Tables ?? []).Select(k => new SqlCompletions.Candidate(k.Name, Quoted(k.Name), SqlCompletions.Kind.Table, k.Schema)),
            .. Options.Select(o => new SqlCompletions.Candidate(o, o, SqlCompletions.Kind.Keyword, "option"))]);
    }

    /// <summary>A key as a command takes it: as written, in double quotes where it holds white space or a quote.</summary>
    private static string Quoted(string key) => key.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'')
        ? "\"" + key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : key;
}
