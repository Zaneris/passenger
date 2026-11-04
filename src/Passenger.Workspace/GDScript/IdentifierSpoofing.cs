using System.Globalization;
using System.Text;
using Passenger.Core;

namespace Passenger.Workspace.GDScript;

/// <summary>Whether an identifier is one Godot warns of as confusable (CONFUSABLE_IDENTIFIER): a port of ICU 78.3's
/// uspoof_check (<c>i18n/uspoof.cpp</c>'s checkImpl and <c>uspoof_impl.cpp</c>'s script sets, restriction level, numerics
/// and hidden overlay; Unicode License V3) as Godot's TextServerAdvanced::spoof_check sets it up: characters limited to
/// Unicode's allowed ones, at most moderately restrictive in its scripts, digits of one system, no nonspacing mark twice
/// over one character, and no dot above hidden on an i or a j. Pure.</summary>
public static class IdentifierSpoofing
{
    /// <summary>Whether ICU's check fails an identifier, as Godot's spoof_check says.</summary>
    public static bool Spoofs(string identifier)
    {
        if (Ascii.IsValid(identifier))
        {
            return false;
        }
        List<int> points = [.. identifier.EnumerateRunes().Select(r => r.Value)];
        List<IReadOnlySet<string>?> scripts = [];
        foreach (int point in points)
        {
            if (!UnicodeIdentifiers.Allowed(point, out IReadOnlySet<string>? set))
            {
                return true;
            }
            scripts.Add(set);
        }
        return !Restricted(scripts) || MixedNumbers(identifier) || HiddenOverlay(points) || RepeatedMark(identifier);
    }

    /// <summary>Whether the identifier's scripts are at most moderately restrictive (UTS #39 section 5.2): one resolved
    /// script, or, past Latin, a script set with Han's, or one that is not Cyrillic, Greek or Cherokee.</summary>
    private static bool Restricted(List<IReadOnlySet<string>?> scripts)
    {
        if (Resolved(scripts, null) is not { Count: 0 })
        {
            return true;
        }
        HashSet<string>? beyondLatin = Resolved(scripts, "Latn");
        return beyondLatin is null || beyondLatin.Overlaps(["Hanb", "Jpan", "Kore"])
            || beyondLatin.Count > 0 && !beyondLatin.Overlaps(["Cyrl", "Grek", "Cher"]);
    }

    /// <summary>The scripts every character's augmented set holds (null for every script), passing over those whose set
    /// holds <paramref name="without"/>.</summary>
    private static HashSet<string>? Resolved(List<IReadOnlySet<string>?> scripts, string? without)
    {
        HashSet<string>? resolved = null;
        foreach (IReadOnlySet<string>? set in scripts)
        {
            if (set is null || without is not null && set.Contains(without))
            {
                continue;
            }
            if (resolved is null)
            {
                resolved = [.. set];
            }
            else
            {
                resolved.IntersectWith(set);
            }
        }
        return resolved;
    }

    /// <summary>Whether its decimal digits are of more than one system, each known by its zero.</summary>
    private static bool MixedNumbers(string identifier)
    {
        HashSet<int> zeros = [];
        for (int i = 0; i < identifier.Length; i += char.IsSurrogatePair(identifier, i) ? 2 : 1)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(identifier, i) == UnicodeCategory.DecimalDigitNumber)
            {
                zeros.Add(char.ConvertToUtf32(identifier, i) - CharUnicodeInfo.GetDecimalDigitValue(identifier, i));
            }
        }
        return zeros.Count > 1;
    }

    /// <summary>Whether a combining dot above follows a character it would be hidden on, past marks of its own class.</summary>
    private static bool HiddenOverlay(List<int> points)
    {
        bool lead = false;
        foreach (int point in points)
        {
            if (lead && point == 0x0307)
            {
                return true;
            }
            if (!UnicodeIdentifiers.OtherCombining(point))
            {
                lead = UnicodeIdentifiers.DotLead(point);
            }
        }
        return false;
    }

    /// <summary>Whether, decomposed, a run of nonspacing marks holds one mark twice.</summary>
    private static bool RepeatedMark(string identifier)
    {
        string decomposed = identifier.Normalize(NormalizationForm.FormD);
        HashSet<int> marks = [];
        for (int i = 0; i < decomposed.Length; i += char.IsSurrogatePair(decomposed, i) ? 2 : 1)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(decomposed, i) != UnicodeCategory.NonSpacingMark)
            {
                marks.Clear();
            }
            else if (!marks.Add(char.ConvertToUtf32(decomposed, i)))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>The lookups of <see cref="UnicodeIdentifiers"/>' tables, each by halving.</summary>
internal static partial class UnicodeIdentifiers
{
    /// <summary>The script sets read once, as asked first: the tables' order of making across files is not the code's to say.</summary>
    private static IReadOnlySet<string>?[]? parsed;

    private static IReadOnlySet<string>?[] Parsed =>
        parsed ??= [.. ScriptSets.Select(s => s.Length == 0 ? null : (IReadOnlySet<string>)s.Split(' ').ToHashSet(StringComparer.Ordinal))];

    /// <summary>Whether a character is allowed in an identifier, with its augmented script set (null for every script).</summary>
    public static bool Allowed(int point, out IReadOnlySet<string>? scripts)
    {
        int run = Sorted.FirstAfter(AllowedRuns.Length / 3, AllowedRuns, static (runs, i) => runs[i * 3], point) - 1;
        scripts = run >= 0 && AllowedRuns[(run * 3) + 1] >= point ? Parsed[AllowedRuns[(run * 3) + 2]] : null;
        return run >= 0 && AllowedRuns[(run * 3) + 1] >= point;
    }

    /// <summary>Whether a character's combining class is neither 0 nor 230.</summary>
    public static bool OtherCombining(int point)
    {
        int run = Sorted.FirstAfter(CombiningRuns.Length / 2, CombiningRuns, static (runs, i) => runs[i * 2], point) - 1;
        return run >= 0 && CombiningRuns[(run * 2) + 1] >= point;
    }

    /// <summary>Whether a combining dot above is hidden on a character.</summary>
    public static bool DotLead(int point) => Sorted.FirstAtOrAfter<int>(DotLeads, point) is int at && at < DotLeads.Length && DotLeads[at] == point;
}
