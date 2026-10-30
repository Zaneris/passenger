using Passenger.Terminal;

namespace Passenger.UnitTests.Terminal;

/// <summary>A program the terminal starts, given the variables of a tmux or screen Passenger was started in: each one
/// Passenger inherited is taken away unless the caller gives it a value of its own.</summary>
public class OuterTerminalTests
{
    private static readonly Dictionary<string, string> Inherited = new()
    {
        ["TMUX"] = "/tmp/tmux-501/default,4242,0",
        ["TMUX_PANE"] = "%3",
        ["STY"] = "4242.ttys001.host",
        ["COLUMNS"] = "200",
        ["HOME"] = "/home/someone",
    };

    [Fact]
    public void Each_variable_of_the_outer_terminal_passenger_inherited_is_taken_away_from_a_shell()
    {
        Dictionary<string, string> environment = new() { ["TERM"] = "xterm-256color" };
        TerminalSession.WithoutOuterTerminal(environment, Inherited.GetValueOrDefault);
        Assert.True(environment.Count == 5 && environment["TMUX"] == "" && environment["TMUX_PANE"] == "" && environment["STY"] == ""
            && environment["COLUMNS"] == "" && environment["TERM"] == "xterm-256color",
            $"A shell in the Terminal takes itself for a pane of the outer tmux or screen: {Listed(environment)}");
    }

    [Fact]
    public void A_runs_inherited_value_is_taken_away_and_a_value_of_its_own_reaches_its_program()
    {
        Dictionary<string, string> environment = new()
        {
            ["TMUX"] = Inherited["TMUX"],
            ["STY"] = "given",
            ["HOME"] = Inherited["HOME"],
        };
        TerminalSession.WithoutOuterTerminal(environment, Inherited.GetValueOrDefault);
        Assert.True(environment["TMUX"] == "" && environment["STY"] == "given" && environment["HOME"] == Inherited["HOME"],
            $"A run's program in the Terminal got the outer tmux's pane, or lost what its configuration gave it: {Listed(environment)}");
    }

    [Fact]
    public void A_variable_passenger_never_inherited_is_left_alone()
    {
        Dictionary<string, string> environment = [];
        TerminalSession.WithoutOuterTerminal(environment, _ => null);
        Assert.True(environment.Count == 0,
            $"A start outside tmux or screen was given variables to take away, so it no longer runs its program as it is: {Listed(environment)}");
    }

    private static string Listed(Dictionary<string, string> environment) =>
        string.Join(' ', environment.Select(variable => $"{variable.Key}={variable.Value}"));
}
