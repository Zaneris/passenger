using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.UnitTests.Languages;

/// <summary>What the window's thread asks of a file's syntax at a place costs what is near the place, however long the chain
/// of member accesses, operators or else-ifs around it: Extend Selection, Surround With and where a breakpoint's line runs
/// each walked such a chain once for every link of it, over a minute for 100,000 links. Each answers here as it does on a
/// short chain, on a stack a third of the thread pool's.</summary>
public sealed class LongChainsTests
{
    private const int Links = 100_000;

    private static ParsedText Parsed(string text)
    {
        ParsedText? parsed = null;
        SmallStack.Run(() => parsed = new ParsedText(CSharpParsing.Parse(text), TextSnapshot.From(text)));
        return parsed!;
    }

    [Fact]
    public void Extend_selection_in_the_middle_of_a_long_chain_takes_the_word_then_the_chain_up_to_it()
    {
        string before = "class C { void M() { var x = a";
        string text = before + string.Concat(Enumerable.Repeat(".b", Links)) + "; } }";
        ParsedText parsed = Parsed(text);
        int word = before.Length + Links + 1;
        SmallStack.Run(() =>
        {
            TextRange? named = SelectionRanges.Extend(parsed.Text, parsed, new TextRange(word, 0));
            Assert.Equal(new TextRange(word, 1), named);
            Assert.Equal(TextRange.FromBounds(before.Length - 1, word + 1), SelectionRanges.Extend(parsed.Text, parsed, named!.Value));
        });
    }

    [Fact]
    public void Surround_with_reads_the_start_of_a_long_sum_as_an_expression_and_a_long_else_if_chain_as_statements()
    {
        string before = "class C { int M() { return 1";
        string sum = before + string.Concat(Enumerable.Repeat(" + 1", Links)) + "; } }";
        ParsedText added = Parsed(sum);
        int start = before.Length - 1;
        SmallStack.Run(() => Assert.True(SurroundScopes.Of(added, TextRange.FromBounds(start, start + 1 + (4 * (Links / 2)))).Scope.HasFlag(SurroundScope.Expression),
            "The first half of a sum read as no expression"));

        string chain = "class C { void M(int a) {\n        if (a == 0) { }\n" + string.Concat(Enumerable.Repeat("        else if (a == 1) { }\n", Links)) + "    }\n}\n";
        ParsedText branched = Parsed(chain);
        int from = chain.IndexOf("if (a == 0)", StringComparison.Ordinal);
        int to = chain.LastIndexOf("{ }", StringComparison.Ordinal) + 3;
        SmallStack.Run(() => Assert.True(SurroundScopes.Of(branched, TextRange.FromBounds(from, to)).Scope.HasFlag(SurroundScope.Statements),
            "A whole if and its else-ifs read as no statement"));
    }

    [Fact]
    public void The_lines_of_a_string_after_a_long_else_if_chain_are_found_in_one_walk()
    {
        string chain = "class C { void M(int a) {\n        if (a == 0) { }\n" + string.Concat(Enumerable.Repeat("        else if (a == 1) { }\n", Links))
            + "        var s = @\"one\ntwo\nthree\";\n    }\n}\n";
        ParsedText parsed = Parsed(chain);
        int from = chain.IndexOf("if (a == 0)", StringComparison.Ordinal);
        int to = chain.IndexOf("three", StringComparison.Ordinal);
        SmallStack.Run(() => Assert.Equal([Links + 2, Links + 3], SurroundScopes.InStrings(parsed, TextRange.FromBounds(from, to)).Order()));
    }

    [Fact]
    public void A_constants_long_sum_runs_nothing_and_the_next_line_that_runs_is_found_past_it()
    {
        string text = "class C\n{\n    const int K = 1" + string.Concat(Enumerable.Repeat(" + 1", Links)) + ";\n    int k = K;\n}\n";
        ParsedText parsed = Parsed(text);
        SmallStack.Run(() =>
        {
            Assert.False(RunningLines.Runs(parsed, 2), "A constant's value read as code that runs");
            Assert.Equal(3, RunningLines.NextAfter(parsed, 1));
        });
    }
}
