using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Text;

namespace Passenger.UnitTests.Problems;

/// <summary>Which problems Problems shows: those of the severities left on whose message, code or file name holds the
/// filter's text, ignoring case, never the folders over the file.</summary>
public sealed class ProblemFilterTests
{
    private static readonly Problem Error = new(ProblemSeverity.Error, "The name 'missing' does not exist", "CS0103", Path.Combine("src", "Orders", "Order.cs"), new TextPosition(3, 4), "Shop");
    private static readonly Problem Warning = new(ProblemSeverity.Warning, "The variable 'count' is declared but never used", "CS0168", Path.Combine("src", "Orders", "Line.cs"), default, "Shop");
    private static readonly Problem Message = new(ProblemSeverity.Message, "Use a primary constructor", "IDE0290", null, default, "Shop");

    [Fact]
    public void Problems_shows_the_severities_left_on_whose_message_code_or_file_holds_the_filter() => Cases.All(
        ("A window starts with errors and warnings and a severity turned off hides its problems alone", A_window_starts_with_errors_and_warnings_and_a_severity_turned_off_hides_its_problems_alone),
        ("The text is found in a message a code or a file name ignoring case", The_text_is_found_in_a_message_a_code_or_a_file_name_ignoring_case),
        ("A problem holding the text is held whatever its severity and passes only while its severity shows", A_problem_holding_the_text_is_held_whatever_its_severity_and_passes_only_while_its_severity_shows));

    private void A_window_starts_with_errors_and_warnings_and_a_severity_turned_off_hides_its_problems_alone()
    {
        Assert.Equal([true, true, false], new[] { Error, Warning, Message }.Select(ProblemFilter.Default.Passes));
        ProblemFilter filter = ProblemFilter.Default.Showing(ProblemSeverity.Message, true).Showing(ProblemSeverity.Warning, false);
        Assert.Equal([true, false, true], new[] { Error, Warning, Message }.Select(filter.Passes));
        Assert.False(filter.IsNone);
        Assert.True(filter.Showing(ProblemSeverity.Warning, true).IsNone);
    }

    private void The_text_is_found_in_a_message_a_code_or_a_file_name_ignoring_case()
    {
        Assert.Equal([true, false, false], new[] { Error, Warning, Message }.Select(new ProblemFilter(Messages: true, Text: "MISSING").Passes));
        Assert.Equal([false, false, true], new[] { Error, Warning, Message }.Select(new ProblemFilter(Messages: true, Text: "ide02").Passes));
        Assert.Equal([false, true, false], new[] { Error, Warning, Message }.Select(new ProblemFilter(Messages: true, Text: "line.cs").Passes));
        Assert.False(new ProblemFilter(Text: "Orders").Passes(Error), "A folder over the file is no part of its name");
    }

    private void A_problem_holding_the_text_is_held_whatever_its_severity_and_passes_only_while_its_severity_shows()
    {
        ProblemFilter filter = new(Warnings: false, Text: "never used");
        Assert.True(filter.Holds(Warning));
        Assert.False(filter.Passes(Warning));
    }
}
