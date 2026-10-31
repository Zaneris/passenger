using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>A place is counted in lines as Roslyn counts them: a lone '\r', U+0085, U+2028 and U+2029 end a line as '\n'
/// and "\r\n" do.</summary>
public sealed class RoslynLinesTests
{
    [Theory]
    [InlineData("a\nb", 2, 1, 0)]
    [InlineData("a\r\nb", 3, 1, 0)]
    [InlineData("a\r\nb", 2, 0, 2)]
    [InlineData("a\rb", 2, 1, 0)]
    [InlineData("abc", 2, 0, 2)]
    public void A_place_counts_every_break_roslyn_counts(string text, int offset, int line, int column)
    {
        Assert.Equal((line, column), RoslynLines.PlaceOf(text, offset));
    }

    // A fact of its own: a theory's case named by U+0085, U+2028 or U+2029 is a name CI's shards cannot match.
    [Fact]
    public void A_place_counts_the_unicode_line_and_paragraph_separators_and_next_line_as_breaks()
    {
        Assert.Equal((3, 0), RoslynLines.PlaceOf("a\u0085b\u2028c\u2029d", 6));
    }
}
