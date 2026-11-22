using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>A layout's one edit around the caret as the edits it makes to each run of white space, the caret ending a run
/// (<see cref="TextEditing.Respaced"/>): together they write what the edit writes, none rewrites a character of code or
/// reaches across the caret, and an edit changing code, or putting the caret among other code, is none.</summary>
public sealed class RespacedTests
{
    [Fact]
    public void A_layouts_edit_around_the_caret_is_its_edits_to_each_run_of_white_space() => Cases.All(
        ("The edits write what the layout does in white space alone none across the caret", The_edits_write_what_the_layout_does_in_white_space_alone_none_across_the_caret),
        ("An edit changing code or putting the caret among other code is none", An_edit_changing_code_or_putting_the_caret_among_other_code_is_none),
        ("An edit or a caret outside the text or the edit is none", An_edit_or_a_caret_outside_the_text_or_the_edit_is_none));

    private void The_edits_write_what_the_layout_does_in_white_space_alone_none_across_the_caret() => Cases.Each<(string marked, string written, string laid, int count)>(
    [
        ("x = new()[ {\n    |\n]}", "\n{\n    |\n", "x = new()\n{\n    |\n}", 1),
        ("    if (a)[ {\n        |\n    ]}", "\n    {\n        |\n    ", "    if (a)\n    {\n        |\n    }", 1),
        ("[{\n|\n]}", "{\n    |\n  ", "{\n    |\n  }", 2),
        ("f(() =>[{\n  |\n])", " {\n    |\n", "f(() => {\n    |\n)", 2),
        ("x =[ {\r\n    |\r\n]}", "\r\n{\r\n    |\r\n", "x =\r\n{\r\n    |\r\n}", 1),
        ("[{\n    |\n]}", "{\n    |\n", "{\n    |\n}", 0),
    ], row =>
    {
        (string marked, string written, string laid, int count) = row;
        (TextSnapshot text, TextEdit edit, int caret, int into) = Parse(marked, written);
        List<TextEdit>? edits = TextEditing.Respaced(text, edit, caret, into);
        Assert.NotNull(edits);
        Assert.Equal(laid.Replace("|", "", StringComparison.Ordinal), text.Apply(edits).ToString());
        Assert.Equal(count, edits.Count);
        foreach (TextEdit made in edits)
        {
            Assert.True(string.IsNullOrWhiteSpace(text.GetText(made.Start, made.Length)) && string.IsNullOrWhiteSpace(made.Text),
                $"An edit at {made.Start} rewrote code: '{text.GetText(made.Start, made.Length)}' to '{made.Text}'");
            Assert.False(made.Start < caret && made.End > caret, $"An edit at {made.Start} reached across the caret at {caret}");
        }
        Assert.Equal(laid.IndexOf('|', StringComparison.Ordinal), edit.Start + into);
    });

    private void An_edit_changing_code_or_putting_the_caret_among_other_code_is_none() => Cases.Each<(string marked, string written)>(
    [
        ("[a b|]", "a c|"),
        ("[a b|]", "a bc|"),
        ("[{ |x }]", "{ x| }"),
        ("[{ |x }]", "{ x |}"),
        ("[{|}]", "{}|"),
        ("[{|}]", "{ |} x"),
    ], row =>
    {
        (string marked, string written) = row;
        (TextSnapshot text, TextEdit edit, int caret, int into) = Parse(marked, written);
        Assert.Null(TextEditing.Respaced(text, edit, caret, into));
    });

    private void An_edit_or_a_caret_outside_the_text_or_the_edit_is_none()
    {
        TextSnapshot text = TextSnapshot.From("{\n}");
        Assert.Null(TextEditing.Respaced(text, new TextEdit(1, 5, "\n"), 1, 0));
        Assert.Null(TextEditing.Respaced(text, new TextEdit(1, 1, "\n"), 3, 0));
        Assert.Null(TextEditing.Respaced(text, new TextEdit(1, 1, "\n"), 1, 2));
    }

    /// <summary>The text with the edit's range between '[' and ']' and the caret at '|', and what the edit writes with the
    /// caret's place in it at '|'.</summary>
    private static (TextSnapshot Text, TextEdit Edit, int Caret, int Into) Parse(string marked, string written)
    {
        int start = marked.IndexOf('[', StringComparison.Ordinal);
        string plain = marked.Replace("[", "", StringComparison.Ordinal);
        int caret = plain.IndexOf('|', StringComparison.Ordinal);
        plain = plain.Replace("|", "", StringComparison.Ordinal);
        int end = marked.Replace("|", "", StringComparison.Ordinal).IndexOf(']', StringComparison.Ordinal) - 1;
        plain = plain.Replace("]", "", StringComparison.Ordinal);
        caret = caret > end ? caret - 1 : caret;
        int into = written.IndexOf('|', StringComparison.Ordinal);
        return (TextSnapshot.From(plain), new TextEdit(start, end - start, written.Replace("|", "", StringComparison.Ordinal)), caret, into);
    }
}
