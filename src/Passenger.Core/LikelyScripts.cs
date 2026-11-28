using System.Collections.Frozen;

namespace Passenger.Core;

/// <summary>The script a language is likely written in where its tag names none, as the Unicode CLDR's likely subtags say
/// (LikelyScripts.Cldr.cs, from tools/cldr.py): Punjabi in Gurmukhi, Serbian in Cyrillic, Chinese in Han Simplified.</summary>
public static partial class LikelyScripts
{
    private static readonly Lazy<FrozenDictionary<string, string>> Read = new(() =>
    {
        string[] words = Pairs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Dictionary<string, string> scripts = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < words.Length; i += 2)
        {
            scripts[words[i]] = words[i + 1];
        }
        return scripts.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    });

    /// <summary>The script (a four-letter ISO 15924 code, "Guru") a culture is written in: the one its tag names, else the
    /// one its language is likely written in; null where CLDR knows neither.</summary>
    public static string? Of(string tag)
    {
        string[] subtags = tag.Split('-', '_');
        if (subtags.Length > 1 && subtags[1].Length == 4 && subtags[1].All(char.IsAsciiLetter))
        {
            return char.ToUpperInvariant(subtags[1][0]) + subtags[1][1..].ToLowerInvariant();
        }
        return subtags[0].Length > 0 && Read.Value.TryGetValue(subtags[0], out string? script) ? script : null;
    }
}
