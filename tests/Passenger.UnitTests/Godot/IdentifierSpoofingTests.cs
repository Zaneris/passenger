using Passenger.Workspace.GDScript;

namespace Passenger.UnitTests.Godot;

/// <summary>The identifiers Godot warns of as confusable are those ICU's spoof check fails as Godot sets it up, each case
/// checked against Godot 4.7.2's own GDScript server: ASCII never; one script, or Latin with Han's or another script not
/// Cyrillic, Greek or Cherokee, never; Latin mixed with Cyrillic or Greek, a character Unicode doesn't allow in identifiers,
/// digits of two systems, a nonspacing mark twice over one letter and a dot above on an i always.</summary>
public class IdentifierSpoofingTests
{
    [Theory]
    [InlineData("port")]
    [InlineData("_private_2")]
    [InlineData("café")]
    [InlineData("名前")]
    [InlineData("日本語abc")]
    [InlineData("ab٣٤")]
    [InlineData("αβγ")]
    [InlineData("да")]
    [InlineData("कx")]
    public void An_identifier_of_one_script_or_latin_with_one_godot_takes_is_no_spoof(string identifier) =>
        Assert.False(IdentifierSpoofing.Spoofs(identifier), $"{identifier} would be warned of as confusable, which Godot doesn't");

    [Theory]
    [InlineData("usеr")]
    [InlineData("pοrt")]
    [InlineData("дa")]
    [InlineData("aα")]
    [InlineData("Ꭺbc")]
    [InlineData("ŀ")]
    [InlineData("x١2")]
    [InlineData("i̇x")]
    [InlineData("ä̈")]
    public void An_identifier_mixing_confusable_scripts_digits_or_marks_or_of_a_character_not_allowed_is_a_spoof(string identifier) =>
        Assert.True(IdentifierSpoofing.Spoofs(identifier), $"{identifier} would not be warned of as confusable, which Godot does");
}
