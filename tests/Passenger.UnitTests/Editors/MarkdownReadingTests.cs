using System.Text;
using Passenger.App.Editors;
using M = Passenger.App.Editors.Markdown;

namespace Passenger.UnitTests.Editors;

/// <summary>A Markdown file read as GitHub reads it: emphasis by GFM's rules, a code span inside strong among them, and the
/// HTML a README holds, Passenger's own README's excerpts among it, read into blocks and words (written here as the HTML
/// GFM's spec writes, to set beside its examples), every tag not read standing as written.</summary>
public sealed class MarkdownReadingTests
{
    /// <summary>Emphasis and code read as GitHub Flavored Markdown's spec reads them.</summary>
    [Fact]
    public void Emphasis_reads_as_gfms_spec_reads_it() => Cases.All(
        (nameof(Emphasis_reads_as_GFMs_spec_examples_read), Emphasis_reads_as_GFMs_spec_examples_read));

    /// <summary>HTML in Markdown reads as a README draws it: pictures, tags, blocks and details.</summary>
    [Fact]
    public void Html_in_markdown_reads_as_a_readme_draws_it() => Cases.All(
        (nameof(A_pictures_sources_are_chosen_for_each_look_by_their_media_and_the_first_that_fits),
            A_pictures_sources_are_chosen_for_each_look_by_their_media_and_the_first_that_fits),
        (nameof(A_tag_not_read_stands_as_written_and_what_is_inside_it_is_read), A_tag_not_read_stands_as_written_and_what_is_inside_it_is_read),
        (nameof(Details_left_open_take_the_Markdown_after_them_to_the_line_that_closes_them_and_a_div_leaves_its_headings_the_pages),
            Details_left_open_take_the_Markdown_after_them_to_the_line_that_closes_them_and_a_div_leaves_its_headings_the_pages),
        (nameof(A_div_never_closed_takes_nothing_after_its_block_and_a_stray_end_tag_of_one_read_draws_nothing),
            A_div_never_closed_takes_nothing_after_its_block_and_a_stray_end_tag_of_one_read_draws_nothing));

    /// <summary>GFM 0.29's examples 351, 487 to 489, 361, 362, 364, 383, 387 to 390, 398, 404, 378, 405, 409 and 403 (its
    /// soft break a space, as a paragraph's lines are joined here), and a close taking the nearest opening.</summary>
    private void Emphasis_reads_as_GFMs_spec_examples_read() => Cases.Each<(string Markdown, string Html)>(
    [
        ("*foo`*`", "*foo<code>*</code>"),
        ("**a<http://foo.bar/?q=**>", "**a<a href=\"http://foo.bar/?q=**\">http://foo.bar/?q=**</a>"),
        ("foo*bar*", "foo<em>bar</em>"),
        ("** foo bar**", "** foo bar**"),
        ("__foo, __bar__, baz__", "<strong>foo, <strong>bar</strong>, baz</strong>"),
        ("*(*foo*)*", "<em>(<em>foo</em>)</em>"),
        ("**Gomphocarpus (*Gomphocarpus physocarpus*, syn. *Asclepias physocarpa*)**", "<strong>Gomphocarpus (<em>Gomphocarpus physocarpus</em>, syn. <em>Asclepias physocarpa</em>)</strong>"),
        ("*a *b* c", "*a <em>b</em> c"),
    ], row => Emphasis_reads_as_GFMs_spec_examples_read(row.Markdown, row.Html));

    private void Emphasis_reads_as_GFMs_spec_examples_read(string markdown, string html) => Assert.Equal(html, Html(markdown));

    private void A_pictures_sources_are_chosen_for_each_look_by_their_media_and_the_first_that_fits()
    {
        const string Html = """
            <picture>
              <source media="(min-width: 600px)" srcset="wide.png">
              <source media="(prefers-color-scheme:dark)" srcset="dark.png 2x, dark-small.png 1x">
              <source media="(prefers-color-scheme: dark)" srcset="later.png">
              <source srcset="any.png">
              <img src="fallback.png" alt="A logo" width="120" height="40px">
            </picture>
            """;
        M.Image image = Assert.IsType<M.Image>(Assert.Single(Assert.IsType<M.Phrase>(Assert.Single(M.Parse(Html))).Parts));
        Assert.Equal(new M.Image("A logo", "fallback.png", 120, 40, Light: "any.png", Dark: "dark.png"), image);
    }

    private void A_tag_not_read_stands_as_written_and_what_is_inside_it_is_read()
    {
        IReadOnlyList<M.Block> blocks = M.Parse("<table align=\"center\"><tr><td><b>cell</b> &amp; more</td></tr></table>\n\nWords with <span title=\"x\">a span</span> and List<T>.");
        Assert.Equal("&lt;table align=&quot;center&quot;&gt;&lt;tr&gt;&lt;td&gt;<strong>cell</strong> &amp; more&lt;/td&gt;&lt;/tr&gt;&lt;/table&gt;",
            Html(Assert.IsType<M.Phrase>(Assert.Single(blocks, b => b is M.Phrase)).Parts));
        Assert.Equal("Words with &lt;span title=&quot;x&quot;&gt;a span&lt;/span&gt; and List&lt;T&gt;.", Html(Assert.IsType<M.Paragraph>(blocks[1]).Text));
    }

    private void Details_left_open_take_the_Markdown_after_them_to_the_line_that_closes_them_and_a_div_leaves_its_headings_the_pages()
    {
        const string Readme = """
            <div align="center">

            # Passenger

            **An IDE**

            <div>inner</div>

            </div>

            <details open>
            <summary>More <b>to read</b></summary>

            - one

            </details>

            After.
            """;
        IReadOnlyList<M.Placed> placed = M.ParsePlaced(Readme);
        Assert.Equal(["Heading", "Paragraph", "Division", "Details", "Paragraph"], placed.Select(p => p.Block.GetType().Name));
        Assert.Equal(new M.Heading(1, "Passenger"), placed[0].Block);
        M.Details details = (M.Details)placed[3].Block;
        Assert.True(placed[3].Line == 10 && placed[3].End == 16, $"The details are read from line {placed[3].Line} to {placed[3].End}, not 10 to past their close's 15");
        Assert.True(details.Open, "The details' open is not read");
        Assert.Equal("More <strong>to read</strong>", Html(details.Summary));
        Assert.IsType<M.Listing>(Assert.Single(details.Blocks));
        Assert.Equal("After.", ((M.Paragraph)placed[4].Block).Text);
    }

    private void A_div_never_closed_takes_nothing_after_its_block_and_a_stray_end_tag_of_one_read_draws_nothing()
    {
        IReadOnlyList<M.Block> blocks = M.Parse("<div align=\"right\">\n<p>Right</p>\n\n# Heading\n\n</p>\n\nEnd");
        Assert.Equal(["Division", "Heading", "Paragraph"], blocks.Select(b => b.GetType().Name));
        M.Phrase right = Assert.IsType<M.Phrase>(Assert.Single(((M.Division)blocks[0]).Blocks));
        Assert.Equal("Right", Html(right.Parts));
    }

    /// <summary>Words read as GFM's spec writes them in HTML.</summary>
    private static string Html(string markdown) => Html(M.Inlines(markdown));

    private static string Html(IReadOnlyList<M.Inline> parts)
    {
        StringBuilder html = new();
        foreach (M.Inline part in parts)
        {
            html.Append(part switch
            {
                M.Words words => Encoded(words.Text),
                M.CodeSpan code => $"<code>{Encoded(code.Text)}</code>",
                M.Strong strong => $"<strong>{Html(strong.Parts)}</strong>",
                M.Emphasis emphasis => $"<em>{Html(emphasis.Parts)}</em>",
                M.Link link => $"<a href=\"{Encoded(link.Target)}\">{Html(link.Parts)}</a>",
                M.Image image => $"<img src=\"{Encoded(image.Target)}\" alt=\"{Encoded(image.Alt)}\"{(image.Width is { } width ? $" width=\"{width}\"" : "")} />",
                M.Break => "<br />",
                _ => throw new InvalidOperationException($"No HTML for {part}"),
            });
        }
        return html.ToString();
    }

    private static string Encoded(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
}
