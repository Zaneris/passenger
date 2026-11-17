using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>What a git in a folder not trusted is given, read from git's own listings apart from any git: each program
/// key the repository sets given empty (a filter's whose name holds an '=' whole), none of the person's; a git that took
/// nothing from its environment found out; the submodules an index holds; and settings given after those the environment
/// gives already.</summary>
public sealed class UntrustedGitTests
{
    /// <summary>What `git config --list --show-scope --name-only -z` writes with <see cref="UntrustedGit"/>'s own settings
    /// given through the environment.</summary>
    private const string Given = "command\0core.fsmonitor\0command\0core.hookspath\0command\0submodule.recurse\0command\0maintenance.auto\0command\0gc.auto\0";

    [Fact]
    public void Each_program_the_repository_names_is_given_empty_its_filter_not_required_and_the_persons_own_left()
    {
        string listed = "global\0core.pager\0global\0credential.helper\0local\0core.repositoryformatversion\0local\0filter.a=b.clean\0"
            + "local\0diff.Shown.textconv\0worktree\0gpg.ssh.defaultkeycommand\0local\0alias.st\0local\0remote.origin.uploadpack\0"
            + "local\0filter.a=b.smudge\0local\0core.sshcommand\0" + Given;

        Assert.Equal(
        [
            ("filter.a=b.clean", ""), ("filter.a=b.required", "false"), ("diff.Shown.textconv", ""), ("gpg.ssh.defaultkeycommand", ""), ("alias.st", ""),
            ("filter.a=b.smudge", ""), ("core.sshcommand", ""),
        ], UntrustedGit.Overrides(listed));
    }

    [Fact]
    public void A_git_that_lists_none_of_the_settings_given_through_its_environment_is_found_out()
    {
        Assert.Null(UntrustedGit.Overrides("local\0filter.x.clean\0"));
        Assert.Null(UntrustedGit.Overrides("command\0core.fsmonitor\0local\0filter.x.clean\0"));
        Assert.Equal([], UntrustedGit.Overrides(Given));
    }

    [Fact]
    public void The_submodules_of_an_index_are_its_entries_of_mode_160000_each_once()
    {
        string staged = "100644 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 0\ta.txt\0"
            + "160000 8af5d675e849159e5ede7b797779a1977a248bc7 0\tlib/sub\0"
            + "160000 8af5d675e849159e5ede7b797779a1977a248bc7 2\t../moved\0"
            + "160000 1af5d675e849159e5ede7b797779a1977a248bc7 3\t../moved\0"
            + "120000 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 0\tlink\t160000\0";

        Assert.Equal(["lib/sub", "../moved"], UntrustedGit.Gitlinks(staged));
    }

    [Fact]
    public void Settings_go_after_those_the_environment_gives_and_a_count_git_could_not_read_is_replaced()
    {
        Dictionary<string, string?> environment = new()
        {
            [GitVariables.ConfigCount] = "1", [GitVariables.ConfigKey(0)] = "user.name", [GitVariables.ConfigValue(0)] = "Person",
        };
        UntrustedGit.Give(environment, [("filter.a=b.clean", ""), (GitConfigKeys.CoreFsmonitor, "false")]);
        Assert.Equal(("3", "user.name", "filter.a=b.clean", "", GitConfigKeys.CoreFsmonitor, "false"), (environment[GitVariables.ConfigCount], environment[GitVariables.ConfigKey(0)],
            environment[GitVariables.ConfigKey(1)], environment[GitVariables.ConfigValue(1)], environment[GitVariables.ConfigKey(2)], environment[GitVariables.ConfigValue(2)]));

        Dictionary<string, string?> unread = new() { [GitVariables.ConfigCount] = "two" };
        UntrustedGit.Give(unread, [(GitConfigKeys.CoreFsmonitor, "false")]);
        Assert.Equal(("1", GitConfigKeys.CoreFsmonitor), (unread[GitVariables.ConfigCount], unread[GitVariables.ConfigKey(0)]));
    }
}
