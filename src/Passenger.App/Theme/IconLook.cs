namespace Passenger.App;

/// <summary>How every icon is drawn beside its own colours, as the theme in force gives it in three tokens Passenger's own
/// leaves out (<see cref="ThemeFiles.Optional"/>): its saturation and its brightness, each as CSS's filter of that name
/// works on sRGB (1 as drawn, 0 grey or black), then a colour washed over it at the tint's opacity. Every colour of an icon
/// goes through the same three steps, a brush's and a picture's alike (<see cref="Steps"/>), where icons are drawn
/// (RowIcon.Draw). Its alpha is kept. Pure; the brushes it draws are IconLook.Brushes.cs's.</summary>
public readonly partial record struct IconLook(double Saturation, double Brightness, uint Tint)
{
    /// <summary>The token of an icon's saturation.</summary>
    public const string SaturationToken = "IconSaturation";

    /// <summary>The token of an icon's brightness.</summary>
    public const string BrightnessToken = "IconBrightness";

    /// <summary>The token of the colour washed over every icon, its opacity the wash's strength.</summary>
    public const string TintToken = "IconTint";

    /// <summary>Icons drawn as they are.</summary>
    public static IconLook AsDrawn { get; } = new(1, 1, 0);

    /// <summary>Whether it changes any colour.</summary>
    public bool Changes => Saturation != 1 || Brightness != 1 || (Tint >> 24) != 0;

    /// <summary>The three steps as colour matrices on sRGB from 0 to 1, each four rows of five (red, green, blue, alpha and
    /// what is added), clamped after each: saturation, brightness, then the tint.</summary>
    public IReadOnlyList<float[]> Steps
    {
        get
        {
            double s = Saturation;
            double b = Brightness;
            double k = (Tint >> 24) / 255.0;
            (double tr, double tg, double tb) = (((Tint >> 16) & 0xFF) / 255.0, ((Tint >> 8) & 0xFF) / 255.0, (Tint & 0xFF) / 255.0);
            return
            [
                Matrix(
                    0.213 + (0.787 * s), 0.715 - (0.715 * s), 0.072 - (0.072 * s), 0,
                    0.213 - (0.213 * s), 0.715 + (0.285 * s), 0.072 - (0.072 * s), 0,
                    0.213 - (0.213 * s), 0.715 - (0.715 * s), 0.072 + (0.928 * s), 0),
                Matrix(b, 0, 0, 0, 0, b, 0, 0, 0, 0, b, 0),
                Matrix(1 - k, 0, 0, k * tr, 0, 1 - k, 0, k * tg, 0, 0, 1 - k, k * tb),
            ];

            static float[] Matrix(double rr, double rg, double rb, double ra, double gr, double gg, double gb, double ga, double br, double bg, double bb, double ba) =>
            [
                (float)rr, (float)rg, (float)rb, 0, (float)ra,
                (float)gr, (float)gg, (float)gb, 0, (float)ga,
                (float)br, (float)bg, (float)bb, 0, (float)ba,
                0, 0, 0, 1, 0,
            ];
        }
    }

    /// <summary>A colour, <c>0xAARRGGBB</c>, as the look draws it.</summary>
    public uint Apply(uint argb)
    {
        if (!Changes)
        {
            return argb;
        }
        (double r, double g, double b) = (((argb >> 16) & 0xFF) / 255.0, ((argb >> 8) & 0xFF) / 255.0, (argb & 0xFF) / 255.0);
        foreach (float[] step in Steps)
        {
            (r, g, b) = (Row(step, 0, r, g, b), Row(step, 5, r, g, b), Row(step, 10, r, g, b));
        }
        return (argb & 0xFF000000) | ((uint)Byte(r) << 16) | ((uint)Byte(g) << 8) | Byte(b);

        static double Row(float[] step, int at, double r, double g, double b) => Math.Clamp((step[at] * r) + (step[at + 1] * g) + (step[at + 2] * b) + step[at + 4], 0, 1);

        static byte Byte(double value) => (byte)Math.Round(value * 255);
    }
}
