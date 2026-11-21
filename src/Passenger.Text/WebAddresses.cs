using System.Text.RegularExpressions;

namespace Passenger.Text;

/// <summary>The web and file addresses written in text: mailto:, http, https, ftp, ftps, news and www. without a scheme, each
/// to its last character that cannot end a sentence, and a file: URL. An address takes in its balanced parentheses, two
/// deep (Wikipedia's C_(programming_language)), so a ')' that only wraps it is left out. A scheme that is the end of another's
/// is none (Aspire's service discovery, https+http://). What a console's lines and the editor's comments and strings link,
/// read only within the stretch given. Pure.</summary>
public static partial class WebAddresses
{
    /// <summary>A web address's characters, and those it may end with.</summary>
    private const string WebCharacter = "[-A-Za-z0-9+$&@#/%?=~_|!:,.;]";
    private const string WebLast = "[-A-Za-z0-9+$&@#/%=~_|]";

    /// <summary>A file address's characters, and those it may end with.</summary>
    private const string FileCharacter = @"[^\s()\[\]]";
    private const string FileLast = @"[^\s()\[\]:;?!,.]";

    /// <summary>Parentheses that close in an address, one pair inside another at most.</summary>
    private const string WebPair = $@"\((?:{WebCharacter}|\({WebCharacter}*\))*\)";
    private const string FilePair = $@"\((?:{FileCharacter}|\({FileCharacter}*\))*\)";

    private const string Address = $@"(?:(?<![\w+.\-])(?:mailto:|(?:news|(?:ht|f)tps?)://)|(?<![\p{{L}}0-9_.])www\.)(?:{WebCharacter}|{WebPair})*(?:{WebLast}|{WebPair})"
        + $@"|\bfile:(?:/|[A-Za-z]:)(?:{FileCharacter}|{FilePair})*(?:{FileLast}|{FilePair})";

    [GeneratedRegex(Address)]
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
