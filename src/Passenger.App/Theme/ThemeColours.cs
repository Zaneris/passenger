namespace Passenger.App;

/// <summary>Colour arithmetic for themes worked out from a few colours, as tools/themes.py does it: colours as
/// <c>0xAARRGGBB</c>, OKLab for moving lightness while hue and chroma hold, WCAG's luminance and contrast ratio, and CIELAB's
/// L* for telling grounds apart. Pure.</summary>
public static class ThemeColours
{
    /// <summary>The colour opaque, its alpha dropped.</summary>
    public static uint Opaque(uint argb) => 0xFF000000 | argb;

    /// <summary>The colour at an alpha from 0 to 255.</summary>
    public static uint Wash(uint ink, byte alpha) => ((uint)alpha << 24) | (ink & 0xFFFFFF);

    /// <summary>The colour at OKLab lightness <paramref name="lightness"/>, its hue kept and its chroma lowered only as far as
    /// sRGB needs.</summary>
    public static uint WithLightness(uint argb, double lightness)
    {
        (_, double a, double b) = Lab(argb);
        for (int i = 0; i < 40; i++)
        {
            (double r, double g, double bl) = FromLab(lightness, a, b);
            if (InGamut(r) && InGamut(g) && InGamut(bl))
            {
                return Hex(r, g, bl);
            }
            (a, b) = (a * 0.93, b * 0.93);
        }
        (double gr, double gg, double gb) = FromLab(lightness, 0, 0);
        return Hex(gr, gg, gb);

        static bool InGamut(double v) => v is >= -0.0005 and <= 1.0005;
    }

    /// <summary>The colour's OKLab lightness, 0 black to 1 white.</summary>
    public static double Lightness(uint argb) => Lab(argb).L;

    /// <summary>The colour made lighter (or darker, below 0) by <paramref name="by"/> in OKLab lightness.</summary>
    public static uint Shift(uint argb, double by) => WithLightness(argb, Math.Clamp(Lightness(argb) + by, 0, 1));

    /// <summary>One colour <paramref name="t"/> of the way to the other, in OKLab.</summary>
    public static uint Mix(uint one, uint other, double t)
    {
        (double l1, double a1, double b1) = Lab(one);
        (double l2, double a2, double b2) = Lab(other);
        (double r, double g, double b) = FromLab(l1 + (l2 - l1) * t, a1 + (a2 - a1) * t, b1 + (b2 - b1) * t);
        return Hex(r, g, b);
    }

    /// <summary>WCAG's relative luminance of the colour, its alpha left out.</summary>
    public static double Luminance(uint argb) =>
        0.2126 * Linear(((argb >> 16) & 0xFF) / 255.0) + 0.7152 * Linear(((argb >> 8) & 0xFF) / 255.0) + 0.0722 * Linear((argb & 0xFF) / 255.0);

    /// <summary>A colour with alpha drawn over an opaque ground.</summary>
    public static uint Over(uint fore, uint ground)
    {
        double alpha = (fore >> 24) / 255.0;
        uint Channel(int shift) => (uint)Math.Round(((fore >> shift) & 0xFF) * alpha + ((ground >> shift) & 0xFF) * (1 - alpha));
        return 0xFF000000 | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    /// <summary>WCAG's contrast ratio of a colour, drawn over its ground, with that ground.</summary>
    public static double Ratio(uint fore, uint ground)
    {
        uint solid = Opaque(ground);
        double a = Luminance(Over(fore, solid));
        double b = Luminance(solid);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>CIELAB's L*, 0 to 100, by which two grounds are told apart.</summary>
    public static double CieLightness(uint argb)
    {
        double y = Luminance(argb);
        return y > 216.0 / 24389 ? 116 * Math.Cbrt(y) - 16 : y * 24389 / 27;
    }

    /// <summary>Whether a colour is a dark one to draw words on, as its lightness says.</summary>
    public static bool IsDark(uint argb) => Lightness(Opaque(argb)) < 0.6;

    /// <summary>The colour moved away from its grounds in lightness, lighter on dark grounds and darker on light ones, until
    /// it reads on each at <paramref name="least"/>:1; the other way where that way cannot, and the most it can where
    /// neither can.</summary>
    public static uint Legible(uint fore, IReadOnlyList<uint> grounds, double least, bool dark)
    {
        uint? Moved(double step)
        {
            uint colour = fore;
            for (int i = 0; i < 200; i++)
            {
                if (Worst(colour) >= least)
                {
                    return colour;
                }
                colour = Shift(Opaque(colour), step) & 0xFFFFFF | (fore & 0xFF000000);
            }
            return null;
        }
        double Worst(uint colour)
        {
            double worst = double.MaxValue;
            foreach (uint ground in grounds)
            {
                worst = Math.Min(worst, Ratio(colour, ground));
            }
            return worst;
        }
        if (Moved(dark ? 0.006 : -0.006) is { } reads)
        {
            return reads;
        }
        if (Moved(dark ? -0.006 : 0.006) is { } other)
        {
            return other;
        }
        uint white = 0xFFFFFFFF;
        uint black = 0xFF000000;
        return Worst(white) >= Worst(black) ? white : black;
    }

    private static (double L, double A, double B) Lab(uint argb)
    {
        double r = Linear(((argb >> 16) & 0xFF) / 255.0);
        double g = Linear(((argb >> 8) & 0xFF) / 255.0);
        double b = Linear((argb & 0xFF) / 255.0);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static (double R, double G, double B) FromLab(double lightness, double a, double b)
    {
        double l = Math.Pow(lightness + 0.3963377774 * a + 0.2158037573 * b, 3);
        double m = Math.Pow(lightness - 0.1055613458 * a - 0.0638541728 * b, 3);
        double s = Math.Pow(lightness - 0.0894841775 * a - 1.2914855480 * b, 3);
        return (Gamma(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
            Gamma(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
            Gamma(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s));
    }

    private static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    private static double Gamma(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(Math.Max(v, 0), 1 / 2.4) - 0.055;

    private static uint Hex(double r, double g, double b)
    {
        static uint Byte(double v) => (uint)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return 0xFF000000 | (Byte(r) << 16) | (Byte(g) << 8) | Byte(b);
    }
}
