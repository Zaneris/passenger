using Avalonia.Input;
using Passenger.Terminal;

namespace Passenger.UnitTests.Terminal;

/// <summary>A key's bytes as the program in the terminal reads them: xterm's until it asks for the kitty keyboard
/// protocol, every one as before; then the protocol's "disambiguate escape codes", which tells Enter, Tab, Backspace and
/// Esc held with any modifier apart from the key alone, and Ctrl+J from Enter's line feed. A function key held with
/// modifiers is its one number and final either way, each modifier, Meta too, counted alike.</summary>
public class TerminalKeysTests
{
    private const KeyModifiers Shift = KeyModifiers.Shift;
    private const KeyModifiers Ctrl = KeyModifiers.Control;
    private const KeyModifiers Alt = KeyModifiers.Alt;
    private const KeyModifiers Super = KeyModifiers.Meta;

    [Theory]
    [InlineData(KeyModifiers.None, true, "\r")]
    [InlineData(Shift, true, "\r")]
    [InlineData(Ctrl, true, "\r")]
    [InlineData(Ctrl | Shift, true, "\r")]
    [InlineData(Alt, true, "\e\r")]
    [InlineData(Alt | Shift, true, "\e\r")]
    [InlineData(Alt, false, "\r")]
    public void Without_the_protocol_Enter_with_any_modifier_is_a_carriage_return_as_before(KeyModifiers held, bool altSendsEscape, string bytes) =>
        Assert.True(Legacy(Key.Enter, held, altSendsEscape) == bytes,
            $"Enter with {held} sent {Shown(Legacy(Key.Enter, held, altSendsEscape))} to a program that never asked for the kitty protocol, where it sent {Shown(bytes)}");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ctrl_J_is_a_line_feed_without_the_protocol_and_its_own_key_with_it(bool altSendsEscape)
    {
        Assert.True(Legacy(Key.J, Ctrl, altSendsEscape) == "\n", $"Ctrl+J sent {Shown(Legacy(Key.J, Ctrl, altSendsEscape))}: a program reading a newline from it gets none");
        Assert.True(Kitty(Key.J, Ctrl, altSendsEscape) == "\e[106;5u",
            $"Ctrl+J under the kitty protocol sent {Shown(Kitty(Key.J, Ctrl, altSendsEscape))}: the program cannot tell it from Enter's line feed");
    }

    [Theory]
    [InlineData(KeyModifiers.None, "\r")]
    [InlineData(Shift, "\e[13;2u")]
    [InlineData(Alt, "\e[13;3u")]
    [InlineData(Alt | Shift, "\e[13;4u")]
    [InlineData(Ctrl, "\e[13;5u")]
    [InlineData(Ctrl | Shift, "\e[13;6u")]
    [InlineData(Ctrl | Alt, "\e[13;7u")]
    [InlineData(Ctrl | Alt | Shift, "\e[13;8u")]
    [InlineData(Super, "\e[13;9u")]
    public void Under_the_protocol_Enter_names_each_modifier_held_with_it_and_alone_stays_a_carriage_return(KeyModifiers held, string bytes)
    {
        // Option on the Mac types rather than sends ESC, but Enter types nothing, so it is told as Alt all the same.
        foreach (bool altSendsEscape in (bool[])[true, false])
        {
            Assert.True(Kitty(Key.Enter, held, altSendsEscape) == bytes,
                $"Enter with {held} under the kitty protocol sent {Shown(Kitty(Key.Enter, held, altSendsEscape))}, where the protocol gives {Shown(bytes)}: a program binding it reads another key");
        }
    }

    [Theory]
    [InlineData(Key.Tab, KeyModifiers.None, "\t")]
    [InlineData(Key.Tab, Shift, "\e[9;2u")]
    [InlineData(Key.Tab, Ctrl, "\e[9;5u")]
    [InlineData(Key.Back, KeyModifiers.None, "\x7f")]
    [InlineData(Key.Back, Ctrl, "\e[127;5u")]
    [InlineData(Key.Back, Alt, "\e[127;3u")]
    [InlineData(Key.Escape, KeyModifiers.None, "\e[27u")]
    [InlineData(Key.Escape, Shift, "\e[27;2u")]
    [InlineData(Key.Space, Ctrl, "\e[32;5u")]
    [InlineData(Key.Space, Alt, "\e[32;3u")]
    [InlineData(Key.C, Ctrl, "\e[99;5u")]
    [InlineData(Key.A, Ctrl | Shift, "\e[97;6u")]
    [InlineData(Key.A, Alt, "\e[97;3u")]
    [InlineData(Key.A, Ctrl | Super, "\e[97;13u")]
    [InlineData(Key.I, Ctrl, "\e[105;5u")]
    [InlineData(Key.M, Ctrl, "\e[109;5u")]
    [InlineData(Key.D1, Ctrl, "\e[49;5u")]
    [InlineData(Key.OemOpenBrackets, Ctrl, "\e[91;5u")]
    [InlineData(Key.OemOpenBrackets, Alt, "\e[91;3u")]
    [InlineData(Key.OemQuestion, Ctrl | Shift, "\e[47;6u")]
    [InlineData(Key.NumPad1, Ctrl, "\e[57400;5u")]
    [InlineData(Key.Add, Alt, "\e[57413;3u")]
    public void Under_the_protocol_keys_the_old_bytes_confuse_are_sent_by_their_numbers(Key key, KeyModifiers held, string bytes) =>
        Assert.True(Kitty(key, held) == bytes,
            $"{held} {key} under the kitty protocol sent {Shown(Kitty(key, held))}, where the protocol gives {Shown(bytes)}: the program reads another key, or Esc for the start of a sequence");

    [Theory]
    [InlineData(Key.Up, KeyModifiers.None, "\e[A")]
    [InlineData(Key.Up, Ctrl, "\e[1;5A")]
    [InlineData(Key.Home, KeyModifiers.None, "\e[H")]
    [InlineData(Key.End, Shift, "\e[1;2F")]
    [InlineData(Key.Insert, KeyModifiers.None, "\e[2~")]
    [InlineData(Key.Delete, Alt, "\e[3;3~")]
    [InlineData(Key.PageDown, KeyModifiers.None, "\e[6~")]
    [InlineData(Key.F1, KeyModifiers.None, "\e[P")]
    [InlineData(Key.F2, Shift, "\e[1;2Q")]
    [InlineData(Key.F3, KeyModifiers.None, "\e[13~")]
    [InlineData(Key.F3, Ctrl, "\e[13;5~")]
    [InlineData(Key.F4, KeyModifiers.None, "\e[S")]
    [InlineData(Key.F5, KeyModifiers.None, "\e[15~")]
    [InlineData(Key.F12, Shift, "\e[24;2~")]
    [InlineData(Key.F13, KeyModifiers.None, "\e[57376u")]
    [InlineData(Key.F24, Ctrl, "\e[57387;5u")]
    [InlineData(Key.Apps, KeyModifiers.None, "\e[57363u")]
    public void Under_the_protocol_the_function_keys_take_its_forms(Key key, KeyModifiers held, string bytes) =>
        Assert.True(Kitty(key, held) == bytes, $"{held} {key} under the kitty protocol sent {Shown(Kitty(key, held))}, where the protocol gives {Shown(bytes)}");

    [Fact]
    public void Under_the_protocol_the_arrows_take_its_form_whatever_the_cursor_mode()
    {
        string? up = TerminalKeys.Of(Key.Up, KeyModifiers.None, PhysicalKey.None, applicationCursor: true, TerminalKeys.Disambiguate, altSendsEscape: true);
        Assert.True(up == "\e[A", $"Up in the application cursor mode under the kitty protocol sent {Shown(up)}, the protocol's form being ESC [ A");
        Assert.True(Legacy(Key.Up, KeyModifiers.None, applicationCursor: true) == "\eOA", "Up in the application cursor mode without the protocol no longer sends ESC O A");
    }

    [Theory]
    [InlineData(Key.Enter, PhysicalKey.NumPadEnter, KeyModifiers.None, "\e[57414u")]
    [InlineData(Key.Enter, PhysicalKey.NumPadEnter, Shift, "\e[57414;2u")]
    [InlineData(Key.Up, PhysicalKey.NumPad8, KeyModifiers.None, "\e[57419u")]
    [InlineData(Key.Insert, PhysicalKey.NumPad0, KeyModifiers.None, "\e[57425u")]
    [InlineData(Key.Clear, PhysicalKey.NumPad5, KeyModifiers.None, "\e[E")]
    [InlineData(Key.Delete, PhysicalKey.NumPadDecimal, Ctrl, "\e[57426;5u")]
    public void Under_the_protocol_the_keypads_keys_that_type_nothing_are_its_own(Key key, PhysicalKey physical, KeyModifiers held, string bytes)
    {
        string? sent = TerminalKeys.Of(key, held, physical, applicationCursor: false, TerminalKeys.Disambiguate, altSendsEscape: true);
        Assert.True(sent == bytes, $"The keypad's {key} with {held} under the kitty protocol sent {Shown(sent)}, where the protocol gives {Shown(bytes)}");
        Assert.True(Legacy(key, held, physical: physical) == Legacy(key, held),
            $"The keypad's {key} without the protocol sent {Shown(Legacy(key, held, physical: physical))}, not what the key it moves as sends");
    }

    [Theory]
    [InlineData(KeyModifiers.None, false, "\e[E")]
    [InlineData(KeyModifiers.None, true, "\eOE")]
    [InlineData(Shift, false, "\e[1;2E")]
    [InlineData(Ctrl, true, "\e[1;5E")]
    public void Without_the_protocol_the_keypads_5_with_Num_Lock_off_is_xterms_Begin(KeyModifiers held, bool applicationCursor, string bytes)
    {
        string? sent = Legacy(Key.Clear, held, applicationCursor: applicationCursor, physical: PhysicalKey.NumPad5);
        Assert.True(sent == bytes,
            $"The keypad's 5 with Num Lock off and {held}{(applicationCursor ? " in the application cursor mode" : string.Empty)} sent {Shown(sent)} to a program that never asked for the kitty protocol, where xterm sends {Shown(bytes)}: the program never hears the key");
    }

    [Theory]
    [InlineData(Key.Up, Super, "\e[1;9A")]
    [InlineData(Key.Home, Super | Shift, "\e[1;10H")]
    [InlineData(Key.Delete, Super | Ctrl, "\e[3;13~")]
    [InlineData(Key.F1, Super, "\e[1;9P")]
    [InlineData(Key.F12, Super | Ctrl | Alt | Shift, "\e[24;16~")]
    public void Without_the_protocol_a_function_key_held_with_Meta_says_so_as_xterm_does(Key key, KeyModifiers held, string bytes) =>
        Assert.True(Legacy(key, held) == bytes,
            $"{held} {key} sent {Shown(Legacy(key, held))} to a program that never asked for the kitty protocol, where xterm's modifiers give {Shown(bytes)}: the program reads it as the key without Meta");

    [Theory]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Right)]
    [InlineData(Key.Left)]
    [InlineData(Key.Home)]
    [InlineData(Key.End)]
    [InlineData(Key.Clear)]
    [InlineData(Key.Insert)]
    [InlineData(Key.Delete)]
    [InlineData(Key.PageUp)]
    [InlineData(Key.PageDown)]
    [InlineData(Key.F1)]
    [InlineData(Key.F2)]
    [InlineData(Key.F4)]
    [InlineData(Key.F5)]
    [InlineData(Key.F6)]
    [InlineData(Key.F7)]
    [InlineData(Key.F8)]
    [InlineData(Key.F9)]
    [InlineData(Key.F10)]
    [InlineData(Key.F11)]
    [InlineData(Key.F12)]
    public void A_function_key_held_with_any_modifier_is_sent_alike_with_the_protocol_or_without(Key key)
    {
        foreach (KeyModifiers held in (KeyModifiers[])[Shift, Alt, Ctrl, Super, Ctrl | Shift, Alt | Super, Ctrl | Alt | Shift | Super])
        {
            Assert.True(Legacy(key, held) == Kitty(key, held),
                $"{held} {key} sent {Shown(Legacy(key, held))} without the kitty protocol and {Shown(Kitty(key, held))} with it, where both give its number and final with the modifiers");
        }
    }

    [Fact]
    public void F3_held_with_a_modifier_keeps_xterms_letter_without_the_protocol_and_the_protocols_number_with_it()
    {
        Assert.True(Legacy(Key.F3, Ctrl) == "\e[1;5R", $"Ctrl+F3 without the kitty protocol sent {Shown(Legacy(Key.F3, Ctrl))}, where xterm sends ESC [ 1 ; 5 R");
        Assert.True(Legacy(Key.F3, KeyModifiers.None) == "\eOR", $"F3 without the kitty protocol sent {Shown(Legacy(Key.F3, KeyModifiers.None))}, where xterm sends ESC O R");
    }

    [Theory]
    [InlineData(Key.A, KeyModifiers.None, true)]
    [InlineData(Key.A, Shift, true)]
    [InlineData(Key.Space, Shift, true)]
    [InlineData(Key.NumPad5, KeyModifiers.None, true)]
    [InlineData(Key.A, Alt, false)]
    [InlineData(Key.A, Super, true)]
    [InlineData(Key.OemPlus, Alt | Shift, false)]
    public void A_key_that_types_leaves_its_text_to_the_text_input_under_the_protocol(Key key, KeyModifiers held, bool altSendsEscape) =>
        Assert.True(Kitty(key, held, altSendsEscape) is null,
            $"{held} {key} under the kitty protocol sent {Shown(Kitty(key, held, altSendsEscape))} as well as the text it types");

    private static string? Legacy(Key key, KeyModifiers held, bool altSendsEscape = true, bool applicationCursor = false, PhysicalKey physical = PhysicalKey.None) =>
        TerminalKeys.Of(key, held, physical, applicationCursor, keyboardFlags: 0, altSendsEscape);

    private static string? Kitty(Key key, KeyModifiers held, bool altSendsEscape = true) =>
        TerminalKeys.Of(key, held, PhysicalKey.None, applicationCursor: false, TerminalKeys.Disambiguate, altSendsEscape);

    private static string Shown(string? bytes) => bytes is null ? "nothing" : $"\"{bytes.Replace("\e", "ESC", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal).Replace("\x7f", "DEL", StringComparison.Ordinal)}\"";
}
