using Passenger.Debugger;

namespace Passenger.UnitTests.Debugger;

/// <summary>The C# a row of Variables is reached by, made from its owner's: each is one the evaluator takes, and none is
/// made where the owner has none.</summary>
public class RowExpressionsTests
{
    [Fact]
    public void A_rows_expression_is_made_from_its_owners_as_the_evaluator_takes_it_and_none_where_the_owner_has_none() => Cases.All(
        ("a member follows its owner as an operand, cast where only a cast reaches it", () => Cases.Each(
        [
            ("item", "count", (string?)null, "item.count"),
            ("item", "count", "Base", "((Base)item).count"),
            ("*p", "x", null, "(*p).x"),
            ("a + b", "Length", null, "(a + b).Length"),
        ], row => Assert.Equal(row.Item4, RowExpressions.Member(row.Item1, row.Item2, row.Item3)))),
        ("a member named by a keyword takes an at and a type's static member follows the type", () =>
        {
            Assert.Equal("holder.@event", RowExpressions.Member("holder", "event"));
            Assert.Equal("Outer.Inner<string>.@static", RowExpressions.Member("Outer.Inner<string>", "static"));
            Assert.Equal("var", RowExpressions.Name("var"));
        }),
        ("a value cast to its own type reaches its elements and its view", () =>
        {
            string? boxed = RowExpressions.As("boxedList", "System.Collections.Generic.List<string>");
            Assert.Equal("((System.Collections.Generic.List<string>)boxedList)[0]", RowExpressions.Element(boxed, [0u]));
            Assert.Equal("(string[])(a ?? b)", RowExpressions.As("a ?? b", "string[]"));
        }),
        ("an element, a pointed-at value and an enum's number are reached from their owner", () =>
        {
            Assert.Equal("grid[100007, 0]", RowExpressions.Element("grid", [100007u, 0u]));
            Assert.Equal("(a ?? b)[2]", RowExpressions.Element("a ?? b", [2u]));
            Assert.Equal("*p", RowExpressions.Pointed("p"));
            Assert.Equal("((int)colour)", RowExpressions.Cast("colour", "int"));
        }),
        ("no row has an expression where its owner has none", () =>
        {
            Assert.Null(RowExpressions.Member(null, "count"));
            Assert.Null(RowExpressions.Element(null, [0u]));
            Assert.Null(RowExpressions.Viewed(null, "View"));
        }),
        ("an explicit implementation is reached through its interface as the value's type arguments close it", () =>
        {
            Assert.Equal(("System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string,int>>", "IsReadOnly"),
                RowExpressions.Explicit("System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<TKey,TValue>>.IsReadOnly", ["TKey", "TValue"], ["string", "int"]));
            Assert.Equal(("IShape<System.Collections.Generic.List<int>[]>", "Side"), RowExpressions.Explicit("IShape<T[]>.Side", ["T"], ["System.Collections.Generic.List<int>"]));
            Assert.Null(RowExpressions.Explicit("<Count>k__BackingField", [], []));
        }));
}
