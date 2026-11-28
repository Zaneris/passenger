using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Passenger.App.Database;

/// <summary>A call of MongoDB's shell a console runs: on a collection (db.orders.find(…)), or on the database itself
/// (db.getCollectionNames(), db.runCommand(…)) where <see cref="Collection"/> is null, with the calls chained after it
/// (.sort(…).limit(…)), each argument read as MongoDB's shell writes values.</summary>
internal sealed record ShellCall(string? Collection, string Method, IReadOnlyList<BsonValue> Arguments, IReadOnlyList<(string Method, IReadOnlyList<BsonValue> Arguments)> Chain);

/// <summary>MongoDB's shell as a console writes it: its statements, each a call on db ending at a semicolon or at a line
/// that begins another (a chained .sort(…) on the next line goes on); what each does to the database; and each run
/// against the database through the driver, its documents as rows. Values are read by the driver's own JSON reader, which
/// takes the shell's words (unquoted names, ObjectId(…), ISODate(…), /regex/).</summary>
internal static partial class DocumentShell
{
    /// <summary>A statement read as a call: <c>show collections</c> and <c>show dbs</c>; db.name.method(…), db["name"] and
    /// db.getCollection("name") for a collection; or a call on db. Throws a <see cref="FormatException"/> saying what it lacks.</summary>
    public static ShellCall Parse(string run)
    {
        string text = run.Trim().TrimEnd(';').Trim();
        if (text.StartsWith("show ", StringComparison.Ordinal))
        {
            string what = text[5..].Trim();
            return what is "collections" or "tables" or "dbs" or "databases" ? new ShellCall(null, "show", [new BsonString(what)], [])
                : throw new FormatException($"show {what} is not known here: show collections or show dbs");
        }
        int at = 0;
        Space(text, ref at);
        if (Word(text, ref at) != "db")
        {
            throw new FormatException("A statement begins with db: db.orders.find({ qty: { $gt: 1 } })");
        }
        string? collection = null;
        string method;
        IReadOnlyList<BsonValue> arguments;
        if (At(text, at) == '[')
        {
            collection = Bracketed(text, ref at);
            method = Member(text, ref at);
            arguments = Arguments(text, ref at);
        }
        else
        {
            string first = Member(text, ref at);
            if (At(text, at) == '(')
            {
                arguments = Arguments(text, ref at);
                if (first == "getCollection" && At(text, at) == '.')
                {
                    collection = arguments is [BsonString named] ? named.AsString : throw new FormatException("getCollection takes the collection's name");
                    method = Member(text, ref at);
                    arguments = Arguments(text, ref at);
                }
                else
                {
                    method = first;
                }
            }
            else
            {
                collection = first;
                method = Member(text, ref at);
                arguments = Arguments(text, ref at);
            }
        }
        List<(string, IReadOnlyList<BsonValue>)> chain = [];
        while (At(text, at) == '.')
        {
            string next = Member(text, ref at);
            chain.Add((next, Arguments(text, ref at)));
        }
        Space(text, ref at);
        return at < text.Length ? throw new FormatException($"Nothing may follow the call: {text[at..]}") : new ShellCall(collection, method, arguments, chain);
    }

    /// <summary>Runs a statement against a database: its documents as one set of rows (at most <paramref name="limit"/>), or
    /// the documents it wrote, changed or deleted.</summary>
    public static async Task<(IReadOnlyList<DatabaseRows> Sets, int Affected)> RunAsync(IMongoDatabase database, string run, int limit, CancellationToken cancel)
    {
        ShellCall call = Parse(run);
        if (call.Collection is null)
        {
            return await OnDatabaseAsync(database, call, limit, cancel);
        }
        IMongoCollection<BsonDocument> collection = database.GetCollection<BsonDocument>(call.Collection);
        BsonDocument Document(int at) => call.Arguments.Count > at ? call.Arguments[at] is BsonDocument document ? document
            : throw new FormatException($"{call.Method}'s argument {(at + 1).ToString(CultureInfo.InvariantCulture)} is no document") : [];
        switch (call.Method)
        {
            case "find" or "findOne":
                IFindFluent<BsonDocument, BsonDocument> found = collection.Find(Document(0));
                if (call.Arguments.Count > 1)
                {
                    found = found.Project(Document(1));
                }
                int most = call.Method == "findOne" ? 1 : limit;
                bool counted = false;
                foreach ((string method, IReadOnlyList<BsonValue> arguments) in call.Chain)
                {
                    switch (method)
                    {
                        case "sort" when arguments is [BsonDocument order]:
                            found = found.Sort(order);
                            break;
                        case "skip" when arguments is [{ IsNumeric: true } skip]:
                            found = found.Skip(skip.ToInt32());
                            break;
                        case "limit" when arguments is [{ IsNumeric: true } taken]:
                            most = Math.Min(most, Math.Max(1, Math.Abs(taken.ToInt32())));
                            break;
                        case "projection" or "project" when arguments is [BsonDocument shape]:
                            found = found.Project(shape);
                            break;
                        case "count" or "itcount" or "size":
                            counted = true;
                            break;
                        case "toArray" or "pretty" or "batchSize":
                            break;
                        default:
                            throw new FormatException($".{method}(…) is not taken after find");
                    }
                }
                if (counted)
                {
                    return ([Single("count", await collection.CountDocumentsAsync(Document(0), cancellationToken: cancel))], -1);
                }
                List<BsonDocument> read = await found.Limit(most + 1).ToListAsync(cancel);
                return ([DocumentClient.Rows([.. read.Take(most)], read.Count > most)], -1);
            case "aggregate":
                BsonDocument[] stages = call.Arguments is [BsonArray pipeline, ..] ? [.. pipeline.Select(s => s as BsonDocument ?? throw new FormatException("A pipeline's stages are documents"))]
                    : throw new FormatException("aggregate takes its pipeline: db.orders.aggregate([{ $match: { … } }])");
                List<BsonDocument> aggregated = await (await collection.AggregateAsync<BsonDocument>(stages, cancellationToken: cancel)).ToListAsync(cancel);
                return ([DocumentClient.Rows([.. aggregated.Take(limit)], aggregated.Count > limit)], -1);
            case "countDocuments" or "count":
                return ([Single("count", await collection.CountDocumentsAsync(Document(0), cancellationToken: cancel))], -1);
            case "estimatedDocumentCount":
                return ([Single("count", await collection.EstimatedDocumentCountAsync(cancellationToken: cancel))], -1);
            case "distinct":
                string field = call.Arguments is [BsonString named, ..] ? named.AsString : throw new FormatException("distinct takes the field's name first");
                List<BsonValue> values = await (await collection.DistinctAsync<BsonValue>(field, Document(1), cancellationToken: cancel)).ToListAsync(cancel);
                return ([DocumentClient.Rows([.. values.Take(limit).Select(v => new BsonDocument(field, v))], values.Count > limit)], -1);
            case "getIndexes":
                return ([DocumentClient.Rows(await (await collection.Indexes.ListAsync(cancel)).ToListAsync(cancel), false)], -1);
            case "insertOne":
                await collection.InsertOneAsync(Document(0), cancellationToken: cancel);
                return ([], 1);
            case "insertMany":
                BsonDocument[] many = call.Arguments is [BsonArray documents, ..] ? [.. documents.Select(d => d as BsonDocument ?? throw new FormatException("insertMany takes documents"))]
                    : throw new FormatException("insertMany takes an array of documents");
                await collection.InsertManyAsync(many, cancellationToken: cancel);
                return ([], many.Length);
            case "updateOne" or "updateMany":
                UpdateDefinition<BsonDocument> update = call.Arguments.Count > 1 && call.Arguments[1] is BsonArray stagesOfUpdate
                    ? new PipelineUpdateDefinition<BsonDocument>(new BsonDocumentStagePipelineDefinition<BsonDocument, BsonDocument>(stagesOfUpdate.OfType<BsonDocument>()))
                    : new BsonDocumentUpdateDefinition<BsonDocument>(Document(1));
                UpdateOptions upsert = new() { IsUpsert = call.Arguments.Count > 2 && Document(2).GetValue("upsert", false).ToBoolean() };
                UpdateResult updated = call.Method == "updateOne"
                    ? await collection.UpdateOneAsync(Document(0), update, upsert, cancel)
                    : await collection.UpdateManyAsync(Document(0), update, upsert, cancel);
                return ([], (int)Math.Min(int.MaxValue, updated.IsModifiedCountAvailable ? updated.ModifiedCount : updated.MatchedCount));
            case "replaceOne":
                ReplaceOneResult replaced = await collection.ReplaceOneAsync(Document(0), Document(1),
                    new ReplaceOptions { IsUpsert = call.Arguments.Count > 2 && Document(2).GetValue("upsert", false).ToBoolean() }, cancel);
                return ([], (int)Math.Min(int.MaxValue, replaced.IsModifiedCountAvailable ? replaced.ModifiedCount : replaced.MatchedCount));
            case "deleteOne" or "deleteMany":
                DeleteResult deleted = call.Method == "deleteOne" ? await collection.DeleteOneAsync(Document(0), cancel) : await collection.DeleteManyAsync(Document(0), cancel);
                return ([], (int)Math.Min(int.MaxValue, deleted.DeletedCount));
            case "createIndex":
                BsonDocument given = Document(1);
                CreateIndexOptions options = new()
                {
                    Name = given.TryGetValue("name", out BsonValue indexName) ? indexName.AsString : null,
                    Unique = given.TryGetValue("unique", out BsonValue unique) ? unique.ToBoolean() : null,
                    Sparse = given.TryGetValue("sparse", out BsonValue sparse) ? sparse.ToBoolean() : null,
                };
                string index = await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Document(0), options), cancellationToken: cancel);
                return ([Single("index", index)], 0);
            case "dropIndex":
                await collection.Indexes.DropOneAsync(call.Arguments is [BsonString name] ? name.AsString
                    : throw new FormatException("dropIndex takes the index's name, as getIndexes() says it"), cancel);
                return ([], 0);
            case "drop":
                await database.DropCollectionAsync(call.Collection, cancel);
                return ([], 0);
            default:
                throw new FormatException($"{call.Method} is not run here: find, findOne, aggregate, countDocuments, distinct, insertOne, insertMany, updateOne, "
                    + "updateMany, replaceOne, deleteOne, deleteMany, createIndex, dropIndex, getIndexes or drop");
        }
    }

    /// <summary>A call on the database itself.</summary>
    private static async Task<(IReadOnlyList<DatabaseRows>, int)> OnDatabaseAsync(IMongoDatabase database, ShellCall call, int limit, CancellationToken cancel)
    {
        switch (call.Method)
        {
            case "show" when call.Arguments is [BsonString { Value: "dbs" or "databases" }]:
                List<string> databases = await (await database.Client.ListDatabaseNamesAsync(cancel)).ToListAsync(cancel);
                return ([DocumentClient.Rows([.. databases.Select(d => new BsonDocument("name", d))], false)], -1);
            case "show" or "getCollectionNames":
                List<string> names = await (await database.ListCollectionNamesAsync(cancellationToken: cancel)).ToListAsync(cancel);
                return ([DocumentClient.Rows([.. names.Order(StringComparer.Ordinal).Select(n => new BsonDocument("name", n))], false)], -1);
            case "runCommand" when call.Arguments is [BsonDocument command, ..]:
                BsonDocument answer = await database.RunCommandAsync<BsonDocument>(command, cancellationToken: cancel);
                // A cursor's first batch is its documents.
                return answer.TryGetValue("cursor", out BsonValue cursor) && cursor is BsonDocument held && held.TryGetValue("firstBatch", out BsonValue batch) && batch is BsonArray first
                    ? ([DocumentClient.Rows([.. first.OfType<BsonDocument>().Take(limit)], first.Count > limit)], -1)
                    : ([DocumentClient.Rows([answer], false)], -1);
            case "createCollection" when call.Arguments is [BsonString name, ..]:
                await database.CreateCollectionAsync(name.AsString, cancellationToken: cancel);
                return ([], 0);
            case "dropDatabase":
                await database.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName, cancel);
                return ([], 0);
            default:
                throw new FormatException($"db.{call.Method}(…) is not run here: getCollectionNames, runCommand, createCollection or dropDatabase, or a collection's call");
        }
    }

    private static DatabaseRows Single(string name, BsonValue value) => DocumentClient.Rows([new BsonDocument(name, value)], false);

    private static char At(string text, int at) => at < text.Length ? text[at] : '\0';

    private static void Space(string text, ref int at)
    {
        while (at < text.Length)
        {
            if (char.IsWhiteSpace(text[at]))
            {
                at++;
            }
            else if (text[at] == '/' && Skipped(text, at) is { } past)
            {
                at = past;
            }
            else
            {
                return;
            }
        }
    }

    private static string Word(string text, ref int at)
    {
        int start = at;
        while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] is '_' or '$' or '-'))
        {
            at++;
        }
        return text[start..at];
    }

    /// <summary>A member after a dot: a name, or a bracketed string.</summary>
    private static string Member(string text, ref int at)
    {
        Space(text, ref at);
        if (At(text, at) == '[')
        {
            return Bracketed(text, ref at);
        }
        if (At(text, at) != '.')
        {
            throw new FormatException($"A call goes on with a dot: {(at < text.Length ? text[at..] : "the statement ends")}");
        }
        at++;
        Space(text, ref at);
        string name = Word(text, ref at);
        return name.Length > 0 ? name : throw new FormatException("A name follows the dot");
    }

    /// <summary>A name in brackets and quotes: db["order items"].</summary>
    private static string Bracketed(string text, ref int at)
    {
        int open = at;
        int close = text.IndexOf(']', open);
        if (close < 0 || BsonValueOf(text[(open + 1)..close]) is not BsonString name)
        {
            throw new FormatException("A bracketed name is a string: db[\"order items\"]");
        }
        at = close + 1;
        return name.AsString;
    }

    /// <summary>A call's arguments between its parentheses, each read as a value.</summary>
    private static IReadOnlyList<BsonValue> Arguments(string text, ref int at)
    {
        Space(text, ref at);
        if (At(text, at) != '(')
        {
            throw new FormatException("A call takes its arguments in parentheses: find({ … })");
        }
        List<BsonValue> arguments = [];
        int depth = 0;
        int start = at + 1;
        for (int i = at; i < text.Length;)
        {
            if (Skipped(text, i) is { } past && !(text[i] == '/' && depth > 0 && Regex(text, i)))
            {
                i = past;
                continue;
            }
            char c = text[i];
            if (c == '/' && depth > 0 && Regex(text, i))
            {
                i = RegexEnd(text, i);
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    Add(text[start..i]);
                    at = i + 1;
                    return arguments;
                }
            }
            else if (c == ',' && depth == 1)
            {
                Add(text[start..i]);
                start = i + 1;
            }
            i++;
        }
        throw new FormatException("A call's parentheses are not closed");

        void Add(string argument)
        {
            if (argument.Trim().Length > 0 || arguments.Count > 0)
            {
                arguments.Add(BsonValueOf(argument));
            }
        }
    }

    /// <summary>Whether a slash starts a regular expression, standing where a value does (after a colon, comma or bracket).</summary>
    private static bool Regex(string text, int at)
    {
        int before = at - 1;
        while (before >= 0 && char.IsWhiteSpace(text[before]))
        {
            before--;
        }
        return before >= 0 && text[before] is ':' or ',' or '(' or '[' && at + 1 < text.Length && text[at + 1] is not ('/' or '*');
    }

    private static int RegexEnd(string text, int at)
    {
        for (int i = at + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == '/')
            {
                int end = i + 1;
                while (end < text.Length && char.IsLetter(text[end]))
                {
                    end++;
                }
                return end;
            }
        }
        return text.Length;
    }

    /// <summary>A value as the shell writes it, read by the driver's JSON reader inside a document of its own.</summary>
    private static BsonValue BsonValueOf(string written)
    {
        try
        {
            return BsonDocument.Parse("{ \"v\": " + written.Trim() + " }")["v"];
        }
        catch (Exception unread) when (unread is FormatException or BsonException or EndOfStreamException or ArgumentException)
        {
            throw new FormatException($"Not a value MongoDB's shell writes: {written.Trim()} ({unread.Message})", unread);
        }
    }
}
