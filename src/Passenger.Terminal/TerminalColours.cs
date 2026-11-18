using Passenger.Editor;

namespace Passenger.Terminal;

/// <summary>What a cell's colour (<see cref="CellStyle"/>) is drawn in, the terminal's and a console's alike: the theme's
/// sixteen named colours by their tokens, xterm's 6×6×6 cube and grey ramp for the rest of the 256, a 24-bit colour as
/// given; how much of its colour faint text keeps; and a style as an editor draws it (<see cref="Look"/>).</summary>
public static class TerminalColours
{
    /// <summary>How opaque faint (SGR 2) text is drawn.</summary>
    public const double Faint = 0.6;

    private static readonly string[] Named =
    [
        TokenKeys.Terminal_Ansi0, TokenKeys.Terminal_Ansi1, TokenKeys.Terminal_Ansi2, TokenKeys.Terminal_Ansi3,
        TokenKeys.Terminal_Ansi4, TokenKeys.Terminal_Ansi5, TokenKeys.Terminal_Ansi6, TokenKeys.Terminal_Ansi7,
        TokenKeys.Terminal_Ansi8, TokenKeys.Terminal_Ansi9, TokenKeys.Terminal_Ansi10, TokenKeys.Terminal_Ansi11,
        TokenKeys.Terminal_Ansi12, TokenKeys.Terminal_Ansi13, TokenKeys.Terminal_Ansi14, TokenKeys.Terminal_Ansi15,
    ];

    /// <summary>The theme's token for one of the sixteen named colours; null for any other colour.</summary>
    public static string? Token(int colour) => colour is >= 0 and < 16 ? Named[colour] : null;

    /// <summary>A colour past the sixteen as its red, green and blue (0xRRGGBB); null for the default and the named ones.</summary>
    public static int? Rgb(int colour)
    {
        if (colour == CellStyle.Default || colour is >= 0 and < 16)
        {
            return null;
        }
        if ((colour & CellStyle.Rgb) != 0)
        {
            return colour & 0xFFFFFF;
        }
        if (colour < 232)
        {
            int index = colour - 16;
            static int Level(int step) => step == 0 ? 0 : 55 + (step * 40);
            return (Level(index / 36) << 16) | (Level(index / 6 % 6) << 8) | Level(index % 6);
        }
        int grey = 8 + ((colour - 232) * 10);
        return (grey << 16) | (grey << 8) | grey;
    }

    /// <summary>A style as an editor draws it (a console's), its default colours the tokens <paramref name="text"/> and
    /// <paramref name="ground"/> name, as inverse and hidden text need: no ground where it asks for none.</summary>
    public static TextLook Look(CellStyle style, string text, string ground)
    {
        LookColour fore = Of(style.Fore);
        LookColour back = Of(style.Back);
        CellFlags flags = style.Flags;
        if (flags.HasFlag(CellFlags.Inverse))
        {
            (fore, back) = (back.IsNone ? LookColour.Of(ground) : back, fore.IsNone ? LookColour.Of(text) : fore);
        }
        else if (fore.IsNone)
        {
            fore = LookColour.Of(text);
        }
        if (flags.HasFlag(CellFlags.Hidden))
        {
            fore = back.IsNone ? LookColour.Of(ground) : back;
        }
        LookStyle set = (flags.HasFlag(CellFlags.Bold) ? LookStyle.Bold : LookStyle.None) | (flags.HasFlag(CellFlags.Italic) ? LookStyle.Italic : LookStyle.None)
            | (flags.HasFlag(CellFlags.Underline) ? LookStyle.Underline : LookStyle.None) | (flags.HasFlag(CellFlags.Strike) ? LookStyle.Strike : LookStyle.None);
        return new TextLook(fore, back, set, flags.HasFlag(CellFlags.Dim) ? Faint : 1);

        static LookColour Of(int colour) => Token(colour) is { } token ? LookColour.Of(token) : Rgb(colour) is { } rgb ? LookColour.Of(rgb) : default;
    }
}
