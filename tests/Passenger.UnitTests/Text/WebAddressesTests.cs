using System.Text.RegularExpressions;
using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>The addresses written in text (<see cref="WebAddresses"/>): each scheme's, www.'s and a file's, to the last
/// character that cannot end a sentence, its balanced parentheses with it and a ')' that only wraps it left out, an IPv6
/// host in its brackets, and none starting inside another scheme, as Aspire's service discovery writes one.</summary>
public class WebAddressesTests
{
    [Theory]
    [InlineData("see https://example.com/docs. now", "https://example.com/docs")]
    [InlineData("(http://localhost:5000/api?x=1)", "http://localhost:5000/api?x=1")]
    [InlineData("mail mailto:team@example.com, then", "mailto:team@example.com")]
    [InlineData("at www.example.com!", "www.example.com")]
    [InlineData("kept in file:///tmp/Links.cs, never", "file:///tmp/Links.cs")]
    [InlineData("\"https+http://apiservice\"", null)]
    [InlineData("\"http+https://apiservice/weather\"", null)]
    [InlineData("a.http://example.com and x-https://example.com", null)]
    [InlineData("no address here, only www and http words", null)]
    [InlineData("See https://en.wikipedia.org/wiki/C_(programming_language) for more.", "https://en.wikipedia.org/wiki/C_(programming_language)")]
    [InlineData("(see https://en.wikipedia.org/wiki/C_(programming_language))", "https://en.wikipedia.org/wiki/C_(programming_language)")]
    [InlineData("Read https://en.wikipedia.org/wiki/C_(programming_language).", "https://en.wikipedia.org/wiki/C_(programming_language)")]
    [InlineData("[the docs](https://example.com/a_(b)_c), then", "https://example.com/a_(b)_c")]
    [InlineData("at https://example.com/f(x_(y)) now", "https://example.com/f(x_(y))")]
    [InlineData("open https://example.com/a(b c", "https://example.com/a")]
    [InlineData("in (www.example.com/q?(a)=1).", "www.example.com/q?(a)=1")]
    [InlineData("kept (file:///C:/Program%20Files%20(x86)/App/readme.txt).", "file:///C:/Program%20Files%20(x86)/App/readme.txt")]
    [InlineData("      Now listening on: http://[::]:8080", "http://[::]:8080")]
    [InlineData("served at http://[::1]:5000/api.", "http://[::1]:5000/api")]
    [InlineData("(http://[2001:db8::1])", "http://[2001:db8::1]")]
    public void An_address_is_read_to_its_end_and_none_starts_inside_another_scheme(string text, string? address)
    {
        List<string> found = [];
        foreach (ValueMatch match in WebAddresses.In(text))
        {
            found.Add(text.Substring(match.Index, match.Length));
        }
        Assert.Equal(address is null ? [] : [address], found);
    }
}
