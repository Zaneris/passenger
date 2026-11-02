using Microsoft.CodeAnalysis.CSharp;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.UnitTests.Languages;

/// <summary>Which lines of a C# file begin inside a string running over lines, whose leading whitespace is the string's:
/// a verbatim string's, a raw string's with its closing line, an interpolated one's of either kind, a UTF-8 one's; never
/// a line a string only begins on, a comment's, or code after a string has closed.</summary>
public sealed class StringLinesTests
{
    private static int[] Of(string code)
    {
        TextSnapshot text = TextSnapshot.From(code);
        return [.. StringLines.Of(CSharpSyntaxTree.ParseText(code).GetRoot(), text).Order()];
    }

    [Fact]
    public void A_verbatim_strings_lines_after_its_first_are_inside_it()
    {
        Assert.Equal([3, 4], Of("class C\n{\n    string s = @\"one\n    two\n    three\";\n    int x;\n}\n"));
    }

    [Fact]
    public void A_raw_strings_lines_its_closing_line_among_them_are_inside_it()
    {
        Assert.Equal([3, 4, 5], Of("class C\n{\n    string s = \"\"\"\n        one\n          two\n        \"\"\";\n    int x;\n}\n"));
    }

    [Fact]
    public void Interpolated_verbatim_and_raw_strings_and_a_UTF_8_ones_lines_are_inside_them()
    {
        Assert.Equal([3, 6, 7, 10], Of("class C\n{\n    string a = $@\"{1}\n    {2}\";\n    int b;\n    string c = $\"\"\"\n        {3}\n        \"\"\";\n    int d;\n    byte[] e = @\"x\n    y\"u8.ToArray();\n}\n"));
    }

    [Fact]
    public void A_plain_string_a_block_comment_and_code_after_a_string_are_no_strings_lines()
    {
        Assert.Empty(Of("class C\n{\n    /* one\n       two */\n    string s = \"one\";\n    string t = @\"x\";\n    int y;\n}\n"));
    }
}
