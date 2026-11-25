using System.Globalization;
using System.Text;

namespace Passenger.App.Database;

/// <summary>Redis's commands as a console writes them, read without its client: a command a line (a line beginning with #
/// a comment), its words split as redis-cli splits them ("double quotes" taking backslash escapes, 'single quotes' none),
/// what each does to its database, which a console's marks and an agent's refusals go by, and each known command's
/// arguments and what it does, which completion offers.</summary>
public static class RedisCommands
{
    /// <summary>A command: its arguments as redis-cli's help writes them, what it does, and what it does to the database.</summary>
    public readonly record struct Command(string Name, string Arguments, string Said, SqlEffect Effect);

    /// <summary>The commands completion offers, the database's own words for each.</summary>
    public static readonly IReadOnlyList<Command> Known =
    [
        new("GET", "key", "a string's value", SqlEffect.Reads),
        new("SET", "key value [EX seconds]", "sets a string", SqlEffect.ChangesData),
        new("MGET", "key [key ...]", "strings' values", SqlEffect.Reads),
        new("INCR", "key", "adds one to a number", SqlEffect.ChangesData),
        new("DEL", "key [key ...]", "deletes keys", SqlEffect.ChangesData),
        new("EXISTS", "key [key ...]", "how many of the keys exist", SqlEffect.Reads),
        new("TYPE", "key", "a key's type", SqlEffect.Reads),
        new("TTL", "key", "a key's time to live in seconds", SqlEffect.Reads),
        new("EXPIRE", "key seconds", "sets a key's time to live", SqlEffect.ChangesData),
        new("PERSIST", "key", "takes a key's time to live away", SqlEffect.ChangesData),
        new("RENAME", "key newkey", "renames a key", SqlEffect.ChangesData),
        new("SCAN", "cursor [MATCH pattern] [COUNT count] [TYPE type]", "keys a page at a time", SqlEffect.Reads),
        new("KEYS", "pattern", "every key a pattern takes", SqlEffect.Reads),
        new("DBSIZE", string.Empty, "how many keys the database holds", SqlEffect.Reads),
        new("HGET", "key field", "a hash's field", SqlEffect.Reads),
        new("HGETALL", "key", "a hash's fields and values", SqlEffect.Reads),
        new("HSET", "key field value [field value ...]", "sets a hash's fields", SqlEffect.ChangesData),
        new("HDEL", "key field [field ...]", "deletes a hash's fields", SqlEffect.ChangesData),
        new("HKEYS", "key", "a hash's fields", SqlEffect.Reads),
        new("HLEN", "key", "how many fields a hash holds", SqlEffect.Reads),
        new("LRANGE", "key start stop", "a list's items", SqlEffect.Reads),
        new("LPUSH", "key element [element ...]", "puts items at a list's head", SqlEffect.ChangesData),
        new("RPUSH", "key element [element ...]", "puts items at a list's tail", SqlEffect.ChangesData),
        new("LPOP", "key", "takes a list's first item", SqlEffect.ChangesData),
        new("LSET", "key index element", "sets a list's item", SqlEffect.ChangesData),
        new("LLEN", "key", "how many items a list holds", SqlEffect.Reads),
        new("SMEMBERS", "key", "a set's members", SqlEffect.Reads),
        new("SADD", "key member [member ...]", "adds a set's members", SqlEffect.ChangesData),
        new("SREM", "key member [member ...]", "removes a set's members", SqlEffect.ChangesData),
        new("SISMEMBER", "key member", "whether a set holds a member", SqlEffect.Reads),
        new("SCARD", "key", "how many members a set holds", SqlEffect.Reads),
        new("ZRANGE", "key start stop [WITHSCORES]", "a sorted set's members", SqlEffect.Reads),
        new("ZADD", "key score member [score member ...]", "adds a sorted set's members", SqlEffect.ChangesData),
        new("ZREM", "key member [member ...]", "removes a sorted set's members", SqlEffect.ChangesData),
        new("ZSCORE", "key member", "a sorted set's member's score", SqlEffect.Reads),
        new("ZCARD", "key", "how many members a sorted set holds", SqlEffect.Reads),
        new("XADD", "key * field value [field value ...]", "adds a stream's entry", SqlEffect.ChangesData),
        new("XRANGE", "key start end [COUNT count]", "a stream's entries", SqlEffect.Reads),
        new("XLEN", "key", "how many entries a stream holds", SqlEffect.Reads),
        new("XDEL", "key id [id ...]", "deletes a stream's entries", SqlEffect.ChangesData),
        new("INFO", "[section]", "the server's state", SqlEffect.Reads),
        new("PING", string.Empty, "whether the server answers", SqlEffect.Reads),
        new("FLUSHDB", string.Empty, "deletes every key of the database", SqlEffect.ChangesSchema),
    ];

    /// <summary>The commands beyond <see cref="Known"/> that only read.</summary>
    private static readonly HashSet<string> Reading = new(StringComparer.OrdinalIgnoreCase)
    {
        "GETRANGE", "STRLEN", "PTTL", "EXPIRETIME", "PEXPIRETIME", "RANDOMKEY", "HMGET", "HVALS", "HEXISTS", "HSCAN", "HSTRLEN", "HRANDFIELD",
        "LINDEX", "LPOS", "SMISMEMBER", "SSCAN", "SRANDMEMBER", "SINTER", "SUNION", "SDIFF", "SINTERCARD", "ZRANGEBYSCORE", "ZREVRANGE",
        "ZREVRANGEBYSCORE", "ZRANGEBYLEX", "ZCOUNT", "ZLEXCOUNT", "ZRANK", "ZREVRANK", "ZSCAN", "ZMSCORE", "ZRANDMEMBER", "XREVRANGE", "XINFO",
        "XREAD", "XPENDING", "ECHO", "TIME", "LASTSAVE", "GETBIT", "BITCOUNT", "BITPOS", "PFCOUNT", "GEOPOS", "GEODIST", "GEOHASH", "GEOSEARCH",
        "OBJECT", "MEMORY", "DUMP", "TOUCH", "LOLWUT", "COMMAND", "ROLE", "SLOWLOG", "LATENCY",
    };

    /// <summary>The commands that change the server or every key rather than some keys' values.</summary>
    private static readonly HashSet<string> Shaping = new(StringComparer.OrdinalIgnoreCase)
    {
        "FLUSHDB", "FLUSHALL", "SWAPDB", "SHUTDOWN", "DEBUG", "SCRIPT", "FUNCTION", "ACL", "MODULE", "REPLICAOF", "SLAVEOF", "FAILOVER",
        "SAVE", "BGSAVE", "BGREWRITEAOF", "MIGRATE", "CLUSTER",
    };

    /// <summary>Where a string or comment starting at <paramref name="at"/> ends, or null where none starts there: a quote's
    /// words, and a line beginning with #.</summary>
    public static int? Skipped(string text, int at)
    {
        char c = text[at];
        if (c == '#' && text.AsSpan(0, at).TrimEnd(" \t").Length is var before && (before == 0 || text[before - 1] == '\n'))
        {
            int end = text.IndexOf('\n', at);
            return end < 0 ? text.Length : end;
        }
        if (c is '"' or '\'')
        {
            for (int i = at + 1; i < text.Length; i++)
            {
                if (c == '"' && text[i] == '\\')
                {
                    i++;
                }
                else if (text[i] == c)
                {
                    return i + 1;
                }
                else if (text[i] == '\n')
                {
                    return i;
                }
            }
            return text.Length;
        }
        return null;
    }

    /// <summary>Each command of a console: a line's words, past the lines that are blank or comments.</summary>
    public static List<(int Start, int End)> Statements(string text)
    {
        List<(int Start, int End)> statements = [];
        for (int start = 0; start < text.Length;)
        {
            int end = text.IndexOf('\n', start);
            end = end < 0 ? text.Length : end;
            ReadOnlySpan<char> line = text.AsSpan(start, end - start);
            int indent = line.Length - line.TrimStart().Length;
            ReadOnlySpan<char> words = line.Trim();
            if (words.Length > 0 && words[0] != '#')
            {
                statements.Add((start + indent, start + indent + words.Length));
            }
            start = end + 1;
        }
        return statements;
    }

    /// <summary>A command's words as redis-cli splits them; a quote left open is said as a <see cref="FormatException"/>.</summary>
    public static List<string> Words(string run)
    {
        List<string> words = [];
        StringBuilder word = new();
        bool inWord = false;
        for (int i = 0; i < run.Length; i++)
        {
            char c = run[i];
            if (char.IsWhiteSpace(c))
            {
                if (inWord)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                }
                continue;
            }
            inWord = true;
            if (c is '"' or '\'')
            {
                int close = Skipped(run, i) ?? i;
                if (close > run.Length || close <= i + 1 || run[close - 1] != c || close - 1 == i)
                {
                    throw new FormatException($"A quote is left open: {run[i..]}");
                }
                string inside = run[(i + 1)..(close - 1)];
                word.Append(c == '"' ? Unescaped(inside) : inside);
                i = close - 1;
                continue;
            }
            word.Append(c);
        }
        if (inWord)
        {
            words.Add(word.ToString());
        }
        return words;
    }

    /// <summary>What a command does to its database: a known one's effect, one that only reads, CONFIG and CLIENT by what
    /// they are asked, one that changes the server; any other taken as changing data.</summary>
    public static SqlEffect EffectOf(string run)
    {
        List<string> words;
        try
        {
            words = Words(run);
        }
        catch (FormatException)
        {
            return SqlEffect.ChangesData;
        }
        if (words.Count == 0)
        {
            return SqlEffect.Reads;
        }
        string name = words[0];
        if (name.Equals("CONFIG", StringComparison.OrdinalIgnoreCase) || name.Equals("CLIENT", StringComparison.OrdinalIgnoreCase))
        {
            return words.Count > 1 && words[1].ToUpperInvariant() is "GET" or "LIST" or "INFO" or "GETNAME" or "ID" ? SqlEffect.Reads : SqlEffect.ChangesSchema;
        }
        return Known.FirstOrDefault(k => k.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } known ? known.Effect
            : Reading.Contains(name) ? SqlEffect.Reads
            : Shaping.Contains(name) ? SqlEffect.ChangesSchema
            : SqlEffect.ChangesData;
    }

    /// <summary>A double-quoted word's escapes, as redis-cli reads them: \n, \r, \t, \b, \a, \xHH and any other character itself.</summary>
    private static string Unescaped(string inside)
    {
        StringBuilder text = new(inside.Length);
        for (int i = 0; i < inside.Length; i++)
        {
            if (inside[i] != '\\' || i + 1 >= inside.Length)
            {
                text.Append(inside[i]);
                continue;
            }
            char next = inside[++i];
            if (next == 'x' && i + 2 < inside.Length && int.TryParse(inside.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
            {
                text.Append((char)code);
                i += 2;
                continue;
            }
            text.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'a' => '\a', _ => next });
        }
        return text.ToString();
    }
}

/// <summary>Redis's numbered databases as the explorer names them: db0, db1.</summary>
public static class RedisNames
{
    public static string Database(int number) => $"db{number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A database's number from its name (db3) or its number (3); the first, 0, for any other.</summary>
    public static int Number(string name) =>
        int.TryParse(name.StartsWith("db", StringComparison.OrdinalIgnoreCase) ? name[2..] : name, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            ? number : 0;
}
