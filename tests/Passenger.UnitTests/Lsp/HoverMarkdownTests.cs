using System.Text.Json;
using Passenger.App.CSharp;
using Passenger.Core;

namespace Passenger.UnitTests.Lsp;

/// <summary>A hover's Markdown read once, for the window's card (its words) and an agent (its text): a code span's
/// characters as written, a backslash before punctuation and a character reference read outside one, its fenced code
/// apart from its prose, and no image or data URI; the card shows a link as its words and takes out emphasis and a code
/// span's backticks, which an agent reads as written. A hover of plain text is read as written, by the card and an agent.</summary>
public sealed class HoverMarkdownTests
{
    /// <summary>A hover's Markdown reads as its words, code and links say, its escapes as written.</summary>
    [Fact]
    public void A_hover_reads_as_its_markdown_says() => Cases.All(
        (nameof(A_code_span_closes_only_at_as_many_backticks_and_an_unclosed_run_is_written_as_it_is),
            A_code_span_closes_only_at_as_many_backticks_and_an_unclosed_run_is_written_as_it_is),
        (nameof(A_line_of_more_escapes_than_can_be_held_is_read_whole), A_line_of_more_escapes_than_can_be_held_is_read_whole),
        (nameof(A_hover_is_read_by_its_kind_plain_text_as_written_and_markdown_without_its_marks),
            A_hover_is_read_by_its_kind_plain_text_as_written_and_markdown_without_its_marks),
        (nameof(Fenced_code_comes_apart_from_the_prose_around_it_the_fences_left_out),
            Fenced_code_comes_apart_from_the_prose_around_it_the_fences_left_out),
        (nameof(A_comments_tags_written_escaped_read_as_tags_but_in_code_and_where_they_read_as_no_xml),
            A_comments_tags_written_escaped_read_as_tags_but_in_code_and_where_they_read_as_no_xml));

    private void A_code_span_closes_only_at_as_many_backticks_and_an_unclosed_run_is_written_as_it_is()
    {
        Assert.Equal("a b`c d", HoverMarkdown.Words("a `` b`c `` d"));
        Assert.Equal("a `` b`c `` d", HoverMarkdown.Text("a `` b`c `` d"));
        Assert.Equal("it`s *so*", HoverMarkdown.Text("it`s *so*"));
        Assert.Equal("it`s so", HoverMarkdown.Words("it`s *so*"));
        Assert.Equal("a ` b", HoverMarkdown.Words(@"a \` b"));
    }

    private void A_line_of_more_escapes_than_can_be_held_is_read_whole()
    {
        string line = string.Concat(Enumerable.Repeat(@"\_", 7_000));
        Assert.True(HoverMarkdown.Words(line) == new string('_', 7_000),
            "A line with more escapes than the Private Use Area holds lost or misread some");
    }

    private void A_hover_is_read_by_its_kind_plain_text_as_written_and_markdown_without_its_marks()
    {
        using JsonDocument plain = JsonDocument.Parse("""{"kind":"plaintext","value":"C:\\temp *x*\n&lt;T&gt; data:text/plain;base64,QUJD"}""");
        using JsonDocument markdown = JsonDocument.Parse("""{"kind":"markdown","value":"**Sum**(`a`, *b*)\n```csharp\nint *p;\n```"}""");
        using JsonDocument marked = JsonDocument.Parse("\"*x*\"");
        Assert.True(HoverMarkdown.IsPlain(plain.RootElement));
        Assert.False(HoverMarkdown.IsPlain(markdown.RootElement));
        Assert.False(HoverMarkdown.IsPlain(marked.RootElement));
        foreach (bool words in (bool[])[true, false])
        {
            (IReadOnlyList<string> lines, bool code, _) = Assert.Single(HoverMarkdown.ByKind(Value(plain), HoverMarkdown.IsPlain(plain.RootElement), words));
            Assert.False(code);
            Assert.True(lines.SequenceEqual([@"C:\temp *x*", "&lt;T&gt; "]),
                $"A plain text hover read as Markdown {(words ? "in the card" : "by an agent")}: {string.Join(" | ", lines)}");
        }
        string shown = string.Join(" | ", HoverMarkdown.ByKind(Value(markdown), HoverMarkdown.IsPlain(markdown.RootElement), words: true)
            .Select(b => $"{(b.Code ? $"code {b.Info}" : "prose")}: {string.Join(" ", b.Lines)}"));
        Assert.True(shown == "prose: Sum(a, b) | code csharp: int *p; | prose: ",
            $"A Markdown hover's card, a signature's too, showed its marks, or lost the language its fence names: {shown}");

        static string Value(JsonDocument hover) => JsonMembers.Text(hover.RootElement, "value")!;
    }

    /// <summary>What Roslyn's server formats a C# comment's section from (<see cref="Passenger.Roslyn.Extension.EscapedDocumentationTags"/>).</summary>
    private void A_comments_tags_written_escaped_read_as_tags_but_in_code_and_where_they_read_as_no_xml()
    {
        Assert.Equal("""<b>Length</b> <see href="https://example.test/?x=1&amp;y=2">Guide</see> <see cref="T:Example.Unit" /> List&lt;T&gt;""",
            Passenger.Roslyn.Extension.EscapedDocumentationTags.Of(
                """&lt;b&gt;Length&lt;/b&gt; &lt;see href="https://example.test/?x=1&amp;y=2"&gt;Guide&lt;/see&gt; &lt;see cref="T:Example.Unit"/&gt; List&lt;T&gt;"""));
        string[] asWritten =
        [
            "<c>&lt;b&gt;literal&lt;/b&gt;</c> and <code>&lt;br/&gt;</code>",
            "a &lt; b and &lt;b&gt; never closed",
            "&lt;span&gt;not a comment's&lt;/span&gt;",
            "no tags & no XML <b>",
        ];
        foreach (string xml in asWritten)
        {
            Assert.True(ReferenceEquals(xml, Passenger.Roslyn.Extension.EscapedDocumentationTags.Of(xml)),
                $"Words that write no comment's tag, or write one in code or as no XML, were read as tags: {xml}");
        }
        Assert.Equal("<para>One<br />two</para> <c>&lt;i&gt;kept&lt;/i&gt;</c>",
            Passenger.Roslyn.Extension.EscapedDocumentationTags.Of("&lt;para&gt;One&lt;br/&gt;two&lt;/para&gt; <c>&lt;i&gt;kept&lt;/i&gt;</c>"));
    }

    private void Fenced_code_comes_apart_from_the_prose_around_it_the_fences_left_out()
    {
        string[] blocks = [.. HoverMarkdown.Blocks("```csharp\r\nvoid M()\r\n```\r\nWords\n```\nx\n```").Select(b => $"{(b.Code ? "code" : "prose")}: {string.Join(" | ", b.Lines)}")];
        Assert.Equal(["prose: ", "code: void M()", "prose: Words", "code: x", "prose: "], blocks);
    }
}
