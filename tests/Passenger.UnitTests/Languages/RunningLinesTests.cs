using Microsoft.CodeAnalysis.CSharp;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.UnitTests.Languages;

/// <summary>A C# line runs code only where the debugger can bind a breakpoint there: never on a blank or comment line, a
/// using, a namespace's or a type's own words, an attribute, a field with no value, an auto-property's accessors or a
/// method's declaration; always in a body, an expression body, an initializer, a constructor's declaration (its base call)
/// and a primary constructor's type's. A breakpoint on a line that runs nothing moves to the next that may.</summary>
public sealed class RunningLinesTests
{
    private static readonly (string Line, bool Runs)[] Order =
    [
        ("namespace Shop;", false),
        ("", false),
        ("using System;", false),
        ("", false),
        ("/// <summary>A thing.</summary>", false),
        ("[Serializable]", false),
        ("public sealed class Order", false),
        ("{", false),
        ("    private const int Most = 3;", false),
        ("    private int count = 1;", true),
        ("    private string? name;", false),
        ("", false),
        ("    public Order(int start)", true),
        ("    {", true),
        ("        count = start;", true),
        ("    }", true),
        ("", false),
        ("    public int Count => count;", true),
        ("    public string Name { get; set; } = \"\";", true),
        ("    public int Size { get; }", false),
        ("    public int Twice", false),
        ("    {", false),
        ("        get => count * 2;", true),
        ("    }", false),
        ("", false),
        ("    public void Add(", false),
        ("        int more = 1)", false),
        ("    {", true),
        ("        // more of it", false),
        ("#if DEBUG", false),
        ("        count += more;", true),
        ("#endif", false),
        ("        Func<int, int> twice = x =>", true),
        ("            x * 2;", true),
        ("    }", true),
        ("}", false),
        ("", false),
        ("public enum Kind", false),
        ("{", false),
        ("    Small = 1,", false),
        ("}", false),
        ("", false),
        ("public interface IPriced", false),
        ("{", false),
        ("    decimal Price { get; }", false),
        ("}", false),
        ("", false),
        ("public class Square(double side)", true),
        ("{", false),
        ("}", false),
    ];

    private static ParsedText Parsed(out string text)
    {
        text = string.Join('\n', Order.Select(l => l.Line)) + "\n";
        CSharpParseOptions debug = CSharpParseOptions.Default.WithPreprocessorSymbols("DEBUG");
        return new ParsedText(CSharpSyntaxTree.ParseText(text, debug, cancellationToken: TestContext.Current.CancellationToken), TextSnapshot.From(text));
    }

    [Fact]
    public void A_line_runs_code_only_where_the_debugger_can_bind_a_breakpoint_on_it()
    {
        ParsedText parsed = Parsed(out _);
        List<string> wrong = [.. Order.Select((line, at) => (line, at)).Where(l => RunningLines.Runs(parsed, l.at) != l.line.Runs)
            .Select(l => $"line {l.at + 1} \"{l.line.Line.Trim()}\" read as {(l.line.Runs ? "running nothing" : "running code")}")];
        Assert.True(wrong.Count == 0, $"A breakpoint there would be said to bind or move wrongly:\n{string.Join('\n', wrong)}");
    }

    [Fact]
    public void A_breakpoint_on_a_line_that_runs_nothing_moves_to_the_next_that_may_and_none_after_the_last()
    {
        ParsedText parsed = Parsed(out _);
        int field = Array.FindIndex(Order, l => l.Line.Contains("string? name", StringComparison.Ordinal));
        int constructor = Array.FindIndex(Order, l => l.Line.Contains("public Order(int start)", StringComparison.Ordinal));
        Assert.Equal(constructor, RunningLines.NextAfter(parsed, field));
        int signature = Array.FindIndex(Order, l => l.Line.Contains("public void Add(", StringComparison.Ordinal));
        Assert.Equal(signature + 2, RunningLines.NextAfter(parsed, signature));
        Assert.Null(RunningLines.NextAfter(parsed, Order.Length - 2));
        Assert.False(RunningLines.Runs(parsed, Order.Length + 5));
    }

    /// <summary>Every line's next is the first later line that runs, a string over several lines running on each of them, with
    /// either line end, and past the file's end there is none.</summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void The_next_line_is_the_first_later_line_that_runs(string end)
    {
        string text = string.Join(end, Order.Select(l => l.Line).Concat(["public sealed class Quoted", "{", "    private string? unset;", "", "    private string said = @\"one", "", "two\";",
            "    private int after;", "}", ""]));
        ParsedText parsed = new(CSharpSyntaxTree.ParseText(text, cancellationToken: TestContext.Current.CancellationToken), TextSnapshot.From(text));
        for (int line = -1; line < parsed.Text.LineCount + 2; line++)
        {
            int start = line;
            int? first = Enumerable.Range(0, parsed.Text.LineCount).Where(l => l > start && RunningLines.Runs(parsed, l)).Select(l => (int?)l).FirstOrDefault();
            Assert.True(first == RunningLines.NextAfter(parsed, line), $"A breakpoint on line {line + 1} would be said to move to line {RunningLines.NextAfter(parsed, line) + 1}, not {first + 1}");
        }
    }

    /// <summary>A class of nothing but auto-properties has no line that runs, so the hover over a breakpoint at its top reads to
    /// the file's end, on the window's thread: what that allocates, once its tree is read, stays that of one walk however long
    /// the class, where a walk for each line allocates a little more for every line and takes time by the square of them.</summary>
    [Fact]
    public void Finding_the_next_line_that_runs_is_one_walk_however_long_the_class()
    {
        long few = WalkBytes(200);
        long many = WalkBytes(4000);
        Assert.True(many < (few * 2) + 4096, $"Finding the next line in 4000 properties allocated {many} bytes against {few} in 200: a walk for each line freezes the window on a long class");
    }

    private static long WalkBytes(int properties)
    {
        string text = "public sealed class Many\n{\n" + string.Concat(Enumerable.Range(0, properties).Select(i => $"    public int P{i} {{ get; set; }}\n")) + "}\n";
        ParsedText parsed = new(CSharpSyntaxTree.ParseText(text, cancellationToken: TestContext.Current.CancellationToken), TextSnapshot.From(text));
        Assert.Null(RunningLines.NextAfter(parsed, 0));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(RunningLines.NextAfter(parsed, 0));
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
