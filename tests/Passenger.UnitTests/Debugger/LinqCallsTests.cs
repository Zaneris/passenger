using Passenger.Debugger.ExpressionEvaluator.Compiler;

namespace Passenger.UnitTests.Debugger;

/// <summary>Which expressions the evaluator takes for LINQ calls, which it compiles again once the program has loaded
/// System.Linq, whatever their compile's error said without it.</summary>
public class LinqCallsTests
{
    [Theory]
    [InlineData("list.Count()")]
    [InlineData("list.Count(x => x > 1)")]
    [InlineData("list?.FirstOrDefault()")]
    [InlineData("AppDomain.CurrentDomain.GetAssemblies().Count(a => a.IsDynamic)")]
    [InlineData("Enumerable.Range(1, 3).Sum()")]
    [InlineData("System.Linq.Enumerable.Empty<int>()")]
    [InlineData("from x in list select x * 2")]
    [InlineData("$\"{names.Aggregate((a, b) => a + b)}\"")]
    public void An_expression_calling_a_LINQ_method_is_taken_for_one(string expression) =>
        Assert.True(CilExpressionCompiler.NamesLinq(expression), $"{expression} calls a LINQ method, but would be compiled only once, failing where the program has not loaded System.Linq");

    [Theory]
    [InlineData("text.Length")]
    [InlineData("Twice(4) + 1")]
    [InlineData("new[] { 1, 2 }[0]")]
    [InlineData("first ?? second")]
    public void An_expression_naming_no_LINQ_method_is_not_taken_for_one(string expression) =>
        Assert.False(CilExpressionCompiler.NamesLinq(expression), $"{expression} names no LINQ method, but its failed compile would load System.Linq into the program");
}
