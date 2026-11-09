using Passenger.App.CSharp;

namespace Passenger.UnitTests.Lsp;

/// <summary>A hover's Markdown read once, for the window's card (its words) and an agent (its text): a code span's
/// characters as written, a backslash before punctuation and a character reference read outside one, its fenced code
/// apart from its prose, and no image or data URI; the card shows a link as its words and takes out emphasis and a code
/// span's backticks, which an agent reads as written.</summary>
public sealed class HoverMarkdownTests
{
    [Fact]
    public void A_doc_comments_path_keeps_its_backslash_in_a_code_span_and_where_an_escape_writes_it()
    {
        // Roslyn's hover of <c>C:\temp</c>, and of C:\temp in the words, which it escapes.
        const string Line = @"Writes under `C:\temp`, as C:\\temp\\logs names it.";
        Assert.True(HoverMarkdown.Words(Line) == @"Writes under C:\temp, as C:\temp\logs names it.",
            $"The card showed a doc comment's path without its backslashes: {HoverMarkdown.Words(Line)}");
        Assert.True(HoverMarkdown.Text(Line) == @"Writes under `C:\temp`, as C:\temp\logs names it.",
            $"An agent read a doc comment's path without its backslashes: {HoverMarkdown.Text(Line)}");
    }

    [Fact]
    public void Character_references_read_as_xml_reads_them_and_nbsp_as_a_space_outside_a_code_span_alone()
    {
        const string Line = "&lt;T&gt; &quot;a&quot; &#39;b&#39; &apos;c&apos; &#x41;&nbsp;x &bogus; `&lt;`";
        Assert.Equal("<T> \"a\" 'b' 'c' A x &bogus; &lt;", HoverMarkdown.Words(Line));
        Assert.Equal("<T> \"a\" 'b' 'c' A x &bogus; `&lt;`", HoverMarkdown.Text(Line));
    }

    [Fact]
    public void The_card_shows_a_link_as_its_words_without_emphasis_leaving_escaped_marks_and_code_as_written()
    {
        const string Line = @"See [MDN Reference](https://developer.mozilla.org/docs/Web/CSS/color) on **bold**, _it_, *@param*, \_init\_, `__init__` and `a*b*c`";
        Assert.Equal("See MDN Reference on bold, it, @param, _init_, __init__ and a*b*c", HoverMarkdown.Words(Line));
        Assert.Equal(@"See [MDN Reference](https://developer.mozilla.org/docs/Web/CSS/color) on **bold**, _it_, *@param*, _init_, `__init__` and `a*b*c`",
            HoverMarkdown.Text(Line));
    }

    [Fact]
    public void A_link_around_code_shows_as_its_code()
    {
        Assert.Equal("Calls greet first.", HoverMarkdown.Words("Calls [`greet`](file:///app.ts#L1%2C1) first."));
    }

    [Fact]
    public void An_image_and_a_data_uri_are_left_out_with_the_blanks_after_the_image()
    {
        const string Line = "![Baseline icon](data:image/svg+xml;base64,PHN2Zz4=) _Widely available_ since data:text/plain;base64,QUJD 2015";
        Assert.Equal("Widely available since  2015", HoverMarkdown.Words(Line));
        Assert.Equal("_Widely available_ since  2015", HoverMarkdown.Text(Line));
    }

    [Fact]
    public void A_code_span_closes_only_at_as_many_backticks_and_an_unclosed_run_is_written_as_it_is()
    {
        Assert.Equal("a b`c d", HoverMarkdown.Words("a `` b`c `` d"));
        Assert.Equal("a `` b`c `` d", HoverMarkdown.Text("a `` b`c `` d"));
        Assert.Equal("it`s *so*", HoverMarkdown.Text("it`s *so*"));
        Assert.Equal("it`s so", HoverMarkdown.Words("it`s *so*"));
        Assert.Equal("a ` b", HoverMarkdown.Words(@"a \` b"));
    }

    [Fact]
    public void A_backslash_ending_a_line_is_markdowns_line_break_and_one_before_a_letter_is_the_texts()
    {
        Assert.Equal("first", HoverMarkdown.Words("first\\"));
        Assert.Equal(@"a\b", HoverMarkdown.Text(@"a\b"));
    }

    [Fact]
    public void Characters_of_the_private_use_area_in_the_text_are_kept_as_written()
    {
        const string Line = "\uE000 \uE001 `x` \uF8FF";
        Assert.Equal("\uE000 \uE001 x \uF8FF", HoverMarkdown.Words(Line));
        Assert.Equal(Line, HoverMarkdown.Text(Line));
    }

    [Fact]
    public void A_line_of_more_escapes_than_can_be_held_is_read_whole()
    {
        string line = string.Concat(Enumerable.Repeat(@"\_", 7_000));
        Assert.True(HoverMarkdown.Words(line) == new string('_', 7_000),
            "A line with more escapes than the Private Use Area holds lost or misread some");
    }

    [Fact]
    public void Fenced_code_comes_apart_from_the_prose_around_it_the_fences_left_out()
    {
        string[] blocks = [.. HoverMarkdown.Blocks("```csharp\r\nvoid M()\r\n```\r\nWords\n```\nx\n```").Select(b => $"{(b.Code ? "code" : "prose")}: {string.Join(" | ", b.Lines)}")];
        Assert.Equal(["prose: ", "code: void M()", "prose: Words", "code: x", "prose: "], blocks);
    }
}
