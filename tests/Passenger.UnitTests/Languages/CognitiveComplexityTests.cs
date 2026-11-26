using Microsoft.CodeAnalysis.CSharp;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.UnitTests.Languages;

public sealed class CognitiveComplexityTests
{
    [Fact]
    public void Branches_nesting_and_logical_runs_are_summed_on_their_source_lines()
    {
        FunctionComplexity function = Assert.Single(Read("""
            class Meter
            {
                int Read(bool a, bool b, bool c)
                {
                    if (a && b && c || a)
                    {
                        for (; a;)
                        {
                            if (b) goto Stop;
                        }
                    }
                    else if (b)
                    {
                        return c ? 1 : 0;
                    }
                    else
                    {
                        while (b) break;
                    }
                    Stop:
                    return Read(a, b, c) + this.Read(a, b, c);
                }
            }
            """));

        Assert.Equal(19, function.Score);
        Assert.Equal([(5, 3), (7, 2), (9, 7), (12, 1), (14, 2), (16, 1), (18, 2), (21, 1)],
            function.Lines.Select(line => (line.Line + 1, line.Points)));
    }

    [Fact]
    public void Every_function_on_shared_lines_keeps_its_own_score()
    {
        IReadOnlyList<FunctionComplexity> functions = Read("""
            class Meter
            {
                public Meter() { } public Meter(bool a) : this(a ? 1 : 0) { }
                private Meter(int value) { }
                int Read(bool a)
                {
                    int Local() => a ? 1 : 0;
                    static int Other(bool b) => b ? 1 : 0;
                    System.Func<int> read = () => a ? 1 : 0;
                    return Local() + Other(a) + read();
                }
                int Value { get => 0; set { if (value > 0) { } } }
            }
            """);

        Assert.Equal([("Meter", 0), ("Meter", 1), ("Meter", 0), ("Read", 4), ("Local", 1), ("Other", 1), ("lambda", 1),
            ("get", 0), ("set", 1)], functions.Select(function => (function.Name, function.Score)));
        Assert.Equal(functions[0].RowOffset, functions[1].RowOffset);
        Assert.Equal([(7, 2), (9, 2)], functions[3].Lines.Select(line => (line.Line + 1, line.Points)));
    }

    [Fact]
    public void Logical_runs_follow_source_order_across_parentheses_and_reset_at_negation()
    {
        FunctionComplexity function = Assert.Single(Read("""
            class Meter
            {
                bool Matches(bool a, bool b, bool c) =>
                    a &&
                    (b && c) ||
                    !(a && b) ||
                    a &&
                    b;
            }
            """));

        Assert.Equal(4, function.Score);
        Assert.Equal([(4, 1), (5, 1), (6, 1), (7, 1)], function.Lines.Select(line => (line.Line + 1, line.Points)));
    }

    private static IReadOnlyList<FunctionComplexity> Read(string code)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        return CognitiveComplexity.Of(new ParsedText(CSharpSyntaxTree.ParseText(code, cancellationToken: cancel), TextSnapshot.From(code)), cancel);
    }
}
