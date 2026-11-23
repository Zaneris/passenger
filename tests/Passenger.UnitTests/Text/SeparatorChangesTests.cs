using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>How the lines two texts have alike changed their separators (<see cref="LineDiff.SeparatorChanges(string, string, bool)"/>),
/// which a comparison of their lines alone never sees.</summary>
public class SeparatorChangesTests
{
    [Fact]
    public void Lines_alike_in_two_texts_tell_how_their_separators_changed() => Cases.All(
        ("Lines alike but for their separators are said by how many from which to which", Lines_alike_but_for_their_separators_are_said_by_how_many_from_which_to_which),
        ("Texts whose lines alike keep their separators have none changed", Texts_whose_lines_alike_keep_their_separators_have_none_changed),
        ("Lines turned between separators git stores alike are said only where one is added or removed at the end", Lines_turned_between_separators_git_stores_alike_are_said_only_where_one_is_added_or_removed_at_the_end),
        ("A changed lines separator goes with its change and only the lines alike are counted", A_changed_lines_separator_goes_with_its_change_and_only_the_lines_alike_are_counted));

    private void Lines_alike_but_for_their_separators_are_said_by_how_many_from_which_to_which() => Cases.Each<(string before, string after, string said)>(
    [
        ("a\nb\nc\n", "a\r\nb\r\nc\r\n", "Line separators changed: 3 lines from LF to CRLF"),
        ("a\r\nb\r\nc\r\n", "a\nb\r\nc\r\n", "Line separators changed: 1 line from CRLF to LF"),
        ("a\nb", "a\nb\n", "Line separators changed: LF added at the end"),
        ("a\r\nb\r\n", "a\r\nb", "Line separators changed: CRLF removed from the end"),
        ("a\nb\nc", "a\r\nb\r\nc\r\n", "Line separators changed: 2 lines from LF to CRLF; CRLF added at the end"),
        // The text's end is its last line's, whichever lines were added or changed before it.
        ("a\nb", "a\nb\nc\n", "Line separators changed: LF added at the end"),
        ("a\nb\n", "a\nc", "Line separators changed: LF removed from the end"),
    ], row =>
    {
        (string before, string after, string said) = row;
        
                Assert.Equal(said, LineDiff.SeparatorChanges(before, after, storedAlike: false));
    });

    private void Texts_whose_lines_alike_keep_their_separators_have_none_changed() => Cases.Each<(string before, string after)>(
    [
        ("a\nb\nc\n", "a\nb\nc\n"),
        ("a\r\nb\r\n", "a\r\nb\r\n"),
        ("", ""),
        ("a\nb\n", "a\nB\n"),
        // A line added after a last line with none, or a last line removed, leaves the text ending as it did.
        ("a\nb", "a\nb\nc"),
        ("a\nb\nc", "a\nb"),
    ], row =>
    {
        (string before, string after) = row;
        
                Assert.Null(LineDiff.SeparatorChanges(before, after, storedAlike: false));
    });

    /// <summary>Where git stores LF and CRLF alike (core.autocrlf, a text attribute), a line turned from one to the other
    /// changed nothing git keeps, but a separator added or removed at the end is still a change.</summary>
    private void Lines_turned_between_separators_git_stores_alike_are_said_only_where_one_is_added_or_removed_at_the_end() => Cases.Each<(string before, string after, string? said)>(
    [
        ("a\nb\nc\n", "a\r\nb\r\nc\r\n", null),
        ("a\r\nb\r\nc\r\n", "a\nb\r\nc\n", null),
        ("a\nb\nc\n", "a\r\nB\r\nc\r\n", null),
        ("a\nb", "a\r\nb\r\n", "Line separators changed: CRLF added at the end"),
        ("a\r\nb\r\n", "a\nb", "Line separators changed: CRLF removed from the end"),
    ], row =>
    {
        (string before, string after, string? said) = row;
        
                Assert.Equal(said, LineDiff.SeparatorChanges(before, after, storedAlike: true));
    });

    private void A_changed_lines_separator_goes_with_its_change_and_only_the_lines_alike_are_counted()
    {
        // Line b changed; a and c turned CRLF, and the lines around the change keep their pairs.
        Assert.Equal("Line separators changed: 2 lines from LF to CRLF", LineDiff.SeparatorChanges("a\nb\nc\n", "a\r\nB\r\nc\r\n", storedAlike: false));
        Assert.Equal("Line separators changed: 2 lines from LF to CRLF", LineDiff.SeparatorChanges("a\nb\nc\nd\n", "a\r\nx\ny\nc\nd\r\n", storedAlike: false));
    }
}
