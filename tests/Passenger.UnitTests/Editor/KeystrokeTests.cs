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

    [Fact]
    public void A_keystroke_reads_its_modifiers_aliases_and_typed_keys_as_the_keymap_writes_them() => Cases.All(
        ("A modifiers word or its alias names that modifier in any case", A_modifiers_word_or_its_alias_names_that_modifier_in_any_case),
        ("Another word names no modifier", Another_word_names_no_modifier),
        ("A keystroke written with aliases reads as written with the keymaps own words", A_keystroke_written_with_aliases_reads_as_written_with_the_keymaps_own_words),
        ("A key written as the character it types on a US keyboard is that key with shift for a shifted one", A_key_written_as_the_character_it_types_on_a_US_keyboard_is_that_key_with_shift_for_a_shifted_one));

    private void A_modifiers_word_or_its_alias_names_that_modifier_in_any_case() => Cases.Each<(string word, KeyModifiers modifier)>(
    [
        ("meta", KeyModifiers.Meta),
        ("command", KeyModifiers.Meta),
        ("super", KeyModifiers.Meta),
        ("Ctrl", KeyModifiers.Control),
        ("Option", KeyModifiers.Alt),
        ("SHIFT", KeyModifiers.Shift),
    ], row =>
    {
        (string word, KeyModifiers modifier) = row;
        
                Assert.True(Keystroke.ModifierOf(word) == modifier, $"\"{word}\" names {Keystroke.ModifierOf(word)?.ToString() ?? "no modifier"}, not {modifier}, so a key held with it is pressed without it");
    });

    private void Another_word_names_no_modifier() => Cases.Each<string>(
    [
        "K",
        "primary",
        "hyper",
        "",
    ], word =>
    {
        
                Assert.True(Keystroke.ModifierOf(word) is null, $"\"{word}\" was read as {Keystroke.ModifierOf(word)}, so a key or a word of none is taken for a modifier");
    });

    private void A_keystroke_written_with_aliases_reads_as_written_with_the_keymaps_own_words() =>
        Assert.Equal(Keystroke.Parse("meta alt shift K"), Keystroke.Parse("command opt Shift k"));

    private void A_key_written_as_the_character_it_types_on_a_US_keyboard_is_that_key_with_shift_for_a_shifted_one() => Cases.Each<(string written, Key key, KeyModifiers modifiers)>(
    [
        ("meta+,", Key.OemComma, KeyModifiers.Meta),
        ("/", Key.OemQuestion, None),
        ("-", Key.OemMinus, None),
        ("?", Key.OemQuestion, Shift),
        ("_", Key.OemMinus, Shift),
        ("cmd+shift+?", Key.OemQuestion, KeyModifiers.Meta | Shift),
    ], row =>
    {
        (string written, Key key, KeyModifiers modifiers) = row;
        
                Assert.True(Keystroke.ParseJoined(written) == new Keystroke(key, modifiers),
                    $"\"{written}\" was read as {Keystroke.ParseJoined(written)}, not {modifiers} with {key}, so a step pressing it presses another key");
    });

    [Fact]
    public void A_keystroke_joined_by_plus_or_named_by_an_agent_reads_as_its_key() => Cases.All(
        ("A keystroke joined by plus reads as the keymaps notation whether its key is named or typed", A_keystroke_joined_by_plus_reads_as_the_keymaps_notation_whether_its_key_is_named_or_typed),
        ("A key typed as its character in the keymaps notation is the key its name gives", A_key_typed_as_its_character_in_the_keymaps_notation_is_the_key_its_name_gives),
        ("A key an agent names by a word of its own or avalonias name in any case is that key", A_key_an_agent_names_by_a_word_of_its_own_or_avalonias_name_in_any_case_is_that_key),
        ("A keystroke joined by plus is parted at each plus but the plus key", A_keystroke_joined_by_plus_is_parted_at_each_plus_but_the_plus_key));

    private void A_keystroke_joined_by_plus_reads_as_the_keymaps_notation_whether_its_key_is_named_or_typed() => Cases.Each<(string joined, string notation)>(
    [
        ("meta+,", "meta COMMA"),
        ("ctrl+[", "control OPEN_BRACKET"),
        ("meta+OemComma", "meta COMMA"),
        ("cmd+shift+L", "meta shift L"),
        ("Escape", "ESCAPE"),
        ("alt+PageDown", "alt PAGE_DOWN"),
    ], row =>
    {
        (string joined, string notation) = row;
        
                Assert.True(Keystroke.ParseJoined(joined) == Keystroke.Parse(notation),
                    $"\"{joined}\" was read as {Keystroke.ParseJoined(joined)}, where the keymap's \"{notation}\" is {Keystroke.Parse(notation)}");
    });

    private void A_key_typed_as_its_character_in_the_keymaps_notation_is_the_key_its_name_gives() =>
        Assert.Equal(Keystroke.Parse("meta shift SLASH"), Keystroke.Parse("meta ?"));

    private void A_key_an_agent_names_by_a_word_of_its_own_or_avalonias_name_in_any_case_is_that_key() => Cases.Each<(string written, Key key, KeyModifiers modifiers)>(
    [
        ("esc", Key.Escape, None),
        ("backspace", Key.Back, None),
        ("ins", Key.Insert, None),
        ("comma", Key.OemComma, None),
        ("ctrl+plus", Key.OemPlus, KeyModifiers.Control | Shift),
        ("pagedown", Key.PageDown, None),
    ], row =>
    {
        (string written, Key key, KeyModifiers modifiers) = row;
        
                Assert.True(Keystroke.ParseJoined(written) == new Keystroke(key, modifiers),
                    $"\"{written}\" was read as {Keystroke.ParseJoined(written)}, not {modifiers} with {key}, so an agent's key presses another");
    });

    private void A_keystroke_joined_by_plus_is_parted_at_each_plus_but_the_plus_key() => Cases.Each<(string joined, string[] parts)>(
    [
        ("ctrl++", new[] { "ctrl", "+" }),
        ("+", new[] { "+" }),
        ("cmd+shift+L", new[] { "cmd", "shift", "L" }),
        ("ctrl+/", new[] { "ctrl", "/" }),
    ], row =>
    {
        (string joined, string[] parts) = row;
        
                Assert.Equal(parts, Keystroke.JoinedParts(joined));
    });

    [Fact]
    public void A_keystroke_types_the_character_its_key_gives_or_nothing_or_is_refused_saying_why() => Cases.All(
        ("A keystroke types the character its key gives on a US keyboard with its shift", A_keystroke_types_the_character_its_key_gives_on_a_US_keyboard_with_its_shift),
        ("A keystroke held with ctrl alt or meta or of a key typing no character types nothing", A_keystroke_held_with_ctrl_alt_or_meta_or_of_a_key_typing_no_character_types_nothing),
        ("A keystroke of no key a US keyboard types or of two keys is refused saying why", A_keystroke_of_no_key_a_US_keyboard_types_or_of_two_keys_is_refused_saying_why));

    private void A_keystroke_types_the_character_its_key_gives_on_a_US_keyboard_with_its_shift() => Cases.Each<(string written, string typed)>(
    [
        ("/", "/"),
        ("shift+4", "$"),
        ("+", "+"),
        ("A", "a"),
        ("Space", " "),
        ("Add", "+"),
    ], row =>
    {
        (string written, string typed) = row;
        
                Assert.True(Keystroke.ParseJoined(written).Types == typed,
                    $"\"{written}\" types \"{Keystroke.ParseJoined(written).Types}\", not \"{typed}\", so a text box it is pressed in shows another character");
    });

    private void A_keystroke_held_with_ctrl_alt_or_meta_or_of_a_key_typing_no_character_types_nothing() => Cases.Each<string>(
    [
        "ctrl+/",
        "alt+A",
        "Enter",
        "Tab",
        "F5",
        "Left",
    ], written =>
    {
        
                Assert.True(Keystroke.ParseJoined(written).Types is null,
                    $"\"{written}\" types \"{Keystroke.ParseJoined(written).Types}\", where a keyboard types nothing, so a shortcut or a key that moves or ends puts a character in a text box");
    });

    private void A_keystroke_of_no_key_a_US_keyboard_types_or_of_two_keys_is_refused_saying_why() => Cases.Each<(string joined, string why)>(
    [
        ("meta+§", "unknown key '§'"),
        ("meta+,+.", "names two keys"),
        ("shift+1000", "unknown key '1000'"),
        ("None", "unknown key 'None'"),
    ], row =>
    {
        (string joined, string why) = row;
        FormatException refused = Assert.Throws<FormatException>(() => Keystroke.ParseJoined(joined));
        Assert.Contains(why, refused.Message, StringComparison.Ordinal);
    });
}
