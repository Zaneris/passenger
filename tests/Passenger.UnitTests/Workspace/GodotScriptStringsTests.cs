using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A GDScript's words and strings as Godot's tokenizer reads them, each string given straight to a call told by
/// the call's name and its argument: comments passed over, quotes of each kind and prefix, escapes read.</summary>
public class GodotScriptStringsTests
{
    [Fact]
    public void A_string_given_to_a_call_is_told_by_the_call_and_its_argument()
    {
        const string text = "if Input.get_axis(\"left\", \"right\") > 0 and is_in_group(&\"enemies\"):\n\tprint([\"a\", foo(\"b\")])\n";
        IReadOnlyList<GodotScriptToken> strings = [.. GodotScriptStrings.Read(text).Where(t => !t.Word)];
        Assert.Equal([("left", "get_axis", 0), ("right", "get_axis", 1), ("enemies", "is_in_group", 0), ("a", null, 0), ("b", "foo", 0)],
            strings.Select(s => (s.Text, s.Call, s.Argument)));
        Assert.All(strings, s => Assert.Equal(s.Text, text[s.Start..s.End]));
    }

    [Fact]
    public void Comments_are_passed_over_and_quotes_escapes_and_prefixes_are_read_as_godot_reads_them()
    {
        const string text = "# tr(\"no\")\nvar a = 'it\\'s' + r\"raw\\d\" + \"\"\"long \"quoted\" text\"\"\" + ^\"/root/Globals\"\nGlobals.go(self.Globals)\n";
        IReadOnlyList<GodotScriptToken> tokens = GodotScriptStrings.Read(text);
        Assert.Equal(["it's", "raw\\d", "long \"quoted\" text", "/root/Globals"], tokens.Where(t => !t.Word).Select(t => t.Text));
        Assert.Equal([false, true, true, true], tokens.Where(t => !t.Word).Select(t => t.Plain));
        Assert.Equal([false, true], tokens.Where(t => t.Word && t.Text == "Globals").Select(t => t.AfterDot));
        Assert.DoesNotContain(tokens, t => t.Text is "tr" or "no");
    }
}
