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

    [AvaloniaFact]
    public void An_agent_changes_a_signature_for_every_caller_moves_types_and_extracts_an_interface_each_undone_whole_by_the_person()
    {
        Shop(("Calc.cs", Calc), ("Till.cs", Till), ("Report.cs", Report), ("Order.cs", Order), ("OrderBook.cs", OrderBook), ("Shapes.cs", Shapes));
        MainWindow window = AgentsWindow(Folder);
        List<string> asked = AnswerUndo(window);
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<JsonElement> describing = Task.Run(() => Call(client, McpTools.Refactor, Place("change_signature", "Calc.cs", Calc, "int Sca")));
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

            CallToolResult both = Await(Task.Run(() => Result(client, McpTools.Refactor, Place("move", "Shapes.cs", File.ReadAllText(PathOf("Shapes.cs")), "class Squ", new { file = "Square.cs", @namespace = "Shop.Shapes" }))), "a move to a file and a namespace at once");
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
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<CallToolResult> refusing = Task.Run(() => Result(client, McpTools.Refactor, Place("safe_delete", "Calc.cs", Calc, "int Sca")));
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

    [AvaloniaFact]
    public void An_agent_optimizes_imports_and_cleans_up_a_file_a_project_and_the_solution_and_hears_what_other_files_have()
    {
        const string messy = "using System.Text;\nusing System.Collections.Generic;\n\nnamespace Shop;\n\npublic static class Messy\n{\n    public static int Count( )\n    {\n"
            + "        List<string> names = [];\n        return   names.Count;\n    }\n}\n";
        const string tidy = "using System.Text;\nusing System;\n\nnamespace Shop;\n\npublic static class Tidy\n{\n    public static int Length() => Console.Title.Length;\n}\n";
        Shop(("Messy.cs", messy), ("Tidy.cs", tidy), ("Notes.txt", "notes\n"));
        MainWindow window = AgentsWindow(Folder);
        List<string> asked = AnswerUndo(window);
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            // System is the project's implicit using, so nothing needs it.
            Task<JsonElement> optimizing = Task.Run(() => Call(client, McpTools.Refactor, new() { ["kind"] = "optimize_imports", ["path"] = "Tidy.cs" }));
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
                CallToolResult refused = Await(Task.Run(() => Result(client, McpTools.Refactor, arguments)), $"refactor {arguments["kind"]} on {arguments["path"]}");
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
        return Await(Task.Run(() => Call(client, McpTools.Refactor, sent)), $"refactor {arguments["kind"]}");
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

    /// <summary>The agent goes, then the window closes.</summary>
    private void Leave(McpClient client, MainWindow window)
    {
        Gone(client);
        window.Close();
    }
}
