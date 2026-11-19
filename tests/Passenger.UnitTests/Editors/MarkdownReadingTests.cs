using System.Text;
using Passenger.App.Editors;
using M = Passenger.App.Editors.Markdown;

namespace Passenger.UnitTests.Editors;

/// <summary>A Markdown file read as GitHub reads it: emphasis by GFM's rules, a code span inside strong among them, and the
/// HTML a README holds, Passenger's own README's excerpts among it, read into blocks and words (written here as the HTML
/// GFM's spec writes, to set beside its examples), every tag not read standing as written.</summary>
public sealed class MarkdownReadingTests
{
    [Fact]
    public void Strong_code_at_an_items_start_is_strong_code_where_the_items_words_hold_another_code_span()
    {
        // Passenger's README, its MCP flags: the first item's later `--mcp-attach PID` once took the strong's close for code.
        const string Readme = """
            - **`--mcp-attach`** joins the window you have open over the folder the agent works in (`--mcp-attach PID` chooses one
              of Passenger's windows), so its breakpoints and stops show in your editor.
            - **`--mcp FOLDER`** starts a Passenger of the agent's own with no window
            """;
        M.Listing list = Assert.IsType<M.Listing>(Assert.Single(M.Parse(Readme)));
        string[] items = [.. list.Items.Select(item => Html(Assert.IsType<M.Paragraph>(Assert.Single(item)).Text))];
        Assert.True(items[0].StartsWith("<strong><code>--mcp-attach</code></strong> joins the window", StringComparison.Ordinal)
            && items[0].Contains("(<code>--mcp-attach PID</code> chooses one", StringComparison.Ordinal),
            $"The first flag shows its asterisks: {items[0]}");
        Assert.True(items[1].StartsWith("<strong><code>--mcp FOLDER</code></strong> starts", StringComparison.Ordinal), $"The second flag shows its asterisks: {items[1]}");
    }

    /// <summary>GFM 0.29's examples 351, 487 to 489, 361, 362, 364, 383, 387 to 390, 398, 404, 378, 405, 409 and 403 (its
    /// soft break a space, as a paragraph's lines are joined here), and a close taking the nearest opening.</summary>
    [Theory]
    [InlineData("*foo`*`", "*foo<code>*</code>")]
    [InlineData("*a `*`*", "<em>a <code>*</code></em>")]
    [InlineData("_a `_`_", "<em>a <code>_</code></em>")]
    [InlineData("**a<http://foo.bar/?q=**>", "**a<a href=\"http://foo.bar/?q=**\">http://foo.bar/?q=**</a>")]
    [InlineData("a * foo bar*", "a * foo bar*")]
    [InlineData("a*\"foo\"*", "a*&quot;foo&quot;*")]
    [InlineData("foo*bar*", "foo<em>bar</em>")]
    [InlineData("_foo_bar", "_foo_bar")]
    [InlineData("**foo bar**", "<strong>foo bar</strong>")]
    [InlineData("** foo bar**", "** foo bar**")]
    [InlineData("a**\"foo\"**", "a**&quot;foo&quot;**")]
    [InlineData("foo**bar**", "foo<strong>bar</strong>")]
    [InlineData("__foo, __bar__, baz__", "<strong>foo, <strong>bar</strong>, baz</strong>")]
    [InlineData("**foo \"*bar*\" foo**", "<strong>foo &quot;<em>bar</em>&quot; foo</strong>")]
    [InlineData("*(*foo*)*", "<em>(<em>foo</em>)</em>")]
    [InlineData("**foo**bar", "<strong>foo</strong>bar")]
    [InlineData("__foo__bar", "__foo__bar")]
    [InlineData("**Gomphocarpus (*Gomphocarpus physocarpus*, syn. *Asclepias physocarpa*)**",
        "<strong>Gomphocarpus (<em>Gomphocarpus physocarpus</em>, syn. <em>Asclepias physocarpa</em>)</strong>")]
    [InlineData("*a *b* c", "*a <em>b</em> c")]
    public void Emphasis_reads_as_GFMs_spec_examples_read(string markdown, string html) => Assert.Equal(html, Html(markdown));

    [Fact]
    public void Of_many_marks_that_open_the_one_close_closes_the_nearest_and_the_rest_stay_words()
    {
        string read = Html(string.Concat(Enumerable.Repeat("*a ", 2_000)) + "b*");
        Assert.True(read.EndsWith("*a <em>a b</em>", StringComparison.Ordinal) && read.StartsWith("*a *a ", StringComparison.Ordinal) && read.Count(c => c == '<') == 2,
            $"The close took another opening than the nearest: …{read[^40..]}");
    }

    [Fact]
    public void The_READMEs_centred_row_of_links_is_one_centred_paragraph_of_its_links()
    {
        const string Readme = """
            <p align="center">
              <a href="https://passenger-ide.net">Website</a> •
              <a href="https://passenger-ide.net/#download">Download</a> •
              <a href="docs/release-notes/0.4.0.md">What's new in 0.4.0</a> •
              <a href="#what-it-does">What it does</a> •
              <a href="CONTRIBUTING.md">Contributing</a>
            </p>

            Passenger is an IDE.
            """;
        IReadOnlyList<M.Block> blocks = M.Parse(Readme);
        M.Phrase row = Assert.IsType<M.Phrase>(blocks[0]);
        Assert.Equal(M.Align.Center, row.Aligned);
        Assert.Equal(
            "<a href=\"https://passenger-ide.net\">Website</a> • <a href=\"https://passenger-ide.net/#download\">Download</a> • "
            + "<a href=\"docs/release-notes/0.4.0.md\">What's new in 0.4.0</a> • <a href=\"#what-it-does\">What it does</a> • "
            + "<a href=\"CONTRIBUTING.md\">Contributing</a>",
            Html(row.Parts));
        Assert.Equal("Passenger is an IDE.", Assert.IsType<M.Paragraph>(blocks[1]).Text);
    }

    [Fact]
    public void The_READMEs_picture_is_its_img_with_the_light_looks_source()
    {
        const string Readme = """
            <picture>
              <source media="(prefers-color-scheme: light)" srcset="site/images/debugger-light.webp">
              <img src="site/images/debugger-dark.webp" alt="Passenger stopped at a breakpoint in a C# method">
            </picture>

            ## Debugging
            """;
        IReadOnlyList<M.Block> blocks = M.Parse(Readme);
        M.Image image = Assert.IsType<M.Image>(Assert.Single(Assert.IsType<M.Phrase>(blocks[0]).Parts));
        Assert.Equal(new M.Image("Passenger stopped at a breakpoint in a C# method", "site/images/debugger-dark.webp", Light: "site/images/debugger-light.webp"), image);
        Assert.Equal(new M.Heading(2, "Debugging"), blocks[1]);
    }

    [Fact]
    public void A_pictures_sources_are_chosen_for_each_look_by_their_media_and_the_first_that_fits()
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

    [Fact]
    public void An_img_alone_and_one_among_words_are_images_with_their_width_and_a_br_breaks_the_line()
    {
        IReadOnlyList<M.Block> blocks = M.Parse("<img src=\"logo.png\" alt=\"Logo\" width=\"50%\">\n\nA <img src='i.png' alt=\"&quot;i&quot;\" width=24> icon<br>and on<br/>\n<b>bold</b>, <i>slanted</i>, <kbd>Ctrl</kbd> <a href=\"https://x.dev\">**site**</a>");
        Assert.Equal(new M.Image("Logo", "logo.png"), Assert.Single(Assert.IsType<M.Phrase>(blocks[0]).Parts));
        Assert.Equal("A <img src=\"i.png\" alt=\"&quot;i&quot;\" width=\"24\" /> icon<br />and on<br /> <strong>bold</strong>, <em>slanted</em>, <code>Ctrl</code> <a href=\"https://x.dev\"><strong>site</strong></a>",
            Html(Assert.IsType<M.Paragraph>(blocks[1]).Text));
    }

    [Fact]
    public void A_tag_not_read_stands_as_written_and_what_is_inside_it_is_read()
    {
        IReadOnlyList<M.Block> blocks = M.Parse("<table align=\"center\"><tr><td><b>cell</b> &amp; more</td></tr></table>\n\nWords with <span title=\"x\">a span</span> and List<T>.");
        Assert.Equal("&lt;table align=&quot;center&quot;&gt;&lt;tr&gt;&lt;td&gt;<strong>cell</strong> &amp; more&lt;/td&gt;&lt;/tr&gt;&lt;/table&gt;",
            Html(Assert.IsType<M.Phrase>(Assert.Single(blocks, b => b is M.Phrase)).Parts));
        Assert.Equal("Words with &lt;span title=&quot;x&quot;&gt;a span&lt;/span&gt; and List&lt;T&gt;.", Html(Assert.IsType<M.Paragraph>(blocks[1]).Text));
    }

    [Fact]
    public void A_block_tag_ends_a_paragraph_and_a_lone_tag_of_another_kind_runs_on_in_it()
    {
        IReadOnlyList<M.Block> blocks = M.Parse("Words\n<div>apart</div>\n\nWords\n<img src=\"x.png\" alt=\"x\">\nmore");
        Assert.Equal(["Paragraph", "Division", "Paragraph"], blocks.Select(b => b.GetType().Name));
        Assert.Equal("Words <img src=\"x.png\" alt=\"x\" /> more", Html(((M.Paragraph)blocks[2]).Text));
    }

    [Fact]
    public void Details_left_open_take_the_Markdown_after_them_to_the_line_that_closes_them_and_a_div_leaves_its_headings_the_pages()
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

    [Fact]
    public void A_div_never_closed_takes_nothing_after_its_block_and_a_stray_end_tag_of_one_read_draws_nothing()
    {
        IReadOnlyList<M.Block> blocks = M.Parse("<div align=\"right\">\n<p>Right</p>\n\n# Heading\n\n</p>\n\nEnd");
        Assert.Equal(["Division", "Heading", "Paragraph"], blocks.Select(b => b.GetType().Name));
        M.Phrase right = Assert.IsType<M.Phrase>(Assert.Single(((M.Division)blocks[0]).Blocks));
        Assert.Equal("Right", Html(right.Parts));
    }

    [Fact]
    public void A_heading_hr_and_pre_in_HTML_read_as_a_heading_a_rule_and_code_and_a_script_as_written()
    {
        IReadOnlyList<M.Block> blocks = M.Parse("<h1 align=\"center\">Passenger <img src=\"logo.png\" alt=\"logo\"></h1>\n<hr>\n\n<pre>\nint x = 1 &lt; 2;\n</pre>\n\n<script>alert(1)</script>");
        M.Division read = Assert.IsType<M.Division>(blocks[0]);
        M.Phrase heading = Assert.IsType<M.Phrase>(read.Blocks[0]);
        Assert.True(heading.Level == 1 && heading.Aligned == M.Align.Center, $"The h1 reads as level {heading.Level}, standing {heading.Aligned}");
        Assert.IsType<M.Rule>(read.Blocks[1]);
        Assert.Equal(new M.Code("int x = 1 < 2;"), blocks[1]);
        Assert.Equal("<script>alert(1)</script>", Assert.IsType<M.Words>(Assert.Single(Assert.IsType<M.Phrase>(blocks[2]).Parts)).Text);
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
