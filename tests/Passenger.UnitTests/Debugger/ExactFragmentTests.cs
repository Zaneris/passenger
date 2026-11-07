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

    [Fact]
    public void A_member_named_by_itself_is_read_on_the_value_at_its_exact_type()
    {
        Assert.Equal($"{Value}.Held.Length", Of("Held.Length"));
        Assert.Equal($"{Value}.Held[0] + {Value}.Count", Of("Held[0] + Count"));
        Assert.Equal($"{Value}.Twice({Value}.Held.Length)", Of("Twice(Held.Length)"));
        Assert.Equal($"{Value}.Twice<int>(1)", Of("Twice<int>(1)"));
        Assert.Equal($"$\"{{{Value}.Held.Length}} held\"", Of("$\"{Held.Length} held\""));
    }

    [Fact]
    public void This_is_the_value_at_its_exact_type()
    {
        Assert.Equal($"{Value}.Held.Length", Of("this.Held.Length"));
    }

    /// <summary>A static member has no value to go through: the construction itself holds it.</summary>
    [Fact]
    public void A_static_member_is_read_on_the_construction()
    {
        Assert.Equal($"{Exact}.Empty.Length", Of("Empty.Length"));
        Assert.Equal($"{Exact}.Make({Value}.Held)", Of("Make(Held)"));
    }

    [Fact]
    public void An_expression_naming_nothing_of_the_value_has_nothing_to_write_again()
    {
        Assert.Null(Of("Other.Length"));
        Assert.Null(Of("1 + 2"));
        Assert.Null(Of("typeof(T)"));
    }

    [Fact]
    public void What_only_shares_a_members_name_stays_as_it_is()
    {
        Assert.Equal($"{Value}.Held.Count", Of("Held.Count"));
        Assert.Equal($"{Value}.Held?.Count", Of("Held?.Count"));
        Assert.Equal($"(Count){Value}.Held", Of("(Count)Held"));
        Assert.Equal($"typeof(Count) == {Value}.Held", Of("typeof(Count) == Held"));
        Assert.Equal($"{Value}.Held is Count", Of("Held is Count"));
        Assert.Equal($"{Value}.Twice(Count: 1)", Of("Twice(Count: 1)"));
        Assert.Equal($"new Other {{ Held = {Value}.Count }}", Of("new Other { Held = Count }"));
    }

    /// <summary>What the expression declares hides the member of its name, wherever it says it.</summary>
    [Fact]
    public void A_name_the_expression_declares_is_its_own()
    {
        Assert.Equal($"{Value}.Held.Select(Count => Count + 1)", Of("Held.Select(Count => Count + 1)"));
        Assert.Equal($"from Count in {Value}.Held select Count", Of("from Count in Held select Count"));
        Assert.Equal($"{Value}.Held is int[] Count ? Count : null", Of("Held is int[] Count ? Count : null"));
    }

    [Fact]
    public void Text_that_is_no_expression_is_left_to_the_compiler()
    {
        Assert.Null(Of("Held."));
    }
}
