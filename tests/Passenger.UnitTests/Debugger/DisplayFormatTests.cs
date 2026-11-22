using Passenger.Dap;

namespace Passenger.UnitTests.Debugger;

/// <summary>A tracepoint's message, read one way by every debugger (<see cref="DisplayFormat"/>): words, each {expression}
/// read where the program stands, \{ and \} braces, and a brace written twice said rather than misread.</summary>
public sealed class DisplayFormatTests
{
    /// <summary>{{ and }} are one brace to some formats and two to this one, so a message writing them is said where it
    /// would be misread; every debugger logs a message through one reading, each value shown by its specifiers.</summary>
    [Fact]
    public Task A_tracepoints_message_is_read_into_words_and_expressions_and_logged_one_way_by_every_debugger() => Cases.AllAsync(
        ("a message parts its words from its expressions, \\{ and \\} braces, an expression running to its own closing brace", () =>
        {
            Assert.Equal([("count is ", false), ("counter", true), (" of {all}", false)], DisplayFormat.Pieces("count is {counter} of \\{all\\}"));
            Assert.Equal([("open { never closed", false)], DisplayFormat.Pieces("open { never closed"));
            Assert.Equal([("named ", false), ("names[\"}\"] + '{'", true)], DisplayFormat.Pieces("named {names[\"}\"] + '{'}"));
            Assert.Equal([("gdscript ", false), (" {\"a\": 1}.size() ", true)], DisplayFormat.Pieces("gdscript { {\"a\": 1}.size() }"));
            return Task.CompletedTask;
        }),
        ("an expression's specifiers come after its last comma outside brackets and quotes", () =>
        {
            Assert.Equal("name nq", Specified("name, nq"));
            Assert.Equal("Pick(a, b)", Specified("Pick(a, b)"));
            Assert.Equal("text + \",\" raw", Specified("text + \",\", raw"));
            return Task.CompletedTask;
        }),
        ("a brace written twice outside an expression is said rather than misread", () =>
        {
            string? doubled = DisplayFormat.Misread("count is {count} of {{all}}");
            Assert.True(doubled?.Contains("written \\{ or \\}, not \"{{\"", StringComparison.Ordinal) == true, $"{{{{ was not said as a brace written another way: {doubled}");
            Assert.NotNull(DisplayFormat.Misread("done }}"));
            Assert.Null(DisplayFormat.Misread("second \\{{i}\\}"));
            Assert.Null(DisplayFormat.Misread("key {names[\"{{\"]} and { {\"a\": 1}.size() }"));
            return Task.CompletedTask;
        }),
        ("a message logs its words and each expression read, each shown by its specifiers", async () =>
        {
            List<string> asked = [];
            string logged = await BreakpointHits.LoggedAsync("second \\{{i}\\} of {name} {name,nq} {i,h}{}", expression =>
            {
                asked.Add(expression);
                return Task.FromResult(expression == "i" ? DisplayValue.OfWhole(14, sizeof(int)) : DisplayValue.OfText("Ada", "\"Ada\""));
            });
            Assert.Equal("second {14} of \"Ada\" Ada 0x0000000e", logged);
            Assert.Equal(["i", "name", "name", "i"], asked);
        }),
        ("h shows a whole number in hexadecimal as wide as its type, d in decimal, the last written winning", () =>
        {
            Assert.Equal("0xffffffff", DisplayFormat.Shown(DisplayValue.OfWhole(-1, sizeof(int)), ["h"]));
            Assert.Equal("0xffffffffffffffff", DisplayFormat.Shown(DisplayValue.OfWhole(ulong.MaxValue, sizeof(ulong)), ["h"]));
            Assert.Equal("61541", DisplayFormat.Shown(DisplayValue.OfWhole(61541, sizeof(int)), ["h", "d"]));
            Assert.Equal("2.5", DisplayFormat.Shown(DisplayValue.Said("2.5"), ["h"]));
            return Task.CompletedTask;
        }));

    /// <summary>An expression and its specifiers as read from <paramref name="written"/>, a space between each.</summary>
    private static string Specified(string written)
    {
        (string expression, IReadOnlyList<string> specifiers) = DisplayFormat.Specifiers(written);
        return string.Join(' ', [expression, .. specifiers]);
    }
}
