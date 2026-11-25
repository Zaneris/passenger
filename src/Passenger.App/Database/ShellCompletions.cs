namespace Passenger.App.Database;

/// <summary>What completion offers at a caret in a MongoDB console, from its connection's collections and their fields as a
/// sample has them: after db and a dot, the collections and the database's calls; after a collection, a collection's
/// calls, and after a call's parentheses the cursor's (sort, limit); a word begun with $ the query, update and pipeline
/// operators and the statement's collection's fields as an aggregation names them; within a call's braces that
/// collection's fields.</summary>
public static class ShellCompletions
{
    private static readonly (string Name, string Said)[] OnDatabase =
    [
        ("getCollectionNames()", "the collections"), ("getCollection(\"\")", "a collection by its name"), ("runCommand({ })", "a command"),
        ("createCollection(\"\")", "makes a collection"), ("dropDatabase()", "drops the database"),
    ];

    private static readonly (string Name, string Said)[] OnCollection =
    [
        ("find({ })", "the documents a filter takes"), ("findOne({ })", "the first document a filter takes"), ("aggregate([ ])", "a pipeline's documents"),
        ("countDocuments({ })", "how many documents a filter takes"), ("estimatedDocumentCount()", "how many documents, from the collection's metadata"),
        ("distinct(\"\", { })", "a field's values"), ("insertOne({ })", "adds a document"), ("insertMany([ ])", "adds documents"),
        ("updateOne({ }, { $set: { } })", "changes the first document a filter takes"), ("updateMany({ }, { $set: { } })", "changes the documents a filter takes"),
        ("replaceOne({ }, { })", "replaces the first document a filter takes"), ("deleteOne({ })", "deletes the first document a filter takes"),
        ("deleteMany({ })", "deletes the documents a filter takes"), ("createIndex({ })", "makes an index"), ("dropIndex(\"\")", "drops an index"),
        ("getIndexes()", "the indexes"), ("drop()", "drops the collection"),
    ];

    private static readonly (string Name, string Said)[] OnCursor =
    [
        ("sort({ })", "orders the documents"), ("limit()", "reads at most so many"), ("skip()", "passes over so many first"),
        ("projection({ })", "the fields each holds"), ("count()", "how many there are"), ("toArray()", "every document"),
    ];

    private static readonly string[] Operators =
    [
        "$eq", "$ne", "$gt", "$gte", "$lt", "$lte", "$in", "$nin", "$exists", "$type", "$regex", "$options", "$and", "$or", "$nor", "$not", "$elemMatch",
        "$size", "$all", "$expr", "$set", "$unset", "$inc", "$mul", "$rename", "$push", "$pull", "$addToSet", "$pop", "$currentDate", "$setOnInsert",
        "$match", "$group", "$project", "$sort", "$limit", "$skip", "$lookup", "$unwind", "$count", "$addFields", "$facet", "$replaceRoot", "$sample",
        "$out", "$merge", "$sum", "$avg", "$min", "$max", "$first", "$last", "$concat", "$cond", "$ifNull", "$toString", "$dateToString",
    ];

    /// <summary>Where the word being typed at <paramref name="caret"/> starts, and what may stand there.</summary>
    public static (int Start, IReadOnlyList<SqlCompletions.Candidate> Items) At(string text, int caret, SqlCatalog? catalog)
    {
        int start = caret;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '$'))
        {
            start--;
        }
        List<SqlCompletions.Candidate> items = [];
        string before = text[..start].TrimEnd();
        IReadOnlyList<SqlTableInfo> collections = catalog?.Tables ?? [];
        if (before.EndsWith('.'))
        {
            string owner = before[..^1].TrimEnd();
            if (owner.EndsWith(')'))
            {
                items.AddRange(OnCursor.Select(Call));
            }
            else if (Word(owner) == "db")
            {
                items.AddRange(collections.Select(c => new SqlCompletions.Candidate(c.Name, c.Name, SqlCompletions.Kind.Table, "collection")));
                items.AddRange(OnDatabase.Select(Call));
            }
            else
            {
                items.AddRange(OnCollection.Select(Call));
            }
            return (start, items);
        }
        SqlTableInfo? collection = CollectionOf(text, caret, collections);
        if (caret > start && text[start] == '$')
        {
            items.AddRange(Operators.Select(o => new SqlCompletions.Candidate(o, o, SqlCompletions.Kind.Keyword, "operator")));
            items.AddRange((collection?.Columns ?? []).Select(c => new SqlCompletions.Candidate("$" + c.Name, "$" + c.Name, SqlCompletions.Kind.Column, c.Type)));
            return (start, items);
        }
        if (Depth(text, start) > 0)
        {
            items.AddRange((collection?.Columns ?? []).Select(c => new SqlCompletions.Candidate(c.Name, Key(c.Name), SqlCompletions.Kind.Column, c.Type)));
            items.AddRange(Operators.Select(o => new SqlCompletions.Candidate(o, o, SqlCompletions.Kind.Keyword, "operator")));
            return (start, items);
        }
        items.Add(new SqlCompletions.Candidate("db", "db", SqlCompletions.Kind.Keyword, "the console's database"));
        items.Add(new SqlCompletions.Candidate("show collections", "show collections", SqlCompletions.Kind.Keyword, "the collections"));
        items.Add(new SqlCompletions.Candidate("show dbs", "show dbs", SqlCompletions.Kind.Keyword, "the databases"));
        return (start, items);

        static SqlCompletions.Candidate Call((string Name, string Said) call) =>
            new(call.Name[..call.Name.IndexOf('(', StringComparison.Ordinal)], call.Name, SqlCompletions.Kind.Keyword, call.Said);
    }

    /// <summary>The last word of a text.</summary>
    private static string Word(string text)
    {
        int start = text.Length;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '$'))
        {
            start--;
        }
        return text[start..];
    }

    /// <summary>A field's name as a document's key: plain where it is a name, else quoted.</summary>
    private static string Key(string name) => name.Length > 0 && (char.IsLetter(name[0]) || name[0] is '_' or '$') && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$')
        ? name : System.Text.Json.JsonSerializer.Serialize(name);

    /// <summary>How deep in brackets the text stands at a place, outside its strings and comments.</summary>
    private static int Depth(string text, int at)
    {
        int depth = 0;
        int start = DocumentShell.Statements(text[..at]) is [.., var last] ? last.Start : 0;
        for (int i = start; i < at;)
        {
            if (DocumentShell.Skipped(text, i) is { } past)
            {
                i = past;
                continue;
            }
            depth += text[i] is '(' or '[' or '{' ? 1 : text[i] is ')' or ']' or '}' ? -1 : 0;
            i++;
        }
        return depth;
    }

    /// <summary>The collection the statement at the caret calls on (db.orders, db["orders"], db.getCollection("orders")), as the
    /// catalog has it; null where it names none.</summary>
    private static SqlTableInfo? CollectionOf(string text, int caret, IReadOnlyList<SqlTableInfo> collections)
    {
        string statement = (DocumentShell.Statements(text[..caret]) is [.., var last] ? text[last.Start..caret] : text[..caret]).TrimStart();
        if (!statement.StartsWith("db", StringComparison.Ordinal))
        {
            return null;
        }
        string rest = statement[2..].TrimStart();
        string? name = null;
        if (rest.StartsWith('['))
        {
            name = Quoted(rest[1..].TrimStart());
        }
        else if (rest.StartsWith('.'))
        {
            string word = Word(new string([.. rest[1..].TrimStart().TakeWhile(c => char.IsLetterOrDigit(c) || c is '_' or '$' or '-')]));
            name = word == "getCollection" && rest.IndexOf('(', StringComparison.Ordinal) is >= 0 and int open ? Quoted(rest[(open + 1)..].TrimStart()) : word;
        }
        return name is null ? null : collections.FirstOrDefault(c => c.Name == name);

        // A string's words where one starts the text.
        static string? Quoted(string text) => text.Length > 1 && text[0] is '"' or '\'' && text.IndexOf(text[0], 1) is > 0 and int end ? text[1..end] : null;
    }
}
