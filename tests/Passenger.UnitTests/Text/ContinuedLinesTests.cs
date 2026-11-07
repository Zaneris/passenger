using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>The lines of a Python file its tokenizer reads no indentation of: inside brackets opened before them, read outside
/// strings and comments, and after a line ending in a backslash; a statement never written inside brackets closes any a
/// string read otherwise left open. Those inside a string are no block's either.</summary>
public sealed class ContinuedLinesTests
{
    private static int[] Continued(string text) => [.. ContinuedLines.Python(TextSnapshot.From(text)).Order()];

    [Fact]
    public void A_line_inside_brackets_or_after_a_backslash_goes_on_from_the_line_before()
    {
        Assert.Equal([1, 2, 3], Continued("x = [y\n     for y in z\n     if y\n]\nif a:\n    pass\n"));
        Assert.Equal([1, 2], Continued("call(a, {\n  'k': (1,\n         2)})\nb = 1\n"));
        Assert.Equal([1], Continued("x = 1 + \\\n    2\ny = 3\n"));
        Assert.Equal([1], Continued("x = (1 +\r\n     2)\r\ny = 3\r\n"));
    }

    [Fact]
    public void Brackets_in_strings_and_comments_open_nothing()
    {
        Assert.Empty(Continued("x = '(' + \"[\" # {\nif a:\n    b = r'\\'('\n    c = '''(\n'''\n    d = f\"{e['(']}\"\n"));
        Assert.Empty(Continued("x = 'a\\\n(b'\nif c:\n    pass\n"));
        Assert.Empty(Continued("s = \"\"\"a \\\n(\"\"\"\nif c:\n    pass\n"));
        Assert.Empty(Continued("# a comment ending in a backslash \\\nif c:\n    pass\n"));
    }

    /// <summary>The lines whose indentation is no block's are those continued and those beginning inside a string, a docstring's
    /// text and its closing quotes among them, but not the line a string begins on.</summary>
    [Fact]
    public void A_lines_indentation_is_no_blocks_where_it_goes_on_or_begins_inside_a_string()
    {
        const string Text = "def f(a):\n    \"\"\"Says.\n\n     More.\n    \"\"\"\n    g(a,\n      b)\n    return 'x\\\n  y'\n";
        Assert.Equal([6], Continued(Text));
        Assert.Equal([2, 3, 4, 6, 8], [.. ContinuedLines.PythonBlockless(Text).Order()]);
    }

    /// <summary>A bracket a string read otherwise leaves open, as a quote in a newer f-string's braces can, closes at the next
    /// statement brackets never hold.</summary>
    [Fact]
    public void A_statement_brackets_never_hold_reads_as_outside_them()
    {
        Assert.DoesNotContain(3, Continued("x = f\"{y + \"[\"}\"\nif a:\n    b\ndef f():\n    if c:\n        pass\n"));
        Assert.DoesNotContain(4, Continued("x = f\"{y + \"[\"}\"\nif a:\n    b\ndef f():\n    if c:\n        pass\n"));
    }
}
