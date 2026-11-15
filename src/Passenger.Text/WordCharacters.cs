using System.Globalization;

namespace Passenger.Text;

/// <summary>What words are made of, for the editor's word moves and whole words, Find's Words and Vim's motions and
/// patterns alike: a letter, a decimal digit, connector punctuation (_) or a surrogate; a mark or a joiner carries on
/// the character before it on its line, a word's where that one is, so nothing stops between a letter and its marks
/// and a mark on an emoji is no word's.</summary>
public static class WordCharacters
{
    /// <summary>The most marks and joiners in a row that carry on the character before them, past the 30 Unicode's
    /// stream-safe text allows; one further stands alone, no word's, so a place costs at most this many reads.</summary>
    public const int Reach = 32;

    /// <summary>A word's character standing on its own (<see cref="IsWord"/>), as a regex class.</summary>
    private const string Standing = @"[\p{L}\p{Nd}\p{Pc}\p{Cs}]";

    /// <summary>A mark or a joiner (<see cref="Extends"/>), as a regex class.</summary>
    private const string Carrying = @"[\p{M}\u200C\u200D]";

    /// <summary>Any character a word holds, its marks and joiners among them, as a regex class.</summary>
    public const string Class = @"[\p{L}\p{M}\p{Nd}\p{Pc}\p{Cs}\u200C\u200D]";

    /// <summary>A regex's place no word goes on from before: after no word's character nor a mark carrying one.</summary>
    public static readonly string NotAfterWord = $"(?<!{Standing}{Carrying}{{0,{Reach}}})";

    /// <summary>A regex's place a word ends at, read from before: after a word's character or a mark carrying one.</summary>
    public static readonly string AfterWord = $"(?<={Standing}{Carrying}{{0,{Reach}}})";

    /// <summary>A regex's place no word goes on after: before no word's character, mark or joiner.</summary>
    public const string NotBeforeWord = $"(?!{Class})";

    /// <summary>A regex's place a word begins at, read from after: before a word's character.</summary>
    public const string BeforeWord = $"(?={Standing})";

    /// <summary>Whether a character is a word's on its own: a letter, a decimal digit, connector punctuation or either
    /// half of a surrogate pair.</summary>
    public static bool IsWord(char c) =>
        c < '\u0080' ? char.IsAsciiLetterOrDigit(c) || c == '_'
        : char.IsLetterOrDigit(c) || char.IsSurrogate(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.ConnectorPunctuation;

    /// <summary>Whether a character carries on the one before it: a mark, or a joiner (U+200C, U+200D).</summary>
    public static bool Extends(char c) =>
        c >= '\u0300' && (c is '\u200C' or '\u200D'
            || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark);

    /// <summary>Where the character stands whose kind the one at an offset takes: there, or for a mark or joiner the
    /// character it carries on, within <see cref="Reach"/> before it on its line; there again where none is.</summary>
    public static int Base(TextSnapshot text, int offset)
    {
        if (!Extends(text[offset]))
        {
            return offset;
        }
        for (int at = offset - 1; at >= Math.Max(0, offset - Reach); at--)
        {
            char c = text[at];
            if (!Extends(c))
            {
                return c is '\n' or '\r' ? offset : at;
            }
        }
        return offset;
    }

    /// <summary>Whether the character at an offset is a word's, a mark or joiner as the character it carries on.</summary>
    public static bool IsWordAt(TextSnapshot text, int offset)
    {
        char c = text[offset];
        return IsWord(Extends(c) ? text[Base(text, offset)] : c);
    }
}
