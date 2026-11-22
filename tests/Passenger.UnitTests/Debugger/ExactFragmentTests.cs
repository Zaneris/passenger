using Passenger.Debugger.ExpressionEvaluator.Compiler;

namespace Passenger.UnitTests.Debugger;

/// <summary>An expression on a value of a generic type written to read the value at its exact type: what it names on the value
/// goes through a cast to the construction, and nothing else it says changes.</summary>
public sealed class ExactFragmentTests
{
    private const string Exact = "global::Box<int[]>";
    private const string Value = $"(({Exact})(object)this)";

    private static readonly HashSet<string> Instance = new(StringComparer.Ordinal) { "Held", "Count", "Twice" };
    private static readonly HashSet<string> Static = new(StringComparer.Ordinal) { "Empty", "Make" };

    private static string? Of(string expression) => ExactFragment.Of(expression, Exact, Instance, Static);

    /// <summary>A static member has no value to go through, so the construction itself holds it; what the expression
    /// declares hides the member of its name.</summary>
    [Fact]
    public void What_an_expression_names_of_the_value_is_read_at_its_exact_type_and_nothing_else_it_says_changes() => Cases.All(
        ("a member named by itself or through this is read on the value at its exact type", () =>
        {
            Assert.Equal($"{Value}.Twice({Value}.Held.Length)", Of("Twice(Held.Length)"));
            Assert.Equal($"$\"{{{Value}.Held.Length}} held\"", Of("$\"{Held.Length} held\""));
            Assert.Equal($"{Value}.Held.Length", Of("this.Held.Length"));
        }),
        ("a static member is read on the construction", () => Assert.Equal($"{Exact}.Make({Value}.Held)", Of("Make(Held)"))),
        ("an expression naming nothing of the value, or no expression, has nothing to write again", () =>
        {
            Assert.Null(Of("Other.Length"));
            Assert.Null(Of("Held."));
        }),
        ("what only shares a member's name stays as it is", () =>
        {
            Assert.Equal($"{Value}.Held.Count", Of("Held.Count"));
            Assert.Equal($"typeof(Count) == {Value}.Held", Of("typeof(Count) == Held"));
            Assert.Equal($"{Value}.Twice(Count: 1)", Of("Twice(Count: 1)"));
            Assert.Equal($"new Other {{ Held = {Value}.Count }}", Of("new Other { Held = Count }"));
        }),
        ("a name the expression declares is its own", () =>
        {
            Assert.Equal($"{Value}.Held.Select(Count => Count + 1)", Of("Held.Select(Count => Count + 1)"));
            Assert.Equal($"from Count in {Value}.Held select Count", Of("from Count in Held select Count"));
        }));
}
