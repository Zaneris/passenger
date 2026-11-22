using System.Text.RegularExpressions;
using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>The addresses written in text (<see cref="WebAddresses"/>): each scheme's, www.'s and a file's, to the last
/// character that cannot end a sentence, its balanced parentheses with it and a ')' that only wraps it left out, an IPv6
/// host in its brackets, and none starting inside another scheme, as Aspire's service discovery writes one.</summary>
public class WebAddressesTests
{
    /// <summary>An address is read to its end and none starts inside another scheme.</summary>
    [Fact]
    public void An_address_in_text_is_read_to_its_end() => Cases.All(
        (nameof(An_address_is_read_to_its_end_and_none_starts_inside_another_scheme),
            An_address_is_read_to_its_end_and_none_starts_inside_another_scheme));

    private void An_address_is_read_to_its_end_and_none_starts_inside_another_scheme() => Cases.Each<(string Text, string? Address)>(
    [
        ("see https://example.com/docs. now", "https://example.com/docs"),
        ("mail mailto:team@example.com, then", "mailto:team@example.com"),
        ("kept in file:///tmp/Links.cs, never", "file:///tmp/Links.cs"),
        ("\"https+http://apiservice\"", null),
        ("a.http://example.com and x-https://example.com", null),
        ("See https://en.wikipedia.org/wiki/C_(programming_language) for more.", "https://en.wikipedia.org/wiki/C_(programming_language)"),
        ("[the docs](https://example.com/a_(b)_c), then", "https://example.com/a_(b)_c"),
        ("open https://example.com/a(b c", "https://example.com/a"),
        ("      Now listening on: http://[::]:8080", "http://[::]:8080"),
        ("served at http://[::1]:5000/api.", "http://[::1]:5000/api"),
    ], row => An_address_is_read_to_its_end_and_none_starts_inside_another_scheme(row.Text, row.Address));

    private void An_address_is_read_to_its_end_and_none_starts_inside_another_scheme(string text, string? address)
    {
        List<string> found = [];
        foreach (ValueMatch match in WebAddresses.In(text))
        {
            found.Add(text.Substring(match.Index, match.Length));
        }
        Assert.Equal(address is null ? [] : [address], found);
    }
}
