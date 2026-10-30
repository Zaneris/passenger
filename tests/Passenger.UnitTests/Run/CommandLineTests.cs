using Passenger.App.Run;

namespace Passenger.UnitTests.Run;

/// <summary>A command line naming a program to start (the terminal's shell): one whose first word is empty names none, so
/// Settings refuses it; a program with or without arguments, and no line at all (the program Passenger finds), name one.</summary>
public class CommandLineTests
{
    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"\" -l")]
    [InlineData("\" \"")]
    [InlineData("\"")]
    public void A_line_whose_first_word_is_empty_names_no_program(string line) =>
        Assert.True(CommandLine.NamesNoProgram(line), $"The shell path {line} was taken for a program, so a new terminal tab would try to start one named by nothing");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/bin/zsh")]
    [InlineData("/bin/zsh -l")]
    [InlineData("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo")]
    public void A_program_or_no_line_at_all_is_not_refused(string? line) =>
        Assert.False(CommandLine.NamesNoProgram(line), $"The shell path {line} was refused, which names a program or leaves the choice to Passenger");
}
