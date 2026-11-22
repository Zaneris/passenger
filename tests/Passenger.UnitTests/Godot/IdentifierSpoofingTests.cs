using Passenger.Workspace.GDScript;

namespace Passenger.UnitTests.Godot;

/// <summary>The identifiers Godot warns of as confusable are those ICU's spoof check fails as Godot sets it up, each case
/// checked against Godot 4.7.2's own GDScript server: ASCII never; one script, or Latin with Han's or another script not
/// Cyrillic, Greek or Cherokee, never; Latin mixed with Cyrillic or Greek, a character Unicode doesn't allow in identifiers,
/// digits of two systems, a nonspacing mark twice over one letter and a dot above on an i always.</summary>
public class IdentifierSpoofingTests
{
    /// <summary>An identifier is a spoof only where Godot's confusable check calls it one.</summary>
    [Fact]
    public void An_identifier_is_a_spoof_only_where_godot_calls_it_one() => Cases.All(
        (nameof(An_identifier_of_one_script_or_latin_with_one_godot_takes_is_no_spoof),
            An_identifier_of_one_script_or_latin_with_one_godot_takes_is_no_spoof),
        (nameof(An_identifier_mixing_confusable_scripts_digits_or_marks_or_of_a_character_not_allowed_is_a_spoof),
            An_identifier_mixing_confusable_scripts_digits_or_marks_or_of_a_character_not_allowed_is_a_spoof));

    private void An_identifier_of_one_script_or_latin_with_one_godot_takes_is_no_spoof() => Cases.Each<string>(
    [
        "port",
        "café",
        "名前",
        "ab٣٤",
        "कx",
    ], An_identifier_of_one_script_or_latin_with_one_godot_takes_is_no_spoof);

    private void An_identifier_of_one_script_or_latin_with_one_godot_takes_is_no_spoof(string identifier) =>
        Assert.False(IdentifierSpoofing.Spoofs(identifier), $"{identifier} would be warned of as confusable, which Godot doesn't");

    private void An_identifier_mixing_confusable_scripts_digits_or_marks_or_of_a_character_not_allowed_is_a_spoof() => Cases.Each<string>(
    [
        "usеr",
        "aα",
        "ŀ",
        "x١2",
        "i̇x",
        "ä̈",
    ], An_identifier_mixing_confusable_scripts_digits_or_marks_or_of_a_character_not_allowed_is_a_spoof);

    private void An_identifier_mixing_confusable_scripts_digits_or_marks_or_of_a_character_not_allowed_is_a_spoof(string identifier) =>
        Assert.True(IdentifierSpoofing.Spoofs(identifier), $"{identifier} would not be warned of as confusable, which Godot does");
}
