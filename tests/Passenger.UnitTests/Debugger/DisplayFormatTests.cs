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
    /// same words, an empty expression nothing, each value shown by its specifiers, which the reader is never asked.</summary>
    [Fact]
    public async Task A_message_logs_its_words_and_each_expression_read_with_braces_as_escaped()
    {
        List<string> asked = [];
        string logged = await BreakpointHits.LoggedAsync("second \\{{i}\\} of {name} {name,nq} {i,h}{}", expression =>
        {
            asked.Add(expression);
            return Task.FromResult(expression == "i" ? DisplayValue.OfWhole(14, sizeof(int)) : DisplayValue.OfText("Ada", "\"Ada\""));
        });
        Assert.Equal("second {14} of \"Ada\" Ada 0x0000000e", logged);
        Assert.Equal(["i", "name", "name", "i"], asked);
    }

    /// <summary>A string shows as its debugger quotes it unless nq or raw asks for it bare; nothing else is changed by them.</summary>
    [Fact]
    public void A_string_is_quoted_as_its_debugger_quotes_it_unless_nq_or_raw_shows_it_bare()
    {
        DisplayValue name = DisplayValue.OfText("say \"hi\"", "\"say \\\"hi\\\"\"");
        Assert.Equal("\"say \\\"hi\\\"\"", DisplayFormat.Shown(name, []));
        Assert.Equal("say \"hi\"", DisplayFormat.Shown(name, ["nq"]));
        Assert.Equal("say \"hi\"", DisplayFormat.Shown(name, ["raw"]));
        Assert.Equal("\"say \\\"hi\\\"\"", DisplayFormat.Shown(name, ["h"]));
        Assert.Equal("14", DisplayFormat.Shown(DisplayValue.OfWhole(14, sizeof(int)), ["nq"]));
        Assert.Equal("<error: no x>", DisplayFormat.Shown(DisplayValue.Said("<error: no x>"), ["nq", "h"]));
    }

    /// <summary>h shows a whole number in hexadecimal, two digits to each byte its program holds it in, a negative one as its
    /// bits; d in decimal; the last of the two written wins, and neither changes what holds no whole number.</summary>
    [Fact]
    public void A_whole_number_shows_in_hexadecimal_under_h_as_wide_as_its_type_and_in_decimal_under_d()
    {
        Assert.Equal("0x0000000e", DisplayFormat.Shown(DisplayValue.OfWhole(14, sizeof(int)), ["h"]));
        Assert.Equal("0x000000000000000e", DisplayFormat.Shown(DisplayValue.OfWhole(14, sizeof(long)), ["h"]));
        Assert.Equal("0xff", DisplayFormat.Shown(DisplayValue.OfWhole(255, sizeof(byte)), ["h"]));
        Assert.Equal("0xffffffff", DisplayFormat.Shown(DisplayValue.OfWhole(-1, sizeof(int)), ["h"]));
        Assert.Equal("0xff80", DisplayFormat.Shown(DisplayValue.OfWhole(-128, sizeof(short)), ["h"]));
        Assert.Equal("0xffffffffffffffff", DisplayFormat.Shown(DisplayValue.OfWhole(ulong.MaxValue, sizeof(ulong)), ["h"]));
        Assert.Equal("-1", DisplayFormat.Shown(DisplayValue.OfWhole(-1, sizeof(int)), []));
        Assert.Equal("61541", DisplayFormat.Shown(DisplayValue.OfWhole(61541, sizeof(int)), ["h", "d"]));
        Assert.Equal("0x0000f065", DisplayFormat.Shown(DisplayValue.OfWhole(61541, sizeof(int)), ["d", "nq", "h"]));
        Assert.Equal("2.5", DisplayFormat.Shown(DisplayValue.Said("2.5"), ["h"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayValue.OfWhole(1, 0));
    }
}
