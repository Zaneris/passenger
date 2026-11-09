using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A child's words as a person is told them: the first line that says anything, trimmed, whatever its lines end in;
/// what a failed child said, its errors before its output, cut to its cap, and its exit code as <see cref="ExitCodes"/> says
/// it where it said nothing, a crash's signal named; a child that gave no answer quoted by its first line.</summary>
public class ChildResultTests
{
    [Theory]
    [InlineData("0.0.0.0:55123\n[::]:55123\n", "0.0.0.0:55123")]
    [InlineData("\r\n  \n  Error: no such service: web\r\nmore\r\n", "Error: no such service: web")]
    [InlineData("one line", "one line")]
    [InlineData(" \n\t\n", "")]
    [InlineData("", "")]
    public void The_first_line_is_the_first_that_says_anything_trimmed(string words, string first)
    {
        Assert.Equal(first, ChildResult.FirstLineOf(words));
        Assert.Equal(first, new ChildResult(0, words, string.Empty).FirstLine);
    }

    [Fact]
    public void What_a_failed_child_said_is_its_errors_lines_joined_before_its_output()
    {
        Assert.Equal("Error: the keychain is locked try again", new ChildResult(3, "progress\n", "Error: the keychain is locked\r\n  try again\r\n").Said);
        Assert.Equal("progress done", new ChildResult(3, "progress\r\ndone\n", " \n").Said);
    }

    [Fact]
    public void A_child_that_said_nothing_is_said_by_its_exit_code_a_crash_naming_its_signal()
    {
        ChildResult crashed = new(139, " \n", "\r\n");
        Assert.Equal(ExitCodes.Said(139), crashed.Said);
        Assert.Equal(ExitCodes.Said(139), crashed.Ended);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("exit code 139 (SIGSEGV)", crashed.Said);
        }
    }

    [Fact]
    public void How_a_child_ended_is_its_exit_code_then_the_first_line_it_said()
    {
        Assert.Equal($"{ExitCodes.Said(134)}: handle_crash: Program crashed with signal 6",
            new ChildResult(134, "Godot Engine v4.5\n", "\nhandle_crash: Program crashed with signal 6\nDumping the backtrace\n").Ended);
        Assert.Equal($"{ExitCodes.Said(1)}: Godot Engine v4.5", new ChildResult(1, "Godot Engine v4.5\nmore\n", string.Empty).Ended);
    }

    [Fact]
    public void A_child_that_gave_no_answer_is_quoted_by_its_exit_code_and_first_line_its_words_said_whole()
    {
        ChildAnswer unanswered = new(null, new ChildResult(1, string.Empty, "You must install .NET to run this application.\nApp: dotnet\nArchitecture: arm64\n"));
        Assert.Equal($"{ExitCodes.Said(1)}: You must install .NET to run this application.", unanswered.Ended);
        Assert.Equal("You must install .NET to run this application. App: dotnet Architecture: arm64", unanswered.Said);
        ChildAnswer answered = new("10.0.100", null);
        Assert.Equal(string.Empty, answered.Ended);
        Assert.Equal(string.Empty, answered.Said);
    }

    [Fact]
    public void A_long_failure_is_cut_to_its_first_forty_lines_counting_the_rest()
    {
        string log = string.Concat(Enumerable.Range(1, 45).Select(i => $"#{i} building step {i}\n"));
        string said = new ChildResult(1, string.Empty, log).Said;
        Assert.True(said.StartsWith("#1 building step 1 #2 ", StringComparison.Ordinal) && said.Contains("#40 building step 40 … and 5 more lines", StringComparison.Ordinal)
            && !said.Contains("#41", StringComparison.Ordinal), $"A failed compose build's 45 lines of log, said as its problem, were not cut to 40: {said}");
    }
}
