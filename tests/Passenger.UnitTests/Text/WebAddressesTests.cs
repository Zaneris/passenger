using System.Text.RegularExpressions;
using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>The addresses written in text (<see cref="WebAddresses"/>): each scheme's, www.'s and a file's, to the last
/// character that cannot end a sentence, and none starting inside another scheme, as Aspire's service discovery writes one.</summary>
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
