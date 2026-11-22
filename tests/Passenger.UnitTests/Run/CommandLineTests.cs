using Passenger.App.Run;

namespace Passenger.UnitTests.Run;

/// <summary>A command line naming a program to start (the terminal's shell): one whose first word is empty names none, so
/// Settings refuses it; a program with or without arguments, and no line at all (the program Passenger finds), name one.</summary>
public class CommandLineTests
{
    [Fact]
    public void A_command_line_is_refused_only_where_its_first_word_names_no_program() => Cases.All(
        ("a line whose first word is empty names no program", () => Cases.Each<string>(
        [
            "\"\"",
            "\"\" -l",
            "\"",
        ], A_line_whose_first_word_is_empty_names_no_program)),
        ("a program or no line at all is not refused", () => Cases.Each<string?>(
        [
            null,
            "/bin/zsh -l",
            "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo",
        ], A_program_or_no_line_at_all_is_not_refused)));

    private void A_line_whose_first_word_is_empty_names_no_program(string line) =>
        Assert.True(CommandLine.NamesNoProgram(line), $"The shell path {line} was taken for a program, so a new terminal tab would try to start one named by nothing");

    private void A_program_or_no_line_at_all_is_not_refused(string? line) =>
        Assert.False(CommandLine.NamesNoProgram(line), $"The shell path {line} was refused, which names a program or leaves the choice to Passenger");
}
