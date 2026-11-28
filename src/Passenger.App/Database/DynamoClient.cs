using System.Globalization;
using System.Text.Json.Nodes;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using MongoDB.Bson;

namespace Passenger.App.Database;

/// <summary>DynamoDB as the Database window reads and writes it, through the AWS SDK: a region's (or a local endpoint's)
/// tables with their partition and sort keys, a table's attributes as its first items have them, a page of its items as
/// rows of their top-level attributes with each item whole beside, read by a scan to <see cref="MostItems"/> and sorted
/// and paged here; a page's changes written in one transaction that holds only while each item stands as it was read;
/// and a console's PartiQL. Signed in by the connection's access key, its AWS profile, else the SDK's own chain. Every
/// failure of the client is said as an <see cref="IOException"/>, as a relational driver's is.</summary>
internal static class DynamoClient
{
    /// <summary>The most items a tab sorts here, and the most changes one transaction writes (DynamoDB's own limit).</summary>
    public const int MostItems = 10000;

    private const int MostInTransaction = 100;

    /// <summary>The region the connection names, DynamoDB's one "database".</summary>
    internal static async Task<IReadOnlyList<string>> DatabasesAsync(DatabaseSource source, CancellationToken cancel) =>
        await Guarded<IReadOnlyList<string>>(source, async () =>
        {
            using AmazonDynamoDBClient client = await ClientAsync(source, cancel);
            await client.ListTablesAsync(new ListTablesRequest { Limit = 1 }, cancel);
            return [source.Database];
        });

    /// <summary>The tables, each with its keys said.</summary>
    internal static async Task<IReadOnlyList<(string Schema, IReadOnlyList<DatabaseObject> Objects)>> TablesAsync(DatabaseSource source, CancellationToken cancel) =>
        await Guarded<IReadOnlyList<(string, IReadOnlyList<DatabaseObject>)>>(source, async () =>
        {
            using AmazonDynamoDBClient client = await ClientAsync(source, cancel);
            List<string> names = [];
            string? after = null;
            do
            {
                ListTablesResponse listed = await client.ListTablesAsync(new ListTablesRequest { ExclusiveStartTableName = after }, cancel);
                names.AddRange(listed.TableNames ?? []);
                after = listed.LastEvaluatedTableName;
            }
            while (after is not null && names.Count < 1000);
            List<DatabaseObject> tables = [];
            foreach (string name in names.Order(StringComparer.Ordinal))
            {
                TableDescription table = (await client.DescribeTableAsync(name, cancel)).Table;
                tables.Add(new DatabaseObject(string.Empty, name, DatabaseObjectKind.Table, string.Join(", ", (table.KeySchema ?? []).Select(k => k.AttributeName))));
            }
            return [(string.Empty, tables)];
        });

    /// <summary>A table's attributes: its keys first (the partition key, then the sort key), then the others its first 100
    /// items hold, each with the kind of value most hold; its keys and indexes said.</summary>
    internal static async Task<DatabaseSchema> SchemaAsync(DatabaseSource source, DatabaseTable table, CancellationToken cancel) => await Guarded(source, async () =>
    {
        using AmazonDynamoDBClient client = await ClientAsync(source, cancel);
        TableDescription described = (await client.DescribeTableAsync(table.Name, cancel)).Table;
        string[] keys = [.. (described.KeySchema ?? []).OrderBy(k => k.KeyType == KeyType.HASH ? 0 : 1).Select(k => k.AttributeName)];
        ScanResponse sample = await client.ScanAsync(new ScanRequest { TableName = table.Name, Limit = DocumentClient.Sampled }, cancel);
        List<Dictionary<string, object?>> fields = [.. (sample.Items ?? []).Select(i => DocumentClient.FieldsOf(Json(i)))];
        List<DatabaseColumn> columns = [.. keys.Select(k => new DatabaseColumn(k, TypeOf(described, k), false, true, false))];
        foreach (string name in fields.SelectMany(f => f.Keys).Distinct(StringComparer.Ordinal).Where(n => !keys.Contains(n)))
        {
            string type = fields.Select(f => f.GetValueOrDefault(name)).Where(v => v is not null).GroupBy(DocumentClient.TypeName).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "null";
            columns.Add(new DatabaseColumn(name, type, fields.Any(f => f.GetValueOrDefault(name) is null), false, false));
        }
        IEnumerable<string> said = (described.KeySchema ?? []).Select(k => $"{(k.KeyType == KeyType.HASH ? "partition" : "sort")} key {k.AttributeName} ({AttributeType(described, k.AttributeName)})");
        IEnumerable<DatabasePart> indexes = (described.GlobalSecondaryIndexes ?? []).Select(i => new DatabasePart(i.IndexName, $"{i.IndexName} (global) {string.Join(", ", (i.KeySchema ?? []).Select(k => k.AttributeName))}"))
            .Concat((described.LocalSecondaryIndexes ?? []).Select(i => new DatabasePart(i.IndexName, $"{i.IndexName} (local) {string.Join(", ", (i.KeySchema ?? []).Select(k => k.AttributeName))}")));
        return new DatabaseSchema(table, columns, [.. said], [.. indexes]);
    });

    /// <summary>A page of a table's items, scanned (filtered on the server by an attribute) to as many as the page needs, or
    /// every one to <see cref="MostItems"/> where it is sorted, which is done here.</summary>
    internal static async Task<DatabaseRows> PageAsync(DatabaseSource source, DatabaseSchema schema, int offset, CancellationToken cancel, DatabaseFilter? filter,
        (string Column, bool Descending)? sort) => await Guarded(source, async () =>
    {
        using AmazonDynamoDBClient client = await ClientAsync(source, cancel);
        ScanRequest request = new() { TableName = schema.Table.Name };
        if (filter is not null)
        {
            request.ExpressionAttributeNames = new Dictionary<string, string> { ["#f"] = filter.Column };
            Dictionary<string, AttributeValue> values = [];
            request.FilterExpression = filter.Operator switch
            {
                "Is NULL" => "attribute_not_exists(#f) OR #f = :nul",
                "Is not NULL" => "attribute_exists(#f) AND #f <> :nul",
                "Equals" => decimal.TryParse(filter.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? "#f = :s OR #f = :n" : "#f = :s",
                _ => "contains(#f, :s)",
            };
            if (filter.Operator is "Is NULL" or "Is not NULL")
            {
                values[":nul"] = new AttributeValue { NULL = true };
            }
            else
            {
                values[":s"] = new AttributeValue { S = filter.Value };
                if (filter.Operator == "Equals" && decimal.TryParse(filter.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    values[":n"] = new AttributeValue { N = filter.Value };
                }
            }
            request.ExpressionAttributeValues = values;
        }
        // A page in the table's own order scans as far as it reaches, however far; one sorted here, to MostItems alone.
        int wanted = sort is null ? offset + DatabaseClient.PageSize + 1 : MostItems;
        List<JsonObject> items = [];
        do
        {
            ScanResponse page = await client.ScanAsync(request, cancel);
            items.AddRange((page.Items ?? []).Select(Json));
            request.ExclusiveStartKey = page.LastEvaluatedKey is { Count: > 0 } last ? last : null;
        }
        while (request.ExclusiveStartKey is not null && items.Count < wanted);
        bool cut = sort is not null && request.ExclusiveStartKey is not null;
        DatabaseRows all = DocumentClient.Rows(items.Select(i => (DocumentClient.FieldsOf(i), i.ToJsonString())), false);
        string[] columns = [.. schema.Columns.Select(c => c.Name).Concat(all.Columns).Distinct(StringComparer.Ordinal)];
        DatabaseSchema widened = schema with
        {
            Columns = [.. schema.Columns, .. columns.Skip(schema.Columns.Count).Select(name => new DatabaseColumn(name, "mixed", true, false, false))],
        };
        object?[][] rows = [.. items.Select(i => DocumentClient.FieldsOf(i)).Select(f => columns.Select(c => f.TryGetValue(c, out object? value) ? value : AbsentField.Value).ToArray())];
        int[] order = [.. Enumerable.Range(0, rows.Length)];
        if (sort is { } by && Array.IndexOf(columns, by.Column) is >= 0 and int at)
        {
            order = Tables.TableSort.Order(new DatabaseRows(columns, rows), at, by.Descending);
        }
        int[] chosen = [.. order.Skip(offset).Take(DatabaseClient.PageSize)];
        return new DatabaseRows(columns, [.. chosen.Select(r => rows[r])], order.Length > offset + DatabaseClient.PageSize || sort is null && request.ExclusiveStartKey is not null)
        {
            Schema = widened,
            Offset = offset,
            About = cut ? DatabaseClient.ReadOf(MostItems, null, "items") : null,
            Types = [.. columns.Select(c => widened.Columns.First(w => w.Name == c).Type)],
            Documents = [.. chosen.Select(r => items[r].ToJsonString())],
            OrderedBy = sort is { } ordered && Array.IndexOf(columns, ordered.Column) is >= 0 and int sorted ? (sorted, ordered.Descending) : null,
        };
    });

    /// <summary>Writes a page's changes in one transaction, at most DynamoDB's 100 (more refused before any is written, as
    /// two would commit apart), each change held to its item as read: an attribute changed only where it holds what was
    /// read (missing where it was missing), an item deleted only where it is there, one added only where its key is not
    /// taken; each key's value of the type its table declares, a string's as written, a number's as read.</summary>
    internal static async Task SubmitAsync(DatabaseSource source, DatabaseSchema schema, DatabaseRows page, PendingEdits edits, CancellationToken cancel) =>
        await Guarded(source, async () =>
        {
            using AmazonDynamoDBClient client = await ClientAsync(source, cancel);
            string[] keys = [.. schema.Columns.Where(c => c.Primary).Select(c => c.Name)];
            List<TransactWriteItem> writes = [];
            foreach ((RowKey key, ChangedRow row) in edits.Changed)
            {
                Dictionary<string, string> names = [];
                Dictionary<string, AttributeValue> values = [];
                List<string> sets = [];
                List<string> holds = [];
                int at = 0;
                foreach ((int column, string? text) in row.Written)
                {
                    names[$"#a{at}"] = page.Columns[column];
                    values[$":w{at}"] = Attribute(DocumentClient.Written(text, row.Read[column]));
                    sets.Add($"#a{at} = :w{at}");
                    if (row.Read[column] is AbsentField)
                    {
                        holds.Add($"attribute_not_exists(#a{at})");
                    }
                    else if (row.Read[column] is not JsonText)
                    {
                        values[$":r{at}"] = Attribute(row.Read[column] is null ? BsonNull.Value : BsonValue.Create(row.Read[column]));
                        holds.Add($"#a{at} = :r{at}");
                    }
                    at++;
                }
                writes.Add(new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = schema.Table.Name, Key = KeyOf(schema, keys, key), UpdateExpression = "SET " + string.Join(", ", sets),
                        ConditionExpression = holds.Count > 0 ? string.Join(" AND ", holds) : null, ExpressionAttributeNames = names, ExpressionAttributeValues = values,
                    },
                });
            }
            foreach ((RowKey key, object?[] read) in edits.Deleted)
            {
                // Deleted only while each plain attribute holds what was read, of the type the item holds it as now, a missing one missing.
                Dictionary<string, AttributeValue> now = (await client.GetItemAsync(new GetItemRequest { TableName = schema.Table.Name, Key = KeyOf(schema, keys, key),
                    ConsistentRead = true }, cancel)).Item ?? throw new InvalidOperationException(Refusals.ItemChanged(schema.Table.Name));
                Dictionary<string, string> names = new() { ["#k"] = keys[0] };
                Dictionary<string, AttributeValue> values = [];
                List<string> holds = ["attribute_exists(#k)"];
                for (int column = 0; column < read.Length; column++)
                {
                    if (keys.Contains(page.Columns[column]) || read[column] is JsonText)
                    {
                        continue;
                    }
                    names[$"#a{column}"] = page.Columns[column];
                    if (read[column] is AbsentField)
                    {
                        holds.Add($"attribute_not_exists(#a{column})");
                        continue;
                    }
                    if (!now.TryGetValue(page.Columns[column], out AttributeValue? held))
                    {
                        throw new InvalidOperationException(Refusals.ItemChanged(schema.Table.Name));
                    }
                    if (read[column] is null)
                    {
                        values[$":r{column}"] = new AttributeValue { S = "NULL" };
                        holds.Add($"attribute_type(#a{column}, :r{column})");
                        continue;
                    }
                    values[$":r{column}"] = held.B is not null && read[column] is string base64 && ImportTypes.TryConvert(base64, typeof(byte[]), out object bytes)
                        ? new AttributeValue { B = new MemoryStream((byte[])bytes) } : Attribute(BsonValue.Create(read[column]));
                    holds.Add($"#a{column} = :r{column}");
                }
                writes.Add(new TransactWriteItem
                {
                    Delete = new Delete
                    {
                        TableName = schema.Table.Name, Key = KeyOf(schema, keys, key), ConditionExpression = string.Join(" AND ", holds),
                        ExpressionAttributeNames = names, ExpressionAttributeValues = values.Count > 0 ? values : null,
                    },
                });
            }
            foreach (AddedRow added in edits.Added)
            {
                Dictionary<string, AttributeValue> item = added.Written.Where(w => w.Value is not null)
                    .ToDictionary(w => page.Columns[w.Key], w => Attribute(DocumentClient.Written(w.Value, null)), StringComparer.Ordinal);
                if (keys.FirstOrDefault(k => !item.ContainsKey(k)) is { } lacking)
                {
                    throw new InvalidOperationException(Refusals.ItemLacksKey(lacking));
                }
                // A key's written words, as written, take the type its table declares: 000123 stays a string's.
                foreach (string key in keys)
                {
                    item[key] = KeyValue(schema, key, added.Written[Array.IndexOf(page.Columns, key)]!);
                }
                writes.Add(new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = schema.Table.Name, Item = item, ConditionExpression = "attribute_not_exists(#k)",
                        ExpressionAttributeNames = new Dictionary<string, string> { ["#k"] = keys[0] },
                    },
                });
            }
            if (writes.Count > MostInTransaction)
            {
                throw new InvalidOperationException(Refusals.TooManyItemChanges(writes.Count, MostInTransaction));
            }
            try
            {
                await client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, cancel);
            }
            catch (TransactionCanceledException refused) when (refused.CancellationReasons?.Any(r => r.Code == "ConditionalCheckFailed") == true)
            {
                throw new InvalidOperationException(Refusals.ItemChanged(schema.Table.Name), refused);
            }
            return true;
        });

    /// <summary>Runs each of a console's PartiQL statements in turn as <see cref="DatabaseClient.RunAsync"/> does, its items
    /// (at most <paramref name="limit"/>) as rows with each whole beside; a failure ends the run.</summary>
    internal static async Task RunAsync(DatabaseSource source, IReadOnlyList<string> runs, int limit, Action<SqlResult> told, CancellationToken cancel)
    {
        using AmazonDynamoDBClient client = await ClientAsync(source, cancel);
        foreach (string run in runs)
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                List<JsonObject> items = [];
                string? next = null;
                do
                {
                    ExecuteStatementResponse page = await client.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = run, NextToken = next }, cancel);
                    items.AddRange((page.Items ?? []).Select(Json));
                    next = page.NextToken;
                }
                while (next is not null && items.Count <= limit);
                DatabaseRows rows = DocumentClient.Rows(items.Take(limit).Select(i => (DocumentClient.FieldsOf(i), i.ToJsonString())), items.Count > limit || next is not null);
                told(new SqlResult(run, rows.ColumnCount > 0 ? [rows] : [], SqlText.EffectOf(run, DatabaseKind.DynamoDb) >= SqlEffect.ChangesData ? 1 : -1, null,
                    System.Diagnostics.Stopwatch.GetElapsedTime(began)));
            }
            catch (AmazonDynamoDBException failed)
            {
                told(new SqlResult(run, [], -1, failed.Message, System.Diagnostics.Stopwatch.GetElapsedTime(began)));
                return;
            }
        }
    }

    /// <summary>The tables and each one's attributes, for a console's completion.</summary>
    internal static async Task<SqlCatalog> CatalogAsync(DatabaseSource source, CancellationToken cancel)
    {
        List<SqlTableInfo> tables = [];
        foreach (DatabaseObject table in (await TablesAsync(source, cancel))[0].Objects.Take(50))
        {
            DatabaseSchema schema = await SchemaAsync(source, new DatabaseTable(string.Empty, table.Name), cancel);
            tables.Add(new SqlTableInfo(string.Empty, table.Name, [.. schema.Columns.Select(c => (c.Name, c.Type))]));
        }
        return new SqlCatalog(tables);
    }

    /// <summary>The client of a connection: at its local endpoint (DynamoDB Local) or its region's, signed in by its access key
    /// and secret, else its AWS profile, else the environment's or the default profile's (<see cref="AwsProfiles"/>).</summary>
    private static async Task<AmazonDynamoDBClient> ClientAsync(DatabaseSource source, CancellationToken cancel)
    {
        string connection = DatabaseClient.Whole(source);
        AmazonDynamoDBConfig config = new() { Timeout = TimeSpan.FromSeconds(30), MaxErrorRetry = 1 };
        string region = source.Database;
        if (DatabaseKinds.Value(connection, "ServiceURL", "Endpoint") is { } endpoint)
        {
            config.ServiceURL = endpoint;
            config.AuthenticationRegion = region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }
        AWSCredentials credentials = DatabaseKinds.Value(connection, "AccessKey") is { } access
            ? new BasicAWSCredentials(access, DatabaseKinds.Value(connection, "SecretKey") ?? string.Empty)
            : await AwsProfiles.CredentialsAsync(DatabaseKinds.Value(connection, "Profile"), cancel);
        return new AmazonDynamoDBClient(credentials, config);
    }

    /// <summary>An item as JSON, its numbers as numbers and its sets as arrays.</summary>
    private static JsonObject Json(Dictionary<string, AttributeValue> item) =>
        JsonNode.Parse(Document.FromAttributeMap(item).ToJson()) as JsonObject ?? [];

    /// <summary>A value written as DynamoDB's attribute of its kind: text a string, a number a number, a truth a boolean, NULL null.</summary>
    private static AttributeValue Attribute(BsonValue value) => value switch
    {
        BsonNull => new AttributeValue { NULL = true },
        BsonBoolean truth => new AttributeValue { BOOL = truth.AsBoolean },
        BsonDecimal128 exact => new AttributeValue { N = ((decimal)exact.AsDecimal128).ToString(CultureInfo.InvariantCulture) },
        BsonInt32 or BsonInt64 or BsonDouble => new AttributeValue { N = Convert.ToString(BsonTypeMapper.MapToDotNetValue(value), CultureInfo.InvariantCulture) },
        BsonDateTime at => new AttributeValue { S = at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) },
        _ => new AttributeValue { S = value.ToString() },
    };

    /// <summary>An item's key by its primary attributes' values as read, each of the type its table declares.</summary>
    private static Dictionary<string, AttributeValue> KeyOf(DatabaseSchema schema, string[] keys, RowKey key) =>
        keys.Select((name, at) => (name, value: key.Values[at])).ToDictionary(k => k.name, k => KeyValue(schema, k.name, k.value switch
        {
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            IFormattable exact => exact.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(k.value, CultureInfo.InvariantCulture) ?? string.Empty,
        }), StringComparer.Ordinal);

    /// <summary>A key attribute's value from its text, of the type its table declares: a number's digits as they are, a
    /// binary's bytes from base64 (or 0x hex), a string's text as it is.</summary>
    private static AttributeValue KeyValue(DatabaseSchema schema, string key, string text) => schema.Columns.First(c => c.Name == key).Type switch
    {
        "number" => new AttributeValue { N = text.Trim() },
        "binary" => ImportTypes.TryConvert(text, typeof(byte[]), out object bytes) ? new AttributeValue { B = new MemoryStream((byte[])bytes) }
            : throw new InvalidOperationException(Refusals.NotAValue(text, "bytes", key)),
        _ => new AttributeValue { S = text },
    };

    private static string TypeOf(TableDescription table, string attribute) => AttributeType(table, attribute) switch
    {
        "N" => "number",
        "B" => "binary",
        _ => "string",
    };

    private static string AttributeType(TableDescription table, string attribute) =>
        (table.AttributeDefinitions ?? []).FirstOrDefault(a => a.AttributeName == attribute)?.AttributeType?.Value ?? "S";

    /// <summary>Runs a client's work, its own failures said as an <see cref="IOException"/> holding no secret.</summary>
    private static async Task<T> Guarded<T>(DatabaseSource source, Func<Task<T>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception failed) when (failed is AmazonServiceException or AmazonClientException or ArgumentException)
        {
            throw new IOException(Containers.ContainerSecrets.InSaid(failed.Message, known: [source.ConnectionString]), failed);
        }
    }
}
