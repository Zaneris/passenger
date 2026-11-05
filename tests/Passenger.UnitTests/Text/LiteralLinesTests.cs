using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>The lines whose whitespace a language's reader keeps though a grammar's strings don't show them: a Dockerfile's
/// heredocs, Godot's quoted values, XML's text with words, HTML's preformatted elements; never code, tags or whitespace
/// between them, which is the file's own indentation.</summary>
public sealed class LiteralLinesTests
{
    private static int[] Lines(Func<TextSnapshot, IReadOnlySet<int>> find, string text) => [.. find(TextSnapshot.From(text)).Order()];

    [Fact]
    public void A_dockerfiles_heredocs_are_kept_to_their_delimiters_and_its_other_lines_are_not()
    {
        Assert.Equal([2, 3, 4, 6, 7],
            Lines(LiteralLines.Heredocs, "FROM alpine\nRUN <<EOF\n    echo hi\n  echo there\nEOF\nCOPY <<\"EOT\" /file\n    data\nEOT\nRUN echo a \\\n    && echo b\n"));
    }

    [Fact]
    public void A_stripped_heredocs_delimiter_may_be_indented_two_on_a_line_run_in_turn_and_one_left_open_runs_to_the_end()
    {
        Assert.Equal([1, 2], Lines(LiteralLines.Heredocs, "RUN <<-EOF\n\tone\n\tEOF\nRUN true\n"));
        Assert.Equal([1, 2, 3, 4], Lines(LiteralLines.Heredocs, "COPY <<A <<B /\n  a\nA\n  b\nB\nRUN true\n"));
        Assert.Equal([1, 2, 3], Lines(LiteralLines.Heredocs, "RUN <<EOF\n  one\n  two\n"));
        Assert.Empty(Lines(LiteralLines.Heredocs, "# RUN <<EOF\nRUN echo $((1<<2))\n    done\n"));
    }

    [Fact]
    public void A_godot_resources_quoted_value_over_lines_is_kept_an_escaped_quote_going_on()
    {
        Assert.Equal([1, 2, 3, 4], Lines(LiteralLines.GodotStrings, "script/source = \"extends Node\n\nfunc f():\n\tprint(\\\"a\\\")\n\"\nposition = Vector2(1, 2)\n"));
        Assert.Empty(Lines(LiteralLines.GodotStrings, "a = \"one\"\nb = [\n    1\n]\n"));
    }

    [Fact]
    public void XMLs_text_with_words_is_kept_and_whitespace_between_tags_is_not()
    {
        Assert.Equal([3, 5, 6], Lines(LiteralLines.XmlText,
            "<root>\n  <data>\n    <value>line one\n      line two</value>\n    <note>\n      words\n    </note>\n  </data>\n</root>\n"));
        Assert.Empty(Lines(LiteralLines.XmlText, "<root>\n  <!-- a comment\n    over lines -->\n  <a b=\"one > two\"\n     c=\"3\">\n  </a>\n</root>\n"));
    }

    [Fact]
    public void HTMLs_preformatted_elements_are_kept_a_scripts_and_a_paragraphs_lines_are_not()
    {
        Assert.Equal([3, 4, 5, 7, 8], Lines(LiteralLines.Preformatted,
            "<body>\n  <p>\n  <PRE class=\"a\">\n    keep\n      this\n  </PRE>\n  <textarea>\n    too\n  </textarea>\n  <script>if (a < b) { var c = '<pre>'; }\n  </script>\n  words\n</body>\n"));
    }
}
