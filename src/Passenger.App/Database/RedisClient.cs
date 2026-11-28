using System.Globalization;
using Passenger.App.Tables;
using Passenger.Core;
using StackExchange.Redis;

namespace Passenger.App.Database;

/// <summary>A value its database never changes in place (a stream entry's field): shown as its words, written by no cell.</summary>
public sealed record FixedValue(string Text)
{
    public override string ToString() => Text;
}

/// <summary>Redis as the Database window reads and writes it, through StackExchange.Redis: a server's numbered databases
/// (db0, db1), a database's keys grouped by their type with each one's time to live, and a key's value as a table's rows
/// (a string's one value, a hash's fields, a list's items, a set's members, a sorted set's members and scores, a stream's
/// entries), read whole to <see cref="MostElements"/> and paged, sorted and filtered here, its changes written in one
/// transaction that holds only while what they change stands as it was read. Every failure of the client is said as an
/// <see cref="IOException"/>, as a relational driver's is.</summary>
internal static class RedisClient
{
    /// <summary>The most keys a database lists, and the most elements of a key a tab reads.</summary>
    public const int MostKeys = 1000;

    public const int MostElements = 10000;

    /// <summary>What a database's keys past <see cref="MostKeys"/> are said as, standing as a group of no keys of its own.</summary>
    public static readonly string KeysUnlisted = $"Only the first {Numbers.Said(MostKeys)} keys are listed: SCAN in a console reads the rest";

    /// <summary>The types a key may be, as the explorer groups them, in its order.</summary>
    private static readonly (RedisType Type, string Title)[] Types =
    [
        (RedisType.String, "Strings"), (RedisType.Hash, "Hashes"), (RedisType.List, "Lists"), (RedisType.Set, "Sets"),
        (RedisType.SortedSet, "Sorted sets"), (RedisType.Stream, "Streams"),
    ];

    /// <summary>The server's databases that hold keys, and the one the connection names, whichever holds none.</summary>
    internal static async Task<IReadOnlyList<string>> DatabasesAsync(DatabaseSource source, CancellationToken cancel) => await Guarded(source, async () =>
    {
        await using ConnectionMultiplexer redis = await ConnectAsync(source);
        IServer server = Server(redis);
        HashSet<int> numbers = [RedisNames.Number(source.Database)];
        foreach (IGrouping<string, KeyValuePair<string, string>> section in await server.InfoAsync("keyspace"))
        {
            numbers.UnionWith(section.Select(entry => entry.Key).Where(name => name.StartsWith("db", StringComparison.Ordinal)).Select(RedisNames.Number));
        }
        cancel.ThrowIfCancellationRequested();
        return (IReadOnlyList<string>)[.. numbers.Order().Select(RedisNames.Database)];
    });

    /// <summary>A database's keys (at most <see cref="MostKeys"/>), grouped by type, each a table of its group with its time
    /// to live said.</summary>
    internal static async Task<IReadOnlyList<(string Schema, IReadOnlyList<DatabaseObject> Objects)>> KeysAsync(DatabaseSource source, CancellationToken cancel) =>
        await Guarded(source, async () =>
        {
            await using ConnectionMultiplexer redis = await ConnectAsync(source);
            IDatabase database = redis.GetDatabase(RedisNames.Number(source.Database));
            List<RedisKey> keys = [];
            bool more = false;
            await foreach (RedisKey key in Server(redis).KeysAsync(database.Database, pageSize: 500).WithCancellation(cancel))
            {
                if (keys.Count == MostKeys)
                {
                    more = true;
                    break;
                }
                keys.Add(key);
            }
            // Each key's type and time to live asked together, pipelined as the client sends them.
            RedisType[] types = await Task.WhenAll(keys.Select(k => database.KeyTypeAsync(k)));
            TimeSpan?[] lives = await Task.WhenAll(keys.Select(k => database.KeyTimeToLiveAsync(k)));
            List<(string, IReadOnlyList<DatabaseObject>)> groups = [];
            foreach ((RedisType type, string title) in Types)
            {
                DatabaseObject[] members = [.. keys.Select((key, at) => (key, at)).Where(k => types[k.at] == type)
                    .Select(k => new DatabaseObject(title, k.key.ToString(), DatabaseObjectKind.Table, LifeSaid(lives[k.at])))
                    .OrderBy(o => o.Name, StringComparer.Ordinal)];
                groups.Add((title, members));
            }
            if (more)
            {
                groups.Add((KeysUnlisted, []));
            }
            return (IReadOnlyList<(string, IReadOnlyList<DatabaseObject>)>)groups;
        });

    /// <summary>A key's columns by its type: a string's value; a hash's field and value; a list's index and item; a set's
    /// member; a sorted set's member and score; a stream's entry id and the fields its entries hold. The first is its
    /// key, which a list's index and a stream's id are the database's to give.</summary>
    internal static async Task<DatabaseSchema> SchemaAsync(DatabaseSource source, DatabaseTable table, CancellationToken cancel) => await Guarded(source, async () =>
    {
        await using ConnectionMultiplexer redis = await ConnectAsync(source);
        IDatabase database = redis.GetDatabase(RedisNames.Number(source.Database));
        RedisType type = await database.KeyTypeAsync(table.Name);
        TimeSpan? life = await database.KeyTimeToLiveAsync(table.Name);
        List<DatabaseColumn> columns = type switch
        {
            RedisType.String => [new("key", "string", false, true, false), new("value", "string", true, false, false)],
            RedisType.Hash => [new("field", "string", false, true, false), new("value", "string", true, false, false)],
            RedisType.List => [new("index", "int", false, true, true), new("item", "string", true, false, false)],
            RedisType.Set => [new("member", "string", false, true, false)],
            RedisType.SortedSet => [new("member", "string", false, true, false), new("score", "number", true, false, false)],
            RedisType.Stream => [new("id", "string", false, true, true), .. (await database.StreamRangeAsync(table.Name, count: 100))
                .SelectMany(e => e.Values.Select(v => v.Name.ToString())).Distinct(StringComparer.Ordinal).Select(f => new DatabaseColumn(f, "string", true, false, false))],
            _ => throw new InvalidOperationException($"{table.Name} is no key of {source.Database}: read the database again"),
        };
        cancel.ThrowIfCancellationRequested();
        return new DatabaseSchema(table, columns, [$"{TypeName(type)} · {LifeSaid(life)}"], []) { RowsFixed = type == RedisType.String };
    });

    /// <summary>A page of a key's value as rows, read whole (to <see cref="MostElements"/>) then filtered by a column, sorted
    /// and paged here.</summary>
    internal static async Task<DatabaseRows> PageAsync(DatabaseSource source, DatabaseSchema schema, int offset, CancellationToken cancel, DatabaseFilter? filter,
        (string Column, bool Descending)? sort) => await Guarded(source, async () =>
    {
        await using ConnectionMultiplexer redis = await ConnectAsync(source);
        IDatabase database = redis.GetDatabase(RedisNames.Number(source.Database));
        RedisKey key = schema.Table.Name;
        string[] columns = [.. schema.Columns.Select(c => c.Name)];
        RedisType type = await database.KeyTypeAsync(key);
        TimeSpan? life = await database.KeyTimeToLiveAsync(key);
        List<object?[]> rows = type switch
        {
            RedisType.String => [[schema.Table.Name, (string?)await database.StringGetAsync(key)]],
            RedisType.Hash => [.. (await database.HashGetAllAsync(key)).Take(MostElements).Select(e => new object?[] { e.Name.ToString(), (string?)e.Value })],
            RedisType.List => [.. (await database.ListRangeAsync(key, 0, MostElements - 1)).Select((v, at) => new object?[] { (long)at, (string?)v })],
            RedisType.Set => [.. (await database.SetMembersAsync(key)).Take(MostElements).Select(v => new object?[] { (string?)v })],
            RedisType.SortedSet => [.. (await database.SortedSetRangeByRankWithScoresAsync(key, 0, MostElements - 1)).Select(e => new object?[] { (string?)e.Element, e.Score })],
            RedisType.Stream => [.. (await database.StreamRangeAsync(key, count: MostElements)).Select(e => columns.Select(c => c == "id" ? (object?)e.Id.ToString()
                : e.Values.FirstOrDefault(v => v.Name == c) is { Name.IsNull: false } held ? new FixedValue(held.Value.ToString()) : AbsentField.Value).ToArray())],
            _ => throw new InvalidOperationException($"{schema.Table.Name} is no longer a key of {source.Database}: read the database again"),
        };
        // A value longer than a tab reads is said so, its filter and sort holding among what was read alone.
        long length = type switch
        {
            RedisType.Hash => await database.HashLengthAsync(key),
            RedisType.List => await database.ListLengthAsync(key),
            RedisType.Set => await database.SetLengthAsync(key),
            RedisType.SortedSet => await database.SortedSetLengthAsync(key),
            RedisType.Stream => await database.StreamLengthAsync(key),
            _ => 1,
        };
        cancel.ThrowIfCancellationRequested();
        DatabaseRows all = new(columns, [.. rows]);
        IEnumerable<int> kept = Enumerable.Range(0, all.RowCount);
        if (filter is not null && Array.IndexOf(columns, filter.Column) is >= 0 and int by)
        {
            kept = kept.Where(r => filter.Operator switch
            {
                "Is NULL" => all.Rows[r][by] is null or AbsentField,
                "Is not NULL" => all.Rows[r][by] is not (null or AbsentField),
                "Equals" => all.Cell(r, by) == filter.Value,
                _ => all.Cell(r, by).Contains(filter.Value, StringComparison.OrdinalIgnoreCase),
            });
        }
        if (sort is { } order && Array.IndexOf(columns, order.Column) is >= 0 and int sorted)
        {
            int[] ranked = TableSort.Order(all, sorted, order.Descending);
            int[] place = new int[ranked.Length];
            for (int i = 0; i < ranked.Length; i++)
            {
                place[ranked[i]] = i;
            }
            kept = kept.OrderBy(r => place[r]);
        }
        int[] chosen = [.. kept];
        return new DatabaseRows(columns, [.. chosen.Skip(offset).Take(DatabaseClient.PageSize).Select(r => all.Rows[r])], chosen.Length > offset + DatabaseClient.PageSize)
        {
            Schema = schema,
            Offset = offset,
            Types = [.. schema.Columns.Select(c => c.Type)],
            OrderedBy = sort is { } ordered && Array.IndexOf(columns, ordered.Column) is >= 0 and int at ? (at, ordered.Descending) : null,
            About = $"{TypeName(type)} · {LifeSaid(life)}" + (length > MostElements ? $" · {DatabaseClient.ReadOf(MostElements, length, "elements")}" : string.Empty),
        };
    });

    /// <summary>Writes a page's changes in one transaction, which runs only while each value changed or deleted stands as it
    /// was read: a string's value set; a hash's fields set and deleted; a list's items set by their index, deleted by a mark
    /// put in their place, and added at its tail; a set's and a sorted set's members added and removed, a score set; a
    /// stream's entries added and deleted.</summary>
    internal static async Task SubmitAsync(DatabaseSource source, DatabaseSchema schema, DatabaseRows page, PendingEdits edits, CancellationToken cancel) =>
        await Guarded(source, async () =>
        {
            await using ConnectionMultiplexer redis = await ConnectAsync(source);
            IDatabase database = redis.GetDatabase(RedisNames.Number(source.Database));
            RedisKey key = schema.Table.Name;
            RedisType type = await database.KeyTypeAsync(key);
            ITransaction transaction = database.CreateTransaction();
            List<Task> writes = [];
            string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            switch (type)
            {
                case RedisType.String:
                    if (!edits.Deleted.IsEmpty || !edits.Added.IsEmpty)
                    {
                        throw new InvalidOperationException(Refusals.RowsFixed.English);
                    }
                    foreach ((RowKey _, ChangedRow row) in edits.Changed)
                    {
                        transaction.AddCondition(Condition.StringEqual(key, Text(row.Read[1])));
                        writes.Add(transaction.StringSetAsync(key, row.Written.GetValueOrDefault(1) ?? string.Empty, expiry: null, keepTtl: true));
                    }
                    break;
                case RedisType.Hash:
                    foreach ((RowKey field, ChangedRow row) in edits.Changed)
                    {
                        transaction.AddCondition(Condition.HashEqual(key, Text(field.Values[0]), Text(row.Read[1])));
                        writes.Add(transaction.HashSetAsync(key, Text(field.Values[0]), row.Written.GetValueOrDefault(1) ?? string.Empty));
                    }
                    foreach ((RowKey field, object?[] read) in edits.Deleted)
                    {
                        transaction.AddCondition(Condition.HashEqual(key, Text(field.Values[0]), Text(read[1])));
                        writes.Add(transaction.HashDeleteAsync(key, Text(field.Values[0])));
                    }
                    foreach (AddedRow added in edits.Added)
                    {
                        string field = added.Written.GetValueOrDefault(0) ?? throw new InvalidOperationException("A hash's field added needs its name");
                        transaction.AddCondition(Condition.HashNotExists(key, field));
                        writes.Add(transaction.HashSetAsync(key, field, added.Written.GetValueOrDefault(1) ?? string.Empty));
                    }
                    break;
                case RedisType.List:
                    // A deleted item is marked first, then every mark removed, so the indexes of the rest hold meanwhile.
                    string mark = $"passenger-deleted-{Guid.NewGuid():N}";
                    foreach ((RowKey index, ChangedRow row) in edits.Changed)
                    {
                        transaction.AddCondition(Condition.ListIndexEqual(key, Convert.ToInt64(index.Values[0], CultureInfo.InvariantCulture), Text(row.Read[1])));
                        writes.Add(transaction.ListSetByIndexAsync(key, Convert.ToInt64(index.Values[0], CultureInfo.InvariantCulture), row.Written.GetValueOrDefault(1) ?? string.Empty));
                    }
                    foreach ((RowKey index, object?[] read) in edits.Deleted)
                    {
                        transaction.AddCondition(Condition.ListIndexEqual(key, Convert.ToInt64(index.Values[0], CultureInfo.InvariantCulture), Text(read[1])));
                        writes.Add(transaction.ListSetByIndexAsync(key, Convert.ToInt64(index.Values[0], CultureInfo.InvariantCulture), mark));
                    }
                    if (!edits.Deleted.IsEmpty)
                    {
                        writes.Add(transaction.ListRemoveAsync(key, mark));
                    }
                    foreach (AddedRow added in edits.Added)
                    {
                        writes.Add(transaction.ListRightPushAsync(key, added.Written.GetValueOrDefault(1) ?? string.Empty));
                    }
                    break;
                case RedisType.Set:
                    foreach (RowKey member in edits.Deleted.Keys)
                    {
                        transaction.AddCondition(Condition.SetContains(key, Text(member.Values[0])));
                        writes.Add(transaction.SetRemoveAsync(key, Text(member.Values[0])));
                    }
                    foreach (AddedRow added in edits.Added)
                    {
                        writes.Add(transaction.SetAddAsync(key, added.Written.GetValueOrDefault(0) ?? throw new InvalidOperationException("A set's member added needs its words")));
                    }
                    break;
                case RedisType.SortedSet:
                    foreach ((RowKey member, ChangedRow row) in edits.Changed)
                    {
                        transaction.AddCondition(Condition.SortedSetEqual(key, Text(member.Values[0]), Score(Text(row.Read[1]))));
                        writes.Add(transaction.SortedSetAddAsync(key, Text(member.Values[0]), Score(row.Written.GetValueOrDefault(1))));
                    }
                    foreach ((RowKey member, object?[] read) in edits.Deleted)
                    {
                        transaction.AddCondition(Condition.SortedSetEqual(key, Text(member.Values[0]), Score(Text(read[1]))));
                        writes.Add(transaction.SortedSetRemoveAsync(key, Text(member.Values[0])));
                    }
                    foreach (AddedRow added in edits.Added)
                    {
                        string member = added.Written.GetValueOrDefault(0) ?? throw new InvalidOperationException("A sorted set's member added needs its words");
                        transaction.AddCondition(Condition.SortedSetNotContains(key, member));
                        writes.Add(transaction.SortedSetAddAsync(key, member, Score(added.Written.GetValueOrDefault(1))));
                    }
                    break;
                case RedisType.Stream:
                    if (!edits.Changed.IsEmpty)
                    {
                        throw new InvalidOperationException(Refusals.StreamEntriesFixed.English);
                    }
                    foreach (RowKey entry in edits.Deleted.Keys)
                    {
                        writes.Add(transaction.StreamDeleteAsync(key, [Text(entry.Values[0])]));
                    }
                    foreach (AddedRow added in edits.Added)
                    {
                        NameValueEntry[] fields = [.. added.Written.Where(w => w.Key > 0 && w.Value is not null).Select(w => new NameValueEntry(page.Columns[w.Key], w.Value!))];
                        writes.Add(transaction.StreamAddAsync(key, fields.Length > 0 ? fields : throw new InvalidOperationException("A stream's entry added needs a field")));
                    }
                    break;
                default:
                    throw new InvalidOperationException($"{key} is no longer a key of {source.Database}: read the database again");
            }
            // Once EXEC ran, its writes are the database's whatever Stop asks after: the changes are said written.
            if (!await transaction.ExecuteAsync())
            {
                throw new InvalidOperationException(Refusals.KeyChanged(schema.Table.Name).English);
            }
            await Task.WhenAll(writes);
            return true;
        });

    /// <summary>Runs each of a console's commands in turn as <see cref="DatabaseClient.RunAsync"/> does, against the
    /// connection's database, each reply as rows: a value alone, a list's items each a row, a map's or pairs' each a row of
    /// two; a failure ends the run.</summary>
    internal static async Task RunAsync(DatabaseSource source, IReadOnlyList<string> runs, int limit, Action<SqlResult> told, CancellationToken cancel)
    {
        await using ConnectionMultiplexer redis = await Guarded(source, () => ConnectAsync(source));
        IDatabase database = redis.GetDatabase(RedisNames.Number(source.Database));
        foreach (string run in runs)
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                List<string> words = RedisCommands.Words(run);
                if (words.Count == 0)
                {
                    continue;
                }
                RedisResult reply = await database.ExecuteAsync(words[0], [.. words.Skip(1)]);
                cancel.ThrowIfCancellationRequested();
                told(new SqlResult(run, [Rows(reply, limit, Paired(words))], -1, null, System.Diagnostics.Stopwatch.GetElapsedTime(began)));
            }
            catch (Exception failed) when (failed is RedisException or FormatException or InvalidOperationException or ArgumentException)
            {
                told(new SqlResult(run, [], -1, failed.Message, System.Diagnostics.Stopwatch.GetElapsedTime(began)));
                return;
            }
        }
    }

    /// <summary>The database's keys (to <see cref="MostKeys"/>), each a table of its type, for a console's completion.</summary>
    internal static async Task<SqlCatalog> CatalogAsync(DatabaseSource source, CancellationToken cancel) =>
        new([.. (await KeysAsync(source, cancel)).SelectMany(g => g.Objects.Select(k => new SqlTableInfo(g.Schema, k.Name, [])))]);

    /// <summary>A key's time to live now; null for none, or a key that is gone.</summary>
    internal static async Task<TimeSpan?> LifeAsync(DatabaseSource source, string key, CancellationToken cancel) => await Guarded(source, async () =>
    {
        await using ConnectionMultiplexer redis = await ConnectAsync(source);
        TimeSpan? life = await redis.GetDatabase(RedisNames.Number(source.Database)).KeyTimeToLiveAsync(key);
        cancel.ThrowIfCancellationRequested();
        return life;
    });

    /// <summary>Sets a key's time to live, or takes it away (none); false where the key is gone.</summary>
    internal static async Task<bool> ExpireAsync(DatabaseSource source, string key, TimeSpan? life, CancellationToken cancel) => await Guarded(source, async () =>
    {
        await using ConnectionMultiplexer redis = await ConnectAsync(source);
        IDatabase database = redis.GetDatabase(RedisNames.Number(source.Database));
        bool done = life is { } given ? await database.KeyExpireAsync(key, given) : await database.KeyExistsAsync(key);
        if (life is null && done)
        {
            await database.KeyPersistAsync(key);
        }
        cancel.ThrowIfCancellationRequested();
        return done;
    });

    /// <summary>A key's time to live as the explorer says it.</summary>
    public static string LifeSaid(TimeSpan? life) => life is { } left ? $"expires in {Passenger.Core.Durations.Said(left)}" : "no expiry";

    private static string TypeName(RedisType type) => type switch
    {
        RedisType.SortedSet => "sorted set",
        _ => type.ToString().ToLowerInvariant(),
    };

    /// <summary>Whether a command's reply is pairs, as HGETALL's fields and values, CONFIG GET's names and values and a
    /// sorted set's members and scores WITHSCORES are.</summary>
    private static bool Paired(List<string> words) => words[0].ToUpperInvariant() switch
    {
        "HGETALL" => true,
        "CONFIG" => words.Count > 1 && words[1].Equals("GET", StringComparison.OrdinalIgnoreCase),
        "ZRANGE" or "ZREVRANGE" or "ZRANGEBYSCORE" or "ZREVRANGEBYSCORE" or "HRANDFIELD" or "ZRANDMEMBER" =>
            words.Any(w => w.Equals("WITHSCORES", StringComparison.OrdinalIgnoreCase) || w.Equals("WITHVALUES", StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    /// <summary>A reply as rows: nothing, a value, a list's items each a row, a map's or pairs' entries each a row of two.</summary>
    private static DatabaseRows Rows(RedisResult reply, int limit, bool paired)
    {
        if (reply.Resp3Type == ResultType.Map || paired && reply.Resp2Type == ResultType.Array && !reply.IsNull && reply.Length % 2 == 0
            && ((RedisResult[])reply!).All(i => i.Resp2Type != ResultType.Array))
        {
            RedisResult[] pairs = (RedisResult[])reply!;
            object?[][] rows = [.. Enumerable.Range(0, pairs.Length / 2).Take(limit).Select(i => new object?[] { Said(pairs[2 * i]), Said(pairs[2 * i + 1]) })];
            return new DatabaseRows(["name", "value"], rows, pairs.Length / 2 > limit);
        }
        if (reply.Resp2Type == ResultType.Array && !reply.IsNull)
        {
            RedisResult[] items = (RedisResult[])reply!;
            // Pairs RESP3 sends each as an array of two (ZRANGE … WITHSCORES), and a stream's entries, are rows of two.
            if (items.Length > 0 && items.All(i => i.Resp2Type == ResultType.Array && !i.IsNull && i.Length == 2))
            {
                return new DatabaseRows(["name", "value"], [.. items.Take(limit).Select(i => new object?[] { Said(((RedisResult[])i!)[0]), Said(((RedisResult[])i!)[1]) })],
                    items.Length > limit);
            }
            return new DatabaseRows(["#", "value"], [.. items.Take(limit).Select((item, at) => new object?[] { (long)(at + 1), Said(item) })], items.Length > limit);
        }
        return new DatabaseRows(["value"], [[Said(reply)]]);
    }

    /// <summary>A reply's value: null for nothing, a number as itself, an array as its items in brackets, any other its words.</summary>
    private static object? Said(RedisResult reply) => reply.IsNull ? null : reply.Resp2Type switch
    {
        ResultType.Integer => (long)reply,
        ResultType.Array => "[" + string.Join(", ", ((RedisResult[])reply!).Select(r => Convert.ToString(Said(r), CultureInfo.InvariantCulture) ?? "nil")) + "]",
        _ => reply.ToString(),
    };

    private static double Score(string? text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double score)
        ? score : throw new InvalidOperationException($"A score is a number, not {text}");

    /// <summary>The connection's options: its StackExchange.Redis configuration as written, else its host, port, user and
    /// password; a client of admin commands (INFO, SCAN), failing at once where the server answers nothing.</summary>
    private static async Task<ConnectionMultiplexer> ConnectAsync(DatabaseSource source)
    {
        string connection = DatabaseClient.Whole(source);
        ConfigurationOptions options;
        if (DatabaseKinds.Value(connection, "Configuration") is { } configuration)
        {
            options = ConfigurationOptions.Parse(configuration);
        }
        else
        {
            (string host, int port) = DatabaseKinds.Endpoint(DatabaseKind.Redis, connection);
            options = new ConfigurationOptions { EndPoints = { { host, port } }, Ssl = DatabaseKinds.Value(connection, "Ssl") is "true" or "True" };
            options.User = DatabaseKinds.Value(connection, "Username", "User");
            options.Password = DatabaseKinds.Value(connection, "Password");
        }
        options.AllowAdmin = true;
        options.AbortOnConnectFail = true;
        options.ConnectTimeout = 10000;
        options.ClientName = "Passenger";
        return await ConnectionMultiplexer.ConnectAsync(options);
    }

    private static IServer Server(ConnectionMultiplexer redis) => redis.GetServer(redis.GetEndPoints()[0]);

    /// <summary>Runs a client's work, its own failures said as an <see cref="IOException"/> holding no secret.</summary>
    private static async Task<T> Guarded<T>(DatabaseSource source, Func<Task<T>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception failed) when (failed is RedisException or ArgumentException or FormatException)
        {
            throw new IOException(Containers.ContainerSecrets.InSaid(failed.Message, known: [source.ConnectionString]), failed);
        }
    }
}
