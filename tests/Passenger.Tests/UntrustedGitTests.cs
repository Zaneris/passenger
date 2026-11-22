using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Git in a folder the person has not trusted runs none of the programs its repository names, with real git: a
/// filter whose name holds an '=', a submodule's own filter (its configuration in .git/modules) as a status, a stash and a
/// rollback reach it, a hook its configuration declares as a commit and a move reach it, and a remote's upload-pack and
/// receive-pack as a fetch, a push and a partial clone's read would run them; each runs once the folder is trusted, so it
/// was there to run. Nor does it work on a working tree the repository names elsewhere: a status and a rollback touch the
/// folder's files alone.</summary>
public sealed class UntrustedGitTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("untrusted-git-"));

    /// <summary>Where the programs mark that they ran: outside the repositories, whose status they would change.</summary>
    private readonly string marks = PathNames.Canonical(TestFolders.Create("untrusted-git-marks-"));

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(marks);
    }

    /// <summary>Git in a folder the person has not trusted as one flow, each step the check one test made in an
    /// instance of its own: a filter whose name holds an equals sign and a submodule's own filter run at a status,
    /// stash or rollback only once the folder is trusted, a fetch, a push and a partial clone's read running the
    /// remote's upload and receive pack only then, and a working tree the repository names elsewhere left alone until
    /// then.</summary>
    [Fact]
    public async Task Git_in_a_folder_not_trusted_runs_none_of_the_repositorys_filters_remotes_programs_or_working_trees_until_it_is()
    {
        await Flow.StepApartAsync("a filter whose name holds an equals sign runs at a status only once the folder is trusted", () => new UntrustedGitTests(), t => t.A_filter_whose_name_holds_an_equals_sign_runs_at_a_status_only_once_the_folder_is_trusted());
        await Flow.StepApartAsync("a submodules own filter runs at a status stash or rollback only once the folder is trusted", () => new UntrustedGitTests(), t => t.A_submodules_own_filter_runs_at_a_status_stash_or_rollback_only_once_the_folder_is_trusted());
        await Flow.StepApartAsync("a fetch a push and a partial clones read run the remotes upload and receive pack only once the folder is trusted", () => new UntrustedGitTests(), t => t.A_fetch_a_push_and_a_partial_clones_read_run_the_remotes_upload_and_receive_pack_only_once_the_folder_is_trusted());
        await Flow.StepApartAsync("a working tree the repository names elsewhere is left alone and git works on the folder until it is trusted", () => new UntrustedGitTests(), t => t.A_working_tree_the_repository_names_elsewhere_is_left_alone_and_git_works_on_the_folder_until_it_is_trusted());
    }

    private async Task A_filter_whose_name_holds_an_equals_sign_runs_at_a_status_only_once_the_folder_is_trusted()
    {
        string tree = Directory.CreateDirectory(Path.Combine(folder, "tree")).FullName;
        File.WriteAllText(Path.Combine(tree, "a.txt"), "one\n");
        File.WriteAllText(Path.Combine(tree, ".gitattributes"), "* filter=a=b\n");
        await TestGit.InitAsync(tree);
        // As -c reads it, filter.a=b.clean= sets filter.a to "b.clean=", leaving this one in force.
        await TestGit.RunAsync(tree, "config", "filter.a=b.clean", Marking("clean", "cat"));
        Touch(Path.Combine(tree, "a.txt"));

        using (WorkspaceTrust.Ask(tree))
        {
            Git git = await OpenAsync(tree);
            await git.StatusAsync();
            Assert.False(Ran("clean"), "A status in a folder not trusted ran the clean filter of [filter \"a=b\"]");
        }
        Git trusted = await OpenAsync(tree);
        await trusted.StatusAsync();
        Assert.True(Ran("clean"), "The clean filter never ran once the folder was trusted, so the test proves nothing of the folder not trusted");
    }

    private async Task A_submodules_own_filter_runs_at_a_status_stash_or_rollback_only_once_the_folder_is_trusted()
    {
        string origin = Directory.CreateDirectory(Path.Combine(folder, "origin")).FullName;
        File.WriteAllText(Path.Combine(origin, "s.txt"), "one\n");
        File.WriteAllText(Path.Combine(origin, ".gitattributes"), "* filter=evil\n");
        await TestGit.InitAsync(origin);
        string top = Directory.CreateDirectory(Path.Combine(folder, "top")).FullName;
        File.WriteAllText(Path.Combine(top, "a.txt"), "one\n");
        await TestGit.InitAsync(top);
        await TestGit.RunAsync(top, "-c", "protocol.file.allow=always", "submodule", "add", "-q", origin.Replace('\\', '/'), "sub");
        await TestGit.RunAsync(top, "commit", "-q", "-m", "The submodule");
        string sub = Path.Combine(top, "sub");
        // The submodule's own configuration, in .git/modules/sub, which the folder's never names.
        await TestGit.RunAsync(sub, "config", "filter.evil.clean", Marking("clean", "cat"));
        await TestGit.RunAsync(sub, "config", "filter.evil.smudge", Marking("smudge", "cat"));
        Touch(Path.Combine(sub, "s.txt"));
        File.WriteAllText(Path.Combine(top, "a.txt"), "two\n");

        using (WorkspaceTrust.Ask(top))
        {
            Git git = await OpenAsync(top);
            await git.StatusAsync();
            Assert.False(Ran("clean"), "A status in a folder not trusted ran its submodule's clean filter");
            GitResult stashed = await git.StashAsync("Set aside");
            Assert.True(stashed.Succeeded, $"The stash failed: {stashed.Message}");
            Assert.False(Ran("clean"), "A stash in a folder not trusted ran its submodule's clean filter");

            TestFolders.Delete(sub);
            FileChange gone = Assert.Single((await git.StatusAsync()).Changes, c => c.Name == "sub");
            GitResult rolledBack = await git.RollbackAsync([gone]);
            Assert.Contains("sub was left checked out as it is", rolledBack.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(sub, "s.txt")), "A rollback in a folder not trusted checked its submodule out, from .git/modules");
            Assert.False(Ran("smudge"), "A rollback in a folder not trusted ran its submodule's smudge filter");
        }
        // What the rollback left of the submodule's folder (its index entry put back alone) goes, for it to be deleted again.
        TestFolders.Delete(sub);
        Git trusted = await OpenAsync(top);
        FileChange deleted = Assert.Single((await trusted.StatusAsync()).Changes, c => c.Name == "sub");
        GitResult restored = await trusted.RollbackAsync([deleted]);
        Assert.True(restored.Succeeded, $"The rollback of the submodule in the folder trusted failed: {restored.Message}");
        Assert.True(Ran("smudge"), "The submodule's smudge filter never ran as a rollback checked it out once trusted, so the test proves nothing of the folder not trusted");
        Touch(Path.Combine(sub, "s.txt"));
        await trusted.StatusAsync();
        Assert.True(Ran("clean"), "The submodule's clean filter never ran at a status once trusted, so the test proves nothing of the folder not trusted");
    }

    [Fact]
    public async Task A_hook_the_repositorys_configuration_declares_runs_at_a_commit_or_a_move_only_once_the_folder_is_trusted()
    {
        string? why = await WhyNoHooksFromConfigurationAsync(folder);
        Assert.SkipWhen(why is not null, why ?? string.Empty);
        string tree = Directory.CreateDirectory(Path.Combine(folder, "tree")).FullName;
        string first = Path.Combine(tree, "a.txt");
        File.WriteAllText(first, "one\n");
        await TestGit.InitAsync(tree);
        // Named with a dot and capitals, which git keeps as written.
        await TestGit.RunAsync(tree, "config", "hook.Lint.Check.event", "pre-commit");
        await TestGit.RunAsync(tree, "config", "--add", "hook.Lint.Check.event", "commit-msg");
        await TestGit.RunAsync(tree, "config", "--add", "hook.Lint.Check.event", "post-index-change");
        await TestGit.RunAsync(tree, "config", "hook.Lint.Check.command", Marking("hook", "exit 0"));
        File.WriteAllText(first, "two\n");

        string moved = Path.Combine(tree, "b.txt");
        using (WorkspaceTrust.Ask(tree))
        {
            Git git = await OpenAsync(tree);
            GitResult committed = await git.CommitAsync([first], "Two");
            Assert.True(committed.Succeeded, $"The commit in a folder not trusted failed: {committed.Message}");
            File.Move(first, moved);
            GitResult indexed = await git.MoveInIndexAsync([(first, moved)]);
            Assert.True(indexed.Succeeded, $"The move in a folder not trusted failed: {indexed.Message}");
            Assert.False(Ran("hook"), "A commit or a move in a folder not trusted ran the hook its repository's configuration declares");
        }
        Git trusted = await OpenAsync(tree);
        File.WriteAllText(moved, "three\n");
        GitResult again = await trusted.CommitAsync([moved], "Three");
        Assert.True(again.Succeeded, $"The commit in the folder trusted failed: {again.Message}");
        Assert.True(Ran("hook"), "The hook never ran at a commit once trusted, so the test proves nothing of the folder not trusted");
    }

    private async Task A_fetch_a_push_and_a_partial_clones_read_run_the_remotes_upload_and_receive_pack_only_once_the_folder_is_trusted()
    {
        string origin = Directory.CreateDirectory(Path.Combine(folder, "origin")).FullName;
        File.WriteAllText(Path.Combine(origin, "a.txt"), "one\n");
        await TestGit.InitAsync(origin);
        await TestGit.RunAsync(origin, "config", "uploadpack.allowFilter", "true");
        string address = new Uri(origin).AbsoluteUri;
        string clone = Path.Combine(folder, "clone");
        await TestGit.RunAsync(folder, "clone", "-q", "--filter=blob:none", "--no-checkout", address, clone);
        await TestGit.RunAsync(clone, "config", "remote.origin.uploadpack", Marking("uploadpack", "exit 1"));
        await TestGit.RunAsync(clone, "config", "remote.origin.receivepack", Marking("receivepack", "exit 1"));
        await TestGit.RunAsync(clone, "config", GitConfigKeys.UserEmail, "tests@passenger.invalid");
        await TestGit.RunAsync(clone, "config", "user.name", "Passenger Tests");
        // The clone checked nothing out, so this commit holds no file: the remote's branch is the one read.
        await TestGit.RunAsync(clone, "commit", "-q", "--allow-empty", "-m", "To push");

        using (WorkspaceTrust.Ask(PathNames.Canonical(clone)))
        {
            Git git = await OpenAsync(clone);
            Assert.False((await git.FetchAsync(TestContext.Current.CancellationToken)).Succeeded, "A fetch in a folder not trusted reached its remote");
            Assert.False((await git.PushAsync(cancel: TestContext.Current.CancellationToken)).Succeeded, "A push in a folder not trusted reached its remote");
            Assert.Null(await git.BytesAtAsync("origin/main", Path.Combine(git.Root, "a.txt")));
            Assert.False(Ran("uploadpack"), "Git in a folder not trusted ran the upload-pack its remote's settings name");
            Assert.False(Ran("receivepack"), "Git in a folder not trusted ran the receive-pack its remote's settings name");
        }
        Git trusted = await OpenAsync(clone);
        await trusted.BytesAtAsync("origin/main", Path.Combine(trusted.Root, "a.txt"));
        Assert.True(Ran("uploadpack"), "A partial clone's read never ran the remote's upload-pack once trusted, so the test proves nothing of the folder not trusted");
        await trusted.PushAsync(cancel: TestContext.Current.CancellationToken);
        Assert.True(Ran("receivepack"), "A push never ran the remote's receive-pack once trusted, so the test proves nothing of the folder not trusted");
    }

    private async Task A_working_tree_the_repository_names_elsewhere_is_left_alone_and_git_works_on_the_folder_until_it_is_trusted()
    {
        // Another repository's folder, where git finds that one's .git: as a person's home holding their own.
        string elsewhere = Directory.CreateDirectory(Path.Combine(folder, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "a.txt"), "theirs\n");
        await TestGit.InitAsync(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "a.txt"), "theirs, changed\n");
        string tree = Directory.CreateDirectory(Path.Combine(folder, "tree")).FullName;
        File.WriteAllText(Path.Combine(tree, "a.txt"), "ours\n");
        await TestGit.InitAsync(tree);
        File.WriteAllText(Path.Combine(tree, "a.txt"), "ours, changed\n");
        await TestGit.RunAsync(tree, "config", "core.worktree", elsewhere.Replace('\\', '/'));

        using (WorkspaceTrust.Ask(tree))
        {
            GitLookup found = await Git.OpenAsync(tree);
            Git git = found.Tree ?? throw new InvalidOperationException($"No working tree was found at {tree}: {found.Refused}");
            Assert.True(PathNames.Same(tree, git.Root), $"Git's working tree in a folder not trusted is {git.Root}, where the folder is {tree}");
            Assert.True(found.WorkTreeNamed is { } named && PathNames.Same(elsewhere, PathNames.Canonical(named)),
                $"The working tree the repository names, {elsewhere}, was not said: {found.WorkTreeNamed}");
            FileChange changed = Assert.Single((await git.StatusAsync()).Changes);
            Assert.Equal(Path.Combine(tree, "a.txt"), changed.Path);
            GitResult rolledBack = await git.RollbackAsync([changed]);
            Assert.True(rolledBack.Succeeded, $"The rollback in the folder failed: {rolledBack.Message}");
            Assert.Equal("ours\n", File.ReadAllText(Path.Combine(tree, "a.txt")));
            Assert.Equal("theirs, changed\n", File.ReadAllText(Path.Combine(elsewhere, "a.txt")));
        }
        GitLookup trusted = await Git.OpenAsync(tree);
        Assert.True(trusted.Tree is { } taken && PathNames.Same(elsewhere, taken.Root),
            $"Git took {trusted.Tree?.Root} for the working tree once trusted, not the one the repository names, so the test proves nothing of the folder not trusted");
        Assert.Null(trusted.WorkTreeNamed);
    }

    /// <summary>Why this git runs no hook its configuration declares (hook.NAME.command, from git 2.54); null where it does.</summary>
    internal static async Task<string?> WhyNoHooksFromConfigurationAsync(string folder)
    {
        string said = await TestGit.RunAsync(folder, "--version");
        return Regex.Match(said, @"(\d+)\.(\d+)") is { Success: true } version
            && new Version(int.Parse(version.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(version.Groups[2].Value, CultureInfo.InvariantCulture)) < new Version(2, 54)
            ? $"{said.Trim()} runs no hook its configuration declares, which git does from 2.54"
            : null;
    }

    /// <summary>A shell command for git's configuration that marks <paramref name="name"/> ran, then does <paramref name="then"/>.</summary>
    private string Marking(string name, string then) => Marking(marks, name, then);

    /// <summary>A shell command that marks <paramref name="name"/> ran in <paramref name="marks"/>, then does <paramref name="then"/>.</summary>
    internal static string Marking(string marks, string name, string then) => $"sh -c 'echo ran >> \"{Path.Combine(marks, name).Replace('\\', '/')}\"; {then}'";

    private bool Ran(string name) => File.Exists(Path.Combine(marks, name));

    /// <summary>A file given a time after its last, so a status reads its content again, through any filter it names.</summary>
    internal static void Touch(string path) => File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path) + TimeSpan.FromMinutes(1));

    internal static async Task<Git> OpenAsync(string tree) =>
        (await Git.OpenAsync(tree)).Tree ?? throw new InvalidOperationException($"No working tree was found at {tree}");
}
