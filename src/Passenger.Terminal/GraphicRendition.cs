namespace Passenger.Terminal;

/// <summary>SGR (CSI … m), the one reading of a program's colours and text styles, which the terminal's screen and the
/// consoles both take: the sixteen colours, the 256 and 24-bit ones in either form (38;5;N and 38:2::R:G:B), bold, faint,
/// italic, underline, inverse, hidden and struck through, each set and reset.</summary>
public static class GraphicRendition
{
    /// <summary>A style with an SGR's parameters applied to it.</summary>
    public static CellStyle Apply(CellStyle style, SequenceParameters groups)
    {
        if (groups.Count == 0)
        {
            return CellStyle.Plain;
        }
        for (int i = 0; i < groups.Count; i++)
        {
            int code = groups[i, 0] >= 0 ? groups[i, 0] : 0;
            CellFlags flags = style.Flags;
            switch (code)
            {
                case 0:
                    style = CellStyle.Plain;
                    continue;
                case 1:
                    flags |= CellFlags.Bold;
                    break;
                case 2:
                    flags |= CellFlags.Dim;
                    break;
                case 3:
                    flags |= CellFlags.Italic;
                    break;
                case 4:
                    flags = groups.Parts(i) > 1 && groups[i, 1] == 0 ? flags & ~CellFlags.Underline : flags | CellFlags.Underline;
                    break;
                case 7:
                    flags |= CellFlags.Inverse;
                    break;
                case 8:
                    flags |= CellFlags.Hidden;
                    break;
                case 9:
                    flags |= CellFlags.Strike;
                    break;
                case 21:
                    flags |= CellFlags.Underline;
                    break;
                case 22:
                    flags &= ~(CellFlags.Bold | CellFlags.Dim);
                    break;
                case 23:
                    flags &= ~CellFlags.Italic;
                    break;
                case 24:
                    flags &= ~CellFlags.Underline;
                    break;
                case 27:
                    flags &= ~CellFlags.Inverse;
                    break;
                case 28:
                    flags &= ~CellFlags.Hidden;
                    break;
                case 29:
                    flags &= ~CellFlags.Strike;
                    break;
                case >= 30 and <= 37:
                    style = style with { Fore = code - 30 };
                    continue;
                case 38:
                    if (Extended(groups, ref i) is int fore)
                    {
                        style = style with { Fore = fore };
                    }
                    continue;
                case 39:
                    style = style with { Fore = CellStyle.Default };
                    continue;
                case >= 40 and <= 47:
                    style = style with { Back = code - 40 };
                    continue;
                case 48:
                    if (Extended(groups, ref i) is int back)
                    {
                        style = style with { Back = back };
                    }
                    continue;
                case 49:
                    style = style with { Back = CellStyle.Default };
                    continue;
                case 58:
                    // The underline's own colour: read past it, since it is not drawn.
                    _ = Extended(groups, ref i);
                    continue;
                case >= 90 and <= 97:
                    style = style with { Fore = code - 90 + 8 };
                    continue;
                case >= 100 and <= 107:
                    style = style with { Back = code - 100 + 8 };
                    continue;
            }
            style = style with { Flags = flags };
        }
        return style;
    }

    /// <summary>A colour after 38 or 48: 5;N from the palette or 2;R;G;B, in its own group's parts (38:2::R:G:B) or in
    /// the groups after it (38;2;R;G;B), which it then consumes.</summary>
    private static int? Extended(SequenceParameters groups, ref int i)
    {
        int parts = groups.Parts(i);
        if (parts > 1)
        {
            if (groups[i, 1] == 5 && parts > 2)
            {
                return Math.Clamp(groups[i, 2], 0, 255);
            }
            if (groups[i, 1] == 2 && parts >= 5)
            {
                // 38:2:R:G:B, or 38:2:SPACE:R:G:B with a colour space first.
                int first = parts >= 6 ? 3 : 2;
                return CellStyle.FromRgb(Byte(groups[i, first]), Byte(groups[i, first + 1]), Byte(groups[i, first + 2]));
            }
            return null;
        }
        int at = i;
        switch (Next(groups, at + 1))
        {
            case 5:
                int index = Next(groups, at + 2);
                i += 2;
                return index >= 0 ? Math.Min(index, 255) : null;
            case 2:
                int r = Next(groups, at + 2);
                int g = Next(groups, at + 3);
                int b = Next(groups, at + 4);
                i += 4;
                return CellStyle.FromRgb(Byte(r), Byte(g), Byte(b));
            default:
                return null;
        }

        static int Next(SequenceParameters groups, int index) => index < groups.Count ? groups[index, 0] : -1;

        static int Byte(int value) => Math.Clamp(value, 0, 255);
    }
}
