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

    /// <summary>A call's string says what the call is made on, and a default or a string joined to another that it is no
    /// argument's whole, while each branch of an <c>if</c> expression is.</summary>
    [Fact]
    public void A_string_tells_what_its_call_is_made_on_and_whether_it_is_the_whole_of_an_argument()
    {
        const string text = "func go(label = \"a\"):\n\tInput.go(\"b\")\n\tthing.other.go(\"c\")\n\tmake().go(\"d\")\n\tgo(\"e\" + \"f\")\n\tgo(\"g\" if on else \"h\")\n\tgo(\"i\", # why\n\t\t&\"j\")\n";
        IReadOnlyList<GodotScriptToken> strings = [.. GodotScriptStrings.Read(text).Where(t => !t.Word)];
        Assert.Equal([("a", null), ("b", "Input"), ("c", string.Empty), ("d", string.Empty), ("e", null), ("f", null), ("g", null), ("h", null), ("i", null), ("j", null)],
            strings.Select(s => (s.Text, s.Receiver)));
        Assert.Equal(["a", "e", "f"], strings.Where(s => !s.Whole).Select(s => s.Text));
        Dictionary<string, int[]> calls = new(StringComparer.Ordinal) { ["go"] = [0, 1] };
        Assert.Equal(["b", "c", "d", "g", "h", "i", "j"], GodotScriptStrings.GivenTo(strings, calls).Select(s => s.Text));
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

    /// <summary>Every escape Godot's tokenizer reads, a code point past the first plane and a surrogate pair among them, so an
    /// action written with one is the action it names; one Godot refuses says nothing, and a raw string's backslash before
    /// another keeps both and closes no string.</summary>
    [Fact]
    public void A_strings_escapes_are_read_as_Godots_tokenizer_reads_them()
    {
        const string text = "if Input.is_action_pressed(\"caf\\u00e9\"):\n\tsay(\"\\a\\b\\f\\v\\t\\U01F600\\uD83D\\uDE00\\q\")\n\tsay(r\"C:\\\\\", \"next\")\n";
        IReadOnlyList<GodotScriptToken> strings = [.. GodotScriptStrings.Read(text).Where(t => !t.Word)];
        Assert.Equal(["caf\u00e9", "\a\b\f\v\t\U0001F600\U0001F600", "C:\\\\", "next"], strings.Select(s => s.Text));
        Assert.Equal([("caf\u00e9", "is_action_pressed", 0), ("next", "say", 1)], strings.Where(s => s.Text is "caf\u00e9" or "next").Select(s => (s.Text, s.Call, s.Argument)));
        Assert.Equal([false, false, true, true], strings.Select(s => s.Plain));
    }
}
