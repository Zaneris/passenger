using Passenger.Terminal;

namespace Passenger.UnitTests.Terminal;

/// <summary>A program the terminal runs on macOS or Linux with variables taken away, as env is told to run it: each name
/// unset, then the program and its arguments as given; a program with none to take away runs as it is.</summary>
public class UnsettingTests
{
    [Fact]
    public void Env_unsets_each_variable_taken_away_and_then_runs_the_program_as_given()
    {
        (string app, string[] arguments) = TerminalSession.Unsetting(["GIT_DIR", "-odd"], "/bin/prog", ["a", "b=c"]);
        Assert.Equal("/usr/bin/env", app);
        Assert.Equal(["-u", "GIT_DIR", "-u", "-odd", "--", "/bin/prog", "a", "b=c"], arguments);
        Assert.True(TerminalSession.Unsetting([], "/bin/prog", ["a"]) is ("/bin/prog", ["a"]), "A program with nothing to take away was not run as it is");
    }

    [Fact]
    public void A_name_no_environment_can_hold_is_never_given_to_env_which_would_refuse_it_and_run_nothing()
    {
        Assert.True(TerminalSession.Unsetting(["A=B", ""], "/bin/prog", ["a"]) is ("/bin/prog", ["a"]),
            "env was given a name holding '=' or an empty one: it refuses to unset it and the program never runs");
    }

    [Fact]
    public void A_program_named_with_an_equals_sign_is_run_by_shs_exec_never_taken_by_env_for_a_variable()
    {
        (string app, string[] arguments) = TerminalSession.Unsetting(["GIT_DIR"], "/tmp/a=b/prog", ["x"]);
        Assert.True(app == "/usr/bin/env" && arguments.SequenceEqual(["-u", "GIT_DIR", "--", "/bin/sh", "-c", "exec \"$0\" \"$@\"", "/tmp/a=b/prog", "x"]),
            $"env would take the program's name for a variable, run nothing and list the whole environment on the screen: {string.Join(' ', arguments)}");
    }
}
