using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.App;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Agents making the window's refactorings through refactor, attached to a person's window, which asks before the
/// first: each kind's dialog described first and then applied with its options, on a small shop of files that call each
/// other, every caller following; what the agent reads back and what is on disk after, the project still building after
/// the member moves and the server's refactorings, and the person's Undo taking each change back whole.</summary>
public sealed class McpRefactorTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpRefactorTests(RestoredFolders folders)
        : base(folders)
    {
    }

    private const string Calc = "namespace Shop;\n\npublic static class Calc\n{\n    public static int Scale(int value, int factor, int unused) => value * factor;\n\n"
        + "    public static int Twice(int value) => Scale(value, 2, 0);\n\n    /// <summary>Never called.</summary>\n    public static int Unused(int value) => value + 1;\n}\n";

    private const string Till = "namespace Shop;\n\npublic static class Till\n{\n    public static int Total() => Calc.Scale(2, 3, 9) + Calc.Scale(4, factor: 5, unused: 1);\n}\n";

    private const string Report = "using Shop;\n\nnamespace Shop.Reports;\n\npublic static class Report\n{\n    public static int Of(Order order) => order.Total + Calc.Scale(1, 1, 1);\n}\n";

    private const string Order = "namespace Shop;\n\npublic class Order\n{\n    public int Total { get; private set; }\n\n    public void Add(int amount) => Total += Calc.Twice(amount);\n\n"
        + "    public string Describe() => $\"{Total}\";\n}\n";

    private const string OrderBook = "namespace Shop;\n\npublic static class OrderBook\n{\n    public static Order Open() => new Order();\n}\n";

    private const string Shapes = "namespace Shop;\n\npublic class Circle\n{\n    public double Area(double radius) => Math.PI * radius * radius;\n}\n\n"
        + "public class Square\n{\n    public int Side;\n}\n";

    private const string Animal = "namespace Shop;\n\npublic class Animal\n{\n    public string Name = \"\";\n\n    public virtual string Sound() => \"...\";\n}\n";

    private const string Dog = "using System.Text;\n\nnamespace Shop;\n\npublic class Dog : Animal\n{\n    public int Legs => 4;\n\n    public string Describe()\n    {\n"
        + "        StringBuilder text = new();\n        text.Append(Name).Append(' ').Append(Legs);\n        return text.ToString();\n    }\n}\n";

    private const string Cat = "namespace Shop;\n\npublic class Cat : Animal\n{\n    public bool Purrs => true;\n}\n";

    private const string ScaleText = "namespace Shop;\n\npublic static class Kilo\n{\n    public static int Of(int value)\n    {\n        return value * 1024;\n    }\n\n"
        + "    public static int Two() => Of(2);\n}\n";

    private const string Counter = "namespace Shop;\n\npublic class Counter\n{\n    public int count;\n\n    public void Add() => count++;\n}\n";

    private const string Summer = "namespace Shop;\n\npublic static class Summer\n{\n    public static int Sum(string label, params ReadOnlySpan<int> values) => values.Length;\n}\n";

    private const string Totals = "namespace Shop;\n\npublic static class Totals\n{\n    public static int All() => Summer.Sum(\"a\", 1, 2, 3) + Summer.Sum(\"b\", [4, 5]) + Summer.Sum(\"c\");\n}\n";

    private const string Coins = "namespace Shop;\n\npublic class Coin\n{\n    public int Value;\n\n    /// <summary>A coin, as <see cref=\"Mint(int, string)\"/> makes it.</summary>\n"
        + "    public static Coin Mint(int value, string metal = \"gold\") => new() { Value = value };\n\n    public void OnDropped(object? sender, EventArgs e)\n    {\n    }\n}\n";

    private const string Purse = "namespace Shop;\n\npublic static class Purse\n{\n    public static event EventHandler? Dropped;\n\n    public static int Fill()\n    {\n"
        + "        Func<int, string, Coin> mint = Coin.Mint;\n        Dropped += new Coin().OnDropped;\n        return Coin.Mint(1).Value + mint(2, \"silver\").Value;\n    }\n}\n";

    [AvaloniaFact]
    public void An_agent_changes_a_signature_for_every_caller_moves_types_and_extracts_an_interface_each_undone_whole_by_the_person()
    {
        Shop(("Calc.cs", Calc), ("Till.cs", Till), ("Report.cs", Report), ("Order.cs", Order), ("OrderBook.cs", OrderBook), ("Shapes.cs", Shapes));
        MainWindow window = AgentsWindow(Folder);
        List<string> asked = AnswerUndo(window);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<JsonElement> describing = Waiting.Apart(() => Call(client, McpTools.Refactor, Place("change_signature", "Calc.cs", Calc, "int Sca")));
            Press(Question(window, "the question for the agent's refactoring"), "Allow");
            JsonElement dialog = Await(describing, "Change Signature described").GetProperty("dialog");
            Assert.Equal("public static int Scale(int value, int factor, int unused)", dialog.GetProperty("preview").GetString());
            Assert.Equal(["value", "factor", "unused"], dialog.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
            Assert.Equal(["value", "factor", "unused"], dialog.GetProperty("options").GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("from").GetString()));
            Assert.Equal(Calc, File.ReadAllText(PathOf("Calc.cs")));

            JsonElement changed = Refactor(client, Place("change_signature", "Calc.cs", Calc, "int Sca", new
            {
                parameters = new object[] { new { from = "factor" }, new { from = "value", name = "amount" }, new { type = "int", name = "offset", callValue = "0" } },
            }));
            Assert.StartsWith("Changed the signature of Calc.Scale in 3 files", changed.GetProperty("said").GetString(), StringComparison.Ordinal);
            Assert.Equal(["Calc.cs changed", "Report.cs changed", "Till.cs changed"], Files(changed));
            string calc = Calc.Replace("Scale(int value, int factor, int unused) => value * factor", "Scale(int factor, int amount, int offset) => amount * factor", StringComparison.Ordinal)
                .Replace("Scale(value, 2, 0)", "Scale(2, value, 0)", StringComparison.Ordinal);
            Assert.Equal(calc, File.ReadAllText(PathOf("Calc.cs")));
            Assert.Equal(Till.Replace("Calc.Scale(2, 3, 9) + Calc.Scale(4, factor: 5, unused: 1)", "Calc.Scale(3, 2, 0) + Calc.Scale(factor: 5, 4, 0)", StringComparison.Ordinal),
                File.ReadAllText(PathOf("Till.cs")));
            Assert.Equal(Report.Replace("Calc.Scale(1, 1, 1)", "Calc.Scale(1, 1, 0)", StringComparison.Ordinal), File.ReadAllText(PathOf("Report.cs")));

            UndoInEditor(window, "Calc.cs");
            string question = Assert.Single(asked);
            Assert.True(question.StartsWith("Undo Change Signature? It changes ", StringComparison.Ordinal) && new[] { "Calc.cs", "Report.cs", "Till.cs" }.All(f => question.Contains(f, StringComparison.Ordinal)),
                $"The person's Undo asked: {question}");
            Assert.Equal(Calc, window.EditorArea.Active?.Text.Snapshot.ToString());
            Assert.Equal(Till, File.ReadAllText(PathOf("Till.cs")));
            Assert.Equal(Report, File.ReadAllText(PathOf("Report.cs")));
            window.EditorArea.SaveAll();

            JsonElement source = Refactor(client, Place("extract_interface", "Order.cs", Order, "class Ord")).GetProperty("dialog");
            Assert.Equal(("IOrder", "IOrder.cs"), (source.GetProperty("options").GetProperty("name").GetString(), source.GetProperty("options").GetProperty("file").GetString()));
            Assert.Equal(["Total: int", "Add(int): void", "Describe(): string"], source.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("text").GetString()));
            JsonElement extracted = Refactor(client, Place("extract_interface", "Order.cs", Order, "class Ord", new { members = new[] { "Total", "Add" } }));
            Assert.Equal("Extracted the interface IOrder from Order to IOrder.cs", extracted.GetProperty("said").GetString());
            Assert.Equal(["IOrder.cs created", "Order.cs changed"], Files(extracted));
            Assert.Equal("namespace Shop;\n\npublic interface IOrder\n{\n    int Total { get; }\n\n    void Add(int amount);\n}\n", File.ReadAllText(PathOf("IOrder.cs")).ReplaceLineEndings("\n"));
            Assert.Equal(Order.Replace("public class Order", "public class Order : IOrder", StringComparison.Ordinal), File.ReadAllText(PathOf("Order.cs")));

            JsonElement move = Refactor(client, Place("move", "Shapes.cs", Shapes, "class Circ")).GetProperty("dialog");
            Assert.Equal(("Circle.cs", 2), (move.GetProperty("toFile").GetProperty("file").GetString(), move.GetProperty("typesInFile").GetInt32()));
            JsonElement spaced = Refactor(client, Place("move", "OrderBook.cs", OrderBook, "class OrderBo", new { @namespace = "Sales" }));
            Assert.StartsWith("Moved class OrderBook to namespace Sales", spaced.GetProperty("said").GetString(), StringComparison.Ordinal);
            Assert.Equal("using Shop;\n\n" + OrderBook.Replace("namespace Shop;", "namespace Sales;", StringComparison.Ordinal), File.ReadAllText(PathOf("OrderBook.cs")));
            JsonElement filed = Refactor(client, Place("move", "Shapes.cs", Shapes, "class Circ", new { file = "Circle.cs" }));
            Assert.Equal(["Circle.cs created", "Shapes.cs changed"], Files(filed));
            Assert.Equal("namespace Shop;\n\npublic class Circle\n{\n    public double Area(double radius) => Math.PI * radius * radius;\n}\n", File.ReadAllText(PathOf("Circle.cs")).ReplaceLineEndings("\n"));
            Assert.Equal("namespace Shop;\n\npublic class Square\n{\n    public int Side;\n}\n", File.ReadAllText(PathOf("Shapes.cs")));

            CallToolResult both = Await(Waiting.Apart(() => Result(client, McpTools.Refactor, Place("move", "Shapes.cs", File.ReadAllText(PathOf("Shapes.cs")), "class Squ", new { file = "Square.cs", @namespace = "Shop.Shapes" }))), "a move to a file and a namespace at once");
            Assert.True(both.IsError == true && Text(both).Contains("one at a time", StringComparison.Ordinal), $"A move to a file and a namespace at once answered: {Text(both)}");

            UndoInSolutionView(window, "Shapes.cs");
            Assert.Equal("Undo Move to Another File? It changes Shapes.cs and Circle.cs.", asked[^1]);
            Assert.False(File.Exists(PathOf("Circle.cs")), "Undo left the file Move to Another File made");
            Assert.Equal(Shapes, window.EditorArea.Documents.First(d => d.Name == "Shapes.cs").Text.Snapshot.ToString());
        }
        finally
        {
            Leave(client, window);
        }
    }

    [AvaloniaFact]
    public void An_agent_safe_deletes_only_what_nothing_uses_unless_told_moves_members_up_and_down_and_makes_the_servers_refactorings()
    {
        Shop(("Calc.cs", Calc), ("Till.cs", Till), ("Animal.cs", Animal), ("Dog.cs", Dog), ("Cat.cs", Cat), ("Kilo.cs", ScaleText),
            ("Mega.cs", ScaleText.Replace("Kilo", "Mega", StringComparison.Ordinal)), ("Counter.cs", Counter));
        MainWindow window = AgentsWindow(Folder);
        List<string> asked = AnswerUndo(window);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<CallToolResult> refusing = Waiting.Apart(() => Result(client, McpTools.Refactor, Place("safe_delete", "Calc.cs", Calc, "int Sca")));
            Press(Question(window, "the question for the agent's refactoring"), "Allow");
            CallToolResult refused = Await(refusing, "Safe Delete of a used method");
            Assert.True(refused.IsError == true && Text(refused).Contains("has 3 usages that would no longer compile", StringComparison.Ordinal)
                && Text(refused).Contains("Till.cs:5: public static int Total()", StringComparison.Ordinal) && Text(refused).Contains("deleteAnyway", StringComparison.Ordinal),
                $"Safe Delete of a used method answered: {Text(refused)}");
            Assert.Equal(Calc, File.ReadAllText(PathOf("Calc.cs")));
            JsonElement usages = Refactor(client, Place("safe_delete", "Calc.cs", Calc, "int Sca"), describe: true).GetProperty("usages");
            Assert.Equal(["Calc.cs:7", "Till.cs:5", "Till.cs:5"], usages.EnumerateArray().Select(u => $"{u.GetProperty("path").GetString()}:{u.GetProperty("line").GetInt32()}"));

            JsonElement deleted = Refactor(client, Place("safe_delete", "Calc.cs", Calc, "int Unu"));
            Assert.Equal("Deleted the method 'Unused'", deleted.GetProperty("said").GetString());
            Assert.Equal(Calc.Replace("\n\n    /// <summary>Never called.</summary>\n    public static int Unused(int value) => value + 1;", string.Empty, StringComparison.Ordinal),
                File.ReadAllText(PathOf("Calc.cs")));

            JsonElement hierarchy = Refactor(client, Place("pull_members_up", "Dog.cs", Dog, "class Do")).GetProperty("dialog");
            Assert.Equal("Animal", hierarchy.GetProperty("options").GetProperty("target").GetString());
            Assert.Equal(["Legs: int", "Describe(): string"], hierarchy.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("text").GetString()));
            JsonElement pulled = Refactor(client, Place("pull_members_up", "Dog.cs", Dog, "class Do", new { members = new[] { "Legs", "Describe(): string" } }));
            Assert.Equal("Pulled 2 members of Dog up to Animal", pulled.GetProperty("said").GetString());
            Assert.Equal("using System.Text;\n\nnamespace Shop;\n\npublic class Dog : Animal\n{\n}\n", File.ReadAllText(PathOf("Dog.cs")));
            string animal = File.ReadAllText(PathOf("Animal.cs")).ReplaceLineEndings("\n");
            Assert.True(animal.StartsWith("using System.Text;\n", StringComparison.Ordinal) && animal.Contains("    public int Legs => 4;", StringComparison.Ordinal),
                $"Animal.cs after the pull up:\n{animal}");

            JsonElement pushed = Refactor(client, Place("push_members_down", "Animal.cs", animal, "class Anim", new { members = new[] { "Sound" }, @abstract = new[] { "Sound" } }));
            Assert.Equal("Pushed 1 member of Animal down to 2 classes", pushed.GetProperty("said").GetString());
            Assert.Contains("    public abstract string Sound();", File.ReadAllText(PathOf("Animal.cs")), StringComparison.Ordinal);
            Assert.Contains("public override string Sound() => \"...\";", File.ReadAllText(PathOf("Cat.cs")), StringComparison.Ordinal);
            Assert.Contains("public override string Sound() => \"...\";", File.ReadAllText(PathOf("Dog.cs")), StringComparison.Ordinal);

            Dictionary<string, object?> constant = Selection("introduce_field", "Kilo.cs", ScaleText, "1024");
            JsonElement offered = Refactor(client, constant, describe: true).GetProperty("dialog").GetProperty("actions");
            string[] fieldChoices = [.. offered.EnumerateArray().SelectMany(a => a.GetProperty("choices").EnumerateArray()).Select(c => c.GetString()!)];
            Assert.True(fieldChoices.Contains("Introduce constant for '1024'") && !fieldChoices.Any(c => c.Contains("local", StringComparison.OrdinalIgnoreCase)),
                $"Introduce Field's choices for a constant: {string.Join("; ", fieldChoices)}");
            string fieldAction = offered.EnumerateArray().First(a => a.GetProperty("choices").EnumerateArray().Any(c => c.GetString() == "Introduce constant for '1024'")).GetProperty("title").GetString()!;
            JsonElement field = Refactor(client, With(constant, new { action = fieldAction, choice = "Introduce constant for '1024'" }));
            Assert.Equal(["Kilo.cs changed"], Files(field));
            Assert.Matches(@"\n    private const int \w+ = 1024;\n", File.ReadAllText(PathOf("Kilo.cs")));

            string mega = ScaleText.Replace("Kilo", "Mega", StringComparison.Ordinal);
            Dictionary<string, object?> expression = Selection("introduce_parameter", "Mega.cs", mega, "1024");
            JsonElement actions = Refactor(client, expression, describe: true).GetProperty("dialog").GetProperty("actions");
            JsonElement one = actions.EnumerateArray().First(a => a.GetProperty("title").GetString() == "Introduce parameter for '1024'");
            string calls = one.GetProperty("choices").EnumerateArray().Select(c => c.GetString()!).First(c => c.Contains("call sites", StringComparison.Ordinal));
            Refactor(client, With(expression, new { action = "Introduce parameter for '1024'", choice = calls }));
            string introduced = File.ReadAllText(PathOf("Mega.cs"));
            Assert.True(introduced.Contains("Of(2, 1024)", StringComparison.Ordinal) && !introduced.Contains("value * 1024", StringComparison.Ordinal), $"Mega.cs after Introduce Parameter:\n{introduced}");
            JsonElement encapsulated = Refactor(client, Place("encapsulate_field", "Counter.cs", Counter, "public int cou", new { action = "Encapsulate field: 'count' (and use property)" }));
            Assert.Equal(["Counter.cs changed"], Files(encapsulated));
            string counter = File.ReadAllText(PathOf("Counter.cs"));
            Assert.True(counter.Contains("public int Count", StringComparison.Ordinal) && counter.Contains("public void Add() => Count++;", StringComparison.Ordinal), $"Counter.cs after Encapsulate Field:\n{counter}");
            Builds("Safe Delete, the member moves and the server's refactorings");

            UndoInSolutionView(window, "Cat.cs");
            Assert.StartsWith("Undo Push Members Down?", asked[^1], StringComparison.Ordinal);
            Assert.DoesNotContain("override string Sound", File.ReadAllText(PathOf("Cat.cs")), StringComparison.Ordinal);
        }
        finally
        {
            Leave(client, window);
        }
    }

    [Fact]
    public async Task An_agent_that_writes_a_file_and_at_once_changes_a_signature_or_renames_what_it_calls_has_the_change_made_in_what_it_wrote()
    {
        await TakeFolderAsync();
        Shop(("Calc.cs", Calc), ("Till.cs", Till), ("Report.cs", Report));
        await using McpClient client = await Start(["--mcp", Folder]);
        // Described first, so the server has read the files as the agent found them.
        await Call(client, McpTools.Refactor, Place("change_signature", "Calc.cs", Calc, "int Sca"));

        // The agent's own write, then at once the change: the language server has yet to read the file again.
        string written = "// Written by the agent\n// a moment ago\n"
            + Report.Replace("Calc.Scale(1, 1, 1)", "Calc.Scale(1, 1, 1) * Calc.Scale(7, 8, 9)", StringComparison.Ordinal);
        File.WriteAllText(PathOf("Report.cs"), written);
        CallToolResult changing = await Result(client, McpTools.Refactor, Place("change_signature", "Calc.cs", Calc, "int Sca", new
        {
            parameters = new object[] { new { from = "factor" }, new { from = "value" }, new { from = "unused" } },
        }));
        Assert.True(changing.IsError != true, $"Change Signature right after the agent wrote Report.cs answered: {Text(changing)}");
        Assert.Equal(written.Replace("Calc.Scale(7, 8, 9)", "Calc.Scale(8, 7, 9)", StringComparison.Ordinal), File.ReadAllText(PathOf("Report.cs")));

        string rewritten = "// And again\n" + File.ReadAllText(PathOf("Report.cs"));
        File.WriteAllText(PathOf("Report.cs"), rewritten);
        (int line, int column) = LineColumn(Calc, "Scale", Calc.IndexOf("Scale", StringComparison.Ordinal));
        CallToolResult renaming = await Result(client, McpTools.Rename, new() { ["path"] = "Calc.cs", ["line"] = line, ["column"] = column, ["newName"] = "Times" });
        Assert.True(renaming.IsError != true, $"A rename right after the agent wrote Report.cs answered: {Text(renaming)}");
        Assert.Equal(rewritten.Replace("Calc.Scale(", "Calc.Times(", StringComparison.Ordinal), File.ReadAllText(PathOf("Report.cs")));
        Assert.DoesNotContain("Scale", File.ReadAllText(PathOf("Till.cs")), StringComparison.Ordinal);
    }

    /// <summary>Change Signature passes a new parameter's value in every call of a method taking a params collection, the
    /// values spread out or passed as one, gives a cref the new parameters, and names each method group it cannot change
    /// with why (a delegate's, an event handler's): the delegate it makes no longer fits, which the agent would otherwise
    /// hear of only as the build breaks.</summary>
    [Fact]
    public async Task An_agent_changing_a_signature_has_every_params_collection_call_changed_and_hears_of_each_method_group_left_as_it_was()
    {
        await TakeFolderAsync();
        Shop(("Summer.cs", Summer), ("Totals.cs", Totals), ("Coins.cs", Coins), ("Purse.cs", Purse));
        await using McpClient client = await Start(["--mcp", Folder]);

        JsonElement summed = await Call(client, McpTools.Refactor, Place("change_signature", "Summer.cs", Summer, "int Su", new
        {
            parameters = new object[] { new { type = "int", name = "start", callValue = "0" }, new { from = "label" }, new { from = "values" } },
        }));
        Assert.Equal(["Summer.cs changed", "Totals.cs changed"], Files(summed));
        Assert.Empty(summed.GetProperty("notApplied").EnumerateArray());
        Assert.Equal(Totals.Replace("Summer.Sum(\"a\", 1, 2, 3) + Summer.Sum(\"b\", [4, 5]) + Summer.Sum(\"c\")",
            "Summer.Sum(0, \"a\", 1, 2, 3) + Summer.Sum(0, \"b\", [4, 5]) + Summer.Sum(0, \"c\")", StringComparison.Ordinal), File.ReadAllText(PathOf("Totals.cs")));

        JsonElement minted = await Call(client, McpTools.Refactor, Place("change_signature", "Coins.cs", Coins, "Coin Mi", new
        {
            parameters = new object[] { new { from = "value" }, new { type = "int", name = "year", callValue = "2026" }, new { from = "metal" } },
        }));
        (int line, int column) = LineColumn(Purse, "Mint;", Purse.IndexOf("Mint;", StringComparison.Ordinal));
        const string why = "Coin.Mint as a Func<int, string, Coin>, which the new signature no longer fits";
        Assert.Equal([$"Purse.cs:{line}:{column}: {why}, so it was left as it is"], minted.GetProperty("notApplied").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal($"Changed the signature of Coin.Mint in 2 files, but left 1 use as it was: Purse.cs:{line}, {why}", minted.GetProperty("said").GetString());
        Assert.Equal(Purse.Replace("Coin.Mint(1)", "Coin.Mint(1, 2026)", StringComparison.Ordinal), File.ReadAllText(PathOf("Purse.cs")));
        string coins = Coins.Replace("Mint(int, string)", "Mint(int, int, string)", StringComparison.Ordinal)
            .Replace("Mint(int value, string metal", "Mint(int value, int year, string metal", StringComparison.Ordinal);
        Assert.Equal(coins, File.ReadAllText(PathOf("Coins.cs")));

        JsonElement handled = await Call(client, McpTools.Refactor, Place("change_signature", "Coins.cs", coins, "void OnDro", new { parameters = new object[] { new { from = "sender" } } }));
        (line, column) = LineColumn(Purse, "OnDropped;", Purse.IndexOf("OnDropped;", StringComparison.Ordinal));
        Assert.Equal([$"Purse.cs:{line}:{column}: Coin.OnDropped as an EventHandler, which the new signature no longer fits, so it was left as it is"],
            handled.GetProperty("notApplied").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(["Coins.cs changed"], Files(handled));
    }

    /// <summary>Change Signature onto a name and parameter types another member of the type has (renamed to an existing
    /// overload's, or a parameter retyped to match one) is refused naming that member, nothing changed, and a change made
    /// says the files it changed: the build would otherwise break, a second change writing its temporary names into the
    /// other member.</summary>
    [Fact]
    public async Task An_agent_changing_a_signature_onto_another_members_is_refused_naming_it_and_a_change_counts_the_files_it_changed()
    {
        await TakeFolderAsync();
        const string Tally = "namespace Shop;\n\npublic class Tally\n{\n    public int Add(int a, int b)\n    {\n        return a + b;\n    }\n\n"
            + "    public int Multiply(int a, int b) => a * b;\n\n    public int Multiply(long a, int b) => (int)a * b;\n\n    public int Count;\n}\n";
        Shop(("Calc.cs", Calc), ("Till.cs", Till), ("Report.cs", Report), ("Tally.cs", Tally));
        await using McpClient client = await Start(["--mcp", Folder]);

        CallToolResult renamed = await Result(client, McpTools.Refactor, Place("change_signature", "Tally.cs", Tally, "int Ad", new { name = "Multiply" }));
        Assert.True(renamed.IsError == true && Text(renamed).Contains("Tally already has Tally.Multiply(int a, int b), whose name and parameter types the new signature of method 'Tally.Add' would take", StringComparison.Ordinal),
            $"Change Signature renaming Add(int, int) to Multiply beside Multiply(int, int) answered: {Text(renamed)}");
        CallToolResult retyped = await Result(client, McpTools.Refactor, Place("change_signature", "Tally.cs", Tally, "int Multiply(int", new
        {
            parameters = new object[] { new { from = "a", type = "long" }, new { from = "b" } },
        }));
        Assert.True(retyped.IsError == true && Text(retyped).Contains("Tally already has Tally.Multiply(long a, int b)", StringComparison.Ordinal),
            $"Change Signature retyping Multiply(int, int)'s first parameter to long beside Multiply(long, int) answered: {Text(retyped)}");
        CallToolResult field = await Result(client, McpTools.Refactor, Place("change_signature", "Tally.cs", Tally, "int Ad", new { name = "Count" }));
        Assert.True(field.IsError == true && Text(field).Contains("Tally already has the field Tally.Count, whose name the new signature of method 'Tally.Add' would take", StringComparison.Ordinal),
            $"Change Signature renaming Add to Count beside the field Count answered: {Text(field)}");
        Assert.Equal(Tally, File.ReadAllText(PathOf("Tally.cs")));
        const string Twins = "namespace Shop;\n\npublic class Twins\n{\n    public int Of(int a) => a;\n\n    public int Of(int b) => b;\n}\n";
        Write("Twins.cs", Twins);
        CallToolResult twin = await Result(client, McpTools.Refactor, Place("change_signature", "Twins.cs", Twins, "int Of(int b", new
        {
            parameters = new object[] { new { from = "b", name = "c" } },
        }));
        Assert.True(twin.IsError == true && Text(twin).Contains("Twins already has Twins.Of(int a) with the parameter types of method 'Twins.Of': Change Signature cannot tell the two apart", StringComparison.Ordinal),
            $"Change Signature of one of two methods declared alike answered: {Text(twin)}");
        Assert.Equal(Twins, File.ReadAllText(PathOf("Twins.cs")));

        JsonElement changed = await Call(client, McpTools.Refactor, Place("change_signature", "Tally.cs", Tally, "int Ad", new
        {
            name = "Sum",
            parameters = new object[] { new { from = "b", name = "second" }, new { from = "a", name = "first" } },
        }));
        Assert.Equal(["Tally.cs changed"], Files(changed));
        Assert.Equal("Changed the signature of Tally.Sum in 1 file", changed.GetProperty("said").GetString());
        Assert.Contains("public int Sum(int second, int first)\n    {\n        return first + second;", File.ReadAllText(PathOf("Tally.cs")), StringComparison.Ordinal);
    }

    /// <summary>Push Members Down gives a method to a derived class that declares only another overload of it, and Pull
    /// Members Up takes one variable of a field declaring two as a field of its own, the other staying: no member is lost
    /// on the way, so the project still builds.</summary>
    [Fact]
    public async Task An_agent_pushing_down_an_overload_or_pulling_up_one_of_two_fields_finds_each_where_it_went()
    {
        await TakeFolderAsync();
        const string Shape = "namespace Shop;\n\npublic class Shape\n{\n    public string Name(string prefix) => prefix + \"shape\";\n}\n";
        const string Square = "namespace Shop;\n\npublic class Square : Shape\n{\n    public int a, b;\n\n    public string Name(int sides) => $\"{sides}\";\n}\n";
        Shop(("Shape.cs", Shape), ("Square.cs", Square));
        await using McpClient client = await Start(["--mcp", Folder]);

        JsonElement pushed = await Call(client, McpTools.Refactor, Place("push_members_down", "Shape.cs", Shape, "class Sha", new { members = new[] { "Name(string): string" } }));
        Assert.Equal(["Shape.cs changed", "Square.cs changed"], Files(pushed));
        string square = File.ReadAllText(PathOf("Square.cs"));
        Assert.True(square.Contains("    public string Name(string prefix) => prefix + \"shape\";\n", StringComparison.Ordinal)
            && square.Contains("    public string Name(int sides) => $\"{sides}\";\n", StringComparison.Ordinal),
            $"Pushing Name(string) down into a class declaring Name(int) left it so:\n{square}");
        Assert.DoesNotContain("Name(", File.ReadAllText(PathOf("Shape.cs")), StringComparison.Ordinal);

        JsonElement pulled = await Call(client, McpTools.Refactor, Place("pull_members_up", "Square.cs", square, "class Squ", new { members = new[] { "a" } }));
        Assert.Equal(["Shape.cs changed", "Square.cs changed"], Files(pulled));
        string shape = File.ReadAllText(PathOf("Shape.cs"));
        square = File.ReadAllText(PathOf("Square.cs"));
        Assert.True(shape.Contains("    public int a;\n", StringComparison.Ordinal) && square.Contains("    public int b;\n", StringComparison.Ordinal)
            && !square.Contains(" a,", StringComparison.Ordinal), $"Pulling a of 'int a, b;' up left Shape.cs so:\n{shape}\nand Square.cs so:\n{square}");
        await Compiles(client, "the members moved");
    }

    /// <summary>Pull Members Up of an override of the base's abstract member puts it in place of that declaration, made
    /// virtual, the other class's override standing; one the base declares with a body of its own, or one using what stays
    /// below, is refused with why, nothing changed, and goes once pulled up with what it uses, the project building.</summary>
    [Fact]
    public async Task An_agent_pulling_up_an_override_or_a_member_using_one_left_behind_finds_it_in_place_or_refused()
    {
        await TakeFolderAsync();
        const string Shape = "namespace Shop;\n\npublic abstract class Shape\n{\n    public abstract string Label();\n\n    public virtual string Kind() => \"shape\";\n}\n";
        const string Square = "namespace Shop;\n\npublic class Square : Shape\n{\n    public double Side { get; set; }\n\n    /// <summary>The square's label.</summary>\n"
            + "    public override string Label() => \"square\";\n\n    public override string Kind() => \"square\";\n\n    public double Half() => Side / 2;\n}\n";
        const string Circle = "namespace Shop;\n\npublic class Circle : Shape\n{\n    public override string Label() => \"circle\";\n}\n";
        Shop(("Shape.cs", Shape), ("Square.cs", Square), ("Circle.cs", Circle));
        await using McpClient client = await Start(["--mcp", Folder]);

        foreach ((string member, string said) in new[]
        {
            ("Kind", "'Shape' already declares Kind(): string"),
            ("Half", "'Half' uses 'Side', which stays in 'Square': pull it up with 'Half'"),
        })
        {
            CallToolResult refused = await Result(client, McpTools.Refactor, Place("pull_members_up", "Square.cs", Square, "class Squ", new { members = new[] { member } }));
            Assert.True(refused.IsError == true && Text(refused).Contains(said, StringComparison.Ordinal), $"Pulling {member} up into Shape answered: {Text(refused)}");
            Assert.Equal(Shape, File.ReadAllText(PathOf("Shape.cs")));
            Assert.Equal(Square, File.ReadAllText(PathOf("Square.cs")));
        }

        JsonElement replaced = await Call(client, McpTools.Refactor, Place("pull_members_up", "Square.cs", Square, "class Squ", new { members = new[] { "Label" } }));
        Assert.Equal(["Shape.cs changed", "Square.cs changed"], Files(replaced));
        string shape = File.ReadAllText(PathOf("Shape.cs"));
        string square = File.ReadAllText(PathOf("Square.cs"));
        Assert.True(shape == Shape.Replace("    public abstract string Label();", "    /// <summary>The square's label.</summary>\n    public virtual string Label() => \"square\";", StringComparison.Ordinal)
            && square == Square.Replace("    /// <summary>The square's label.</summary>\n    public override string Label() => \"square\";\n\n", string.Empty, StringComparison.Ordinal),
            $"Pulling up Square's override of the abstract Label left Shape.cs so:\n{shape}\nand Square.cs so:\n{square}");

        JsonElement pulled = await Call(client, McpTools.Refactor, Place("pull_members_up", "Square.cs", square, "class Squ", new { members = new[] { "Side", "Half" } }));
        Assert.Equal(["Shape.cs changed", "Square.cs changed"], Files(pulled));
        shape = File.ReadAllText(PathOf("Shape.cs"));
        square = File.ReadAllText(PathOf("Square.cs"));
        Assert.True(shape.Contains("    public double Side { get; set; }\n", StringComparison.Ordinal) && shape.Contains("    public double Half() => Side / 2;\n", StringComparison.Ordinal)
            && !square.Contains("Side", StringComparison.Ordinal), $"Pulling Half up with the Side it uses left Shape.cs so:\n{shape}\nand Square.cs so:\n{square}");
        await Compiles(client, "an override and a member with what it uses were pulled up");
    }

    /// <summary>Push Members Down of a member the base's own code still calls, one a class not chosen calls as its own, or one
    /// reached through the base's type, is refused naming each use, nothing changed; members nothing left behind uses go,
    /// the chosen class's override of one becoming its own virtual member, and the project builds.</summary>
    [Fact]
    public async Task An_agent_pushing_down_a_member_still_used_where_it_would_be_gone_finds_it_refused()
    {
        await TakeFolderAsync();
        const string Shape = "namespace Shop;\n\npublic class Shape\n{\n    public virtual string Describe() => \"shape\";\n\n    public string Summary() => Describe() + \"!\";\n\n"
            + "    public string Label() => \"label\";\n\n    public int Corners() => 0;\n\n    public virtual int Sides() => 0;\n\n    public string Badge() => \"badge\";\n}\n";
        const string Square = "namespace Shop;\n\npublic class Square : Shape\n{\n    public override string Describe() => \"square\";\n\n    public override int Sides() => 4;\n\n"
            + "    public string Tag() => Badge() + Sides();\n}\n";
        const string Circle = "namespace Shop;\n\npublic class Circle : Shape\n{\n    public string Show(Shape other) => Label() + other.Corners();\n}\n";
        Shop(("Shape.cs", Shape), ("Square.cs", Square), ("Circle.cs", Circle));
        await using McpClient client = await Start(["--mcp", Folder]);

        foreach ((string member, string said) in new[]
        {
            ("Describe", "'Describe' would be gone from code still using it: Shape.Summary in Shape.cs, through 'Shape'"),
            ("Label", "'Label' would be gone from code still using it: Circle.Show in Circle.cs, through 'Circle': push it down to 'Circle' too"),
            ("Corners", "'Corners' would be gone from code still using it: Circle.Show in Circle.cs, through 'Shape'"),
        })
        {
            CallToolResult refused = await Result(client, McpTools.Refactor, Place("push_members_down", "Shape.cs", Shape, "class Sha", new { targets = new[] { "Square" }, members = new[] { member } }));
            Assert.True(refused.IsError == true && Text(refused).Contains(said, StringComparison.Ordinal), $"Pushing {member} down into Square answered: {Text(refused)}");
            Assert.Equal(Shape, File.ReadAllText(PathOf("Shape.cs")));
            Assert.Equal(Square, File.ReadAllText(PathOf("Square.cs")));
            Assert.Equal(Circle, File.ReadAllText(PathOf("Circle.cs")));
        }

        JsonElement pushed = await Call(client, McpTools.Refactor, Place("push_members_down", "Shape.cs", Shape, "class Sha", new { targets = new[] { "Square" }, members = new[] { "Sides", "Badge" } }));
        Assert.Equal(["Shape.cs changed", "Square.cs changed"], Files(pushed));
        string shape = File.ReadAllText(PathOf("Shape.cs"));
        string square = File.ReadAllText(PathOf("Square.cs"));
        Assert.True(!shape.Contains("Sides", StringComparison.Ordinal) && !shape.Contains("Badge", StringComparison.Ordinal)
            && square.Contains("    public virtual int Sides() => 4;\n", StringComparison.Ordinal) && square.Contains("    public string Badge() => \"badge\";\n", StringComparison.Ordinal),
            $"Pushing Sides and Badge down into Square left Shape.cs so:\n{shape}\nand Square.cs so:\n{square}");
        await Compiles(client, "members nothing left behind uses were pushed down");
    }

    /// <summary>Extract Interface from a class whose type parameter a member uses is constrained by another the members do
    /// not name: the interface declares both, so the constraint it copies names a type parameter of its own.</summary>
    [Fact]
    public async Task An_agent_extracting_an_interface_has_it_declare_every_type_parameter_its_constraints_name()
    {
        await TakeFolderAsync();
        const string Holder = "namespace Shop;\n\npublic class Holder<T, U, V> where T : U\n{\n    public T Keep(T value) => value;\n\n    public V Other(V value) => value;\n}\n";
        Shop(("Holder.cs", Holder));
        await using McpClient client = await Start(["--mcp", Folder]);

        JsonElement extracted = await Call(client, McpTools.Refactor, Place("extract_interface", "Holder.cs", Holder, "class Hol", new { members = new[] { "Keep" } }));
        Assert.Equal(["Holder.cs changed", "IHolder.cs created"], Files(extracted));
        Assert.Equal("namespace Shop;\n\npublic interface IHolder<T, U> where T : U\n{\n    T Keep(T value);\n}\n", File.ReadAllText(PathOf("IHolder.cs")).ReplaceLineEndings("\n"));
        Assert.Equal(Holder.Replace("Holder<T, U, V> where", "Holder<T, U, V> : IHolder<T, U> where", StringComparison.Ordinal), File.ReadAllText(PathOf("Holder.cs")));
        await Compiles(client, "the interface was extracted");
    }

    /// <summary>Change Signature of a parameter's ref kind gives each call's argument the keyword it now takes: ref to in,
    /// in to none, none to ref before a variable, each leaving the project building; a value that cannot be passed by
    /// ref is named among the uses left, its call as it was.</summary>
    [Fact]
    public async Task An_agent_changing_how_a_parameter_is_passed_has_every_callers_argument_passed_so()
    {
        await TakeFolderAsync();
        const string Tally = "namespace Shop;\n\npublic static class Tally\n{\n    public static int Add(ref int total, int amount) => total + amount;\n}\n";
        const string Counting = "namespace Shop;\n\npublic static class Counting\n{\n    public static int Count()\n    {\n        int total = 1;\n        int amount = 2;\n"
            + "        return Tally.Add(ref total, amount) + Tally.Add(ref total, 3);\n    }\n}\n";
        Shop(("Tally.cs", Tally), ("Counting.cs", Counting));
        await using McpClient client = await Start(["--mcp", Folder]);

        string tally = Tally;
        foreach ((string modifier, string calls) in new[] { ("in", "Tally.Add(in total, amount) + Tally.Add(in total, 3)"), ("", "Tally.Add(total, amount) + Tally.Add(total, 3)") })
        {
            JsonElement changed = await Call(client, McpTools.Refactor, Place("change_signature", "Tally.cs", tally, "int Ad", new
            {
                parameters = new object[] { new { from = "total", modifier }, new { from = "amount" } },
            }));
            Assert.Equal(["Counting.cs changed", "Tally.cs changed"], Files(changed));
            Assert.Empty(changed.GetProperty("notApplied").EnumerateArray());
            tally = File.ReadAllText(PathOf("Tally.cs"));
            Assert.Contains($"Add({(modifier.Length > 0 ? modifier + " " : string.Empty)}int total, int amount)", tally, StringComparison.Ordinal);
            Assert.Equal(Counting.Replace("Tally.Add(ref total, amount) + Tally.Add(ref total, 3)", calls, StringComparison.Ordinal), File.ReadAllText(PathOf("Counting.cs")));
            await Compiles(client, $"total was made a parameter passed {(modifier.Length > 0 ? "as " + modifier : "by value")}");
        }

        JsonElement byRef = await Call(client, McpTools.Refactor, Place("change_signature", "Tally.cs", tally, "int Ad", new
        {
            parameters = new object[] { new { from = "total" }, new { from = "amount", modifier = "ref" } },
        }));
        string counting = Counting.Replace("Tally.Add(ref total, amount) + Tally.Add(ref total, 3)", "Tally.Add(total, amount) + Tally.Add(total, 3)", StringComparison.Ordinal);
        Assert.Equal(counting.Replace("Add(total, amount)", "Add(total, ref amount)", StringComparison.Ordinal), File.ReadAllText(PathOf("Counting.cs")));
        (int line, int column) = LineColumn(counting, "Add(total, 3)", counting.IndexOf("Add(total, 3)", StringComparison.Ordinal));
        Assert.Equal([$"Counting.cs:{line}:{column}: a call of Tally.Add whose argument 3 cannot be passed by ref to 'amount', so it was left as it is"],
            byRef.GetProperty("notApplied").EnumerateArray().Select(n => n.GetString()));
    }

    /// <summary>Change Signature of an out parameter to ref or by value: a call passing a local with a value follows, and
    /// one passing a local that may have none yet, which the new passing would read, is left as it was and named among
    /// the uses left; once the agent gives each such local a value, the project builds.</summary>
    [Fact]
    public async Task An_agent_making_an_out_parameter_ref_or_by_value_hears_of_each_call_whose_local_may_have_no_value_yet()
    {
        await TakeFolderAsync();
        const string Meter = "namespace Shop;\n\npublic static class Meter\n{\n    public static void Read(out int steps) => steps = 3;\n\n    public static void Count(out int steps) => steps = 4;\n}\n";
        const string Reading = "namespace Shop;\n\npublic static class Reading\n{\n    public static int Run(bool twice)\n    {\n        int counted = 1;\n        Meter.Read(out counted);\n"
            + "        int fresh;\n        Meter.Read(out fresh);\n        int maybe;\n        if (twice)\n        {\n            maybe = 2;\n        }\n        Meter.Count(out maybe);\n"
            + "        Meter.Count(out counted);\n        return counted + fresh + maybe;\n    }\n}\n";
        Shop(("Meter.cs", Meter), ("Reading.cs", Reading));
        await using McpClient client = await Start(["--mcp", Folder]);

        string meter = Meter;
        string reading = Reading;
        foreach ((string method, string modifier, string was, string followed, string left, string passing) in new[]
        {
            ("void Rea", "ref", "Meter.Read(out counted)", "Meter.Read(ref counted)", "Read(out fresh)", "by ref"),
            ("void Cou", "", "Meter.Count(out counted)", "Meter.Count(counted)", "Count(out maybe)", "by value"),
        })
        {
            JsonElement changed = await Call(client, McpTools.Refactor, Place("change_signature", "Meter.cs", meter, method, new
            {
                parameters = new object[] { new { from = "steps", modifier } },
            }));
            string name = left[..left.IndexOf('(', StringComparison.Ordinal)];
            string local = left[(left.IndexOf(' ', StringComparison.Ordinal) + 1)..^1];
            (int line, int column) = LineColumn(reading, left, reading.IndexOf(left, StringComparison.Ordinal));
            Assert.Equal([$"Reading.cs:{line}:{column}: a call of Meter.{name} whose argument {local} may have no value yet, which 'steps' passed {passing} would read, so it was left as it is"],
                changed.GetProperty("notApplied").EnumerateArray().Select(n => n.GetString()));
            reading = reading.Replace(was, followed, StringComparison.Ordinal);
            Assert.Equal(reading, File.ReadAllText(PathOf("Reading.cs")));
            meter = File.ReadAllText(PathOf("Meter.cs"));
        }
        Assert.Equal(Meter.Replace("Read(out int steps)", "Read(ref int steps)", StringComparison.Ordinal).Replace("Count(out int steps)", "Count(int steps)", StringComparison.Ordinal), meter);

        foreach ((string oldText, string newText) in new[]
        {
            ("int fresh;\n        Meter.Read(out fresh);", "int fresh = 0;\n        Meter.Read(ref fresh);"),
            ("int maybe;", "int maybe = 0;"),
            ("Meter.Count(out maybe);", "Meter.Count(maybe);"),
        })
        {
            await Call(client, McpTools.Edit, new() { ["path"] = "Reading.cs", ["oldText"] = oldText, ["newText"] = newText });
        }
        await Compiles(client, "out was made ref and by value, and each local left was given a value");
    }

    /// <summary>Change Signature swapping two parameters of a method called with a call for each, and with plain values:
    /// the calls keep the order they run in, each named, so Open still runs before Read; the plain values move with their
    /// parameters.</summary>
    [Fact]
    public async Task An_agent_swapping_two_parameters_keeps_the_order_the_calls_passed_to_them_run_in()
    {
        await TakeFolderAsync();
        const string Pipe = "namespace Shop;\n\npublic static class Pipe\n{\n    public static string Copy(string from, string to) => from + to;\n\n"
            + "    public static string Open() => \"open\";\n\n    public static string Read() => \"read\";\n}\n";
        const string Copying = "namespace Shop;\n\npublic static class Copying\n{\n    public static string Run() => Pipe.Copy(Pipe.Open(), Pipe.Read()) + Pipe.Copy(\"a\", \"b\");\n}\n";
        Shop(("Pipe.cs", Pipe), ("Copying.cs", Copying));
        await using McpClient client = await Start(["--mcp", Folder]);

        JsonElement swapped = await Call(client, McpTools.Refactor, Place("change_signature", "Pipe.cs", Pipe, "string Cop", new
        {
            parameters = new object[] { new { from = "to" }, new { from = "from" } },
        }));
        Assert.Equal(["Copying.cs changed", "Pipe.cs changed"], Files(swapped));
        Assert.Empty(swapped.GetProperty("notApplied").EnumerateArray());
        Assert.Contains("Copy(string to, string from)", File.ReadAllText(PathOf("Pipe.cs")), StringComparison.Ordinal);
        string copying = File.ReadAllText(PathOf("Copying.cs"));
        Assert.True(copying == Copying.Replace("Pipe.Copy(Pipe.Open(), Pipe.Read()) + Pipe.Copy(\"a\", \"b\")",
            "Pipe.Copy(from: Pipe.Open(), to: Pipe.Read()) + Pipe.Copy(\"b\", \"a\")", StringComparison.Ordinal),
            $"Swapping Copy's parameters ran Read before Open, or moved the plain values otherwise:\n{copying}");
        await Compiles(client, "Copy's parameters were swapped");
    }

    [Fact]
    public async Task An_agent_that_writes_a_new_class_and_its_caller_and_at_once_renames_it_or_asks_its_usages_has_the_caller_counted()
    {
        await TakeFolderAsync();
        Shop(("Calc.cs", Calc));
        await using McpClient client = await Start(["--mcp", Folder]);
        // The projects loaded first, so the files written next come after.
        await Call(client, McpTools.Diagnostics, new() { ["path"] = "Calc.cs" });

        // Both new, then at once the rename: the language server has yet to take either into the project.
        const string Triangle = "namespace Shop;\n\npublic class Triangle\n{\n    public static Triangle Make() => new();\n}\n";
        const string UseTriangle = "namespace Shop;\n\npublic static class UseTriangle\n{\n    public static Triangle Run() => Triangle.Make();\n}\n";
        File.WriteAllText(PathOf("Triangle.cs"), Triangle);
        File.WriteAllText(PathOf("UseTriangle.cs"), UseTriangle);
        (int line, int column) = LineColumn(Triangle, "Make", Triangle.IndexOf("Make", StringComparison.Ordinal));
        CallToolResult renaming = await Result(client, McpTools.Rename, new() { ["path"] = "Triangle.cs", ["line"] = line, ["column"] = column, ["newName"] = "Build" });
        Assert.True(renaming.IsError != true, $"A rename right after the agent wrote Triangle.cs and its caller answered: {Text(renaming)}");
        Assert.Equal(Triangle.Replace("Make()", "Build()", StringComparison.Ordinal), File.ReadAllText(PathOf("Triangle.cs")));
        Assert.True(File.ReadAllText(PathOf("UseTriangle.cs")) == UseTriangle.Replace("Triangle.Make()", "Triangle.Build()", StringComparison.Ordinal),
            $"The rename ({Text(renaming)}) left the caller the agent had just written calling a method that is no longer there:\n{File.ReadAllText(PathOf("UseTriangle.cs"))}");

        const string Square = "namespace Shop;\n\npublic class Square\n{\n    public static Square Make() => new();\n}\n";
        const string UseSquare = "namespace Shop;\n\npublic static class UseSquare\n{\n    public static Square Run() => Square.Make();\n}\n";
        File.WriteAllText(PathOf("Square.cs"), Square);
        File.WriteAllText(PathOf("UseSquare.cs"), UseSquare);
        (line, column) = LineColumn(Square, "Make", Square.IndexOf("Make", StringComparison.Ordinal));
        JsonElement usages = await Call(client, McpTools.Usages, new() { ["path"] = "Square.cs", ["line"] = line, ["column"] = column });
        Assert.True(Used(usages).Select(u => u.Path).Order(StringComparer.Ordinal).SequenceEqual(["Square.cs", "UseSquare.cs"]),
            $"usages right after the agent wrote Square.cs and its caller missed the caller, so a change made by it would leave the caller behind: {Text(usages)}");
    }

    /// <summary>App references Lib, and a caller of Lib's method is written into App while App's design-time build is
    /// held, so the language server cannot take it in: usages of the method answer at once, saying they went without the
    /// caller, where they waited for App and refused; a rename of it waits for App, which loads once its build is let go,
    /// and renames the caller there too.</summary>
    [Fact]
    public async Task Usages_answer_at_once_without_a_file_another_project_has_yet_to_load_and_a_rename_waits_for_it()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-reach-"));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "Shapes.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "Lib/Lib.csproj", TestProjects.Library());
        const string Shape = "namespace Lib;\n\npublic static class Shape\n{\n    public static int Make() => 1;\n}\n";
        TestFolders.Write(folder, "Lib/Shape.cs", Shape);
        TestFolders.Write(folder, "App/App.csproj", TestProjects.Library().With("  <ItemGroup>\n    <ProjectReference Include=\"../Lib/Lib.csproj\" />\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "App/Start.cs", "namespace App;\n\npublic static class Start\n{\n    public static int Go() => 0;\n}\n");
        // App's design-time builds, which the language server loads it from, held while App/hold is there until App/released is.
        TestFolders.Write(folder, "App/Directory.Build.targets", """
            <Project>
              <Target Name="HeldLoad" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
                <Exec Condition="'$(OS)' != 'Windows_NT' and Exists('$(MSBuildProjectDirectory)/hold')" Command="for i in `seq 3000`; do test -f released &amp;&amp; break; sleep 0.1; done" />
                <Exec Condition="'$(OS)' == 'Windows_NT' and Exists('$(MSBuildProjectDirectory)/hold')" Command="powershell -NoProfile -Command &quot;for (%24i = 0; %24i -lt 3000 -and -not (Test-Path released); %24i++) { Start-Sleep -Milliseconds 100 }&quot;" />
              </Target>
            </Project>
            """);
        await Restore.RunAsync(Path.Combine(folder, "Shapes.slnx"));
        await using McpClient client = await Start(["--mcp", folder]);
        // The projects loaded first, so the file written next comes after.
        await Call(client, McpTools.Diagnostics, new() { ["path"] = "Lib/Shape.cs" });

        TestFolders.Write(folder, "App/hold", string.Empty);
        const string Caller = "namespace App;\n\npublic static class Caller\n{\n    public static int Call() => Lib.Shape.Make();\n}\n";
        TestFolders.Write(folder, "App/Caller.cs", Caller);
        (int line, int column) = LineColumn(Shape, "Make", Shape.IndexOf("Make", StringComparison.Ordinal));
        Dictionary<string, object?> make = new() { ["path"] = "Lib/Shape.cs", ["line"] = line, ["column"] = column };
        Heard heard = new();
        CallToolResult asked = await Result(client, McpTools.Usages, make, heard: heard);
        Assert.True(asked.IsError != true, $"usages of Lib's method, while App's build was held with a new file in it, did not answer: {Text(asked)}");
        string caller = Path.Combine("App", "Caller.cs");
        Assert.True(!heard.Told.Any(t => t.Message?.Contains("Caller.cs", StringComparison.Ordinal) == true),
            $"usages waited for App, whose build was held, to load its new caller: {string.Join(" | ", heard.Told.Select(t => t.Message))}");
        using JsonDocument usages = JsonDocument.Parse(Text(asked));
        Assert.Equal([Path.Combine("Lib", "Shape.cs")], Used(usages.RootElement).Select(u => u.Path));
        Assert.True(usages.RootElement.TryGetProperty("note", out JsonElement note) && note.GetString()!.Contains(caller, StringComparison.Ordinal),
            $"usages answered without App's new caller and did not say so, so an agent takes the method for used nowhere else: {Text(asked)}");

        Task<CallToolResult> renaming = Result(client, McpTools.Rename, new(make) { ["newName"] = "Build" });
        string status = string.Empty;
        Assert.True(await Waiting.UntilAsync(async () => renaming.IsCompleted
                || (status = Text(await Call(client, McpTools.AppTree, new() { ["app"] = "ide", ["filter"] = "#StatusText" }))).Contains("to load Caller.cs into its project", StringComparison.Ordinal),
            TimeSpan.FromSeconds(30), Cancel, TimeSpan.FromMilliseconds(100)),
            $"The rename did not say it waited for App to load its new caller: {status}");
        if (renaming.IsCompleted)
        {
            Assert.Fail($"The rename did not wait for App, whose build was held, to load its new caller: {Text(await renaming)}");
        }
        TestFolders.Write(folder, "App/released", string.Empty);
        CallToolResult renamed = await renaming;
        Assert.True(renamed.IsError != true, $"The rename, once App's build was let go, answered: {Text(renamed)}");
        Assert.Equal(Shape.Replace("Make()", "Build()", StringComparison.Ordinal), File.ReadAllText(Path.Combine(folder, "Lib", "Shape.cs")));
        Assert.True(File.ReadAllText(Path.Combine(folder, "App", "Caller.cs")) == Caller.Replace("Shape.Make()", "Shape.Build()", StringComparison.Ordinal),
            $"The rename ({Text(renamed)}) left App's new caller calling a method that is no longer there:\n{File.ReadAllText(Path.Combine(folder, "App", "Caller.cs"))}");
    }

    [AvaloniaFact]
    public void An_agent_optimizes_imports_and_cleans_up_a_file_a_project_and_the_solution_and_hears_what_other_files_have()
    {
        const string messy = "using System.Text;\nusing System.Collections.Generic;\n\nnamespace Shop;\n\npublic static class Messy\n{\n    public static int Count( )\n    {\n"
            + "        List<string> names = [];\n        return   names.Count;\n    }\n}\n";
        const string tidy = "using System.Text;\nusing System;\n\nnamespace Shop;\n\npublic static class Tidy\n{\n    public static int Length() => Console.Title.Length;\n}\n";
        Shop(("Messy.cs", messy), ("Tidy.cs", tidy), ("Notes.txt", "notes\n"));
        MainWindow window = AgentsWindow(Folder);
        List<string> asked = AnswerUndo(window);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            // System is the project's implicit using, so nothing needs it.
            Task<JsonElement> optimizing = Waiting.Apart(() => Call(client, McpTools.Refactor, new() { ["kind"] = "optimize_imports", ["path"] = "Tidy.cs" }));
            Press(Question(window, "the question for the agent's refactoring"), "Allow");
            JsonElement optimized = Await(optimizing, "Optimize Imports on a file");
            Assert.Equal(["Tidy.cs changed"], Files(optimized));
            string tidied = File.ReadAllText(PathOf("Tidy.cs"));
            Assert.True(!tidied.Contains("using", StringComparison.Ordinal) && tidied.Contains("public static int Length() => Console.Title.Length;", StringComparison.Ordinal),
                $"Optimize Imports left Tidy.cs so:\n{tidied}");

            JsonElement described = Refactor(client, new() { ["kind"] = "code_cleanup", ["path"] = "Sum.csproj", ["options"] = new { scope = "project" } }, describe: true);
            Assert.Contains("Messy.cs unchanged", Files(described));
            Assert.Equal(messy, File.ReadAllText(PathOf("Messy.cs")));
            JsonElement cleaned = Refactor(client, new() { ["kind"] = "code_cleanup", ["path"] = "Sum.csproj", ["options"] = new { scope = "project" } });
            Assert.Contains("Messy.cs changed", Files(cleaned));
            string cleanedUp = File.ReadAllText(PathOf("Messy.cs"));
            Assert.True(!cleanedUp.Contains("using", StringComparison.Ordinal) && cleanedUp.Contains("public static int Count()\n", StringComparison.Ordinal)
                && cleanedUp.Contains("        return names.Count;\n", StringComparison.Ordinal), $"Code Cleanup left Messy.cs, which is not open, so:\n{cleanedUp}");
            Assert.DoesNotContain(window.EditorArea.Documents, d => d.Name == "Messy.cs");
            UndoInSolutionView(window, "Messy.cs");
            Assert.Equal("Undo Code Cleanup? It changes Messy.cs.", asked[^1]);
            Assert.Equal(messy, File.ReadAllText(PathOf("Messy.cs")));

            JsonElement solution = Refactor(client, new() { ["kind"] = "optimize_imports", ["options"] = new { scope = "solution" } });
            Assert.Equal(["Messy.cs changed"], Files(solution));
            Assert.DoesNotContain("using", File.ReadAllText(PathOf("Messy.cs")), StringComparison.Ordinal);

            foreach ((Dictionary<string, object?> arguments, string said) in new (Dictionary<string, object?>, string)[]
            {
                (new() { ["kind"] = "change_signature", ["path"] = "Sum.csproj", ["line"] = 1, ["column"] = 2 }, "MSBuild files have no Change Signature in Passenger"),
                (new() { ["kind"] = "safe_delete", ["path"] = "Notes.txt", ["line"] = 1, ["column"] = 1 }, "Passenger has no code help for"),
                (new() { ["kind"] = "inline_everything", ["path"] = "Tidy.cs", ["line"] = 1, ["column"] = 1 }, "there are change_signature, safe_delete, move"),
            })
            {
                CallToolResult refused = Await(Waiting.Apart(() => Result(client, McpTools.Refactor, arguments)), $"refactor {arguments["kind"]} on {arguments["path"]}");
                Assert.True(refused.IsError == true && Text(refused).Contains(said, StringComparison.Ordinal), $"refactor {JsonSerializer.Serialize(arguments)} answered: {Text(refused)}");
            }
        }
        finally
        {
            Leave(client, window);
        }
    }

    /// <summary>Writes the shop's files into the test's project, before its window opens.</summary>
    private void Shop(params (string Name, string Text)[] files)
    {
        foreach ((string name, string text) in files)
        {
            Write(name, text);
        }
    }

    private string PathOf(string name) => Path.Combine(Folder, name);

    /// <summary>refactor's arguments at the end of some text in a file, with its options when given.</summary>
    private static Dictionary<string, object?> Place(string kind, string path, string text, string after, object? options = null)
    {
        (int line, int column) = LineColumn(text, after, text.IndexOf(after, StringComparison.Ordinal) + after.Length);
        Dictionary<string, object?> arguments = new() { ["kind"] = kind, ["path"] = path, ["line"] = line, ["column"] = column };
        if (options is not null)
        {
            arguments["options"] = options;
        }
        return arguments;
    }

    /// <summary>refactor's arguments over the first occurrence of some text in a file, as a selection.</summary>
    private static Dictionary<string, object?> Selection(string kind, string path, string text, string chosen)
    {
        int at = text.IndexOf(chosen, StringComparison.Ordinal);
        (int line, int column) = LineColumn(text, chosen, at);
        (int endLine, int endColumn) = LineColumn(text, chosen, at + chosen.Length);
        return new() { ["kind"] = kind, ["path"] = path, ["line"] = line, ["column"] = column, ["endLine"] = endLine, ["endColumn"] = endColumn };
    }

    private static Dictionary<string, object?> With(Dictionary<string, object?> arguments, object options) => new(arguments) { ["options"] = options };

    private static (int Line, int Column) LineColumn(string text, string what, int offset)
    {
        Assert.True(offset >= 0 && text.Contains(what, StringComparison.Ordinal), $"'{what}' is not in the file");
        int line = text[..offset].Count(c => c == '\n');
        return (line + 1, offset - (text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1) + 1);
    }

    /// <summary>A refactoring the agent makes (or has described), within its time, failing with its words.</summary>
    private JsonElement Refactor(McpClient client, Dictionary<string, object?> arguments, bool describe = false)
    {
        Dictionary<string, object?> sent = new(arguments);
        if (describe)
        {
            sent["describe"] = true;
        }
        return Await(Waiting.Apart(() => Call(client, McpTools.Refactor, sent)), $"refactor {arguments["kind"]}");
    }

    /// <summary>Each file a refactoring answered, as "path change".</summary>
    private static string[] Files(JsonElement answer) =>
        [.. answer.GetProperty("files").EnumerateArray().Select(f => $"{f.GetProperty("path").GetString()} {f.GetProperty("change").GetString()}").Order(StringComparer.Ordinal)];

    /// <summary>Agrees to every Undo the person is asked about, keeping each question's text.</summary>
    private static List<string> AnswerUndo(MainWindow window)
    {
        List<string> asked = [];
        window.Confirm = (_, text, _) =>
        {
            asked.Add(text);
            return Task.FromResult(true);
        };
        return asked;
    }

    /// <summary>The person's Undo in the editor of a file the agent changed.</summary>
    private static void UndoInEditor(MainWindow window, string name)
    {
        window.OpenFile(window.EditorArea.Documents.First(d => d.Name == name).Path);
        Ui.Settle();
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
    }

    /// <summary>The person's Undo in the Solution view, a file's row chosen, which takes back the last command of files.</summary>
    private void UndoInSolutionView(MainWindow window, string file)
    {
        window.ExplorerView.Reveal(PathNames.Canonical(PathOf(file)));
        Ui.Settle();
        Passenger.App.Explorer.ExplorerNode row = Assert.Single(window.ExplorerView.Tree.Rows, r => r.Name == file);
        Ui.ClickRow(window, row);
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
    }

    /// <summary>Saves the files and builds the project with the .NET command line, as a person would check it.</summary>
    private void Builds(string after)
    {
        ChildResult built = Ui.Await(ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["build", Folder, "--no-restore", "-nologo", "-v:q"]), limit: TimeSpan.FromMinutes(3)),
            "the project to build", TimeSpan.FromMinutes(4));
        Assert.True(built.Succeeded, $"The project does not build after {after}:\n{built.Output}{built.Error}");
    }

    /// <summary>The agent's build of the project, as an agent checks what a refactoring left.</summary>
    private async Task Compiles(McpClient client, string after)
    {
        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The project does not build after {after}: {built}");
    }

    /// <summary>The agent goes, then the window closes.</summary>
    private void Leave(McpClient client, MainWindow window)
    {
        Gone(client);
        window.Close();
    }
}
