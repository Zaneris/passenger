using Passenger.Editor;
using Passenger.Text;
using Passenger.UnitTests.Text;

namespace Passenger.UnitTests.Editor;

/// <summary>An insert session the host formats as it types (a statement laid out at its ';', a block left below): . types
/// again what the session's keys made, wherever the formatting changed the line before the session's start or the text
/// after the caret, and an edit that takes in that start makes what it writes part of what is typed again. Vim's own
/// keys are held to Vim itself by Passenger.Tests' VimTests; this is what only a host that formats can show.</summary>
public sealed class VimFollowedEditsTests
{
    [Fact]
    public void Dot_types_again_what_an_insert_session_typed_however_the_host_laid_it_out_meanwhile() => Cases.All(
        ("a formatting before the session's start leaves . typing what the keys typed", () =>
            // The host's layout of the statement: a space either side of its '='.
            Assert.Equal("x = 1;\ny=2|;\n", Followed("x=|1\ny=2\n", ["A", ";"], "x=1;|\ny=2\n", [new(1, 0, " "), new(2, 0, " ")], ["j", "."]))),
        ("a formatting taking in the session's start is typed again from where it begins", () =>
            // Two spaces, the first before the session's start and the second typed in it, laid out as one.
            Assert.Equal("int x;\nlong x|;\n", Followed("int| \nlong\n", ["a", " ", "x", ";"], "int  x;|\nlong\n", [new(3, 2, " ")], ["j", "$", "."]))),
        ("a formatting after the caret leaves . typing what the keys typed", () =>
            // The host's layout of what follows: two spaces laid out as one.
            Assert.Equal("xa\n|xb c\n", Followed("|a\nb  c\n", ["i", "x"], "x|a\nb  c\n", [new(4, 2, " ")], ["j", "0", "."]))));

    /// <summary>The keys typed on the marked text, checked against <paramref name="typed"/>, the host's layout followed,
    /// then Escape and the keys after it: the text and caret they leave.</summary>
    private static string Followed(string marked, string[] keys, string typed, TextEdit[] laid, string[] after)
    {
        Vim vim = new();
        (TextSnapshot text, SelectionSet carets) = Drive(vim, Marked.Parse(marked), keys);
        Assert.Equal(typed, Marked.Show(text, carets));
        vim.FollowEdits(laid, carets.Primary.Caret);
        (text, carets) = Drive(vim, (text.Apply(laid), TextEditing.MapThrough(carets, laid)), [VimKeys.Escape, .. after]);
        return Marked.Show(text, carets);
    }

    /// <summary>The keys through Vim mode as an editor takes them, insert mode's typed by the editor.</summary>
    private static (TextSnapshot Text, SelectionSet Carets) Drive(Vim vim, (TextSnapshot Text, SelectionSet Carets) at, IEnumerable<string> keys)
    {
        (TextSnapshot text, SelectionSet carets) = at;
        foreach (string key in keys)
        {
            VimResult result = vim.Press(key, text, carets);
            if (!result.Taken && vim.Mode == VimMode.Insert)
            {
                EditPlan typed = TextEditing.Insert(text, carets, key);
                (text, carets) = (text.Apply(typed.Edits), typed.Selections);
                continue;
            }
            foreach (EditPlan edit in result.Edits ?? [])
            {
                (text, carets) = (text.Apply(edit.Edits), edit.Selections);
            }
            carets = result.Edits is not { Count: > 0 } && result.Selections is { } moved ? moved : carets;
            carets = vim.Mode == VimMode.Normal ? Vim.Settled(text, carets, breakEnds: true) : carets;
        }
        return (text, carets);
    }
}
