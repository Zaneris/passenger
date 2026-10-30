using Microsoft.CodeAnalysis.CSharp;
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
        + "    private class Drawer\n    {\n        private int Count() => 0;\n    }\n}\n";

    [Fact]
    public void A_files_outline_names_each_namespace_type_and_member_nested_with_its_kind_and_line()
    {
        string outline = Declarations.Outline(new ParsedText(CSharpSyntaxTree.ParseText(Shop, cancellationToken: TestContext.Current.CancellationToken), TextSnapshot.From(Shop)));
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
            """.ReplaceLineEndings("\n"), outline);
    }

    [Fact]
    public void A_file_declaring_nothing_has_an_empty_outline()
    {
        const string Statements = "Console.WriteLine(1);\n";
        Assert.Equal(string.Empty, Declarations.Outline(new ParsedText(CSharpSyntaxTree.ParseText(Statements, cancellationToken: TestContext.Current.CancellationToken), TextSnapshot.From(Statements))));
    }
}
