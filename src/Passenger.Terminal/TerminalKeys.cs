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

    /// <summary>xterm's bytes for a key that is not plain text: Enter, the arrows and the keypad's keys, the function keys,
    /// and Ctrl (or Alt, as a leading ESC) with a letter.</summary>
    private static string? Xterm(Key key, KeyModifiers mods, bool applicationCursor, bool altSendsEscape)
    {
        bool ctrl = mods.HasFlag(KeyModifiers.Control);
        bool alt = mods.HasFlag(KeyModifiers.Alt) && altSendsEscape;
        string? plain = key switch
        {
            Key.Enter => "\r",
            Key.Back => ctrl ? "\x08" : "\x7f",
            Key.Tab => mods.HasFlag(KeyModifiers.Shift) ? "\x1b[Z" : "\t",
            Key.Escape => "\x1b",
            Key.Up => Cursor('A'),
            Key.Down => Cursor('B'),
            Key.Right => Cursor('C'),
            Key.Left => Cursor('D'),
            Key.Home => Cursor('H'),
            Key.End => Cursor('F'),
            Key.Insert => Tilde(2),
            Key.Delete => Tilde(3),
            Key.PageUp => Tilde(5),
            Key.PageDown => Tilde(6),
            Key.F1 => Function('P'),
            Key.F2 => Function('Q'),
            Key.F3 => Function('R'),
            Key.F4 => Function('S'),
            Key.F5 => Tilde(15),
            Key.F6 => Tilde(17),
            Key.F7 => Tilde(18),
            Key.F8 => Tilde(19),
            Key.F9 => Tilde(20),
            Key.F10 => Tilde(21),
            Key.F11 => Tilde(23),
            Key.F12 => Tilde(24),
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

        int Modifier() => 1 + (mods.HasFlag(KeyModifiers.Shift) ? 1 : 0) + (mods.HasFlag(KeyModifiers.Alt) ? 2 : 0) + (ctrl ? 4 : 0);
        string Cursor(char final) => Modifier() > 1 ? $"\x1b[1;{Modifier()}{final}" : applicationCursor ? $"\x1bO{final}" : $"\x1b[{final}";
        string Tilde(int code) => Modifier() > 1 ? $"\x1b[{code};{Modifier()}~" : $"\x1b[{code}~";
        string Function(char final) => Modifier() > 1 ? $"\x1b[1;{Modifier()}{final}" : $"\x1bO{final}";
    }

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
        int held = (mods.HasFlag(KeyModifiers.Shift) ? 1 : 0) + (mods.HasFlag(KeyModifiers.Alt) ? 2 : 0)
            + (mods.HasFlag(KeyModifiers.Control) ? 4 : 0) + (mods.HasFlag(KeyModifiers.Meta) ? 8 : 0);
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
            Key.Up => Csi(1, 'A', held),
            Key.Down => Csi(1, 'B', held),
            Key.Right => Csi(1, 'C', held),
            Key.Left => Csi(1, 'D', held),
            Key.Home => Csi(1, 'H', held),
            Key.End => Csi(1, 'F', held),
            Key.Clear => Csi(1, 'E', held),
            Key.Insert => Csi(2, '~', held),
            Key.Delete => Csi(3, '~', held),
            Key.PageUp => Csi(5, '~', held),
            Key.PageDown => Csi(6, '~', held),
            Key.F1 => Csi(1, 'P', held),
            Key.F2 => Csi(1, 'Q', held),
            Key.F3 => Csi(13, '~', held),
            Key.F4 => Csi(1, 'S', held),
            Key.F5 => Csi(15, '~', held),
            Key.F6 => Csi(17, '~', held),
            Key.F7 => Csi(18, '~', held),
            Key.F8 => Csi(19, '~', held),
            Key.F9 => Csi(20, '~', held),
            Key.F10 => Csi(21, '~', held),
            Key.F11 => Csi(23, '~', held),
            Key.F12 => Csi(24, '~', held),
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
    /// give (Avalonia names those as the moves themselves), each by the protocol's own number for it.</summary>
    private static (int Number, char Final)? Keypad(Key key, PhysicalKey physical) => physical switch
    {
        PhysicalKey.NumPadEnter => (57414, 'u'),
        _ when key is >= Key.NumPad0 and <= Key.NumPad9 or Key.Decimal => null,
        PhysicalKey.NumPad0 => (57425, 'u'),
        PhysicalKey.NumPad1 => (57424, 'u'),
        PhysicalKey.NumPad2 => (57420, 'u'),
        PhysicalKey.NumPad3 => (57422, 'u'),
        PhysicalKey.NumPad4 => (57417, 'u'),
        PhysicalKey.NumPad5 => (1, 'E'),
        PhysicalKey.NumPad6 => (57418, 'u'),
        PhysicalKey.NumPad7 => (57423, 'u'),
        PhysicalKey.NumPad8 => (57419, 'u'),
        PhysicalKey.NumPad9 => (57421, 'u'),
        PhysicalKey.NumPadDecimal => (57426, 'u'),
        _ => null,
    };
}
