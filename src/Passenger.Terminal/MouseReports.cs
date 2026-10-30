using System.Globalization;
using System.Text;

namespace Passenger.Terminal;

/// <summary>A mouse report's bytes as xterm writes them for a program that asked for the mouse: SGR's form (1006),
/// ESC [ &lt; button ; column ; row and M (m for a release), or else the original, ESC [ M and a byte each for the button,
/// the column and the row, 32 past their values, which names no column or row past 223. Its bytes go to the program as
/// they are: a byte past 127 is a place, never a character to encode.</summary>
public static class MouseReports
{
    /// <summary>The original form's button of every release, which names none.</summary>
    private const int NoButton = 3;

    /// <summary>The furthest column or row the original form names: a byte holds 255, 32 past it.</summary>
    private const int OriginalLimit = 255 - 32;

    /// <summary>The report of a button (0 left, 1 middle, 2 right, 64 and 65 the wheel) at a column and row counted
    /// from 1; null in the original form past the column or row it names.</summary>
    public static byte[]? Of(int button, int column, int row, bool release, bool sgr)
    {
        if (sgr)
        {
            return Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"\e[<{button};{column};{row}{(release ? 'm' : 'M')}"));
        }
        if (column > OriginalLimit || row > OriginalLimit)
        {
            return null;
        }
        return [0x1b, (byte)'[', (byte)'M', (byte)(32 + (release ? NoButton : button)), (byte)(32 + column), (byte)(32 + row)];
    }
}
