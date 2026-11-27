using System.Globalization;

namespace Passenger.Core;

/// <summary>The language Passenger's words are read in: the culture asked for (the person's choice, else the system's first
/// preferred language), which numbers and dates are written by, and the carried language its chain reaches, which the words
/// come from; English where it reaches none. The chain is .NET's own (fr-CA, fr), walked by <see cref="Chain"/>, which never
/// crosses a change of script, as CLDR's parents never do (pa-Arab reaches no Gurmukhi pa). The pseudo-locales
/// (<see cref="PseudoTag"/>, <see cref="MirroredTag"/>) show English words accented, longer and bracketed, the second
/// laid out right to left, so a word left unextracted or cut short shows itself.</summary>
public sealed class UiLanguage
{
    /// <summary>The pseudo-locale of accented, longer, bracketed words.</summary>
    public const string PseudoTag = "qps-ploc";

    /// <summary>The pseudo-locale laid out right to left.</summary>
    public const string MirroredTag = "qps-plocm";

    private UiLanguage(CultureInfo asked, CultureInfo? reached, string? pseudo)
    {
        Asked = asked;
        Reached = reached;
        Pseudo = pseudo;
    }

    /// <summary>English, the words as written, its numbers and dates Passenger's own.</summary>
    public static UiLanguage English { get; } = new(CultureInfo.InvariantCulture, null, null);

    /// <summary>The language in force for the person, English until Passenger puts one (<see cref="Put"/>).</summary>
    public static UiLanguage InForce { get; private set; } = English;

    /// <summary>Raised on the thread that put a new language in force, after it is in force.</summary>
    public static event EventHandler? Changed;

    /// <summary>The culture asked for: the language's own, or a region's of it (fr-CA), or the invariant culture for
    /// English as written.</summary>
    public CultureInfo Asked { get; }

    /// <summary>The carried culture whose words are read (fr for fr-CA); null for English.</summary>
    public CultureInfo? Reached { get; }

    /// <summary>The pseudo-locale's tag where this is one; null otherwise.</summary>
    public string? Pseudo { get; }

    /// <summary>Whether the words are English as written: no carried language reached, and no pseudo-locale.</summary>
    public bool IsEnglish => Reached is null && Pseudo is null;

    /// <summary>Whether windows are laid out right to left.</summary>
    public bool RightToLeft => Pseudo == MirroredTag || Reached?.TextInfo.IsRightToLeft == true;

    /// <summary>The culture numbers and dates are written in: the asked one's where its words are carried, else Passenger's own.</summary>
    public CultureInfo Formats => Reached is null ? CultureInfo.InvariantCulture : Asked;

    /// <summary>The tag as Settings keeps it and the log says it: the pseudo-locale's, the asked culture's, or "en".</summary>
    public string Tag => Pseudo ?? (Asked.Name.Length == 0 ? "en" : Asked.Name);

    /// <summary>The cultures of an assembly's carried languages (<see cref="CarriedLanguagesAttribute"/>); a tag .NET doesn't
    /// know is passed over.</summary>
    public static IReadOnlyList<CultureInfo> CarriedBy(System.Reflection.Assembly assembly) =>
        [.. (assembly.GetCustomAttributes(typeof(CarriedLanguagesAttribute), false).OfType<CarriedLanguagesAttribute>().FirstOrDefault()?.Tags ?? [])
            .Select(Known).OfType<CultureInfo>().DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Puts a language in force for the person: the words resolve in it from now on, threads with no culture of their
    /// own take its asked culture, and <see cref="Changed"/> tells whoever shows words.</summary>
    public static void Put(UiLanguage language)
    {
        if (language.Tag == InForce.Tag && language.Reached?.Name == InForce.Reached?.Name)
        {
            return;
        }
        InForce = language;
        CultureInfo.DefaultThreadCurrentUICulture = language.Asked.Name.Length > 0 ? language.Asked : null;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>The language a tag names among <paramref name="carried"/> (the pseudo-locales, English, or a culture whose
    /// chain reaches a carried one); null for a tag .NET doesn't know or one that reaches none.</summary>
    public static UiLanguage? Of(string tag, IReadOnlyList<CultureInfo> carried)
    {
        if (tag.Equals(PseudoTag, StringComparison.OrdinalIgnoreCase) || tag.Equals(MirroredTag, StringComparison.OrdinalIgnoreCase))
        {
            return new UiLanguage(CultureInfo.InvariantCulture, null, tag.ToLowerInvariant());
        }
        if (Known(tag) is not { } asked)
        {
            return null;
        }
        foreach (CultureInfo step in Chain(asked))
        {
            if (step.Name == "en")
            {
                return new UiLanguage(asked, null, null);
            }
            if (carried.FirstOrDefault(c => c.Name.Equals(step.Name, StringComparison.OrdinalIgnoreCase)) is { } reached)
            {
                return new UiLanguage(asked, reached, null);
            }
        }
        return null;
    }

    /// <summary>The language of the first tag in <paramref name="preferred"/> (the system's languages, most preferred first)
    /// that reaches a carried language or English; English, asked as the first tag, where none does.</summary>
    public static UiLanguage Choose(IEnumerable<string> preferred, IReadOnlyList<CultureInfo> carried)
    {
        CultureInfo? first = null;
        foreach (string tag in preferred)
        {
            if (Of(tag, carried) is { } language)
            {
                return language;
            }
            first ??= Known(tag);
        }
        return first is null ? English : new UiLanguage(first, null, null);
    }

    /// <summary>.NET's chain of cultures from <paramref name="culture"/> to the last before the invariant culture (fr-CA, fr;
    /// zh-CN, zh-Hans, zh), ending before a step from a culture whose tag names its script to one written in another
    /// (pa-Arab-PK, pa-Arab: never pa, written in Gurmukhi).</summary>
    public static IEnumerable<CultureInfo> Chain(CultureInfo culture)
    {
        for (CultureInfo at = culture; at.Name.Length > 0; at = at.Parent)
        {
            yield return at;
            CultureInfo parent = at.Parent;
            if (parent.Name.Length > 0 && NamesScript(at.Name)
                && !string.Equals(LikelyScripts.Of(at.Name), LikelyScripts.Of(parent.Name), StringComparison.Ordinal))
            {
                yield break;
            }
        }
    }

    private static bool NamesScript(string tag) => tag.Split('-') is [_, { Length: 4 } script, ..] && script.All(char.IsAsciiLetter);

    /// <summary>The culture a tag names, where .NET knows it; null otherwise.</summary>
    private static CultureInfo? Known(string tag)
    {
        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(tag.Replace('_', '-'));
            return culture.Name.Length > 0 && !culture.EnglishName.StartsWith("Unknown", StringComparison.Ordinal) ? culture : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}
