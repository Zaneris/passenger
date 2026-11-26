using Passenger.App.CSharp;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.UnitTests.Languages;

/// <summary>A C# file's outline read from its syntax alone, as an agent gets it for a file the language server does not
/// hold: every namespace, type and member, nested as declared, each with its kind and line.</summary>
public sealed class DeclarationsOutlineTests
{
    private const string Shop = "namespace Shop;\n\npublic delegate void Sold<T>(T item, int count);\n\npublic record Receipt(int Total);\n\n"
        + "public interface ITill\n{\n    event EventHandler? Opened, Closed;\n    int this[int slot] { get; }\n}\n\n"
        + "public struct Coin\n{\n    public const int Cents = 100;\n    public int Value, Year;\n\n    public static Coin operator +(Coin one, Coin other) => one;\n\n"
        + "    public static implicit operator int(Coin coin) => coin.Value;\n}\n\npublic enum Metal\n{\n    Gold,\n    Silver,\n}\n\n"
        + "public class Till\n{\n    public Till(string name)\n    {\n    }\n\n    ~Till()\n    {\n    }\n\n    public string Name { get; } = \"\";\n\n"
        + "    public event EventHandler Rung { add { } remove { } }\n\n    public T Take<T>(List<T> items, ref int count) => items[0];\n\n"
        + "    private class Drawer\n    {\n        private int Count() => 0;\n    }\n}\n\npublic union Tender(Coin, Receipt)\n{\n    public bool Exact() => true;\n}\n\n"
        + "public static class Tills\n{\n    extension(Till till)\n    {\n        public int Drawers => 1;\n    }\n\n"
        + "    extension<T>(List<T> items) where T : struct\n    {\n        public T Last() => items[^1];\n    }\n}\n";

    [Fact]
    public void A_files_outline_names_each_declaration_nested_as_declared_with_its_kind_and_line() => Cases.All(
        ("a files outline names each namespace type and member nested with its kind and line, a union and an extension blocks members too (#2332)",
            A_files_outline_names_each_namespace_type_and_member_nested_with_its_kind_and_line),
        ("a files rows nest as declared so a line finds the member type and namespace it is in", A_files_rows_nest_as_declared_so_a_line_finds_the_member_type_and_namespace_it_is_in));

    private void A_files_outline_names_each_namespace_type_and_member_nested_with_its_kind_and_line()
    {
        string outline = Declarations.Outline(new ParsedText(CSharpParsing.Parse(Shop), TextSnapshot.From(Shop)));
        Assert.Equal("""
            Shop (namespace, line 1)
              Sold<T>(T, int) (delegate, line 3)
              Receipt (record, line 5)
              ITill (interface, line 7)
                Opened (event, line 9)
                Closed (event, line 9)
                this[int] (indexer, line 10)
              Coin (struct, line 13)
                Cents (constant, line 15)
                Value (field, line 16)
                Year (field, line 16)
                operator +(Coin, Coin) (operator, line 18)
                implicit operator int (operator, line 20)
              Metal (enum, line 23)
                Gold (enum member, line 25)
                Silver (enum member, line 26)
              Till (class, line 29)
                Till(string) (constructor, line 31)
                ~Till() (destructor, line 35)
                Name (property, line 39)
                Rung (event, line 41)
                Take<T>(List<T>, int) (method, line 43)
                Drawer (class, line 45)
                  Count() (method, line 47)
              Tender (union, line 51)
                Exact() (method, line 53)
              Tills (class, line 56)
                extension(Till) (extension, line 58)
                  Drawers (property, line 60)
                extension<T>(List<T>) (extension, line 63)
                  Last() (method, line 65)
            """.ReplaceLineEndings("\n"), outline);
    }

    /// <summary>The navigation bar's outline of the same file: rows nested as declared, each with the type it has or returns,
    /// spanning its declaration, so the caret's line finds the member it is in, the type it is in between members and the
    /// namespace above the types; a type's members are its rows' own, and the file read again unchanged is the outline it
    /// had, which the bar then leaves as it is; a member of a union or an extension block is the union's or the block's.</summary>
    private void A_files_rows_nest_as_declared_so_a_line_finds_the_member_type_and_namespace_it_is_in()
    {
        ParsedText parsed = new(CSharpParsing.Parse(Shop), TextSnapshot.From(Shop));
        Outline<string> rows = Declarations.Rows(parsed, Row, cancel: TestContext.Current.CancellationToken);

        int take = rows.InnermostAt(42);
        Assert.Equal("2 Take<T>(List<T>, int) method: T", rows.Rows[take]);
        int till = rows.ParentOf(take);
        Assert.Equal("1 Till class", rows.Rows[till]);
        Assert.Equal("0 Shop namespace", rows.Rows[rows.ParentOf(till)]);
        Assert.Equal(-1, rows.ParentOf(rows.ParentOf(till)));
        Assert.Equal(["2 Till(string) constructor", "2 ~Till() destructor", "2 Name property: string", "2 Rung event: EventHandler",
            "2 Take<T>(List<T>, int) method: T", "2 Drawer class"], rows.ChildrenOf(till).Select(i => rows.Rows[i]));
        Assert.Equal(till, rows.InnermostAt(37));
        Assert.Equal("3 Count() method: int", rows.Rows[rows.InnermostAt(46)]);
        Assert.Equal("0 Shop namespace", rows.Rows[rows.InnermostAt(1)]);
        Assert.Equal(["0 Shop namespace"], rows.ChildrenOf(-1).Select(i => rows.Rows[i]));
        int last = rows.InnermostAt(64);
        Assert.Equal("3 Last() method: T", rows.Rows[last]);
        Assert.Equal("2 extension<T>(List<T>) extension", rows.Rows[rows.ParentOf(last)]);
        Assert.Equal("1 Tender union", rows.Rows[rows.ParentOf(rows.InnermostAt(52))]);

        ParsedText again = new(CSharpParsing.Parse(Shop), TextSnapshot.From(Shop));
        Assert.Same(rows, Declarations.Rows(again, Row, rows, TestContext.Current.CancellationToken));

        // Its depth, its name, its kind and its type.
        static string Row(Declarations.Declared declared, int depth) =>
            $"{depth} {declared.Name} {declared.Kind}{(declared.Type is { } type ? $": {type}" : string.Empty)}";
    }
}
