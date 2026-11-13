using Passenger.Editor;
using Passenger.Text;
using Passenger.UnitTests.Text;

namespace Passenger.UnitTests.Editor;

/// <summary>Vim mode's keys as the carets and text they leave (<see cref="Marked"/>), checked against what Vim does with
/// the same keys: motions with counts, x and dd, Backspace, Enter, Delete and Tab, entering and leaving insert mode, and
/// the keys it leaves to the keymap.</summary>
public class VimTests
{
    [Theory]
    [InlineData("ab|cd", "h", "a|bcd")]
    [InlineData("|abcd", "h", "|abcd")]
    [InlineData("ab|cd", "9h", "|abcd")]
    [InlineData("a|bcd", "l", "ab|cd")]
    [InlineData("abc|d", "l", "abc|d")]
    [InlineData("a|bcd\nef", "5l", "abc|d\nef")]
    [InlineData("on|e\ntwo", "l", "on|e\ntwo")]
    [InlineData("  a|bc", "0", "|  abc")]
    [InlineData("  a|bc", "^", "  |abc")]
    [InlineData("|abc\ndef", "$", "ab|c\ndef")]
    [InlineData("|abc\ndef", "2$", "abc\nde|f")]
    public void Moves_along_a_line_stay_on_its_characters(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("ab|cd\nefgh", "j", "abcd\nef|gh")]
    [InlineData("abcd\nef|gh", "k", "ab|cd\nefgh")]
    [InlineData("abc|d\nx\nabcd", "jj", "abcd\nx\nabc|d")]
    [InlineData("a|b\nc\nd", "9j", "ab\nc\n|d")]
    [InlineData("ab\nc\n|d", "99k", "|ab\nc\nd")]
    [InlineData("a|b\nc\n", "5j", "ab\n|c\n")]
    [InlineData("|abc\nx\nabcdef", "$jj", "abc\nx\nabcde|f")]
    public void Up_and_down_keep_the_column_aimed_for_and_stop_at_the_last_line(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("|one two", "w", "one |two")]
    [InlineData("|foo.bar", "w", "foo|.bar")]
    [InlineData("|foo.bar", "ww", "foo.|bar")]
    [InlineData("on|e\n\ntwo", "w", "one\n|\ntwo")]
    [InlineData("on|e\n\ntwo", "ww", "one\n\n|two")]
    [InlineData("one\n  |  two", "w", "one\n    |two")]
    [InlineData("one t|wo", "w", "one tw|o")]
    [InlineData("|a b c d", "3w", "a b c |d")]
    [InlineData("one |two", "b", "|one two")]
    [InlineData("one tw|o", "b", "one |two")]
    [InlineData("one\n\n|two", "b", "one\n|\ntwo")]
    [InlineData("foo.|bar", "b", "foo|.bar")]
    [InlineData("|one two", "e", "on|e two")]
    [InlineData("on|e two", "e", "one tw|o")]
    [InlineData("one\n\n|two", "e", "one\n\ntw|o")]
    [InlineData("|ab\n\ncd", "e", "a|b\n\ncd")]
    [InlineData("|a.b", "e", "a|.b")]
    [InlineData("one\r\n\r\n|two", "b", "one\r\n|\r\ntwo")]
    public void Word_motions_stop_as_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("a\n  b\nc|", "gg", "|a\n  b\nc")]
    [InlineData("|a\n  b\nc", "G", "a\n  b\n|c")]
    [InlineData("|a\n  b\nc\n", "G", "a\n  b\n|c\n")]
    [InlineData("|a\n  b\nc", "2G", "a\n  |b\nc")]
    [InlineData("a\n  b\n|c", "2gg", "a\n  |b\nc")]
    [InlineData("|a\nb", "99G", "a\n|b")]
    public void Line_jumps_land_on_the_first_non_blank(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("a|bc", "x", "a|c")]
    [InlineData("ab|c", "x", "a|b")]
    [InlineData("a|bcd\nef", "9x", "|a\nef")]
    [InlineData("|\nab", "x", "|\nab")]
    [InlineData("a|b|c", "x", "|a")]
    [InlineData("one\n  t|wo\nthree", "dd", "one\n|three")]
    [InlineData("one\ntwo\nth|ree", "dd", "one\n|two")]
    [InlineData("o|ne\ntwo\nthree", "2dd", "|three")]
    [InlineData("o|ne\ntwo", "9dd", "|")]
    [InlineData("one\ntw|o\n", "dd", "|one\n")]
    public void X_and_dd_delete_and_leave_the_caret_where_vim_does(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("ab\n|cd", "<BS>", "a|b\ncd")]
    [InlineData("a|b", "<BS>", "|ab")]
    [InlineData("ab\n\n|cd", "3<BS>", "|ab\n\ncd")]
    [InlineData("a|b\n  cd", "<CR>", "ab\n  |cd")]
    [InlineData("a|b\nc\n  d", "2<CR>", "ab\nc\n  |d")]
    [InlineData("ab\n  c|d", "<CR>", "ab\n  c|d")]
    [InlineData("a|bc", "<Del>", "a|c")]
    [InlineData("a|bcd", "2<Del>x", "a|cd")]
    [InlineData("a|bc", "<Tab>", "a|bc")]
    public void Keys_that_would_edit_in_the_keymap_move_or_delete_as_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("a|bc", "iX<Esc>", "a|Xbc")]
    [InlineData("a|bc", "aX<Esc>", "ab|Xc")]
    [InlineData("  a|bc", "IX<Esc>", "  |Xabc")]
    [InlineData("a|bc", "AX<Esc>", "abc|X")]
    [InlineData("a|bc", "i<Esc>", "|abc")]
    [InlineData("|abc", "i<Esc>", "|abc")]
    public void Insert_mode_types_and_escape_steps_back_onto_the_text(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Fact]
    public void A_count_with_d_waits_and_escape_forgets_it()
    {
        Vim vim = new();
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("a|b");
        Assert.True(vim.Press("2", text, carets).Taken);
        Assert.True(vim.Press("d", text, carets).Taken);
        Assert.Equal("2d", vim.Pending);
        Assert.True(vim.Press("<Esc>", text, carets).Taken);
        Assert.Equal(string.Empty, vim.Pending);
    }

    [Fact]
    public void Keys_normal_mode_does_not_take_go_to_the_keymap()
    {
        Vim vim = new();
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("a|b");
        Assert.False(vim.Press("<D-s>", text, carets).Taken);
        Assert.False(vim.Press("<Esc>", text, carets).Taken);
        Assert.True(vim.Press("q", text, carets).Taken);
        vim.Press("i", text, carets);
        Assert.False(vim.Press("x", text, carets).Taken);
    }

    [Theory]
    [InlineData("u", EditorActions.Undo, 1)]
    [InlineData("3u", EditorActions.Undo, 3)]
    [InlineData("<C-r>", EditorActions.Redo, 1)]
    [InlineData("o", EditorActions.StartNewLine, 1)]
    [InlineData("O", EditorActions.StartNewLineBefore, 1)]
    public void Undo_redo_and_open_line_run_the_editors_own_actions(string keys, string action, int repeat)
    {
        Vim vim = new();
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("a|b");
        VimResult result = VimResult.Left;
        foreach (string key in Keys(keys))
        {
            result = vim.Press(key, text, carets);
        }
        Assert.Equal((action, repeat), (result.Action, result.Repeat));
        Assert.Equal(keys is "o" or "O" ? VimMode.Insert : VimMode.Normal, vim.Mode);
    }

    [Fact]
    public void Each_x_is_an_edit_of_its_own_kind_so_undo_takes_back_one_at_a_time()
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("a|bc");
        Assert.Equal(EditKind.Other, new Vim().Press("x", text, carets).Edit?.Kind);
    }

    [Fact]
    public void A_huge_count_ends_at_the_texts_edge()
    {
        Assert.Equal("a b |c", Run("|a b c", "99999999999w"));
    }

    /// <summary>The keys pressed in turn on the marked text, insert mode's typing standing in for the editor's.</summary>
    private static string Run(string before, string keys)
    {
        Vim vim = new();
        (TextSnapshot text, SelectionSet carets) = Marked.Parse(before);
        foreach (string key in Keys(keys))
        {
            VimResult result = vim.Press(key, text, carets);
            if (!result.Taken)
            {
                Assert.Equal(VimMode.Insert, vim.Mode);
                EditPlan typed = TextEditing.Insert(text, carets, key);
                (text, carets) = (text.Apply(typed.Edits), typed.Selections);
            }
            else if (result.Edit is { } edit)
            {
                (text, carets) = (text.Apply(edit.Edits), edit.Selections);
            }
            else if (result.Selections is { } moved)
            {
                carets = moved;
            }
        }
        return Marked.Show(text, carets);
    }

    private static IEnumerable<string> Keys(string keys)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            int close = keys[i] == '<' ? keys.IndexOf('>', i) : -1;
            yield return close > i ? keys[i..(close + 1)] : keys[i].ToString();
            i = close > i ? close : i;
        }
    }
}
