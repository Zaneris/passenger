using Passenger.Debugger.ExpressionEvaluator.Compiler;

namespace Passenger.UnitTests.Debugger;

/// <summary>Which expressions the evaluator takes for LINQ calls, which it compiles again once the program has loaded
/// System.Linq, whatever their compile's error said without it.</summary>
public class LinqCallsTests
{
    [Fact]
    public void An_expression_is_taken_for_a_LINQ_call_where_it_names_a_LINQ_method_and_only_there() => Cases.All(
        ("an expression calling a LINQ method is taken for one", () => Cases.Each(
            ["list?.FirstOrDefault()", "System.Linq.Enumerable.Empty<int>()", "from x in list select x * 2", "$\"{names.Aggregate((a, b) => a + b)}\""],
            expression => Assert.True(CilExpressionCompiler.NamesLinq(expression), $"{expression} calls a LINQ method, but would be compiled only once, failing where the program has not loaded System.Linq"))),
        ("an expression naming no LINQ method is not taken for one", () => Cases.Each(
            ["text.Length", "first ?? second"],
            expression => Assert.False(CilExpressionCompiler.NamesLinq(expression), $"{expression} names no LINQ method, but its failed compile would load System.Linq into the program"))));
}
