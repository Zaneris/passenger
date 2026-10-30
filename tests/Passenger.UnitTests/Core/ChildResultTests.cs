using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>The first line of a program's words is the first that says anything, trimmed, whatever its lines end in.</summary>
public class ChildResultTests
{
    [Theory]
    [InlineData("0.0.0.0:55123\n[::]:55123\n", "0.0.0.0:55123")]
    [InlineData("\r\n  \n  Error: no such service: web\r\nmore\r\n", "Error: no such service: web")]
    [InlineData("one line", "one line")]
    [InlineData(" \n\t\n", "")]
    [InlineData("", "")]
    public void The_first_line_is_the_first_that_says_anything_trimmed(string words, string first)
    {
        Assert.Equal(first, ChildResult.FirstLineOf(words));
        Assert.Equal(first, new ChildResult(0, words, string.Empty).FirstLine);
    }
}
