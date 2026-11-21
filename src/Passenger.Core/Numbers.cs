using System.Globalization;

namespace Passenger.Core;

/// <summary>A number as a person reads it, written one way wherever the IDE says one: its digits the IDE's own whatever the
/// person's culture, thousands set apart by a comma and a fraction by a point ("1,234", "1,234.5", "12.0"), as sizes and
/// whiles are said. A number naming something (a line, a column, a port, a process) or a picture's size in pixels
/// ("1920 × 1080") is written plain, in the invariant culture.</summary>
public static class Numbers
{
    /// <summary>The formats of each number of places, from none to the most a measure is said to.</summary>
    private static readonly string[] AtMost = ["#,0", "#,0.#", "#,0.##", "#,0.###"];
    private static readonly string[] Exactly = ["N0", "N1", "N2", "N3"];

    /// <summary>A count: "0", "12", "1,234".</summary>
    public static string Said(long count) => count.ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>A measure rounded to at most <paramref name="places"/> places (up to 3), nothing written past its last digit
    /// that counts: "1.5", "2", "1,234.25".</summary>
    public static string Said(double value, int places) => value.ToString(AtMost[places], CultureInfo.InvariantCulture);

    /// <summary>A measure to exactly <paramref name="places"/> places (up to 3), as a column of them lines its points up:
    /// "12.0", "1,234.5".</summary>
    public static string Fixed(double value, int places) => value.ToString(Exactly[places], CultureInfo.InvariantCulture);
}
