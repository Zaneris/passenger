using Avalonia.Input;
using Passenger.Editor;

namespace Passenger.UnitTests.Editor;

/// <summary>The modifier words of the keymap's notation, in any case: a keymap's and the aliases an agent writes alike,
/// each naming one modifier, another word none, and a keystroke read with them.</summary>
public class KeystrokeTests
{
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
}
