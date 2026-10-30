using System.Globalization;
using System.Text;

namespace Passenger.App.Tables;

/// <summary>An Excel number format turned into .NET's: a number's custom format (whose digits, thousands, percent,
/// exponent and sections are Excel's own), a date's, or General. What .NET has no place for (colours, conditions,
/// fills, padding) is left out, and fractions show as General.</summary>
internal sealed class CellFormat
{
    private enum Kinds
    {
        General,
        Number,
        Date,
    }

    public static readonly CellFormat General = new(Kinds.General, null, false);

    /// <summary>The system's short date, Excel's built-in 14, and with the time of day, its 22.</summary>
    public static readonly CellFormat ShortDate = new(Kinds.Date, "d", false);
    public static readonly CellFormat ShortDateTime = new(Kinds.Date, "g", false);

    /// <summary>The 1904 date system's day 0 as a serial of the 1900 system.</summary>
    public const double Day1904 = 1462;

    private readonly Kinds kind;
    private readonly string? net;

    /// <summary>A date format whose hours count past a day ([h]:mm:ss), written before the rest.</summary>
    private readonly bool elapsed;

    private CellFormat(Kinds kind, string? net, bool elapsed)
    {
        this.kind = kind;
        this.net = net;
        this.elapsed = elapsed;
    }

    /// <summary>Whether a number in this format shows as a date or a time.</summary>
    public bool ShowsDate => kind == Kinds.Date;

    /// <summary>A number as Excel shows it in this format.</summary>
    public string Show(double value, bool from1904)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        try
        {
            switch (kind)
            {
                case Kinds.Number:
                    return value.ToString(net, culture);
                case Kinds.Date when value >= 0:
                    double serial = from1904 ? value + Day1904 : value;
                    DateTime date = DateTime.FromOADate(serial);
                    if (elapsed)
                    {
                        // Rounded to the second, as Excel shows an elapsed time.
                        long hours = (long)Math.Floor(Math.Round(value * 86400) / 3600);
                        return hours.ToString(culture) + date.ToString(net, culture);
                    }
                    return date.ToString(net, culture);
            }
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
        }
        // Excel's General: up to 15 significant digits, so 0.1 + 0.2 shows 0.3.
        return value.ToString("G15", culture);
    }

    /// <summary>One of Excel's built-in formats by its id, as an en-US Excel shows it (14 and 22 in the system's own
    /// short date).</summary>
    public static CellFormat BuiltIn(int id) => id switch
    {
        1 => Parse("0"),
        2 => Parse("0.00"),
        3 => Parse("#,##0"),
        4 => Parse("#,##0.00"),
        9 => Parse("0%"),
        10 => Parse("0.00%"),
        11 => Parse("0.00E+00"),
        14 => ShortDate,
        15 => Parse("d-mmm-yy"),
        16 => Parse("d-mmm"),
        17 => Parse("mmm-yy"),
        18 => Parse("h:mm AM/PM"),
        19 => Parse("h:mm:ss AM/PM"),
        20 => Parse("h:mm"),
        21 => Parse("h:mm:ss"),
        22 => ShortDateTime,
        37 => Parse("#,##0 ;(#,##0)"),
        38 => Parse("#,##0 ;[Red](#,##0)"),
        39 => Parse("#,##0.00;(#,##0.00)"),
        40 => Parse("#,##0.00;[Red](#,##0.00)"),
        45 => Parse("mm:ss"),
        46 => Parse("[h]:mm:ss"),
        47 => Parse("mmss.0"),
        48 => Parse("##0.0E+0"),
        _ => General,
    };

    /// <summary>An Excel format code as .NET's.</summary>
    public static CellFormat Parse(string code)
    {
        List<string> sections = Sections(code);
        string first = sections[0];
        if (first.Length == 0 || first.Equals("General", StringComparison.OrdinalIgnoreCase) || first == "@")
        {
            return General;
        }
        if (IsDate(first))
        {
            return DateFormat(first);
        }
        StringBuilder net = new();
        // .NET takes three sections: positive, negative and zero; Excel's fourth is for text.
        for (int i = 0; i < Math.Min(sections.Count, 3); i++)
        {
            if (i > 0)
            {
                net.Append(';');
            }
            if (!NumberSection(sections[i], net))
            {
                return General;
            }
        }
        return new CellFormat(Kinds.Number, net.ToString(), false);
    }

    /// <summary>The code's sections, split at each semicolon outside quotes and brackets.</summary>
    private static List<string> Sections(string code)
    {
        List<string> sections = [];
        int start = 0;
        for (int i = 0; i < code.Length; i++)
        {
            i = Skip(code, i);
            if (i < code.Length && code[i] == ';')
            {
                sections.Add(code[start..i]);
                start = i + 1;
            }
        }
        sections.Add(code[start..]);
        return sections;
    }

    /// <summary>Past a quoted literal, a bracket or an escaped character at <paramref name="i"/>, to its last character.</summary>
    private static int Skip(string code, int i)
    {
        char c = code[i];
        if (c == '"')
        {
            int end = code.IndexOf('"', i + 1);
            return end < 0 ? code.Length - 1 : end;
        }
        if (c == '[')
        {
            int end = code.IndexOf(']', i + 1);
            return end < 0 ? code.Length - 1 : end;
        }
        return c is '\\' or '_' or '*' && i + 1 < code.Length ? i + 1 : i;
    }

    private static bool IsDate(string section)
    {
        for (int i = 0; i < section.Length; i++)
        {
            char c = section[i];
            if (c == '[' && section.IndexOf(']', i) is int end and > 0)
            {
                string inside = section[(i + 1)..end].ToLowerInvariant();
                if (inside.Length > 0 && inside.All(ch => ch is 'h' or 'm' or 's'))
                {
                    return true;
                }
                i = end;
                continue;
            }
            int skipped = Skip(section, i);
            if (skipped != i)
            {
                i = skipped;
                continue;
            }
            if (char.ToLowerInvariant(c) is 'y' or 'd' or 'h' or 's' or 'm')
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A number section in .NET's custom format, false for one .NET cannot show (a fraction).</summary>
    private static bool NumberSection(string section, StringBuilder net)
    {
        for (int i = 0; i < section.Length; i++)
        {
            char c = section[i];
            switch (c)
            {
                case '"':
                    int end = section.IndexOf('"', i + 1);
                    end = end < 0 ? section.Length : end;
                    Literal(net, section[(i + 1)..end]);
                    i = end;
                    break;
                case '\\' when i + 1 < section.Length:
                    Literal(net, section[i + 1].ToString());
                    i++;
                    break;
                case '[':
                    int close = section.IndexOf(']', i);
                    close = close < 0 ? section.Length : close;
                    // A currency ([$€-407]) shows its symbol; colours and conditions show nothing.
                    if (section.AsSpan(i + 1, close - i - 1) is ['$', .. var currency])
                    {
                        int dash = currency.IndexOf('-');
                        Literal(net, (dash < 0 ? currency : currency[..dash]).ToString());
                    }
                    i = close;
                    break;
                case '_' when i + 1 < section.Length:
                    net.Append(' ');
                    i++;
                    break;
                case '*' when i + 1 < section.Length:
                    i++;
                    break;
                case '?':
                    net.Append('#');
                    break;
                case '/':
                    return false;
                case '@':
                    break;
                case 'G' or 'g' when section.AsSpan(i).StartsWith("General", StringComparison.OrdinalIgnoreCase):
                    net.Append("0.##########");
                    i += "General".Length - 1;
                    break;
                default:
                    net.Append(c);
                    break;
            }
        }
        return true;
    }

    private static CellFormat DateFormat(string section)
    {
        bool twelve = section.Contains("AM/PM", StringComparison.OrdinalIgnoreCase) || section.Contains("A/P", StringComparison.OrdinalIgnoreCase);
        bool elapsed = false;
        StringBuilder net = new();
        // Whether the last date part was hours, so an m after it is minutes.
        char last = '\0';
        for (int i = 0; i < section.Length; i++)
        {
            char c = section[i];
            char lower = char.ToLowerInvariant(c);
            int run = 1;
            while (i + run < section.Length && char.ToLowerInvariant(section[i + run]) == lower)
            {
                run++;
            }
            switch (lower)
            {
                case '"':
                    int end = section.IndexOf('"', i + 1);
                    end = end < 0 ? section.Length : end;
                    Literal(net, section[(i + 1)..end]);
                    i = end;
                    continue;
                case '\\' when i + 1 < section.Length:
                    Literal(net, section[i + 1].ToString());
                    i++;
                    continue;
                case '_' when i + 1 < section.Length:
                    net.Append(' ');
                    i++;
                    continue;
                case '*' when i + 1 < section.Length:
                    i++;
                    continue;
                case '[':
                    int close = section.IndexOf(']', i);
                    close = close < 0 ? section.Length : close;
                    string inside = section[(i + 1)..close].ToLowerInvariant();
                    if (inside.Length > 0 && inside[0] == 'h' && inside.All(ch => ch == 'h') && net.Length == 0)
                    {
                        // Elapsed hours come first, written by Show; the rest is the time of day after them.
                        elapsed = true;
                        last = 'h';
                    }
                    else if (inside.StartsWith('$'))
                    {
                        int dash = inside.IndexOf('-');
                        Literal(net, section[(i + 2)..(dash < 0 ? close : i + 1 + dash)]);
                    }
                    i = close;
                    continue;
                case 'y':
                    net.Append(run <= 2 ? "yy" : "yyyy");
                    break;
                case 'd':
                    net.Append(run switch { 1 => "d", 2 => "dd", 3 => "ddd", _ => "dddd" });
                    break;
                case 'h':
                    net.Append(twelve ? (run == 1 ? "h" : "hh") : run == 1 ? "H" : "HH");
                    break;
                case 's':
                    net.Append(run == 1 ? "s" : "ss");
                    // Fractions of a second: s.0, s.00.
                    int zeros = 0;
                    while (i + run + 1 + zeros < section.Length && section[i + run] == '.' && section[i + run + 1 + zeros] == '0')
                    {
                        zeros++;
                    }
                    if (zeros > 0)
                    {
                        net.Append('.').Append('f', zeros);
                        run += 1 + zeros;
                    }
                    break;
                case 'm' when last == 'h' || NextPartIsSeconds(section, i + run):
                    net.Append(run == 1 ? "m" : "mm");
                    break;
                case 'm':
                    net.Append(run switch { 1 => "M", 2 => "MM", 3 => "MMM", 4 => "MMMM", _ => "MMM" });
                    break;
                case 'a' when section.AsSpan(i).StartsWith("AM/PM", StringComparison.OrdinalIgnoreCase):
                    net.Append("tt");
                    i += 4;
                    continue;
                case 'a' when section.AsSpan(i).StartsWith("A/P", StringComparison.OrdinalIgnoreCase):
                    net.Append('t');
                    i += 2;
                    continue;
                default:
                    // Anything else is itself: .NET would read a letter, a '/' or a ':' as a part or the culture's separator.
                    Literal(net, section.Substring(i, run));
                    i += run - 1;
                    continue;
            }
            last = lower;
            i += run - 1;
        }
        return new CellFormat(Kinds.Date, net.ToString(), elapsed);
    }

    /// <summary>Whether the next date part after <paramref name="from"/> is seconds, so an m before it is minutes (mm:ss).</summary>
    private static bool NextPartIsSeconds(string section, int from)
    {
        for (int i = from; i < section.Length; i++)
        {
            char lower = char.ToLowerInvariant(section[i]);
            if (lower is 'y' or 'd' or 'h' or 'm')
            {
                return false;
            }
            if (lower == 's')
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Text shown as itself in a .NET custom format, each character escaped.</summary>
    private static void Literal(StringBuilder net, string text)
    {
        foreach (char c in text)
        {
            net.Append('\\').Append(c);
        }
    }
}
