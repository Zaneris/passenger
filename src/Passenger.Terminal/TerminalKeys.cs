using Avalonia.Input;

namespace Passenger.Terminal;

/// <summary>A key's bytes for the program in the terminal: xterm's, or, once the program asked for the kitty keyboard
/// protocol's first enhancement (<see cref="TerminalScreen.KeyboardFlags"/>), that protocol's, which tell every modifier
/// apart: Shift+Enter is CSI 13;2u, Ctrl+J CSI 106;5u, Esc CSI 27u, a key held with Ctrl or Alt CSI code;mods u by
/// its unshifted character, the keypad's keys by their own numbers. Enter, Tab and Backspace alone stay as xterm
/// sends them, and a key that types sends its text. Null for a key whose text input carries it.</summary>
public static class TerminalKeys
{
    /// <summary>The kitty keyboard protocol's enhancements the keys follow: disambiguate escape codes (1) alone, so a
    /// program asking for more is told only this one is in force.</summary>
    public const int Followed = Disambiguate;

    /// <summary>The kitty keyboard protocol's "disambiguate escape codes" enhancement.</summary>
    public const int Disambiguate = 1;

    /// <summary>The bytes of a key held with these modifiers, the arrows in their application form when the program asked
    /// (xterm's alone), under the kitty keyboard flags in force; <paramref name="altSendsEscape"/> where Alt is a modifier
    /// for the shell rather than the layout's (not macOS's Option, which types).</summary>
    public static string? Of(Key key, KeyModifiers modifiers, PhysicalKey physical, bool applicationCursor, int keyboardFlags, bool altSendsEscape) =>
        (keyboardFlags & Disambiguate) != 0 ? Kitty(key, modifiers, physical, altSendsEscape) : Xterm(key, modifiers, applicationCursor, altSendsEscape);

    /// <summary>xterm's bytes for a key that is not plain text: Enter, the function keys by their number and final
    /// (<see cref="Functional"/>), and Ctrl (or Alt, as a leading ESC) with a letter. Held with none, F1 to F4 take SS3,
    /// as the cursor keys do in the application cursor mode.</summary>
    private static string? Xterm(Key key, KeyModifiers mods, bool applicationCursor, bool altSendsEscape)
    {
        bool ctrl = mods.HasFlag(KeyModifiers.Control);
        bool alt = mods.HasFlag(KeyModifiers.Alt) && altSendsEscape;
        int held = Held(mods);
        string? plain = key switch
        {
            Key.Enter => "\r",
            Key.Back => ctrl ? "\x08" : "\x7f",
            Key.Tab => mods.HasFlag(KeyModifiers.Shift) ? "\x1b[Z" : "\t",
            Key.Escape => "\x1b",
            _ when Functional(key) is (int number, char final) => held == 0 && Ss3(final) ? $"\x1bO{final}" : Csi(number, final, held),
            _ => null,
        };
        if (plain is not null)
        {
            return alt && key is Key.Enter or Key.Back ? "\x1b" + plain : plain;
        }
        if (ctrl && Control(key) is { } control)
        {
            return alt ? "\x1b" + control : control;
        }
        if (alt && key is >= Key.A and <= Key.Z)
        {
            char letter = (char)('a' + (key - Key.A));
            return "\x1b" + (mods.HasFlag(KeyModifiers.Shift) ? char.ToUpperInvariant(letter) : letter);
        }
        return null;

        bool Ss3(char final) => final is >= 'P' and <= 'S' || (applicationCursor && final is >= 'A' and <= 'H');
    }

    /// <summary>The modifiers held, as both xterm and the kitty protocol count them before adding 1: Shift 1, Alt 2,
    /// Ctrl 4 and Meta (the protocol's Super) 8.</summary>
    private static int Held(KeyModifiers mods) =>
        (mods.HasFlag(KeyModifiers.Shift) ? 1 : 0) + (mods.HasFlag(KeyModifiers.Alt) ? 2 : 0)
        + (mods.HasFlag(KeyModifiers.Control) ? 4 : 0) + (mods.HasFlag(KeyModifiers.Meta) ? 8 : 0);

    /// <summary>A function key's number and final, as xterm and the kitty protocol both send it held with modifiers
    /// (CSI number;modifiers final): the cursor keys and the keypad's Begin (its 5 with Num Lock off, which Avalonia names
    /// Clear) by a letter, the editing keys and F5 to F12 by their numbers before ~, F1 to F4 by a letter.</summary>
    private static (int Number, char Final)? Functional(Key key) => key switch
    {
        Key.Up => (1, 'A'),
        Key.Down => (1, 'B'),
        Key.Right => (1, 'C'),
        Key.Left => (1, 'D'),
        Key.Clear => (1, 'E'),
        Key.End => (1, 'F'),
        Key.Home => (1, 'H'),
        Key.Insert => (2, '~'),
        Key.Delete => (3, '~'),
        Key.PageUp => (5, '~'),
        Key.PageDown => (6, '~'),
        Key.F1 => (1, 'P'),
        Key.F2 => (1, 'Q'),
        Key.F3 => (1, 'R'),
        Key.F4 => (1, 'S'),
        Key.F5 => (15, '~'),
        Key.F6 => (17, '~'),
        Key.F7 => (18, '~'),
        Key.F8 => (19, '~'),
        Key.F9 => (20, '~'),
        Key.F10 => (21, '~'),
        Key.F11 => (23, '~'),
        Key.F12 => (24, '~'),
        _ => null,
    };

    /// <summary>The control character Ctrl makes of a key: Ctrl+A is 1 through Ctrl+Z 26, and the few punctuation keys
    /// terminals give the rest of C0 to.</summary>
    private static string? Control(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)(key - Key.A + 1)).ToString(),
        Key.Space or Key.D2 => "\0",
        Key.OemOpenBrackets or Key.D3 => "\x1b",
        Key.OemPipe or Key.D4 => "\x1c",
        Key.OemCloseBrackets or Key.D5 => "\x1d",
        Key.D6 => "\x1e",
        Key.OemMinus or Key.D7 or Key.OemQuestion => "\x1f",
        Key.D8 => "\x7f",
        _ => null,
    };

    /// <summary>The kitty keyboard protocol's bytes for a key under "disambiguate escape codes": every key that types no
    /// text as CSI number;modifiers u (or ~, or CSI 1;modifiers and a letter), the modifiers 1 more than Shift 1, Alt 2,
    /// Ctrl 4 and Super 8 added, and left out when there are none.</summary>
    private static string? Kitty(Key key, KeyModifiers mods, PhysicalKey physical, bool altSendsEscape)
    {
        int held = Held(mods);
        if (Keypad(key, physical) is { } pad)
        {
            return Csi(pad.Number, pad.Final, held);
        }
        string? functional = key switch
        {
            Key.Enter => held == 0 ? "\r" : Csi(13, 'u', held),
            Key.Tab => held == 0 ? "\t" : Csi(9, 'u', held),
            Key.Back => held == 0 ? "\x7f" : Csi(127, 'u', held),
            Key.Escape => Csi(27, 'u', held),
            // CSI R is also the cursor's place reported, so the protocol moves F3 to a number of its own.
            Key.F3 => Csi(13, '~', held),
            _ when Functional(key) is (int number, char final) => Csi(number, final, held),
            >= Key.F13 and <= Key.F24 => Csi(57376 + (key - Key.F13), 'u', held),
            Key.PrintScreen => Csi(57361, 'u', held),
            Key.Scroll => Csi(57359, 'u', held),
            Key.Pause => Csi(57362, 'u', held),
            Key.Apps => Csi(57363, 'u', held),
            Key.MediaPlayPause => Csi(57430, 'u', held),
            Key.MediaStop => Csi(57432, 'u', held),
            Key.MediaNextTrack => Csi(57435, 'u', held),
            Key.MediaPreviousTrack => Csi(57436, 'u', held),
            Key.VolumeDown => Csi(57438, 'u', held),
            Key.VolumeUp => Csi(57439, 'u', held),
            Key.VolumeMute => Csi(57440, 'u', held),
            _ => null,
        };
        if (functional is not null)
        {
            return functional;
        }
        bool typesText = !mods.HasFlag(KeyModifiers.Control) && !(mods.HasFlag(KeyModifiers.Alt) && altSendsEscape);
        return typesText || Unshifted(key) is not { } code ? null : Csi(code, 'u', held);
    }

    /// <summary>A key's sequence: CSI number;modifiers and its final, the modifiers left out with none held, and the
    /// number too where it is 1 and the final a letter.</summary>
    private static string Csi(int number, char final, int held) =>
        held != 0 ? $"\x1b[{number};{held + 1}{final}"
        : number == 1 && final is not ('u' or '~') ? $"\x1b[{final}"
        : $"\x1b[{number}{final}";

    /// <summary>The number of a key that types, held with Ctrl or Alt: its unshifted character, the letter for a
    /// letter in any layout and a US layout's for the rest, or the keypad's own number for a keypad key.</summary>
    private static int? Unshifted(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 'a' + (key - Key.A),
        >= Key.D0 and <= Key.D9 => '0' + (key - Key.D0),
        Key.Space => ' ',
        Key.OemMinus => '-',
        Key.OemPlus => '=',
        Key.OemOpenBrackets => '[',
        Key.OemCloseBrackets => ']',
        Key.OemPipe => '\\',
        Key.OemSemicolon => ';',
        Key.OemQuotes => '\'',
        Key.OemComma => ',',
        Key.OemPeriod => '.',
        Key.OemQuestion => '/',
        Key.OemTilde => '`',
        >= Key.NumPad0 and <= Key.NumPad9 => 57399 + (key - Key.NumPad0),
        Key.Decimal => 57409,
        Key.Divide => 57410,
        Key.Multiply => 57411,
        Key.Subtract => 57412,
        Key.Add => 57413,
        _ => null,
    };

    /// <summary>A keypad key that types no text, by where it is: its Enter, and with Num Lock off the moves its digits
    /// give (Avalonia names those as the moves themselves), each by the protocol's own number for it; its 5, Begin, has
    /// none and goes as <see cref="Functional"/> names it.</summary>
    private static (int Number, char Final)? Keypad(Key key, PhysicalKey physical) => physical switch
    {
        PhysicalKey.NumPadEnter => (57414, 'u'),
        _ when key is >= Key.NumPad0 and <= Key.NumPad9 or Key.Decimal => null,
        PhysicalKey.NumPad0 => (57425, 'u'),
        PhysicalKey.NumPad1 => (57424, 'u'),
        PhysicalKey.NumPad2 => (57420, 'u'),
        PhysicalKey.NumPad3 => (57422, 'u'),
        PhysicalKey.NumPad4 => (57417, 'u'),
        PhysicalKey.NumPad6 => (57418, 'u'),
        PhysicalKey.NumPad7 => (57423, 'u'),
        PhysicalKey.NumPad8 => (57419, 'u'),
        PhysicalKey.NumPad9 => (57421, 'u'),
        PhysicalKey.NumPadDecimal => (57426, 'u'),
        _ => null,
    };
}
