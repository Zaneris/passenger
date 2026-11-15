using System.Globalization;

namespace Passenger.Core;

/// <summary>A C# name's characters as Roslyn reads them (its SyntaxFacts' IsIdentifierStartCharacter and
/// IsIdentifierPartCharacter), for code compiled where Roslyn is not: a letter, a letter number among them, or '_' starts
/// one, and a letter, a decimal digit, connector punctuation, a combining mark or a format character carries it on.</summary>
public static class CSharpNames
{
    /// <summary>Whether a C# name may start with a character.</summary>
    public static bool Starts(char c) =>
        c < 128 ? c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' : IsLetter(CharUnicodeInfo.GetUnicodeCategory(c));

    /// <summary>Whether a character carries a C# name on.</summary>
    public static bool Continues(char c) =>
        c < 128 ? c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_'
            : CharUnicodeInfo.GetUnicodeCategory(c) is var category && (IsLetter(category) || category is UnicodeCategory.DecimalDigitNumber
                or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.Format);

    private static bool IsLetter(UnicodeCategory category) => category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
        or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber;
}
