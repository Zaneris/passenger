using Passenger.Editor;
using Passenger.Text;

namespace Passenger.UnitTests.Editor;

/// <summary>A completion's snippet in the protocol's syntax as the text it inserts and the places of each stop.</summary>
public class SnippetTests
{
    [Fact]
    public void A_completions_snippet_inserts_its_text_and_stops() => Cases.All(
        ("A stop written bare after its placeholder starts with the placeholder text in one stop", A_stop_written_bare_after_its_placeholder_starts_with_the_placeholder_text_in_one_stop),
        ("Every place of a stop starts with its placeholder wherever it is written", Every_place_of_a_stop_starts_with_its_placeholder_wherever_it_is_written),
        ("Placeholders nested past what is read are their text and end nothing", Placeholders_nested_past_what_is_read_are_their_text_and_end_nothing));

    private void A_stop_written_bare_after_its_placeholder_starts_with_the_placeholder_text_in_one_stop()
    {
        Snippet snippet = Snippet.FromProtocol("${1:name} = $1");
        Assert.True("name = name" == snippet.Text, $"The mirror starts empty ('{snippet.Text}'), so Tab past the field leaves it so.");
        IReadOnlyList<TextRange> places = Assert.Single(snippet.Fields);
        Assert.Equal([new TextRange(0, 4), new TextRange(7, 4)], places);
    }

    private void Every_place_of_a_stop_starts_with_its_placeholder_wherever_it_is_written() => Cases.Each<(string protocol, string text)>(
    [
        ("$1 = ${1:name}", "name = name"),
        ("${1|a,b|}($1)", "a(a)"),
        ("${1:x} ${2:y} ${1}$2$0", "x y xy"),
        ("${1:} = $1", " = "),
        ("${1:outer ${2:inner}} $1 $2", "outer inner outer inner inner"),
        ("${1:a} ${1:b} $1", "a b a"),
    ], row =>
    {
        (string protocol, string text) = row;
        Assert.Equal(text, Snippet.FromProtocol(protocol).Text);
    });

    private void Placeholders_nested_past_what_is_read_are_their_text_and_end_nothing()
    {
        string snippet = string.Concat(Enumerable.Repeat("${1:", 100_000)) + "name" + new string('}', 100_000);
        SmallStack.Run(() => Assert.Contains("name", Snippet.FromProtocol(snippet).Text, StringComparison.Ordinal));
    }
}
