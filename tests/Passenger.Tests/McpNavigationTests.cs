using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;

namespace Passenger.Tests;

/// <summary>An agent with no window (<c>--mcp</c>) finding its way through C# code: the solution's own symbols found
/// without a source generator's, the name itself first, usages read by file with the symbol the place took and a
/// generator's place named for what it is, a place with no symbol said to be one, or the symbol its line declares taken;
/// symbols named rather than placed, found exactly, each overload told apart by its documentation id, an indexer by this[]
/// or Item; a hover card as plain text, and a fix's Suppress and Configure choices counted until asked for.</summary>
public sealed class McpNavigationTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpNavigationTests(RestoredFolders folders)
        : base(folders)
    {
    }

    private const string Words = "using System.Text.RegularExpressions;\n\nstatic partial class Words\n{\n"
        + "    [GeneratedRegex(@\"\\w+\")]\n    private static partial Regex Word();\n\n"
        + "    public static int Count(string text) => Word().Count(text);\n\n"
        + "    public static int Count(string text, int from) => Word().Count(text[from..]);\n}\n";

    private const string Tally = "static class Tally\n{\n    public static int Of(string text) => Words.Count(text) + Words.Count(text, 1);\n\n"
        + "    public static int CountAll(string[] texts) => texts.Sum(Words.Count);\n}\n";

    [Fact]
    public async Task An_agent_finds_the_solutions_own_symbols_reads_usages_in_order_and_hears_when_no_symbol_is_where_it_asked()
    {
        Write("Words.cs", Words);
        Write("Aaa.cs", Tally);
        await using McpClient client = await Start(["--mcp", Folder]);

        // The regex generator declares Words and Word again in a file on no disk, which the agent could never open.
        JsonElement[] found = [.. (await Call(client, McpTools.FindSymbol, new() { ["query"] = "Word" })).EnumerateArray()];
        Assert.True(found.Length > 0 && found.All(s => s.GetProperty("path").GetString() == "Words.cs"),
            $"find_symbol Word answered places beyond the solution's own files: {string.Join(", ", found.Select(s => s.GetRawText()))}");

        JsonElement used = await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 6, ["column"] = 34 });
        Assert.Equal(["Words.cs:6", "Words.cs:8", "Words.cs:10", "RegexGenerator.g.cs (generated):"],
            Used(used).Select(u => $"{u.Path}:{(u.Path.EndsWith("(generated)", StringComparison.Ordinal) ? "" : u.Line)}"));
        Assert.Equal("Words.Word() (method)", used.GetProperty("symbol").GetString());

        // By file, each named once, then line, whichever the server found first, after the symbol the place took.
        JsonElement counted = await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 8, ["column"] = 23 });
        Assert.Equal(["Aaa.cs:3:48", "Aaa.cs:5:67", "Words.cs:8:23"], Used(counted).Select(u => $"{u.Path}:{u.Line}:{u.Column}"));
        Assert.Equal(("Words.Count(string) (method)", 3, 2), (counted.GetProperty("symbol").GetString(), counted.GetProperty("count").GetInt32(), counted.GetProperty("files").GetArrayLength()));

        // A column off the name, on the line that declares it, still finds it.
        JsonElement fromStart = await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 8, ["column"] = 1 });
        Assert.Equal(counted.GetRawText(), fromStart.GetRawText());
        string declared = (await Call(client, McpTools.SymbolInfo, new() { ["path"] = "Words.cs", ["line"] = 10, ["column"] = 2 })).GetString()!;
        Assert.Contains("int Words.Count(string text, int from)", declared, StringComparison.Ordinal);

        CallToolResult nothing = await Result(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 2, ["column"] = 1 });
        Assert.True(nothing.IsError == true && Text(nothing).Contains("No symbol at Words.cs:2:1", StringComparison.Ordinal),
            $"usages where no symbol is answered: {Text(nothing)}");
    }

    [Fact]
    public async Task An_agent_names_a_symbol_in_place_of_its_place_finds_names_exactly_and_tells_overloads_apart_by_their_documentation_ids()
    {
        Write("Words.cs", Words);
        Write("Aaa.cs", Tally);
        await using McpClient client = await Start(["--mcp", Folder]);

        // Go to Symbol's matches take in CountAll; an exact find, the two overloads alone, each by its id.
        Assert.Contains((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Count" })).EnumerateArray(), s => s.GetProperty("name").GetString() == "CountAll");
        Assert.Equal(["Count Words.cs:8 M:Words.Count(System.String)", "Count Words.cs:10 M:Words.Count(System.String,System.Int32)"],
            (await Call(client, McpTools.FindSymbol, new() { ["query"] = "Count", ["exact"] = true })).EnumerateArray()
                .Select(s => $"{s.GetProperty("name").GetString()} {s.GetProperty("path").GetString()}:{s.GetProperty("line").GetInt32()} {s.GetProperty("id").GetString()}"));

        string both = (await Call(client, McpTools.SymbolInfo, new() { ["symbol"] = "Words.Count" })).GetString()!;
        Assert.True(both.Contains("Words.Count(string) (method) at Words.cs:8:23, M:Words.Count(System.String)", StringComparison.Ordinal)
            && both.Contains("Words.Count(string, int) (method) at Words.cs:10:23, M:Words.Count(System.String,System.Int32)", StringComparison.Ordinal)
            && both.Contains("int Words.Count(string text, int from)", StringComparison.Ordinal), $"symbol_info of Words.Count answered:\n{both}");
        Assert.EndsWith("Documentation id: T:System.Text.RegularExpressions.Regex",
            (await Call(client, McpTools.SymbolInfo, new() { ["path"] = "Words.cs", ["line"] = 6, ["column"] = 28 })).GetString(), StringComparison.Ordinal);

        Assert.Equal(["Aaa.cs:3:68", "Words.cs:10:23"], Used(await Call(client, McpTools.Usages, new() { ["symbol"] = "Words.Count(string, int)" }))
            .Select(u => $"{u.Path}:{u.Line}:{u.Column}"));
        CallToolResult ambiguous = await Result(client, McpTools.Usages, new() { ["symbol"] = "Count" });
        Assert.True(ambiguous.IsError == true && Text(ambiguous).Contains("Count names 2 symbols", StringComparison.Ordinal)
            && Text(ambiguous).Contains("M:Words.Count(System.String,System.Int32)", StringComparison.Ordinal), $"usages of a name two overloads share answered: {Text(ambiguous)}");
        JsonElement declared = Assert.Single((await Call(client, McpTools.Definition, new() { ["symbol"] = "M:Words.Count(System.String)" })).EnumerateArray());
        Assert.Equal(("Words.cs", 8), (declared.GetProperty("path").GetString(), declared.GetProperty("line").GetInt32()));
        Assert.Equal(["Words.Count(string)", "Words.Count(string, int)"],
            (await Call(client, McpTools.Hierarchy, new() { ["symbol"] = "Words.Word" })).EnumerateArray().Select(c => c.GetProperty("name").GetString()));

        Assert.Equal("Renamed in 1 file: Aaa.cs", (await Call(client, McpTools.Rename, new() { ["symbol"] = "Tally.CountAll", ["newName"] = "All" })).GetString());
        Assert.Contains("public static int All(string[] texts)", File.ReadAllText(Path.Combine(Folder, "Aaa.cs")), StringComparison.Ordinal);
    }

    private const string Shelf = "sealed class Shelf\n{\n    private readonly string[] books = [\"a\", \"b\"];\n\n"
        + "    /// <summary>The book_at a place on the shelf, from a List&lt;string&gt; of them.</summary>\n    public string this[int at] => books[at];\n\n"
        + "    public int Size()\n    {\n        int unused = 1;\n        return books.Length;\n    }\n}\n";

    [Fact]
    public async Task An_agent_finds_a_name_itself_first_an_indexer_by_name_reads_a_card_as_plain_text_and_a_fixs_suppressions_counted()
    {
        Write("Words.cs", Words);
        Write("Aaa.cs", Tally);
        Write("Shelf.cs", Shelf);
        await using McpClient client = await Start(["--mcp", Folder]);

        // The name itself before the names that start with it, each in the type that holds it, not a project's words.
        JsonElement[] counts = [.. (await Call(client, McpTools.FindSymbol, new() { ["query"] = "Count" })).EnumerateArray()];
        Assert.Equal(["Count Words", "Count Words", "CountAll Tally"],
            counts.Take(3).Select(s => $"{s.GetProperty("name").GetString()} {s.GetProperty("container").GetString()}"));

        // An indexer by this[] and by Item, its metadata's name, wherever a symbol is named.
        JsonElement indexer = Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = "this[]" })).EnumerateArray());
        Assert.Equal(("this[]", "Shelf", "Shelf.cs", 6, "P:Shelf.Item(System.Int32)"), (indexer.GetProperty("name").GetString(), indexer.GetProperty("container").GetString(),
            indexer.GetProperty("path").GetString(), indexer.GetProperty("line").GetInt32(), indexer.GetProperty("id").GetString()));
        Assert.Contains((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Item" })).EnumerateArray(), s => s.GetProperty("id").GetString() == "P:Shelf.Item(System.Int32)");
        JsonElement used = await Call(client, McpTools.Usages, new() { ["symbol"] = "Shelf.Item" });
        Assert.Equal(("Shelf.this[int] (property)", "Shelf.cs:6"), (used.GetProperty("symbol").GetString(), string.Join(", ", Used(used).Select(u => $"{u.Path}:{u.Line}"))));

        // The hover card's words, with none of Markdown's escapes or fences.
        string card = (await Call(client, McpTools.SymbolInfo, new() { ["symbol"] = "Shelf.this[int]" })).GetString()!;
        Assert.True(card.Contains("string Shelf.this[int at] { get; }", StringComparison.Ordinal) && card.Contains("The book_at a place on the shelf, from a List<string> of them.", StringComparison.Ordinal)
            && !card.Contains('\\', StringComparison.Ordinal) && !card.Contains("```", StringComparison.Ordinal), $"symbol_info of the indexer said:\n{card}");

        // A warning's Suppress and Configure choices are counted, and listed when the agent names that action.
        Dictionary<string, object?> unused = new() { ["path"] = "Shelf.cs", ["line"] = 10, ["column"] = 13 };
        string suppress = Assert.Single((await Call(client, McpTools.CodeActions, unused)).EnumerateArray().Select(a => a.GetString()!), a => a.StartsWith("Suppress", StringComparison.Ordinal));
        Assert.EndsWith(" choices: title lists them", suppress, StringComparison.Ordinal);
        string title = suppress[..suppress.IndexOf(" [", StringComparison.Ordinal)];
        string listed = Assert.Single((await Call(client, McpTools.CodeActions, new(unused) { ["title"] = title })).EnumerateArray()).GetString()!;
        Assert.Contains("Suppress CS0219 → in Source", listed, StringComparison.Ordinal);
    }
}
