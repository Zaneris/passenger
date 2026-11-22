using Avalonia.Input;
using Passenger.Terminal;

namespace Passenger.UnitTests.Terminal;

/// <summary>A key's bytes as the program in the terminal reads them: xterm's until it asks for the kitty keyboard
/// protocol, every one as before; then the protocol's "disambiguate escape codes", which tells Enter, Tab, Backspace and
/// Esc held with any modifier apart from the key alone, and Ctrl+J from Enter's line feed. A function key held with
/// modifiers is its one number and final either way, each modifier, Meta too, counted alike. The keypad's 5 with Num Lock
/// off is Begin, which X11 names no key for.</summary>
public class TerminalKeysTests
{
    private const KeyModifiers None = KeyModifiers.None;
    private const KeyModifiers Shift = KeyModifiers.Shift;
    private const KeyModifiers Ctrl = KeyModifiers.Control;
    private const KeyModifiers Alt = KeyModifiers.Alt;
    private const KeyModifiers Super = KeyModifiers.Meta;
    private const int Kitty = TerminalKeys.Disambiguate;

    /// <summary>A key pressed as the terminal hears it, and the bytes it is to send, null for none (its text typing it).</summary>
    private sealed record Press(string Name, Key Key, KeyModifiers Held, string? Bytes, int Flags = 0, PhysicalKey Physical = PhysicalKey.None,
        string? Symbol = null, bool ApplicationCursor = false, bool AltSendsEscape = true, bool BeginNamed = false)
    {
        public string? Sent => TerminalKeys.Of(Key, Held, Physical, Symbol, ApplicationCursor, Flags, AltSendsEscape, BeginNamed);
    }

    [Fact]
    public void Without_the_protocol_a_key_sends_xterms_bytes_as_before() => Cases.Each<Press>(
    [
        new("Enter", Key.Enter, None, "\r"),
        new("Ctrl+Enter", Key.Enter, Ctrl, "\r"),
        new("Alt+Enter, Alt sending Escape", Key.Enter, Alt, "\e\r"),
        new("Alt+Enter, Alt typing (the Mac's Option)", Key.Enter, Alt, "\r", AltSendsEscape: false),
        new("Ctrl+J", Key.J, Ctrl, "\n"),
        new("Up in the application cursor mode", Key.Up, None, "\eOA", ApplicationCursor: true),
        new("F3", Key.F3, None, "\eOR"),
        new("Ctrl+F3", Key.F3, Ctrl, "\e[1;5R"),
        new("Meta+Up", Key.Up, Super, "\e[1;9A"),
        new("every modifier with F12", Key.F12, Super | Ctrl | Alt | Shift, "\e[24;16~"),
        new("the keypad's 5 with Num Lock off (Begin)", Key.Clear, None, "\e[E", Physical: PhysicalKey.NumPad5),
        new("Begin in the application cursor mode", Key.Clear, None, "\eOE", Physical: PhysicalKey.NumPad5, ApplicationCursor: true),
        new("Shift+Begin", Key.Clear, Shift, "\e[1;2E", Physical: PhysicalKey.NumPad5),
        new("X11's Begin, the keypad's 5 with no symbol", Key.NumPad5, None, "\e[E", Physical: PhysicalKey.NumPad5),
        new("X11's keypad 5 with Num Lock on, its symbol typing it", Key.NumPad5, None, null, Physical: PhysicalKey.NumPad5, Symbol: "5"),
    ], press => Assert.True(press.Sent == press.Bytes,
        $"{press.Name} sent {Shown(press.Sent)} to a program that never asked for the kitty protocol, where xterm sends {Shown(press.Bytes)}: the program reads another key, or none"),
        press => press.Name);

    [Fact]
    public void Under_the_protocol_a_key_sends_its_forms_and_one_that_types_leaves_it_to_its_text() => Cases.Each<Press>(
    [
        new("Enter alone", Key.Enter, None, "\r", Kitty),
        new("Shift+Enter", Key.Enter, Shift, "\e[13;2u", Kitty),
        new("Ctrl+Alt+Shift+Enter", Key.Enter, Ctrl | Alt | Shift, "\e[13;8u", Kitty),
        new("Meta+Enter", Key.Enter, Super, "\e[13;9u", Kitty),
        new("Alt+Enter, Alt typing", Key.Enter, Alt, "\e[13;3u", Kitty, AltSendsEscape: false),
        new("Ctrl+J, apart from Enter's line feed", Key.J, Ctrl, "\e[106;5u", Kitty),
        new("Shift+Tab", Key.Tab, Shift, "\e[9;2u", Kitty),
        new("Ctrl+Backspace", Key.Back, Ctrl, "\e[127;5u", Kitty),
        new("Esc", Key.Escape, None, "\e[27u", Kitty),
        new("Ctrl+Space", Key.Space, Ctrl, "\e[32;5u", Kitty),
        new("Ctrl+C", Key.C, Ctrl, "\e[99;5u", Kitty),
        new("Ctrl+Meta+A", Key.A, Ctrl | Super, "\e[97;13u", Kitty),
        new("Ctrl+[", Key.OemOpenBrackets, Ctrl, "\e[91;5u", Kitty),
        new("Ctrl and the keypad's 1", Key.NumPad1, Ctrl, "\e[57400;5u", Kitty),
        new("Up, in the application cursor mode too", Key.Up, None, "\e[A", Kitty, ApplicationCursor: true),
        new("Alt+Delete", Key.Delete, Alt, "\e[3;3~", Kitty),
        new("F1", Key.F1, None, "\e[P", Kitty),
        new("Ctrl+F3", Key.F3, Ctrl, "\e[13;5~", Kitty),
        new("F13", Key.F13, None, "\e[57376u", Kitty),
        new("the menu key", Key.Apps, None, "\e[57363u", Kitty),
        new("the keypad's Enter", Key.Enter, None, "\e[57414u", Kitty, PhysicalKey.NumPadEnter),
        new("X11's Begin with Ctrl+Alt", Key.NumPad5, Ctrl | Alt, "\e[1;7E", Kitty, PhysicalKey.NumPad5),
        new("Ctrl and the keypad's 5 with Num Lock on", Key.NumPad5, Ctrl, "\e[57404;5u", Kitty, PhysicalKey.NumPad5, "5"),
        new("Alt and the keypad's 5 where the platform names Begin", Key.NumPad5, Alt, "\e[57404;3u", Kitty, PhysicalKey.NumPad5, BeginNamed: true),
        new("a", Key.A, None, null, Kitty, Symbol: "a"),
        new("Shift+a", Key.A, Shift, null, Kitty, Symbol: "a"),
        new("Alt+a, Alt typing", Key.A, Alt, null, Kitty, Symbol: "a", AltSendsEscape: false),
    ], press => Assert.True(press.Sent == press.Bytes,
        $"{press.Name} under the kitty protocol sent {Shown(press.Sent)}, where the protocol gives {Shown(press.Bytes)}: a program binding it reads another key, or the text twice"),
        press => press.Name);

    [Fact]
    public void A_function_key_held_with_any_modifier_is_sent_alike_with_the_protocol_or_without() => Cases.Each(
        [Key.Up, Key.Home, Key.Delete, Key.PageUp, Key.F1, Key.F5, Key.F12], key =>
        {
            foreach (KeyModifiers held in (KeyModifiers[])[Shift, Alt, Ctrl, Super, Ctrl | Alt | Shift | Super])
            {
                string? legacy = new Press("", key, held, null).Sent;
                string? kitty = new Press("", key, held, null, Kitty).Sent;
                Assert.True(legacy == kitty, $"{held} {key} sent {Shown(legacy)} without the kitty protocol and {Shown(kitty)} with it, where both give its number and final with the modifiers");
            }
        });

    private static string Shown(string? bytes) => bytes is null ? "nothing" : $"\"{bytes.Replace("\e", "ESC", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal).Replace("\x7f", "DEL", StringComparison.Ordinal)}\"";
}
