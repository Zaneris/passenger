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

    /// <summary>A branch a condition leaves out is text to the parse, and its strings are the program's once it is defined.</summary>
    [Fact]
    public void A_strings_lines_in_a_branch_left_out_are_inside_it_and_the_code_around_them_is_not()
    {
        Assert.Equal([4, 5], Of("class C\n{\n#if RELEASE_ONLY\n    string s = @\"one\n        two\n        three\";\n    int x;\n#endif\n}\n"));
    }

    [Theory]
    [InlineData("\"\"\"\n        one\n        \"\"\";")]
    [InlineData("$\"\"\"\n        {one}\n        \"\"\";")]
    [InlineData("$@\"{one}\n        \n        \";")]
    [InlineData("@\"a\"\"\n        \n        \"u8.ToArray();")]
    public void Every_kind_of_string_running_over_lines_is_read_in_a_branch_left_out(string literal)
    {
        string code = "#if LEFT_OUT\nstring s = " + literal + "\nint x;\n#endif\n";
        int last = code.Split('\n').ToList().FindIndex(l => l.StartsWith("int x", StringComparison.Ordinal)) - 1;
        Assert.Equal(Enumerable.Range(2, last - 1), Of(code));
    }

    /// <summary>The way a condition takes is parsed, the other left out, and an else's branch is read as its if's is.</summary>
    [Fact]
    public void The_else_of_a_condition_taken_and_every_way_of_conditions_nested_in_one_left_out_are_read()
    {
        Assert.Equal([2, 5], Of("#if true\nstring a = @\"one\n    two\";\n#else\nstring b = @\"three\n    four\";\n#endif\n"));
        Assert.Equal([3, 6, 9], Of("#if OUTER\n#if INNER\nstring a = @\"one\n    two\";\n#else\nstring b = @\"three\n    four\";\n#endif\nstring c = @\"five\n    six\";\n#endif\n"));
        Assert.Equal([3, 6], Of("#if OUTER\n#if !INNER\nstring a = @\"one\n    two\";\n#elif ANOTHER\nstring b = @\"three\n    four\";\n#endif\n#endif\n"));
    }

    /// <summary>A line like a directive inside a string left out is the string's, a string left open runs to its branch's end,
    /// and words that are no C# are lines as any other.</summary>
    [Fact]
    public void A_directive_like_line_in_a_string_left_out_is_the_strings_and_a_string_left_open_runs_to_the_branchs_end()
    {
        Assert.Equal([2, 3, 4, 5], Of("#if OUT\nstring a = @\"\n#if INNER\n    text\n#endif\n\";\nint x;\n#endif\n"));
        Assert.Equal([2, 3], Of("#if OUT\nstring a = @\"open\n    one\n    two\n#endif\nint x;\n"));
        Assert.Empty(Of("#if OUT\nThis isn't code: it has \"one quote\n    and is indented\n#endif\n"));
    }

    [Fact]
    public void A_plain_string_a_block_comment_and_code_after_a_string_are_no_strings_lines()
    {
        Assert.Empty(Of("class C\n{\n    /* one\n       two */\n    string s = \"one\";\n    string t = @\"x\";\n    int y;\n}\n"));
    }
}
