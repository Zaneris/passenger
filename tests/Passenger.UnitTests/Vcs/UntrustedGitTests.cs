using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>What a git in a folder not trusted is given, read from git's own listings apart from any git: each program
/// key the repository sets given empty (a filter's whose name holds an '=' whole), none of the person's but their filters
/// and hooks; a git that took nothing from its environment found out; the submodules an index holds; and settings given
/// after those the environment gives already.</summary>
public sealed class UntrustedGitTests
{
    /// <summary>What `git config --list --show-scope --name-only -z` writes with <see cref="UntrustedGit"/>'s own settings
    /// given through the environment.</summary>
    private const string Given = "command\0core.fsmonitor\0command\0core.hookspath\0command\0submodule.recurse\0command\0maintenance.auto\0command\0gc.auto\0";

    [Fact]
    public void A_git_in_a_folder_not_trusted_runs_none_of_the_repositorys_programs_filters_or_hooks() => Cases.All(
        ("each program the repository names is given empty its filter not required and the persons own left", Each_program_the_repository_names_is_given_empty_its_filter_not_required_and_the_persons_own_left),
        ("each filter has its programs given empty and is made not required wherever it is set", Each_filter_has_its_programs_given_empty_and_is_made_not_required_wherever_it_is_set),
        ("each hook a configuration declares is turned off by its name as listed wherever it is set", Each_hook_a_configuration_declares_is_turned_off_by_its_name_as_listed_wherever_it_is_set),
        ("the programs git lfs and git archive send email and svn take from the repository are given empty and the persons left", The_programs_git_lfs_and_git_archive_send_email_and_svn_take_from_the_repository_are_given_empty_and_the_persons_left),
        ("a git that lists none of the settings given through its environment is found out", A_git_that_lists_none_of_the_settings_given_through_its_environment_is_found_out),
        ("the submodules of an index are its entries of mode 160000 each once", The_submodules_of_an_index_are_its_entries_of_mode_160000_each_once),
        ("settings go after those the environment gives and a count git could not read is replaced", Settings_go_after_those_the_environment_gives_and_a_count_git_could_not_read_is_replaced));

    private void Each_program_the_repository_names_is_given_empty_its_filter_not_required_and_the_persons_own_left()
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

    private void Each_filter_has_its_programs_given_empty_and_is_made_not_required_wherever_it_is_set()
    {
        // As Git for Windows' own configuration sets git-lfs, and `git lfs install` the person's.
        string listed = "system\0filter.lfs.clean\0system\0filter.lfs.smudge\0system\0filter.lfs.process\0system\0filter.lfs.required\0"
            + "global\0filter.Mine.process\0command\0filter.given.clean\0local\0filter.lfs.smudge\0" + Given;

        Assert.Equal(
        [
            ("filter.lfs.clean", ""), ("filter.lfs.required", "false"), ("filter.lfs.smudge", ""), ("filter.lfs.process", ""),
            ("filter.Mine.process", ""), ("filter.Mine.required", "false"), ("filter.given.clean", ""), ("filter.given.required", "false"),
        ], UntrustedGit.Overrides(listed));
    }

    private void Each_hook_a_configuration_declares_is_turned_off_by_its_name_as_listed_wherever_it_is_set()
    {
        string listed = "local\0hook.Lint.Check.event\0local\0hook.Lint.Check.event\0local\0hook.Lint.Check.command\0global\0hook.mine.command\0"
            + "local\0hook.a=b.event\0local\0hook..command\0local\0hook.command\0system\0hook.pre-commit.jobs\0local\0hooks.allowed\0" + Given;

        Assert.Equal(
        [
            ("hook.Lint.Check.enabled", "false"), ("hook.mine.enabled", "false"), ("hook.a=b.enabled", "false"), ("hook..enabled", "false"),
            ("hook.enabled", "false"), ("hook.pre-commit.enabled", "false"),
        ], UntrustedGit.Overrides(listed));
    }

    private void The_programs_git_lfs_and_git_archive_send_email_and_svn_take_from_the_repository_are_given_empty_and_the_persons_left()
    {
        string listed = "local\0lfs.extension.x.clean\0local\0lfs.extension.x.smudge\0local\0lfs.extension.x.priority\0local\0lfs.customtransfer.x.path\0"
            + "local\0lfs.customtransfer.x.args\0local\0lfs.customtransfer.x.concurrent\0local\0lfs.standalonetransferagent\0"
            + "worktree\0lfs.https://example.invalid/a.git.standalonetransferagent\0local\0lfs.url\0global\0lfs.customtransfer.mine.path\0"
            + "local\0tar.tar.xz.command\0local\0sendemail.sendmailcmd\0local\0sendemail.work.sendmailcmd\0local\0svn.authorsprog\0" + Given;

        Assert.Equal(
        [
            ("lfs.extension.x.clean", ""), ("lfs.extension.x.smudge", ""), ("lfs.customtransfer.x.path", ""), ("lfs.customtransfer.x.args", ""),
            ("lfs.standalonetransferagent", ""), ("lfs.https://example.invalid/a.git.standalonetransferagent", ""), ("tar.tar.xz.command", ""),
            ("sendemail.sendmailcmd", ""), ("sendemail.work.sendmailcmd", ""), ("svn.authorsprog", ""),
        ], UntrustedGit.Overrides(listed));
    }

    private void A_git_that_lists_none_of_the_settings_given_through_its_environment_is_found_out()
    {
        Assert.Null(UntrustedGit.Overrides("local\0filter.x.clean\0"));
        Assert.Null(UntrustedGit.Overrides("command\0core.fsmonitor\0local\0filter.x.clean\0"));
        Assert.Equal([], UntrustedGit.Overrides(Given));
    }

    private void The_submodules_of_an_index_are_its_entries_of_mode_160000_each_once()
    {
        string staged = "100644 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 0\ta.txt\0"
            + "160000 8af5d675e849159e5ede7b797779a1977a248bc7 0\tlib/sub\0"
            + "160000 8af5d675e849159e5ede7b797779a1977a248bc7 2\t../moved\0"
            + "160000 1af5d675e849159e5ede7b797779a1977a248bc7 3\t../moved\0"
            + "120000 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 0\tlink\t160000\0";

        Assert.Equal(["lib/sub", "../moved"], UntrustedGit.Gitlinks(staged));
    }

    private void Settings_go_after_those_the_environment_gives_and_a_count_git_could_not_read_is_replaced()
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
