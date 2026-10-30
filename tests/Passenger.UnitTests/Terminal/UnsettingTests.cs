using Passenger.Terminal;

namespace Passenger.UnitTests.Terminal;

/// <summary>A program the terminal runs on macOS or Linux with variables taken away, as env is told to run it: each name
/// unset, then the program and its arguments as given; a program with none to take away runs as it is. The loader's
/// variables, which env loses on macOS, are carried in the environment for sh to set again, never on its command line.</summary>
public class UnsettingTests
{
    private static readonly Dictionary<string, string> NoLoader = [];

    [Fact]
    public void Env_unsets_each_variable_taken_away_and_then_runs_the_program_as_given()
    {
        Dictionary<string, string> environment = [];
        (string app, string[] arguments) = TerminalSession.Unsetting(["GIT_DIR", "-odd"], "/bin/prog", ["a", "b=c"], environment, NoLoader);
        Assert.Equal("/usr/bin/env", app);
        Assert.Equal(["-u", "GIT_DIR", "-u", "-odd", "--", "/bin/prog", "a", "b=c"], arguments);
        Assert.True(TerminalSession.Unsetting([], "/bin/prog", ["a"], environment, NoLoader) is ("/bin/prog", ["a"]), "A program with nothing to take away was not run as it is");
        Assert.True(environment.Count == 0, "A start with no loader variables was given variables to carry");
    }

    [Fact]
    public void A_name_no_environment_can_hold_is_never_given_to_env_which_would_refuse_it_and_run_nothing()
    {
        Assert.True(TerminalSession.Unsetting(["A=B", ""], "/bin/prog", ["a"], new Dictionary<string, string>(), NoLoader) is ("/bin/prog", ["a"]),
            "env was given a name holding '=' or an empty one: it refuses to unset it and the program never runs");
    }

    [Fact]
    public void A_program_named_with_an_equals_sign_is_run_by_shs_exec_never_taken_by_env_for_a_variable()
    {
        (string app, string[] arguments) = TerminalSession.Unsetting(["GIT_DIR"], "/tmp/a=b/prog", ["x"], new Dictionary<string, string>(), NoLoader);
        Assert.True(app == "/usr/bin/env" && arguments.SequenceEqual(["-u", "GIT_DIR", "--", "/bin/sh", "-c", "exec \"$0\" \"$@\"", "/tmp/a=b/prog", "x"]),
            $"env would take the program's name for a variable, run nothing and list the whole environment on the screen: {string.Join(' ', arguments)}");
    }

    [Fact]
    public void A_loader_variable_env_would_lose_is_carried_in_the_environment_and_set_again_by_sh_its_value_on_no_command_line()
    {
        Dictionary<string, string> environment = new() { ["TMUX"] = "" };
        Dictionary<string, string> loader = new() { ["DYLD_X"] = "/secret/value", ["DYLD_LIBRARY_PATH"] = "/lib's" };
        (string app, string[] arguments) = TerminalSession.Unsetting(["TMUX"], "/bin/prog", ["a"], environment, loader);
        string setAgain = $"DYLD_LIBRARY_PATH=\"${TerminalSession.KeptPrefix}DYLD_LIBRARY_PATH\"; export DYLD_LIBRARY_PATH; unset {TerminalSession.KeptPrefix}DYLD_LIBRARY_PATH; "
            + $"DYLD_X=\"${TerminalSession.KeptPrefix}DYLD_X\"; export DYLD_X; unset {TerminalSession.KeptPrefix}DYLD_X; exec \"$0\" \"$@\"";
        Assert.True(app == "/usr/bin/env" && arguments.SequenceEqual(["-u", "TMUX", "--", "/bin/sh", "-c", setAgain, "/bin/prog", "a"]),
            $"macOS's loader takes DYLD_X out of env's environment, so the program run never has it: {string.Join(' ', arguments)}");
        Assert.True(environment.GetValueOrDefault(TerminalSession.KeptPrefix + "DYLD_X") == "/secret/value"
            && environment.GetValueOrDefault(TerminalSession.KeptPrefix + "DYLD_LIBRARY_PATH") == "/lib's",
            $"The loader's variables were not carried in the environment for sh to set again: {string.Join(' ', environment.Keys)}");
        Assert.False(arguments.Any(word => word.Contains("/secret/value", StringComparison.Ordinal) || word.Contains("/lib's", StringComparison.Ordinal)),
            $"A loader variable's value went on a command line, where any user's ps reads it: {string.Join(' ', arguments)}");
    }

    [Fact]
    public void A_loader_variable_with_nothing_taken_away_reaches_the_program_as_it_is()
    {
        Dictionary<string, string> environment = new() { ["DYLD_X"] = "value" };
        Assert.True(TerminalSession.Unsetting([], "/bin/prog", ["a"], environment, new Dictionary<string, string> { ["DYLD_X"] = "value" }) is ("/bin/prog", ["a"])
            && environment.Count == 1, "A start taking nothing away no longer runs its program as it is");
    }

    [Fact]
    public void A_loader_name_sh_cannot_set_is_left_out_of_its_line_which_would_otherwise_run_nothing()
    {
        Dictionary<string, string> environment = [];
        (_, string[] arguments) = TerminalSession.Unsetting(["TMUX"], "/bin/prog", [], environment, new Dictionary<string, string> { ["DYLD_A-B"] = "x" });
        Assert.True(arguments.SequenceEqual(["-u", "TMUX", "--", "/bin/prog"]) && environment.Count == 0,
            $"sh was told to set a name that is no identifier, a syntax error that runs nothing: {string.Join(' ', arguments)}");
    }

    [Fact]
    public void A_programs_loader_variables_are_those_inherited_set_over_by_the_starts_own_less_those_it_takes_away()
    {
        Dictionary<string, string> inherited = new()
        {
            ["DYLD_FALLBACK_LIBRARY_PATH"] = "/inherited",
            ["DYLD_INSERT_LIBRARIES"] = "/inherited.dylib",
            ["DYLD_FRAMEWORK_PATH"] = "/inherited/frameworks",
            ["HOME"] = "/Users/me",
        };
        Dictionary<string, string> environment = new()
        {
            ["DYLD_FRAMEWORK_PATH"] = "/given/frameworks",
            ["DYLD_INSERT_LIBRARIES"] = "",
            ["DYLD_LIBRARY_PATH"] = "/given",
            ["PATH"] = "/bin",
        };
        Dictionary<string, string> loader = TerminalSession.LoaderVariables(environment, inherited);
        Assert.True(loader.Count == 3 && loader["DYLD_FALLBACK_LIBRARY_PATH"] == "/inherited" && loader["DYLD_FRAMEWORK_PATH"] == "/given/frameworks"
            && loader["DYLD_LIBRARY_PATH"] == "/given",
            $"A program run in the Terminal gets other loader variables than its run gives it: {string.Join(' ', loader.Select(v => $"{v.Key}={v.Value}"))}");
    }
}
