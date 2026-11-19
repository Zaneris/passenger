using System.Text.RegularExpressions;

namespace Passenger.Text;

/// <summary>The web and file addresses written in text: mailto:, http, https, ftp, ftps, news and www. without a scheme, each
/// to its last character that cannot end a sentence, and a file: URL. A scheme that is the end of another's is none (Aspire's
/// service discovery, https+http://). What a console's lines and the editor's comments and strings link, read only within
/// the stretch given. Pure.</summary>
public static partial class WebAddresses
{
    [GeneratedRegex(@"(?:(?<![\w+.\-])(?:mailto:|(?:news|(?:ht|f)tps?)://)|(?<![\p{L}0-9_.])www\.)[-A-Za-z0-9+$&@#/%?=~_|!:,.;]*[-A-Za-z0-9+$&@#/%=~_|]|\bfile:(?:/|[A-Za-z]:)[^\s()\[\]]*[^\s()\[\]:;?!,.]")]
    private static partial Regex Pattern();

    /// <summary>Where each address in a stretch of text stands in it, in order and apart, read without a copy of the text.</summary>
    public static Regex.ValueMatchEnumerator In(ReadOnlySpan<char> text) => Pattern().EnumerateMatches(text);

    /// <summary>The address an address's text names (www. taken as http), or null where it is no absolute address.</summary>
    public static Uri? Parse(string text)
    {
        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            text = "http://" + text;
        }
        return Uri.TryCreate(text, UriKind.Absolute, out Uri? address) ? address : null;
    }
}
