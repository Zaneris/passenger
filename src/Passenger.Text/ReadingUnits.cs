using System.Globalization;

namespace Passenger.Text;

/// <summary>The pieces a screen reader steps a line's text by: a character, as a person sees one (a letter with its marks,
/// an emoji with its joiners: a grapheme cluster), and a word, a run of one kind of character (a word's by
/// <see cref="WordCharacters"/>, white space, or other marks such as an operator), so every place in a line lies in exactly
/// one word and the words follow one another to its end.</summary>
public static class ReadingUnits
{
    private enum Kind
    {
        Word,
        Space,
        Other,
    }

    /// <summary>The character holding an index of <paramref name="piece"/>, as its start and end there.</summary>
    public static (int Start, int End) CharacterAt(ReadOnlySpan<char> piece, int index)
    {
        if (piece.IsEmpty)
        {
            return (0, 0);
        }
        int at = Math.Clamp(index, 0, piece.Length - 1);
        int start = 0;
        while (start < piece.Length)
        {
            int end = start + Math.Max(1, StringInfo.GetNextTextElementLength(piece[start..]));
            if (at < end)
            {
                return (start, end);
            }
            start = end;
        }
        return (piece.Length, piece.Length);
    }

    /// <summary>The word holding an index of <paramref name="piece"/>, as its start and end there: the run of characters of
    /// the kind there, a mark or joiner taking the kind of the character it carries on.</summary>
    public static (int Start, int End) WordAt(ReadOnlySpan<char> piece, int index)
    {
        if (piece.IsEmpty)
        {
            return (0, 0);
        }
        int at = Math.Clamp(index, 0, piece.Length - 1);
        Kind kind = KindAt(piece, at);
        int start = at;
        while (start > 0 && KindAt(piece, start - 1) == kind)
        {
            start--;
        }
        int end = at + 1;
        while (end < piece.Length && KindAt(piece, end) == kind)
        {
            end++;
        }
        return (start, end);
    }

    /// <summary>The kind of the character at an index: a mark or joiner the kind of the one it carries on, within
    /// <see cref="WordCharacters.Reach"/> before it.</summary>
    private static Kind KindAt(ReadOnlySpan<char> piece, int index)
    {
        int at = index;
        while (at > 0 && index - at < WordCharacters.Reach && WordCharacters.Extends(piece[at]))
        {
            at--;
        }
        char c = piece[at];
        return WordCharacters.IsWord(c) || WordCharacters.Extends(c) ? Kind.Word : char.IsWhiteSpace(c) ? Kind.Space : Kind.Other;
    }
}
