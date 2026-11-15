using Passenger.Dap;

namespace Passenger.UnitTests.Debugger;

/// <summary>A tracepoint's message, read one way by every debugger (<see cref="DisplayFormat"/>): words, each {expression}
/// read where the program stands, \{ and \} braces, and a brace written twice said rather than misread.</summary>
public sealed class DisplayFormatTests
{
    [Fact]
    public void A_tracepoints_message_parts_its_words_from_the_expressions_read_where_it_stops()
    {
        Assert.Equal([("count is ", false), ("counter", true), (" of {all}", false)], DisplayFormat.Pieces("count is {counter} of \\{all\\}"));
        Assert.Equal([("second {", false), ("i", true), ("}", false)], DisplayFormat.Pieces("second \\{{i}\\}"));
        Assert.Equal([("name,nq", true)], DisplayFormat.Pieces("{name,nq}"));
        Assert.Equal([("open { never closed", false)], DisplayFormat.Pieces("open { never closed"));
        Assert.Equal([("a \\ b", false)], DisplayFormat.Pieces("a \\ b"));
    }

    [Fact]
    public void An_expression_runs_to_its_own_closing_brace_past_braces_and_quotes_inside_it()
    {
        Assert.Equal([("size ", false), ("new[] { 1, 2 }.Length", true), (" of ", false), ("new { a = 1 }", true)],
            DisplayFormat.Pieces("size {new[] { 1, 2 }.Length} of {new { a = 1 }}"));
        Assert.Equal([("named ", false), ("names[\"}\"] + '{'", true)], DisplayFormat.Pieces("named {names[\"}\"] + '{'}"));
        Assert.Equal([("gdscript ", false), (" {\"a\": 1}.size() ", true)], DisplayFormat.Pieces("gdscript { {\"a\": 1}.size() }"));
    }

    [Fact]
    public void An_expressions_specifiers_come_after_its_last_comma_outside_brackets_and_quotes()
    {
        Assert.Equal("name nq", Read("name, nq"));
        Assert.Equal("Pick(a, b)", Read("Pick(a, b)"));
        Assert.Equal("x, 2", Read("x, 2"));
        Assert.Equal("text + \",\" raw", Read("text + \",\", raw"));
        Assert.Equal("Items.Count d", Read(" Items.Count , d "));

        static string Read(string written)
        {
            (string expression, IReadOnlyList<string> specifiers) = DisplayFormat.Specifiers(written);
            return string.Join(' ', [expression, .. specifiers]);
        }
    }

    /// <summary>{{ and }} are one brace to some formats and two to this one, so a message writing them is said, in words naming
    /// the brace and how to write it, where it would be misread; inside an expression or escaped they read as written.</summary>
    [Fact]
    public void A_brace_written_twice_outside_an_expression_is_said_rather_than_misread()
    {
        string? doubled = DisplayFormat.Misread("count is {count} of {{all}}");
        Assert.True(doubled?.Contains("written \\{ or \\}, not \"{{\"", StringComparison.Ordinal) == true, $"{{{{ was not said as a brace written another way: {doubled}");
        Assert.Contains("not \"}}\"", DisplayFormat.Misread("done }}"), StringComparison.Ordinal);
        Assert.NotNull(DisplayFormat.Misread("{{ never closed"));
        Assert.Null(DisplayFormat.Misread("size {new[] { 1, 2 }.Length} of {new { a = 1 }}"));
        Assert.Null(DisplayFormat.Misread("second \\{{i}\\}"));
        Assert.Null(DisplayFormat.Misread("key {names[\"{{\"]} and { {\"a\": 1}.size() }"));
        Assert.Null(DisplayFormat.Misread("open { alone"));
        Assert.Null(DisplayFormat.Misread(null));
    }

    /// <summary>Every debugger logs a message through one reading: the same message, its expressions read alike, says the
    /// same words, an empty expression nothing and specifiers left to the reader.</summary>
    [Fact]
    public async Task A_message_logs_its_words_and_each_expression_read_with_braces_as_escaped()
    {
        List<string> asked = [];
        string logged = await BreakpointHits.LoggedAsync("second \\{{i}\\} of {name,nq}{}", expression =>
        {
            asked.Add(expression);
            return Task.FromResult(expression == "i" ? "14" : "Ada");
        });
        Assert.Equal("second {14} of Ada", logged);
        Assert.Equal(["i", "name"], asked);
    }
}
