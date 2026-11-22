using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>The lines whose whitespace a language's reader keeps though a grammar's strings don't show them: a Dockerfile's
/// heredocs, Godot's quoted values, XML's text with words, HTML's preformatted elements; never code, tags or whitespace
/// between them, which is the file's own indentation.</summary>
public sealed class LiteralLinesTests
{
    private static int[] Lines(Func<TextSnapshot, IReadOnlySet<int>> find, string text) => [.. find(TextSnapshot.From(text)).Order()];

    [Fact]
    public void Lines_whose_whitespace_a_languages_reader_keeps_are_kept_and_no_others() => Cases.All(
        ("a dockerfiles heredocs are kept to their delimiters and its other lines are not", A_dockerfiles_heredocs_are_kept_to_their_delimiters_and_its_other_lines_are_not),
        ("a godot resources quoted value over lines is kept an escaped quote going on", A_godot_resources_quoted_value_over_lines_is_kept_an_escaped_quote_going_on),
        ("XMLs text with words is kept and whitespace between tags is not", XMLs_text_with_words_is_kept_and_whitespace_between_tags_is_not),
        ("HTMLs preformatted elements are kept a scripts and a paragraphs lines are not", HTMLs_preformatted_elements_are_kept_a_scripts_and_a_paragraphs_lines_are_not));

    private void A_dockerfiles_heredocs_are_kept_to_their_delimiters_and_its_other_lines_are_not()
    {
        Assert.Equal([2, 3, 4, 6, 7],
            Lines(LiteralLines.Heredocs, "FROM alpine\nRUN <<EOF\n    echo hi\n  echo there\nEOF\nCOPY <<\"EOT\" /file\n    data\nEOT\nRUN echo a \\\n    && echo b\n"));
    }

    private void A_godot_resources_quoted_value_over_lines_is_kept_an_escaped_quote_going_on()
    {
        Assert.Equal([1, 2, 3, 4], Lines(LiteralLines.GodotStrings, "script/source = \"extends Node\n\nfunc f():\n\tprint(\\\"a\\\")\n\"\nposition = Vector2(1, 2)\n"));
        Assert.Empty(Lines(LiteralLines.GodotStrings, "a = \"one\"\nb = [\n    1\n]\n"));
    }

    private void XMLs_text_with_words_is_kept_and_whitespace_between_tags_is_not()
    {
        Assert.Equal([3, 5, 6], Lines(LiteralLines.XmlText,
            "<root>\n  <data>\n    <value>line one\n      line two</value>\n    <note>\n      words\n    </note>\n  </data>\n</root>\n"));
        Assert.Empty(Lines(LiteralLines.XmlText, "<root>\n  <!-- a comment\n    over lines -->\n  <a b=\"one > two\"\n     c=\"3\">\n  </a>\n</root>\n"));
    }

    private void HTMLs_preformatted_elements_are_kept_a_scripts_and_a_paragraphs_lines_are_not()
    {
        Assert.Equal([3, 4, 5, 7, 8], Lines(LiteralLines.Preformatted,
            "<body>\n  <p>\n  <PRE class=\"a\">\n    keep\n      this\n  </PRE>\n  <textarea>\n    too\n  </textarea>\n  <script>if (a < b) { var c = '<pre>'; }\n  </script>\n  words\n</body>\n"));
    }
}
