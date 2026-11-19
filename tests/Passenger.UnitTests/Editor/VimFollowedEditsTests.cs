using Passenger.Editor;
using Passenger.Text;
using Passenger.UnitTests.Text;

namespace Passenger.UnitTests.Editor;

/// <summary>An insert session the host formats as it types (a statement laid out at its ';', a block left below): . types
/// again what the session's keys made, wherever the formatting changed the line before the session's start or the text
/// after the caret, and an edit that takes in that start makes what it writes part of what is typed again.</summary>
public sealed class VimFollowedEditsTests
{
    [Fact]
    public void A_formatting_before_the_sessions_start_leaves_dot_typing_what_the_keys_typed()
    {
        Vim vim = new();
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("x=|1\ny=2\n");
        (text, carets) = VimTests.Drive(vim, text, carets, "A;");
        Assert.Equal("x=1;|\ny=2\n", Marked.Show(text, carets));
        // The host's layout of the statement: a space either side of its '='.
        TextEdit[] laid = [new(1, 0, " "), new(2, 0, " ")];
        vim.FollowEdits(laid, carets.Primary.Caret);
        (text, carets) = (text.Apply(laid), TextEditing.MapThrough(carets, laid));
        (text, carets) = VimTests.Drive(vim, text, carets, "<Esc>j.");
        Assert.Equal("x = 1;\ny=2|;\n", Marked.Show(text, carets));
    }

    [Fact]
    public void A_formatting_taking_in_the_sessions_start_is_typed_again_from_where_it_begins()
    {
        Vim vim = new();
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("int| \nlong\n");
        (text, carets) = VimTests.Drive(vim, text, carets, "a x;");
        Assert.Equal("int  x;|\nlong\n", Marked.Show(text, carets));
        // Two spaces, the first before the session's start and the second typed in it, laid out as one.
        TextEdit[] laid = [new(3, 2, " ")];
        vim.FollowEdits(laid, carets.Primary.Caret);
        (text, carets) = (text.Apply(laid), TextEditing.MapThrough(carets, laid));
        (text, carets) = VimTests.Drive(vim, text, carets, "<Esc>j$.");
        Assert.Equal("int x;\nlong x|;\n", Marked.Show(text, carets));
    }

    [Fact]
    public void A_formatting_after_the_caret_leaves_dot_typing_what_the_keys_typed()
    {
        Vim vim = new();
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("|a\nb  c\n");
        (text, carets) = VimTests.Drive(vim, text, carets, "ix");
        Assert.Equal("x|a\nb  c\n", Marked.Show(text, carets));
        // The host's layout of what follows: two spaces laid out as one.
        TextEdit[] laid = [new(4, 2, " ")];
        vim.FollowEdits(laid, carets.Primary.Caret);
        (text, carets) = (text.Apply(laid), TextEditing.MapThrough(carets, laid));
        (text, carets) = VimTests.Drive(vim, text, carets, "<Esc>j0.");
        Assert.Equal("xa\n|xb c\n", Marked.Show(text, carets));
    }
}
