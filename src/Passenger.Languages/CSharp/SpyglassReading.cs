using Microsoft.CodeAnalysis.CSharp;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>What Code Spyglass decides apart from the language server and the window: the words of a hint, where a hint's
/// declaration is named now, which places the server names are no use of it, and when what was read is read again.</summary>
public static class SpyglassReading
{
    /// <summary>How old the numbers of an edited file are let to grow before they are read again; a save reads them at once.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    /// <summary>How many places use a declaration, in words ("1 usage", "no usages").</summary>
    public static string Usages(int count) => count == 0 ? "no usages" : Plural.Of(count, "usage");

    /// <summary>What implements, overrides or derives from a declaration, in words by what it is ("1 override").</summary>
    public static string Inheriting(Heritage heritage, int count)
    {
        string noun = heritage switch
        {
            Heritage.Overrides => "override",
            Heritage.Inheritors => "inheritor",
            _ => "implementation",
        };
        return count == 0 ? $"no {noun}s" : Plural.Of(count, noun);
    }

    /// <summary>Whether a place the server names is the declaration asked about, which is among its references and is no use of
    /// it: the same place of the same document, however the server's URI is cased.</summary>
    public static bool IsDeclaration((string Uri, int Line, int Character) place, (string Uri, int Line, int Character) declaration) =>
        place.Line == declaration.Line && place.Character == declaration.Character && string.Equals(place.Uri, declaration.Uri, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where a declaration's name stands on its line now: at the column it had when it was read while the name is still
    /// there, else the first whole word that is it; none when the line no longer names it.</summary>
    public static int? NameOffset(TextSnapshot text, int line, string name, int column)
    {
        if (line < 0 || line >= text.LineCount || name.Length == 0)
        {
            return null;
        }
        int start = text.GetLineStart(line);
        string code = text.GetLineText(line);
        if (column >= 0 && column + name.Length <= code.Length && string.CompareOrdinal(code, column, name, 0, name.Length) == 0)
        {
            return start + column;
        }
        for (int at = code.IndexOf(name, StringComparison.Ordinal); at >= 0; at = code.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            int end = at + name.Length;
            if ((at == 0 || !SyntaxFacts.IsIdentifierPartCharacter(code[at - 1])) && (end >= code.Length || !SyntaxFacts.IsIdentifierPartCharacter(code[end])))
            {
                return start + at;
            }
        }
        return null;
    }

    /// <summary>Whether a declaration's numbers are to be read: none yet, a kind wanted that was not read, or numbers of an
    /// earlier text that the file's save, or their age, has made out of date.</summary>
    public static bool Needs(bool known, int? usages, int? inheritors, int countedVersion, Heritage heritage, bool wantUsages, bool wantInheritors,
        int version, bool saved, TimeSpan age)
    {
        if (!known || wantUsages && usages is null || wantInheritors && heritage != Heritage.None && inheritors is null)
        {
            return true;
        }
        return countedVersion != version && (saved || age > StaleAfter);
    }
}
