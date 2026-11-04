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
}
