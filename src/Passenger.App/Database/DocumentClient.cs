using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Azure.Cosmos;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace Passenger.App.Database;

/// <summary>A field of a document that is itself a document or an array: shown as its JSON, and changed only by its whole
/// document, never by a cell.</summary>
public sealed record JsonText(string Json)
{
    public override string ToString() => Json;
}

/// <summary>MongoDB's and Cosmos DB's documents as the Database window reads and writes them, through each one's own client:
/// a database's collections (Cosmos DB's containers), a collection's fields as a sample of its documents has them, a page of
/// its documents as rows of their top-level fields with each whole document beside, and a page's changes written document
/// by document. Every failure of a client is said as an <see cref="IOException"/>, as a relational driver's is.</summary>
internal static class DocumentClient
{
    /// <summary>The documents a collection's fields are read from.</summary>
    public const int Sampled = 100;

    private static readonly JsonWriterSettings Relaxed = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

    /// <summary>The field a document is found again by: MongoDB's _id, Cosmos DB's id.</summary>
    public static string KeyOf(DatabaseKind kind) => kind == DatabaseKind.MongoDb ? "_id" : "id";

    internal static async Task<IReadOnlyList<string>> DatabasesAsync(DatabaseSource source, CancellationToken cancel) => await Guarded<IReadOnlyList<string>>(source, async () =>
    {
        if (source.Kind == DatabaseKind.MongoDb)
        {
            using MongoClient client = Mongo(source);
            List<string> names = await (await client.ListDatabaseNamesAsync(cancel)).ToListAsync(cancel);
            // A database is made by its first write, so the one the connection names is listed before it holds anything.
            return [.. names.Where(n => n is not ("admin" or "config" or "local")).Append(source.Database).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }
        using CosmosClient cosmos = Cosmos(source);
        List<string> databases = [];
        using FeedIterator<DatabaseProperties> read = cosmos.GetDatabaseQueryIterator<DatabaseProperties>();
        while (read.HasMoreResults)
        {
            databases.AddRange((await read.ReadNextAsync(cancel)).Select(d => d.Id));
        }
        return [.. databases.Order(StringComparer.Ordinal)];
    });

    /// <summary>A database's collections (MongoDB's views as views) or containers, each container with its partition key's path.</summary>
    internal static async Task<IReadOnlyList<(string Schema, IReadOnlyList<DatabaseObject> Objects)>> CollectionsAsync(DatabaseSource source, CancellationToken cancel) =>
        await Guarded<IReadOnlyList<(string Schema, IReadOnlyList<DatabaseObject> Objects)>>(source, async () =>
        {
            List<DatabaseObject> objects = [];
            if (source.Kind == DatabaseKind.MongoDb)
            {
                using MongoClient client = Mongo(source);
                List<BsonDocument> listed = await (await client.GetDatabase(source.Database).ListCollectionsAsync(cancellationToken: cancel)).ToListAsync(cancel);
                objects.AddRange(listed.Select(c => (Name: c["name"].AsString, Type: c.GetValue("type", "collection").AsString))
                    .Where(c => !c.Name.StartsWith("system.", StringComparison.Ordinal))
                    .Select(c => new DatabaseObject(string.Empty, c.Name, c.Type == "view" ? DatabaseObjectKind.View : DatabaseObjectKind.Table)));
            }
            else
            {
                using CosmosClient cosmos = Cosmos(source);
                using FeedIterator<ContainerProperties> read = cosmos.GetDatabase(source.Database).GetContainerQueryIterator<ContainerProperties>();
                while (read.HasMoreResults)
                {
                    objects.AddRange((await read.ReadNextAsync(cancel)).Select(c => new DatabaseObject(string.Empty, c.Id, DatabaseObjectKind.Table, c.PartitionKeyPath ?? string.Empty)));
                }
            }
            return [(string.Empty, [.. objects.OrderBy(o => o.Name, StringComparer.Ordinal)])];
        });

    /// <summary>A collection's fields as its first <see cref="Sampled"/> documents have them, the key first, each with the kind
    /// of value most of them hold and whether any lacks it; its key and Cosmos DB's partition key; MongoDB's indexes.</summary>
    internal static async Task<DatabaseSchema> SchemaAsync(DatabaseSource source, DatabaseTable table, CancellationToken cancel) => await Guarded<DatabaseSchema>(source, async () =>
    {
        List<DatabasePart> indexes = [];
        List<string> keys = [KeyOf(source.Kind)];
        IReadOnlyList<Dictionary<string, object?>> sample;
        if (source.Kind == DatabaseKind.MongoDb)
        {
            using MongoClient client = Mongo(source);
            IMongoCollection<BsonDocument> collection = client.GetDatabase(source.Database).GetCollection<BsonDocument>(table.Name);
            List<BsonDocument> read = await collection.Find(FilterDefinition<BsonDocument>.Empty).Limit(Sampled).ToListAsync(cancel);
            sample = [.. read.Select(Fields)];
            if (await IsCollectionAsync(client, source.Database, table.Name, cancel))
            {
                foreach (BsonDocument index in await (await collection.Indexes.ListAsync(cancel)).ToListAsync(cancel))
                {
                    string name = index.GetValue("name", string.Empty).AsString;
                    indexes.Add(new DatabasePart(name, $"{name} {index.GetValue("key", new BsonDocument()).ToJson(Relaxed)}"
                        + (index.GetValue("unique", false).ToBoolean() ? " unique" : string.Empty)));
                }
            }
        }
        else
        {
            using CosmosClient cosmos = Cosmos(source);
            Container container = cosmos.GetDatabase(source.Database).GetContainer(table.Name);
            ContainerProperties properties = (await container.ReadContainerAsync(cancellationToken: cancel)).Resource;
            keys.Add($"partition key {properties.PartitionKeyPath}");
            sample = [.. (await QueryAsync(container, new QueryDefinition($"SELECT TOP {Sampled.ToString(CultureInfo.InvariantCulture)} * FROM c"), Sampled, cancel)).Items.Select(Fields)];
        }
        return new DatabaseSchema(table, Columns(source.Kind, sample), keys, indexes);
    });

    /// <summary>A page of a collection's documents as its table tab reads one: filtered by a field and sorted by one on the
    /// server, its columns the collection's fields with any this page holds besides.</summary>
    internal static async Task<DatabaseRows> PageAsync(DatabaseSource source, DatabaseSchema schema, int offset, CancellationToken cancel, DatabaseFilter? filter,
        (string Column, bool Descending)? sort) => await Guarded<DatabaseRows>(source, async () =>
    {
        int take = DatabaseClient.PageSize + 1;
        List<(Dictionary<string, object?> Fields, string Json)> read;
        if (source.Kind == DatabaseKind.MongoDb)
        {
            using MongoClient client = Mongo(source);
            IMongoCollection<BsonDocument> collection = client.GetDatabase(source.Database).GetCollection<BsonDocument>(schema.Table.Name);
            IFindFluent<BsonDocument, BsonDocument> found = collection.Find(MongoFilter(filter));
            if (sort is { } by)
            {
                found = found.Sort(new BsonDocument(by.Column, by.Descending ? -1 : 1));
            }
            List<BsonDocument> documents = await found.Skip(offset).Limit(take).ToListAsync(cancel);
            read = [.. documents.Select(d => (Fields(d), d.ToJson(Relaxed)))];
        }
        else
        {
            using CosmosClient cosmos = Cosmos(source);
            Container container = cosmos.GetDatabase(source.Database).GetContainer(schema.Table.Name);
            StringBuilder sql = new("SELECT * FROM c");
            QueryDefinition query = CosmosFilter(sql, filter, sort, offset, take);
            read = [.. (await QueryAsync(container, query, take, cancel)).Items.Select(i => (Fields(i), i.ToJsonString()))];
        }
        bool more = read.Count > DatabaseClient.PageSize;
        read = [.. read.Take(DatabaseClient.PageSize)];
        string[] columns = [.. schema.Columns.Select(c => c.Name).Concat(read.SelectMany(r => r.Fields.Keys)).Distinct(StringComparer.Ordinal)];
        DatabaseSchema widened = schema with
        {
            Columns = [.. schema.Columns, .. columns.Skip(schema.Columns.Count).Select(name => new DatabaseColumn(name, "mixed", true, false, IsSystemField(source.Kind, name)))],
        };
        return new DatabaseRows(columns, [.. read.Select(r => columns.Select(c => Of(r.Fields, c)).ToArray())], more)
        {
            Schema = widened,
            Offset = offset,
            Types = [.. columns.Select(c => widened.Columns.First(w => w.Name == c).Type)],
            Documents = [.. read.Select(r => r.Json)],
            OrderedBy = sort is { } ordered && Array.IndexOf(columns, ordered.Column) is >= 0 and int at ? (at, ordered.Descending) : null,
        };
    });

    /// <summary>Writes a page's changes document by document: those deleted, then each changed one's fields set (a cell made
    /// NULL setting null), then each added one made of the fields written, its key the database's own where none was.</summary>
    internal static async Task SubmitAsync(DatabaseSource source, DatabaseSchema schema, DatabaseRows page, PendingEdits edits, CancellationToken cancel) =>
        await Guarded<bool>(source, async () =>
        {
            string key = KeyOf(source.Kind);
            int keyAt = Array.IndexOf(page.Columns, key);
            if (source.Kind == DatabaseKind.MongoDb)
            {
                using MongoClient client = Mongo(source);
                IMongoCollection<BsonDocument> collection = client.GetDatabase(source.Database).GetCollection<BsonDocument>(schema.Table.Name);
                // A document changed since it was read stops every write, as no transaction holds them together.
                foreach ((RowKey changed, ChangedRow row) in edits.Changed)
                {
                    BsonDocument read = new(key, ToBson(changed.Values[0]));
                    read.AddRange(row.Written.Keys.Select(column => new BsonElement(page.Columns[column], row.Read[column] is AbsentField
                        ? new BsonDocument("$exists", false) : ToBson(row.Read[column] is JsonText ? null : row.Read[column]))));
                    if (await collection.CountDocumentsAsync(read, cancellationToken: cancel) == 0)
                    {
                        throw new InvalidOperationException(Refusals.RowChanged(schema.Table.ToString(), changed.ToString()));
                    }
                }
                foreach (RowKey deleted in edits.Deleted.Keys)
                {
                    await collection.DeleteOneAsync(new BsonDocument(key, ToBson(deleted.Values[0])), cancel);
                }
                foreach ((RowKey changed, ChangedRow row) in edits.Changed)
                {
                    BsonDocument set = new(row.Written.Select(w => new BsonElement(page.Columns[w.Key], Written(w.Value, row.Read[w.Key]))));
                    await collection.UpdateOneAsync(new BsonDocument(key, ToBson(changed.Values[0])), new BsonDocument("$set", set), cancellationToken: cancel);
                }
                foreach (AddedRow added in edits.Added)
                {
                    await collection.InsertOneAsync(new BsonDocument(added.Written.Select(w => new BsonElement(page.Columns[w.Key], Written(w.Value, null)))), cancellationToken: cancel);
                }
                return true;
            }
            using CosmosClient cosmos = Cosmos(source);
            Container container = cosmos.GetDatabase(source.Database).GetContainer(schema.Table.Name);
            string path = (await container.ReadContainerAsync(cancellationToken: cancel)).Resource.PartitionKeyPath ?? string.Empty;
            // A document changed since it was read (its _etag moved) stops every write, as no transaction holds them together.
            foreach (RowKey changed in edits.Changed.Keys)
            {
                JsonObject read = DocumentOf(page, keyAt, changed);
                using ResponseMessage now = await container.ReadItemStreamAsync(Convert.ToString(changed.Values[0], CultureInfo.InvariantCulture), PartitionOf(read, path),
                    cancellationToken: cancel);
                if (!now.IsSuccessStatusCode || (await JsonNode.ParseAsync(now.Content, cancellationToken: cancel))?["_etag"]?.ToJsonString() != read["_etag"]?.ToJsonString())
                {
                    throw new InvalidOperationException(Refusals.RowChanged(schema.Table.ToString(), changed.ToString()));
                }
            }
            foreach (RowKey deleted in edits.Deleted.Keys)
            {
                JsonObject read = DocumentOf(page, keyAt, deleted);
                Checked(await container.DeleteItemStreamAsync(Convert.ToString(deleted.Values[0], CultureInfo.InvariantCulture), PartitionOf(read, path), cancellationToken: cancel));
            }
            foreach ((RowKey changed, ChangedRow row) in edits.Changed)
            {
                JsonObject document = DocumentOf(page, keyAt, changed);
                PartitionKey partition = PartitionOf(document, path);
                foreach ((int column, string? text) in row.Written)
                {
                    document[page.Columns[column]] = WrittenJson(text, row.Read[column]);
                }
                using MemoryStream body = new(Encoding.UTF8.GetBytes(document.ToJsonString()));
                Checked(await container.ReplaceItemStreamAsync(body, Convert.ToString(changed.Values[0], CultureInfo.InvariantCulture), partition, cancellationToken: cancel));
            }
            foreach (AddedRow added in edits.Added)
            {
                JsonObject document = [];
                foreach ((int column, string? text) in added.Written)
                {
                    document[page.Columns[column]] = WrittenJson(text, null);
                }
                document[key] ??= Guid.NewGuid().ToString("D");
                using MemoryStream body = new(Encoding.UTF8.GetBytes(document.ToJsonString()));
                Checked(await container.CreateItemStreamAsync(body, PartitionOf(document, path), cancellationToken: cancel));
            }
            return true;
        });

    /// <summary>Runs each of a console's runs in turn as <see cref="DatabaseClient.RunAsync"/> does: MongoDB's in its shell's
    /// words (<see cref="DocumentShell"/>), Cosmos DB's as its SQL against the container its FROM names.</summary>
    internal static async Task RunAsync(DatabaseSource source, IReadOnlyList<string> runs, int limit, Action<SqlResult> told, CancellationToken cancel)
    {
        if (source.Kind == DatabaseKind.CosmosDb && source.Database.Length == 0)
        {
            told(new SqlResult(runs.FirstOrDefault() ?? string.Empty, [], -1, Refusals.NoCosmosDatabase(source.Name), TimeSpan.Zero));
            return;
        }
        using MongoClient? mongo = source.Kind == DatabaseKind.MongoDb ? Mongo(source) : null;
        using CosmosClient? cosmos = source.Kind == DatabaseKind.CosmosDb ? Cosmos(source) : null;
        IReadOnlyList<string>? containers = null;
        foreach (string run in runs)
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                (IReadOnlyList<DatabaseRows> sets, int affected) = mongo is not null
                    ? await DocumentShell.RunAsync(mongo.GetDatabase(source.Database), run, limit, cancel)
                    : await CosmosRunAsync(cosmos!.GetDatabase(source.Database), containers ??= await ContainersAsync(cosmos.GetDatabase(source.Database), cancel), run, limit, cancel);
                told(new SqlResult(run, sets, affected, null, System.Diagnostics.Stopwatch.GetElapsedTime(began)));
            }
            catch (Exception failed) when (failed is MongoException or CosmosException or FormatException or BsonException or InvalidOperationException or JsonException)
            {
                told(new SqlResult(run, [], -1, Said(failed), System.Diagnostics.Stopwatch.GetElapsedTime(began)));
                return;
            }
        }
    }

    /// <summary>The database's collections and each one's fields, for a console's completion.</summary>
    internal static async Task<SqlCatalog> CatalogAsync(DatabaseSource source, CancellationToken cancel)
    {
        IReadOnlyList<DatabaseObject> collections = (await CollectionsAsync(source, cancel))[0].Objects;
        List<SqlTableInfo> tables = [];
        foreach (DatabaseObject collection in collections.Take(50))
        {
            DatabaseSchema schema = await SchemaAsync(source, new DatabaseTable(string.Empty, collection.Name), cancel);
            tables.Add(new SqlTableInfo(string.Empty, collection.Name, [.. schema.Columns.Select(c => (c.Name, c.Type))]));
        }
        return new SqlCatalog(tables);
    }

    /// <summary>MongoDB's client of a connection: its URI as written, a password kept apart laid into its credentials; else
    /// its host, port, user and password, signing in against admin unless it names another source.</summary>
    internal static MongoClient Mongo(DatabaseSource source)
    {
        string connection = source.ConnectionString;
        MongoClientSettings settings;
        string? password = DatabaseKinds.Value(connection, "Password");
        if (DatabaseKinds.Value(connection, "Uri") is { } uri)
        {
            settings = MongoClientSettings.FromConnectionString(uri);
            if (password is not null && settings.Credential is { Username: { } named } credential)
            {
                settings.Credential = MongoCredential.CreateCredential(credential.Source, named, password);
            }
        }
        else
        {
            (string host, int port) = DatabaseKinds.Endpoint(DatabaseKind.MongoDb, connection);
            settings = new MongoClientSettings { Server = new MongoServerAddress(host, port), UseTls = DatabaseKinds.Value(connection, "Tls") is "true" or "True" };
            if (DatabaseKinds.Value(connection, "Username", "User") is { } user)
            {
                settings.Credential = MongoCredential.CreateCredential(DatabaseKinds.Value(connection, "AuthSource") ?? "admin", user, password ?? string.Empty);
            }
        }
        settings.ApplicationName = "Passenger";
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
        settings.ConnectTimeout = TimeSpan.FromSeconds(10);
        return new MongoClient(settings);
    }

    /// <summary>Cosmos DB's client of a connection, through its gateway at its endpoint alone; an emulator's own certificate
    /// taken only where its connection string says to (DisableServerCertificateValidation, as Aspire's does).</summary>
    private static CosmosClient Cosmos(DatabaseSource source)
    {
        string connection = source.ConnectionString;
        string endpoint = DatabaseKinds.Value(connection, "AccountEndpoint") ?? throw new InvalidOperationException("The connection names no AccountEndpoint");
        string key = DatabaseKinds.Value(connection, "AccountKey") ?? string.Empty;
        CosmosClientOptions options = new()
        {
            ApplicationName = "Passenger",
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            RequestTimeout = TimeSpan.FromSeconds(30),
        };
        if (DatabaseKinds.Value(connection, "DisableServerCertificateValidation") is "true" or "True")
        {
            options.ServerCertificateCustomValidationCallback = (_, _, _) => true;
        }
        return new CosmosClient(endpoint, key, options);
    }

    /// <summary>Runs a client's work, its own failures said as an <see cref="IOException"/> holding no secret.</summary>
    private static async Task<T> Guarded<T>(DatabaseSource source, Func<Task<T>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception failed) when (failed is MongoException or CosmosException or BsonException or ArgumentException or FormatException)
        {
            throw new IOException(Containers.ContainerSecrets.InSaid(Said(failed), known: [source.ConnectionString]), failed);
        }
    }

    /// <summary>A client's failure in a line: Cosmos DB's status and the first line of its message.</summary>
    private static string Said(Exception failed) => failed is CosmosException cosmos
        ? $"{(int)cosmos.StatusCode} {cosmos.StatusCode}: {cosmos.ResponseBody?.Split('\n')[0] ?? cosmos.Message.Split('\n')[0]}"
        : failed.Message;

    private static async Task<bool> IsCollectionAsync(MongoClient client, string database, string name, CancellationToken cancel)
    {
        BsonDocument? listed = await (await client.GetDatabase(database).ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, cancel))
            .FirstOrDefaultAsync(cancel);
        return listed is not null && listed.GetValue("type", "collection").AsString == "collection";
    }

    /// <summary>The fields sampled, in the order the documents give them, the key first: each with the kind of value most hold.</summary>
    private static List<DatabaseColumn> Columns(DatabaseKind kind, IReadOnlyList<Dictionary<string, object?>> sample)
    {
        string key = KeyOf(kind);
        List<string> names = [key, .. sample.SelectMany(d => d.Keys).Where(k => k != key).Distinct(StringComparer.Ordinal)];
        // A system field Cosmos DB writes stands last.
        names = [.. names.OrderBy(n => IsSystemField(kind, n))];
        return [.. names.Select(name =>
        {
            string type = sample.Select(d => d.GetValueOrDefault(name)).Where(v => v is not null).GroupBy(TypeName).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "null";
            bool nullable = sample.Any(d => d.GetValueOrDefault(name) is null);
            return new DatabaseColumn(name, type, nullable, name == key, IsSystemField(kind, name));
        })];
    }

    /// <summary>Whether a field is one Cosmos DB writes itself (_rid, _self, _etag, _attachments, _ts), which takes no change.</summary>
    private static bool IsSystemField(DatabaseKind kind, string name) => kind == DatabaseKind.CosmosDb && name is "_rid" or "_self" or "_etag" or "_attachments" or "_ts";

    /// <summary>The kind of value a field holds, as a person reads it.</summary>
    private static string TypeName(object? value) => value switch
    {
        string => "string",
        int or long => "int",
        double or decimal => "number",
        bool => "bool",
        DateTime => "date",
        BsonObjectId => "objectId",
        JsonText json => json.Json.StartsWith('[') ? "array" : "object",
        BsonValue bson => bson.BsonType.ToString().ToLowerInvariant(),
        _ => "null",
    };

    /// <summary>A document's top-level fields as a row's cells hold them: plain values as themselves, an object id as itself,
    /// a document or array as its JSON.</summary>
    private static Dictionary<string, object?> Fields(BsonDocument document) => document.Elements.ToDictionary(e => e.Name, e => Cell(e.Value), StringComparer.Ordinal);

    private static object? Cell(BsonValue value) => value.BsonType switch
    {
        BsonType.String => value.AsString,
        BsonType.Int32 => value.AsInt32,
        BsonType.Int64 => value.AsInt64,
        BsonType.Double => value.AsDouble,
        BsonType.Decimal128 => (decimal)value.AsDecimal128,
        BsonType.Boolean => value.AsBoolean,
        BsonType.DateTime => value.ToUniversalTime(),
        BsonType.Null or BsonType.Undefined => null,
        BsonType.ObjectId => value,
        BsonType.Document or BsonType.Array => new JsonText(value.ToJson(Relaxed)),
        _ => value,
    };

    private static Dictionary<string, object?> Fields(JsonObject document) => document.ToDictionary(p => p.Key, p => Cell(p.Value), StringComparer.Ordinal);

    private static object? Cell(JsonNode? value) => value switch
    {
        null => null,
        JsonObject or JsonArray => new JsonText(value.ToJsonString()),
        JsonValue plain when plain.GetValueKind() == JsonValueKind.String => plain.GetValue<string>(),
        JsonValue plain when plain.GetValueKind() is JsonValueKind.True or JsonValueKind.False => plain.GetValue<bool>(),
        JsonValue plain when plain.TryGetValue(out long whole) => whole,
        JsonValue plain when plain.TryGetValue(out double number) => number,
        _ => value.ToJsonString(),
    };

    /// <summary>A key's value read as BSON again: an object id as itself, any other as the value it is.</summary>
    private static BsonValue ToBson(object? value) => value switch
    {
        BsonValue bson => bson,
        null => BsonNull.Value,
        _ => BsonValue.Create(value),
    };

    /// <summary>A cell's text written as the kind of value its field held, a field it lacked taking a number, true or false
    /// where the words read as one, else the words; NULL as null.</summary>
    internal static BsonValue Written(string? text, object? read)
    {
        if (text is null)
        {
            return BsonNull.Value;
        }
        return read switch
        {
            string => new BsonString(text),
            int when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int small) => small,
            int or long when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole) => whole,
            double or decimal or int or long when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) => number,
            bool when bool.TryParse(text, out bool truth) => truth,
            DateTime when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime at) => new BsonDateTime(at),
            null or AbsentField when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole) => whole,
            null or AbsentField when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) => number,
            null or AbsentField when bool.TryParse(text, out bool truth) => truth,
            _ => new BsonString(text),
        };
    }

    /// <summary>A cell's text written into a JSON document, as <see cref="Written"/> takes it.</summary>
    internal static JsonNode? WrittenJson(string? text, object? read) => Written(text, read) switch
    {
        BsonNull => null,
        BsonString words => JsonValue.Create(words.AsString),
        BsonInt32 small => JsonValue.Create(small.AsInt32),
        BsonInt64 whole => JsonValue.Create(whole.AsInt64),
        BsonDouble number => JsonValue.Create(number.AsDouble),
        BsonBoolean truth => JsonValue.Create(truth.AsBoolean),
        BsonDateTime at => JsonValue.Create(at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        BsonValue other => JsonValue.Create(other.ToString()),
    };

    /// <summary>A page's whole document of a key, as read.</summary>
    private static JsonObject DocumentOf(DatabaseRows page, int keyAt, RowKey key)
    {
        int row = Array.FindIndex(page.Rows, r => keyAt >= 0 && PendingEdits.Same(r[keyAt], key.Values[0]));
        return row >= 0 && row < page.Documents.Length && JsonNode.Parse(page.Documents[row]) is JsonObject document
            ? document : throw new InvalidOperationException($"The document {key} is not on the page read");
    }

    /// <summary>A document's partition key's value, by its container's path (/a/b); none where it holds no value there.</summary>
    private static PartitionKey PartitionOf(JsonObject document, string path)
    {
        JsonNode? at = document;
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            at = at is JsonObject inside ? inside[part] : null;
        }
        return at switch
        {
            null => PartitionKey.None,
            JsonValue plain when plain.GetValueKind() == JsonValueKind.String => new PartitionKey(plain.GetValue<string>()),
            JsonValue plain when plain.GetValueKind() is JsonValueKind.True or JsonValueKind.False => new PartitionKey(plain.GetValue<bool>()),
            JsonValue plain when plain.TryGetValue(out double number) => new PartitionKey(number),
            _ => PartitionKey.None,
        };
    }

    private static void Checked(ResponseMessage answer) => answer.EnsureSuccessStatusCode();

    /// <summary>MongoDB's filter of a field: its words anywhere in it, case aside; its value as text, number, truth or id; or
    /// whether it holds anything.</summary>
    private static FilterDefinition<BsonDocument> MongoFilter(DatabaseFilter? filter) => filter switch
    {
        null => FilterDefinition<BsonDocument>.Empty,
        { Operator: "Is NULL" } => new BsonDocument(filter.Column, BsonNull.Value),
        { Operator: "Is not NULL" } => new BsonDocument(filter.Column, new BsonDocument("$ne", BsonNull.Value)),
        { Operator: "Equals" } => new BsonDocument("$or", new BsonArray(Values(filter.Value).Select(v => new BsonDocument(filter.Column, v)))),
        _ => new BsonDocument(filter.Column, new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(filter.Value), "i")),
    };

    /// <summary>The values a filter's words may stand for in a field: the words, and the number, truth or object id they read as.</summary>
    private static IEnumerable<BsonValue> Values(string words)
    {
        yield return new BsonString(words);
        if (long.TryParse(words, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
        {
            yield return whole;
            yield return (int)Math.Clamp(whole, int.MinValue, int.MaxValue);
        }
        if (double.TryParse(words, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            yield return number;
        }
        if (bool.TryParse(words, out bool truth))
        {
            yield return truth;
        }
        if (ObjectId.TryParse(words, out ObjectId id))
        {
            yield return id;
        }
    }

    /// <summary>Cosmos DB's query of a page: a field's words in it, case aside; its value as text, number or truth; or whether
    /// it holds anything; sorted by a field, a page at a time.</summary>
    private static QueryDefinition CosmosFilter(StringBuilder sql, DatabaseFilter? filter, (string Column, bool Descending)? sort, int offset, int take)
    {
        List<(string Name, object Value)> parameters = [];
        if (filter is not null)
        {
            string field = $"c[{JsonSerializer.Serialize(filter.Column)}]";
            sql.Append(" WHERE ").Append(filter.Operator switch
            {
                "Is NULL" => $"(NOT IS_DEFINED({field}) OR IS_NULL({field}))",
                "Is not NULL" => $"(IS_DEFINED({field}) AND NOT IS_NULL({field}))",
                "Equals" => $"({field} = @text" + (double.TryParse(filter.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? $" OR {field} = @number" : string.Empty)
                    + (bool.TryParse(filter.Value, out _) ? $" OR {field} = @truth" : string.Empty) + ")",
                _ => $"CONTAINS({field}, @text, true)",
            });
            parameters.Add(("@text", filter.Value));
            if (double.TryParse(filter.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                parameters.Add(("@number", number));
            }
            if (bool.TryParse(filter.Value, out bool truth))
            {
                parameters.Add(("@truth", truth));
            }
        }
        if (sort is { } by)
        {
            sql.Append($" ORDER BY c[{JsonSerializer.Serialize(by.Column)}] {(by.Descending ? "DESC" : "ASC")}");
        }
        sql.Append(CultureInfo.InvariantCulture, $" OFFSET {offset} LIMIT {take}");
        QueryDefinition query = new(sql.ToString());
        foreach ((string name, object value) in parameters.Where(p => sql.ToString().Contains(p.Name, StringComparison.Ordinal)))
        {
            query = query.WithParameter(name, value);
        }
        return query;
    }

    /// <summary>A query's items, read page by page to <paramref name="most"/>, and whether more were left.</summary>
    private static async Task<(List<JsonObject> Items, List<JsonNode?> Values, bool More)> QueryAsync(Container container, QueryDefinition query, int most,
        CancellationToken cancel)
    {
        List<JsonObject> items = [];
        List<JsonNode?> values = [];
        using FeedIterator read = container.GetItemQueryStreamIterator(query, requestOptions: new QueryRequestOptions { MaxItemCount = Math.Min(most + 1, 1000) });
        while (read.HasMoreResults && values.Count <= most)
        {
            using ResponseMessage page = await read.ReadNextAsync(cancel);
            Checked(page);
            JsonNode? body = await JsonNode.ParseAsync(page.Content, cancellationToken: cancel);
            foreach (JsonNode? item in body?["Documents"]?.AsArray() ?? [])
            {
                values.Add(item?.DeepClone());
                if (item is JsonObject document)
                {
                    items.Add((JsonObject)document.DeepClone());
                }
            }
        }
        bool more = values.Count > most || read.HasMoreResults;
        return (items, values, more);
    }

    private static async Task<IReadOnlyList<string>> ContainersAsync(Microsoft.Azure.Cosmos.Database database, CancellationToken cancel)
    {
        List<string> names = [];
        using FeedIterator<ContainerProperties> read = database.GetContainerQueryIterator<ContainerProperties>();
        while (read.HasMoreResults)
        {
            names.AddRange((await read.ReadNextAsync(cancel)).Select(c => c.Id));
        }
        return names;
    }

    /// <summary>A Cosmos DB query of a console, against the container its FROM names, else the database's only one.</summary>
    private static async Task<(IReadOnlyList<DatabaseRows>, int)> CosmosRunAsync(Microsoft.Azure.Cosmos.Database database, IReadOnlyList<string> containers, string run,
        int limit, CancellationToken cancel)
    {
        string named = CosmosContainerOf(run) is { } from && containers.FirstOrDefault(c => c.Equals(from, StringComparison.Ordinal)) is { } found ? found
            : containers is [string only] ? only
            : throw new InvalidOperationException(containers.Count == 0 ? $"{database.Id} has no container"
                : $"Name a container of {database.Id} after FROM ({string.Join(", ", containers.Take(5))}): SELECT * FROM {containers[0]} c");
        (List<JsonObject> items, List<JsonNode?> values, bool more) = await QueryAsync(database.GetContainer(named), new QueryDefinition(run), limit, cancel);
        // SELECT VALUE gives values rather than documents: each one a row of its own.
        if (items.Count < values.Count)
        {
            return ([new DatabaseRows(["value"], [.. values.Take(limit).Select(v => new[] { Cell(v) })], more)], -1);
        }
        return ([Rows(items.Take(limit).Select(i => (Fields(i), i.ToJsonString())), more)], -1);
    }

    /// <summary>The container a Cosmos DB query's first FROM names, outside its strings and comments; null where it names none.</summary>
    internal static string? CosmosContainerOf(string run)
    {
        List<SqlText.Token> tokens = SqlText.Tokens(run, DatabaseKind.CosmosDb);
        int from = tokens.FindIndex(t => t is { Word: "FROM", Quoted: false });
        return from >= 0 && from + 1 < tokens.Count && tokens[from + 1] is { Quoted: false } name && (char.IsLetter(name.Word[0]) || name.Word[0] == '_')
            ? run[name.Start..name.End] : null;
    }

    /// <summary>Documents as a console's rows: their top-level fields as columns, in the order they first come, each whole
    /// document beside.</summary>
    internal static DatabaseRows Rows(IEnumerable<(Dictionary<string, object?> Fields, string Json)> documents, bool more)
    {
        List<(Dictionary<string, object?> Fields, string Json)> read = [.. documents];
        string[] columns = [.. read.SelectMany(r => r.Fields.Keys).Distinct(StringComparer.Ordinal)];
        return new DatabaseRows(columns, [.. read.Select(r => columns.Select(c => Of(r.Fields, c)).ToArray())], more)
        {
            Documents = [.. read.Select(r => r.Json)],
        };
    }

    /// <summary>A document's field by its name, or <see cref="AbsentField"/> where it lacks it.</summary>
    private static object? Of(Dictionary<string, object?> fields, string name) => fields.TryGetValue(name, out object? value) ? value : AbsentField.Value;

    /// <summary>MongoDB's documents as a console's rows.</summary>
    internal static DatabaseRows Rows(IReadOnlyList<BsonDocument> documents, bool more) => Rows(documents.Select(d => (Fields(d), d.ToJson(Relaxed))), more);
}
