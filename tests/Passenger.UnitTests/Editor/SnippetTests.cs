using Passenger.Editor;
using Passenger.Text;

namespace Passenger.UnitTests.Editor;

/// <summary>A completion's snippet in the protocol's syntax as the text it inserts and the places of each stop.</summary>
public class SnippetTests
{
    [Fact]
    public void A_stop_written_bare_after_its_placeholder_starts_with_the_placeholder_text_in_one_stop()
    {
        Snippet snippet = Snippet.FromProtocol("${1:name} = $1");
        Assert.True("name = name" == snippet.Text, $"The mirror starts empty ('{snippet.Text}'), so Tab past the field leaves it so.");
        IReadOnlyList<TextRange> places = Assert.Single(snippet.Fields);
        Assert.Equal([new TextRange(0, 4), new TextRange(7, 4)], places);
    }

    [Theory]
    [InlineData("$1 = ${1:name}", "name = name")]
    [InlineData("${1|a,b|}($1)", "a(a)")]
    [InlineData("${1:x} ${2:y} ${1}$2$0", "x y xy")]
    [InlineData("${1:} = $1", " = ")]
    [InlineData("${1:outer ${2:inner}} $1 $2", "outer inner outer inner inner")]
    [InlineData("${1:a} ${1:b} $1", "a b a")]
    public void Every_place_of_a_stop_starts_with_its_placeholder_wherever_it_is_written(string protocol, string text)
    {
        Assert.Equal(text, Snippet.FromProtocol(protocol).Text);
    }
}
