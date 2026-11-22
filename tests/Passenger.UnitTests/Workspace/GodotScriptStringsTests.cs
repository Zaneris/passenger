using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A GDScript's words and strings as Godot's tokenizer reads them, each string given straight to a call told by
/// the call's name and its argument: comments passed over, quotes of each kind and prefix, escapes read.</summary>
public class GodotScriptStringsTests
{
    /// <summary>A script's strings, their calls and escapes are read as Godot's tokenizer reads them.</summary>
    [Fact]
    public void A_scripts_strings_are_read_as_godots_tokenizer_reads_them() => Cases.All(
        (nameof(A_string_given_to_a_call_is_told_by_the_call_and_its_argument), A_string_given_to_a_call_is_told_by_the_call_and_its_argument),
        (nameof(A_strings_escapes_are_read_as_Godots_tokenizer_reads_them), A_strings_escapes_are_read_as_Godots_tokenizer_reads_them));

    private void A_string_given_to_a_call_is_told_by_the_call_and_its_argument()
    {
        const string text = "if Input.get_axis(\"left\", \"right\") > 0 and is_in_group(&\"enemies\"):\n\tprint([\"a\", foo(\"b\")])\n";
        IReadOnlyList<GodotScriptToken> strings = [.. GodotScriptStrings.Read(text).Where(t => !t.Word)];
        Assert.Equal([("left", "get_axis", 0), ("right", "get_axis", 1), ("enemies", "is_in_group", 0), ("a", null, 0), ("b", "foo", 0)],
            strings.Select(s => (s.Text, s.Call, s.Argument)));
        Assert.All(strings, s => Assert.Equal(s.Text, text[s.Start..s.End]));
    }

    /// <summary>Every escape Godot's tokenizer reads, a code point past the first plane and a surrogate pair among them, so an
    /// action written with one is the action it names; one Godot refuses says nothing, and a raw string's backslash before
    /// another keeps both and closes no string.</summary>
    private void A_strings_escapes_are_read_as_Godots_tokenizer_reads_them()
    {
        const string text = "if Input.is_action_pressed(\"caf\\u00e9\"):\n\tsay(\"\\a\\b\\f\\v\\t\\U01F600\\uD83D\\uDE00\\q\")\n\tsay(r\"C:\\\\\", \"next\")\n";
        IReadOnlyList<GodotScriptToken> strings = [.. GodotScriptStrings.Read(text).Where(t => !t.Word)];
        Assert.Equal(["caf\u00e9", "\a\b\f\v\t\U0001F600\U0001F600", "C:\\\\", "next"], strings.Select(s => s.Text));
        Assert.Equal([("caf\u00e9", "is_action_pressed", 0), ("next", "say", 1)], strings.Where(s => s.Text is "caf\u00e9" or "next").Select(s => (s.Text, s.Call, s.Argument)));
        Assert.Equal([false, false, true, true], strings.Select(s => s.Plain));
    }
}
