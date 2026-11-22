using System.Globalization;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A child's words as a person is told them: the first line that says anything, trimmed, whatever its lines end in;
/// what a failed child said, its errors before its output, cut to its cap, and its exit code as <see cref="ExitCodes"/> says
/// it where it said nothing, a crash's signal named; its words masked whole before their lines are joined; a child that
/// gave no answer quoted by its first line.</summary>
public class ChildResultTests
{
    [Fact]
    public void A_childs_words_are_told_as_its_first_line_that_says_anything_and_its_errors_before_its_output() => Cases.All(
        ("The first line is the first that says anything trimmed", The_first_line_is_the_first_that_says_anything_trimmed),
        ("What a failed child said is its errors lines joined before its output", What_a_failed_child_said_is_its_errors_lines_joined_before_its_output),
        ("A child that said nothing is said by its exit code a crash naming its signal", A_child_that_said_nothing_is_said_by_its_exit_code_a_crash_naming_its_signal),
        ("How a child ended is its exit code then the first line it said", How_a_child_ended_is_its_exit_code_then_the_first_line_it_said),
        ("A child that gave no answer is quoted by its exit code and first line its words said whole", A_child_that_gave_no_answer_is_quoted_by_its_exit_code_and_first_line_its_words_said_whole),
        ("A long failure is cut to its first forty lines counting the rest", A_long_failure_is_cut_to_its_first_forty_lines_counting_the_rest),
        ("A secret of several lines is masked whole before its lines are joined", A_secret_of_several_lines_is_masked_whole_before_its_lines_are_joined),
        ("A childs end says how long it ran in the ides digits under a culture writing a decimal comma", A_childs_end_says_how_long_it_ran_in_the_ides_digits_under_a_culture_writing_a_decimal_comma));

    private void The_first_line_is_the_first_that_says_anything_trimmed() => Cases.Each<(string words, string first)>(
    [
        ("0.0.0.0:55123\n[::]:55123\n", "0.0.0.0:55123"),
        ("\r\n  \n  Error: no such service: web\r\nmore\r\n", "Error: no such service: web"),
        ("one line", "one line"),
        (" \n\t\n", ""),
        ("", ""),
    ], row =>
    {
        (string words, string first) = row;
        Assert.Equal(first, ChildResult.FirstLineOf(words));
        Assert.Equal(first, new ChildResult(0, words, string.Empty).FirstLine);
    });

    private void What_a_failed_child_said_is_its_errors_lines_joined_before_its_output()
    {
        Assert.Equal("Error: the keychain is locked try again", new ChildResult(3, "progress\n", "Error: the keychain is locked\r\n  try again\r\n").Said);
        Assert.Equal("progress done", new ChildResult(3, "progress\r\ndone\n", " \n").Said);
    }

    private void A_child_that_said_nothing_is_said_by_its_exit_code_a_crash_naming_its_signal()
    {
        ChildResult crashed = new(139, " \n", "\r\n");
        Assert.Equal(ExitCodes.Said(139), crashed.Said);
        Assert.Equal(ExitCodes.Said(139), crashed.Ended);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("exit code 139 (SIGSEGV)", crashed.Said);
        }
    }

    private void How_a_child_ended_is_its_exit_code_then_the_first_line_it_said()
    {
        Assert.Equal($"{ExitCodes.Said(134)}: handle_crash: Program crashed with signal 6",
            new ChildResult(134, "Godot Engine v4.5\n", "\nhandle_crash: Program crashed with signal 6\nDumping the backtrace\n").Ended);
        Assert.Equal($"{ExitCodes.Said(1)}: Godot Engine v4.5", new ChildResult(1, "Godot Engine v4.5\nmore\n", string.Empty).Ended);
    }

    private void A_child_that_gave_no_answer_is_quoted_by_its_exit_code_and_first_line_its_words_said_whole()
    {
        ChildAnswer unanswered = new(null, new ChildResult(1, string.Empty, "You must install .NET to run this application.\nApp: dotnet\nArchitecture: arm64\n"));
        Assert.Equal($"{ExitCodes.Said(1)}: You must install .NET to run this application.", unanswered.Ended);
        Assert.Equal("You must install .NET to run this application. App: dotnet Architecture: arm64", unanswered.Said);
        ChildAnswer answered = new("10.0.100", null);
        Assert.Equal(string.Empty, answered.Ended);
        Assert.Equal(string.Empty, answered.Said);
    }

    private void A_long_failure_is_cut_to_its_first_forty_lines_counting_the_rest()
    {
        string log = string.Concat(Enumerable.Range(1, 45).Select(i => $"#{i} building step {i}\n"));
        string said = new ChildResult(1, string.Empty, log).Said;
        Assert.True(said.StartsWith("#1 building step 1 #2 ", StringComparison.Ordinal) && said.Contains("#40 building step 40 … and 5 more lines", StringComparison.Ordinal)
            && !said.Contains("#41", StringComparison.Ordinal), $"A failed compose build's 45 lines of log, said as its problem, were not cut to 40: {said}");
    }

    private void A_secret_of_several_lines_is_masked_whole_before_its_lines_are_joined()
    {
        const string Key = "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\n-----END PRIVATE KEY-----";
        ChildResult echoed = new(15, "invalid interpolation: SIGNING_KEY=-----BEGIN PRIVATE KEY-----\r\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\r\n-----END PRIVATE KEY-----\r\nis not valid\r\n",
            string.Empty);
        string said = echoed.SaidAs(words => words.Replace("\r", string.Empty, StringComparison.Ordinal).Replace(Key, "••••••", StringComparison.Ordinal));
        Assert.True(said == "invalid interpolation: SIGNING_KEY=•••••• is not valid", $"A PEM key compose echoed was shown, its lines joined before the mask could find it: {said}");
    }

    /// <summary>A child's line for the log says how long it ran in the IDE's digits, whatever the person's culture writes.</summary>
    private void A_childs_end_says_how_long_it_ran_in_the_ides_digits_under_a_culture_writing_a_decimal_comma()
    {
        CultureInfo was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            string line = new ChildEnd(7, "dotnet build", 1, TimeSpan.FromMilliseconds(1500)).ToString();
            Assert.True(line == "Process 7 exited with code 1 after 1.5 s: dotnet build",
                $"A child's end under a German culture was logged as \"{line}\": its time read one way there and another in every other line");
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }
}
