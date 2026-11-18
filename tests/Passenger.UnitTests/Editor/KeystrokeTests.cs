using Avalonia.Input;
using Passenger.Editor;

namespace Passenger.UnitTests.Editor;

/// <summary>The modifier words of the keymap's notation, in any case: a keymap's and the aliases an agent writes alike,
/// each naming one modifier, another word none, and a keystroke read with them; a key by its name or as the character it
/// types on a US keyboard, its parts apart by spaces or, on a command line or in an agent's keys, joined by +; and the
/// character a keystroke types there.</summary>
public class KeystrokeTests
{
    private const KeyModifiers None = KeyModifiers.None;
    private const KeyModifiers Shift = KeyModifiers.Shift;

    [Theory]
    [InlineData("meta", KeyModifiers.Meta)]
    [InlineData("Cmd", KeyModifiers.Meta)]
    [InlineData("command", KeyModifiers.Meta)]
    [InlineData("WIN", KeyModifiers.Meta)]
    [InlineData("super", KeyModifiers.Meta)]
    [InlineData("control", KeyModifiers.Control)]
    [InlineData("Ctrl", KeyModifiers.Control)]
    [InlineData("alt", KeyModifiers.Alt)]
    [InlineData("Option", KeyModifiers.Alt)]
    [InlineData("opt", KeyModifiers.Alt)]
    [InlineData("SHIFT", KeyModifiers.Shift)]
    public void A_modifiers_word_or_its_alias_names_that_modifier_in_any_case(string word, KeyModifiers modifier) =>
        Assert.True(Keystroke.ModifierOf(word) == modifier, $"\"{word}\" names {Keystroke.ModifierOf(word)?.ToString() ?? "no modifier"}, not {modifier}, so a key held with it is pressed without it");

    [Theory]
    [InlineData("K")]
    [InlineData("primary")]
    [InlineData("hyper")]
    [InlineData("")]
    public void Another_word_names_no_modifier(string word) =>
        Assert.True(Keystroke.ModifierOf(word) is null, $"\"{word}\" was read as {Keystroke.ModifierOf(word)}, so a key or a word of none is taken for a modifier");

    [Fact]
    public void A_keystroke_written_with_aliases_reads_as_written_with_the_keymaps_own_words() =>
        Assert.Equal(Keystroke.Parse("meta alt shift K"), Keystroke.Parse("command opt Shift k"));

    [Theory]
    [InlineData("meta+,", Key.OemComma, KeyModifiers.Meta)]
    [InlineData("shift+/", Key.OemQuestion, Shift)]
    [InlineData("ctrl+[", Key.OemOpenBrackets, KeyModifiers.Control)]
    [InlineData(",", Key.OemComma, None)]
    [InlineData(".", Key.OemPeriod, None)]
    [InlineData("/", Key.OemQuestion, None)]
    [InlineData(";", Key.OemSemicolon, None)]
    [InlineData("'", Key.OemQuotes, None)]
    [InlineData("[", Key.OemOpenBrackets, None)]
    [InlineData("]", Key.OemCloseBrackets, None)]
    [InlineData("\\", Key.OemPipe, None)]
    [InlineData("-", Key.OemMinus, None)]
    [InlineData("=", Key.OemPlus, None)]
    [InlineData("`", Key.OemTilde, None)]
    [InlineData("<", Key.OemComma, Shift)]
    [InlineData(">", Key.OemPeriod, Shift)]
    [InlineData("?", Key.OemQuestion, Shift)]
    [InlineData(":", Key.OemSemicolon, Shift)]
    [InlineData("\"", Key.OemQuotes, Shift)]
    [InlineData("{", Key.OemOpenBrackets, Shift)]
    [InlineData("}", Key.OemCloseBrackets, Shift)]
    [InlineData("|", Key.OemPipe, Shift)]
    [InlineData("_", Key.OemMinus, Shift)]
    [InlineData("+", Key.OemPlus, Shift)]
    [InlineData("~", Key.OemTilde, Shift)]
    [InlineData("$", Key.D4, Shift)]
    [InlineData("ctrl++", Key.OemPlus, KeyModifiers.Control | Shift)]
    [InlineData("cmd+shift+?", Key.OemQuestion, KeyModifiers.Meta | Shift)]
    public void A_key_written_as_the_character_it_types_on_a_US_keyboard_is_that_key_with_shift_for_a_shifted_one(string written, Key key, KeyModifiers modifiers) =>
        Assert.True(Keystroke.ParseJoined(written) == new Keystroke(key, modifiers),
            $"\"{written}\" was read as {Keystroke.ParseJoined(written)}, not {modifiers} with {key}, so a step pressing it presses another key");

    [Theory]
    [InlineData("meta+,", "meta COMMA")]
    [InlineData("ctrl+[", "control OPEN_BRACKET")]
    [InlineData("meta+OemComma", "meta COMMA")]
    [InlineData("cmd+shift+L", "meta shift L")]
    [InlineData("Escape", "ESCAPE")]
    [InlineData("alt+PageDown", "alt PAGE_DOWN")]
    public void A_keystroke_joined_by_plus_reads_as_the_keymaps_notation_whether_its_key_is_named_or_typed(string joined, string notation) =>
        Assert.True(Keystroke.ParseJoined(joined) == Keystroke.Parse(notation),
            $"\"{joined}\" was read as {Keystroke.ParseJoined(joined)}, where the keymap's \"{notation}\" is {Keystroke.Parse(notation)}");

    [Fact]
    public void A_key_typed_as_its_character_in_the_keymaps_notation_is_the_key_its_name_gives() =>
        Assert.Equal(Keystroke.Parse("meta shift SLASH"), Keystroke.Parse("meta ?"));

    [Theory]
    [InlineData("esc", Key.Escape, None)]
    [InlineData("Return", Key.Enter, None)]
    [InlineData("backspace", Key.Back, None)]
    [InlineData("del", Key.Delete, None)]
    [InlineData("ins", Key.Insert, None)]
    [InlineData("pgup", Key.PageUp, None)]
    [InlineData("ctrl+pgdn", Key.PageDown, KeyModifiers.Control)]
    [InlineData("comma", Key.OemComma, None)]
    [InlineData("minus", Key.OemMinus, None)]
    [InlineData("ctrl+plus", Key.OemPlus, KeyModifiers.Control | Shift)]
    [InlineData("OemQuestion", Key.OemQuestion, None)]
    [InlineData("pagedown", Key.PageDown, None)]
    public void A_key_an_agent_names_by_a_word_of_its_own_or_avalonias_name_in_any_case_is_that_key(string written, Key key, KeyModifiers modifiers) =>
        Assert.True(Keystroke.ParseJoined(written) == new Keystroke(key, modifiers),
            $"\"{written}\" was read as {Keystroke.ParseJoined(written)}, not {modifiers} with {key}, so an agent's key presses another");

    [Theory]
    [InlineData("ctrl++", new[] { "ctrl", "+" })]
    [InlineData("+", new[] { "+" })]
    [InlineData("cmd+shift+L", new[] { "cmd", "shift", "L" })]
    [InlineData("ctrl+/", new[] { "ctrl", "/" })]
    public void A_keystroke_joined_by_plus_is_parted_at_each_plus_but_the_plus_key(string joined, string[] parts) =>
        Assert.Equal(parts, Keystroke.JoinedParts(joined));

    [Theory]
    [InlineData("/", "/")]
    [InlineData("?", "?")]
    [InlineData("shift+4", "$")]
    [InlineData("=", "=")]
    [InlineData("+", "+")]
    [InlineData("`", "`")]
    [InlineData("~", "~")]
    [InlineData("A", "a")]
    [InlineData("shift+A", "A")]
    [InlineData("Space", " ")]
    [InlineData("NumPad1", "1")]
    [InlineData("Add", "+")]
    public void A_keystroke_types_the_character_its_key_gives_on_a_US_keyboard_with_its_shift(string written, string typed) =>
        Assert.True(Keystroke.ParseJoined(written).Types == typed,
            $"\"{written}\" types \"{Keystroke.ParseJoined(written).Types}\", not \"{typed}\", so a text box it is pressed in shows another character");

    [Theory]
    [InlineData("ctrl+/")]
    [InlineData("alt+A")]
    [InlineData("meta+shift+,")]
    [InlineData("Enter")]
    [InlineData("Tab")]
    [InlineData("Escape")]
    [InlineData("F5")]
    [InlineData("Left")]
    public void A_keystroke_held_with_ctrl_alt_or_meta_or_of_a_key_typing_no_character_types_nothing(string written) =>
        Assert.True(Keystroke.ParseJoined(written).Types is null,
            $"\"{written}\" types \"{Keystroke.ParseJoined(written).Types}\", where a keyboard types nothing, so a shortcut or a key that moves or ends puts a character in a text box");

    [Theory]
    [InlineData("meta+§", "unknown key '§'")]
    [InlineData("meta+,+.", "names two keys")]
    [InlineData("shift+1000", "unknown key '1000'")]
    [InlineData("None", "unknown key 'None'")]
    public void A_keystroke_of_no_key_a_US_keyboard_types_or_of_two_keys_is_refused_saying_why(string joined, string why)
    {
        FormatException refused = Assert.Throws<FormatException>(() => Keystroke.ParseJoined(joined));
        Assert.Contains(why, refused.Message, StringComparison.Ordinal);
    }
}
