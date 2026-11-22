using Microsoft.CodeAnalysis.CSharp;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.UnitTests.Languages;

/// <summary>Which lines of a C# file begin inside a string running over lines, whose leading whitespace is the string's:
/// a verbatim string's, a raw string's with its closing line, an interpolated one's of either kind, a UTF-8 one's, in code
/// the compiler reads and in the branches it leaves out; never a line a string only begins on, a comment's, or code after a
/// string has closed.</summary>
public sealed class StringLinesTests
{
    private static int[] Of(string code)
    {
        TextSnapshot text = TextSnapshot.From(code);
        return [.. StringLines.Of(CSharpSyntaxTree.ParseText(code).GetRoot(), text).Order()];
    }

    [Fact]
    public void Every_kind_of_strings_lines_after_its_first_are_inside_it_in_code_read_or_left_out_and_no_other_line_is() => Cases.All(
        ("a verbatim strings lines after its first are inside it", A_verbatim_strings_lines_after_its_first_are_inside_it),
        ("a raw strings lines its closing line among them are inside it", A_raw_strings_lines_its_closing_line_among_them_are_inside_it),
        ("interpolated verbatim and raw strings and a UTF 8 ones lines are inside them", Interpolated_verbatim_and_raw_strings_and_a_UTF_8_ones_lines_are_inside_them),
        ("a strings lines in a branch left out are inside it and the code around them is not", A_strings_lines_in_a_branch_left_out_are_inside_it_and_the_code_around_them_is_not),
        ("every kind of string running over lines is read in a branch left out", () => Cases.Each<string>(
        [
            "$\"\"\"\n        {one}\n        \"\"\";",
            "@\"a\"\"\n        \n        \"u8.ToArray();",
        ], Every_kind_of_string_running_over_lines_is_read_in_a_branch_left_out)),
        ("a plain string a block comment and code after a string are no strings lines", A_plain_string_a_block_comment_and_code_after_a_string_are_no_strings_lines));

    private void A_verbatim_strings_lines_after_its_first_are_inside_it()
    {
        Assert.Equal([3, 4], Of("class C\n{\n    string s = @\"one\n    two\n    three\";\n    int x;\n}\n"));
    }

    private void A_raw_strings_lines_its_closing_line_among_them_are_inside_it()
    {
        Assert.Equal([3, 4, 5], Of("class C\n{\n    string s = \"\"\"\n        one\n          two\n        \"\"\";\n    int x;\n}\n"));
    }

    private void Interpolated_verbatim_and_raw_strings_and_a_UTF_8_ones_lines_are_inside_them()
    {
        Assert.Equal([3, 6, 7, 10], Of("class C\n{\n    string a = $@\"{1}\n    {2}\";\n    int b;\n    string c = $\"\"\"\n        {3}\n        \"\"\";\n    int d;\n    byte[] e = @\"x\n    y\"u8.ToArray();\n}\n"));
    }

    /// <summary>A branch a condition leaves out is text to the parse, and its strings are the program's once it is defined.</summary>
    private void A_strings_lines_in_a_branch_left_out_are_inside_it_and_the_code_around_them_is_not()
    {
        Assert.Equal([4, 5], Of("class C\n{\n#if RELEASE_ONLY\n    string s = @\"one\n        two\n        three\";\n    int x;\n#endif\n}\n"));
    }

    private void Every_kind_of_string_running_over_lines_is_read_in_a_branch_left_out(string literal)
    {
        string code = "#if LEFT_OUT\nstring s = " + literal + "\nint x;\n#endif\n";
        int last = code.Split('\n').ToList().FindIndex(l => l.StartsWith("int x", StringComparison.Ordinal)) - 1;
        Assert.Equal(Enumerable.Range(2, last - 1), Of(code));
    }

    private void A_plain_string_a_block_comment_and_code_after_a_string_are_no_strings_lines()
    {
        Assert.Empty(Of("class C\n{\n    /* one\n       two */\n    string s = \"one\";\n    string t = @\"x\";\n    int y;\n}\n"));
    }
}
