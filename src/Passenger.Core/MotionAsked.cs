using System.Globalization;

namespace Passenger.Core;

/// <summary>How a caret blinks: shown for <see cref="Shown"/>, then hidden for <see cref="Hidden"/>, over and over; or,
/// with either zero, never hidden (<see cref="Steady"/>).</summary>
public sealed record CaretBlink(TimeSpan Shown, TimeSpan Hidden)
{
    /// <summary>A caret that stays shown.</summary>
    public static readonly CaretBlink Steady = new(TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>Passenger's own blink, where the system names none: half a second and a little more each way.</summary>
    public static readonly CaretBlink Default = new(TimeSpan.FromMilliseconds(530), TimeSpan.FromMilliseconds(530));

    /// <summary>Whether the caret is ever hidden.</summary>
    public bool Blinks => Shown > TimeSpan.Zero && Hidden > TimeSpan.Zero;

    /// <summary>How long the caret stays as it is: <see cref="Shown"/> while shown, <see cref="Hidden"/> while hidden.</summary>
    public TimeSpan While(bool shown) => shown ? Shown : Hidden;
}

/// <summary>What the system asks of motion and carets: whether what moves of itself stands still, and how a caret blinks;
/// each system's answers read into it by <see cref="OfMac"/>, <see cref="OfWindows"/> and <see cref="OfGnome"/>, and a
/// stand-in's words by <see cref="Parse"/>.</summary>
public sealed record MotionAsked(bool ReducesMotion, CaretBlink Blink)
{
    /// <summary>What Passenger does where the system asks nothing.</summary>
    public static readonly MotionAsked Default = new(false, CaretBlink.Default);

    /// <summary>GetCaretBlinkTime's answer for a caret that never blinks (INFINITE).</summary>
    public const uint WindowsNeverBlinks = uint.MaxValue;

    public override string ToString() => $"{(ReducesMotion ? "less motion" : "full motion")}, "
        + (Blink.Blinks ? $"carets blinking {Blink.Shown.TotalMilliseconds:0} ms shown and {Blink.Hidden.TotalMilliseconds:0} ms hidden" : "carets steady");

    /// <summary>Reads a stand-in's words: <c>full</c> or <c>reduced</c> and <c>blink=none</c>, <c>blink=MS</c> or
    /// <c>blink=SHOWN/HIDDEN</c>, comma-separated, each left out standing as Passenger's own; throws
    /// <see cref="FormatException"/> on any other.</summary>
    public static MotionAsked Parse(string value)
    {
        bool reduces = false;
        CaretBlink blink = CaretBlink.Default;
        foreach (string part in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part is "full" or "reduced")
            {
                reduces = part == "reduced";
            }
            else if (part.StartsWith("blink=", StringComparison.Ordinal) && BlinkOf(part["blink=".Length..]) is { } given)
            {
                blink = given;
            }
            else
            {
                throw new FormatException($"takes full or reduced and blink=none, blink=MS or blink=SHOWN/HIDDEN, comma-separated, not '{value}'");
            }
        }
        return new MotionAsked(reduces, blink);

        static CaretBlink? BlinkOf(string said)
        {
            if (said == "none")
            {
                return CaretBlink.Steady;
            }
            string[] halves = said.Split('/');
            int[] ms = new int[halves.Length];
            for (int i = 0; i < halves.Length; i++)
            {
                if (halves.Length > 2 || !int.TryParse(halves[i], NumberStyles.None, CultureInfo.InvariantCulture, out ms[i]) || ms[i] == 0)
                {
                    return null;
                }
            }
            return new CaretBlink(TimeSpan.FromMilliseconds(ms[0]), TimeSpan.FromMilliseconds(ms[^1]));
        }
    }

    /// <summary>What macOS's answers come to: its Reduce Motion, its non-blinking cursor, and its insertion point's periods
    /// shown and hidden in milliseconds, each null where unset, leaving Passenger's.</summary>
    public static MotionAsked OfMac(bool reducesMotion, bool steady, double? shownMs, double? hiddenMs) => new(reducesMotion,
        steady ? CaretBlink.Steady : new CaretBlink(Period(shownMs) ?? CaretBlink.Default.Shown, Period(hiddenMs) ?? CaretBlink.Default.Hidden));

    private static TimeSpan? Period(double? ms) => ms is > 0 and < int.MaxValue ? TimeSpan.FromMilliseconds(ms.Value) : null;

    /// <summary>What Windows' answers come to: its animations, and its caret's time each way in milliseconds, none for
    /// INFINITE and Passenger's where it could not say (0).</summary>
    public static MotionAsked OfWindows(bool animates, uint blinkTime) => new(!animates,
        blinkTime == WindowsNeverBlinks ? CaretBlink.Steady
        : blinkTime == 0 ? CaretBlink.Default
        : new CaretBlink(TimeSpan.FromMilliseconds(blinkTime), TimeSpan.FromMilliseconds(blinkTime)));

    /// <summary>What <c>gsettings list-recursively org.gnome.desktop.interface</c> says: animations off for less motion,
    /// the cursor not blinking for a steady caret, else half its blink's cycle each way; Passenger's where it says nothing.</summary>
    public static MotionAsked OfGnome(string listing)
    {
        bool reduces = false;
        bool blinks = true;
        int? cycle = null;
        foreach (string line in listing.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[0] != "org.gnome.desktop.interface")
            {
                continue;
            }
            // A number gsettings lists may carry its type first ("uint32 1200").
            string value = parts[2].Split(' ')[^1];
            switch (parts[1])
            {
                case "enable-animations":
                    reduces = value == "false";
                    break;
                case "cursor-blink":
                    blinks = value != "false";
                    break;
                case "cursor-blink-time" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int ms) && ms > 0:
                    cycle = ms;
                    break;
            }
        }
        CaretBlink blink = !blinks ? CaretBlink.Steady
            : cycle is { } whole ? new CaretBlink(TimeSpan.FromMilliseconds(whole / 2.0), TimeSpan.FromMilliseconds(whole / 2.0))
            : CaretBlink.Default;
        return new MotionAsked(reduces, blink);
    }
}
