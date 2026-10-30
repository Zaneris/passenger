using Passenger.Debugger;

namespace Passenger.UnitTests.Debugger;

/// <summary>The C# a row of Variables is reached by, made from its owner's: each is one the evaluator takes, and none is
/// made where the owner has none.</summary>
public class RowExpressionsTests
{
    [Theory]
    [InlineData("item", "count", null, "item.count")]
    [InlineData("item", "count", "Base", "((Base)item).count")]
    [InlineData("*p", "x", null, "(*p).x")]
    [InlineData("$exception", "Message", null, "$exception.Message")]
    [InlineData("a + b", "Length", null, "(a + b).Length")]
    [InlineData("list[0]", "Name", null, "list[0].Name")]
    public void A_member_follows_its_owner_as_an_operand_cast_where_only_a_cast_reaches_it(string owner, string member, string? declaring, string expected) =>
        Assert.Equal(expected, RowExpressions.Member(owner, member, declaring));

    [Theory]
    [InlineData("holder", "event", "holder.@event")]
    [InlineData("Shelf", "Reads", "Shelf.Reads")]
    [InlineData("Typed<int>", "Count", "Typed<int>.Count")]
    [InlineData("Outer.Inner<string>", "static", "Outer.Inner<string>.@static")]
    public void A_member_named_by_a_keyword_takes_an_at_and_a_types_static_member_follows_the_type(string owner, string member, string expected) =>
        Assert.Equal(expected, RowExpressions.Member(owner, member));

    [Fact]
    public void A_name_that_is_a_keyword_takes_an_at_and_no_other_does()
    {
        Assert.Equal("@class", RowExpressions.Name("class"));
        Assert.Equal("@this", RowExpressions.Name("this"));
        Assert.Equal("var", RowExpressions.Name("var"));
        Assert.Equal("count", RowExpressions.Name("count"));
    }

    [Fact]
    public void A_value_cast_to_its_own_type_reaches_its_elements_and_its_view()
    {
        string? boxed = RowExpressions.As("boxedList", "System.Collections.Generic.List<string>");
        Assert.Equal("((System.Collections.Generic.List<string>)boxedList)[0]", RowExpressions.Element(boxed, [0u]));
        Assert.Equal("new System.Collections.Generic.ICollectionDebugView<string>((System.Collections.Generic.HashSet<string>)held)",
            RowExpressions.Viewed(RowExpressions.As("held", "System.Collections.Generic.HashSet<string>"), "System.Collections.Generic.ICollectionDebugView<string>"));
        Assert.Equal("(string[])(a ?? b)", RowExpressions.As("a ?? b", "string[]"));
        Assert.Null(RowExpressions.As(null, "string[]"));
    }

    [Fact]
    public void An_element_a_pointed_at_value_and_an_enums_number_are_reached_from_their_owner()
    {
        Assert.Equal("grid[100007, 0]", RowExpressions.Element("grid", [100007u, 0u]));
        Assert.Equal("(a ?? b)[2]", RowExpressions.Element("a ?? b", [2u]));
        Assert.Equal("*p", RowExpressions.Pointed("p"));
        Assert.Equal("((int)colour)", RowExpressions.Cast("colour", "int"));
        Assert.Equal("new System.Collections.Generic.ICollectionDebugView<int>(set)", RowExpressions.Viewed("set", "System.Collections.Generic.ICollectionDebugView<int>"));
    }

    [Fact]
    public void No_row_has_an_expression_where_its_owner_has_none()
    {
        Assert.Null(RowExpressions.Member(null, "count"));
        Assert.Null(RowExpressions.Element(null, [0u]));
        Assert.Null(RowExpressions.Pointed(null));
        Assert.Null(RowExpressions.Cast(null, "int"));
        Assert.Null(RowExpressions.Viewed(null, "View"));
    }

    [Theory]
    [InlineData("System.Collections.Generic.ICollection<T>.IsReadOnly", "T", "string", "System.Collections.Generic.ICollection<string>", "IsReadOnly")]
    [InlineData("System.Collections.IList.IsFixedSize", "T", "int", "System.Collections.IList", "IsFixedSize")]
    [InlineData("System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<TKey,TValue>>.IsReadOnly", "TKey,TValue", "string,int",
        "System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string,int>>", "IsReadOnly")]
    [InlineData("IShape<T[]>.Side", "T", "System.Collections.Generic.List<int>", "IShape<System.Collections.Generic.List<int>[]>", "Side")]
    public void An_explicit_implementation_is_reached_through_its_interface_as_the_values_type_arguments_close_it(string name, string parameters, string arguments, string iface, string member)
    {
        Assert.Equal((iface, member), RowExpressions.Explicit(name, Split(parameters), Split(arguments)));

        static string[] Split(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void A_name_with_no_interface_is_no_explicit_implementation()
    {
        Assert.Null(RowExpressions.Explicit("Count", ["T"], ["int"]));
        Assert.Null(RowExpressions.Explicit("<Count>k__BackingField", [], []));
    }
}
