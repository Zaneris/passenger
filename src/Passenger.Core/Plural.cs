namespace Passenger.Core;

/// <summary>A count and its noun: "1 file", "3 files", "0 files", never "1 files" or "2 file"; and names as a sentence
/// lists them.</summary>
public static class Plural
{
    /// <summary>The count and its noun, the noun's plural <paramref name="many"/> or else the noun with an s.</summary>
    public static string Of(long count, string one, string? many = null) => $"{count} {Noun(count, one, many)}";

    /// <summary>The noun alone, for a count written its own way ("1,024 objects").</summary>
    public static string Noun(long count, string one, string? many = null) => count == 1 ? one : many ?? one + "s";

    /// <summary>Names as a sentence lists them: "Web", "Api and Web", "Api, Web and Worker"; nothing for none.</summary>
    public static string Listed(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Concat(names) : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
}
