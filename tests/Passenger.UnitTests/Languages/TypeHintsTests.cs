using Microsoft.CodeAnalysis.CSharp;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.UnitTests.Languages;

/// <summary>Where a type name hint shows after a name, as the file's syntax decides it.</summary>
public sealed class TypeHintsTests
{
    /// <summary>A name holding marks is one word with them, so a name sharing only the letters between its marks with its
    /// type's name (पतंग, a kite, of a type पता, an address) still shows its type.</summary>
    [Fact]
    public void A_name_holding_marks_is_read_as_one_word_against_its_types()
    {
        const string Code = "class पता { }\nclass C\n{\n    void M()\n    {\n        var पतंग = Make();\n    }\n}\n";
        ParsedText parsed = new(CSharpSyntaxTree.ParseText(Code, cancellationToken: TestContext.Current.CancellationToken), TextSnapshot.From(Code));
        int name = Code.IndexOf("पतंग", StringComparison.Ordinal);
        InlayHint? hint = TypeHints.Place(parsed, name, "पता");
        Assert.True(hint is { Label: ":पता" }, "पतंग's type is left off as though the name said it, read as words cut at its mark");
    }
}
