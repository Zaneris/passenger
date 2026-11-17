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

    [Fact]
    public async Task A_filter_whose_name_holds_an_equals_sign_runs_at_a_status_only_once_the_folder_is_trusted()
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

    [Fact]
    public async Task A_submodules_own_filter_runs_at_a_status_stash_or_rollback_only_once_the_folder_is_trusted()
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

    [Fact]
    public async Task A_fetch_a_push_and_a_partial_clones_read_run_the_remotes_upload_and_receive_pack_only_once_the_folder_is_trusted()
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

    [Fact]
    public async Task A_working_tree_the_repository_names_elsewhere_is_left_alone_and_git_works_on_the_folder_until_it_is_trusted()
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

/// <summary>The person's own filter, required, in the system's configuration as Git for Windows sets git-lfs there (and
/// `git lfs install` in the person's), and a hook of theirs, run in a folder not trusted by none of a status and a commit,
/// which still succeed, and each once the folder is trusted, the filter then reading the repository's own git-lfs
/// settings as git-lfs would; a file stored through that filter is refused a commit, a rollback, a stash, an unstash and a
/// checkout writing it out until then, its bytes left as they were, with real git-lfs too where it is installed. Nor do a filter and a hook that `git -c` gave above Passenger run there, and a file that
/// GIT_CONFIG names, which `git config` would read alone, keeps git from none of its work. Alone, as the system's
/// configuration and git's environment are the process's for its length.</summary>
[Collection(Alone.Name)]
public sealed class UntrustedGitPersonsFilterTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("untrusted-git-persons-"));

    private readonly string marks = PathNames.Canonical(TestFolders.Create("untrusted-git-persons-marks-"));

    private readonly string? noSystem = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");

    private readonly string? system = Environment.GetEnvironmentVariable("GIT_CONFIG_SYSTEM");

    public void Dispose()
    {
        OwnEnvironment.Set("GIT_CONFIG_SYSTEM", system);
        OwnEnvironment.Set("GIT_CONFIG_NOSYSTEM", noSystem);
        OwnEnvironment.Set(GitVariables.ConfigParameters, null);
        OwnEnvironment.Set(GitVariables.Config, null);
        TestFolders.Delete(folder);
        TestFolders.Delete(marks);
    }

    [Fact]
    public async Task The_persons_own_required_filter_and_hook_run_at_a_status_a_commit_or_a_rollback_only_once_the_folder_is_trusted()
    {
        string tree = Directory.CreateDirectory(Path.Combine(folder, "tree")).FullName;
        string file = Path.Combine(tree, "a.txt");
        string plain = Path.Combine(tree, "b.txt");
        File.WriteAllText(file, "one\n");
        File.WriteAllText(plain, "one\n");
        File.WriteAllText(Path.Combine(tree, ".gitattributes"), "a.txt filter=lfs\n");
        await TestGit.InitAsync(tree);
        // A program git-lfs would run on the repository's files, named by the repository.
        await TestGit.RunAsync(tree, "config", "lfs.extension.x.clean", "cat");
        // The person's git-lfs, which records the repository's settings it would act on and stores what it is given apart.
        string persons = Path.Combine(folder, "gitconfig");
        string read = Path.Combine(marks, "read").Replace('\\', '/');
        string clean = $"sh -c 'git config --get-regexp \"^lfs\\.\" >> \"{read}\"; cat > /dev/null; echo stored by the filter'";
        await TestGit.RunAsync(folder, "config", "--file", persons, "filter.lfs.clean", clean);
        await TestGit.RunAsync(folder, "config", "--file", persons, "filter.lfs.smudge", "cat");
        await TestGit.RunAsync(folder, "config", "--file", persons, "filter.lfs.required", "true");
        await TestGit.RunAsync(folder, "config", "--file", persons, "hook.mine.event", "pre-commit");
        await TestGit.RunAsync(folder, "config", "--file", persons, "hook.mine.command", UntrustedGitTests.Marking(marks, "hook", "exit 0"));
        OwnEnvironment.Set("GIT_CONFIG_NOSYSTEM", null);
        OwnEnvironment.Set("GIT_CONFIG_SYSTEM", persons);
        File.WriteAllText(file, "two\n");
        File.WriteAllText(plain, "two\n");

        using (WorkspaceTrust.Ask(tree))
        {
            Git git = await OpenAsync(tree);
            IReadOnlyList<FileChange> changes = (await git.StatusAsync()).Changes;
            Assert.Equal<string>([file, plain], [.. changes.Select(c => c.Path).Order(StringComparer.Ordinal)]);
            FileChange filtered = changes.Single(c => c.Path == file);
            Assert.Equal("lfs", filtered.Filter);
            Assert.Contains("not trusted: a.txt is stored through Git's lfs filter", filtered.Uncommittable, StringComparison.Ordinal);
            Assert.Null(changes.Single(c => c.Path == plain).Uncommittable);
            GitResult refused = await git.CommitAsync([file, plain], "Two");
            Assert.False(refused.Succeeded, "A file stored through the person's filter was committed in a folder not trusted, as the disk has it");
            Assert.Contains("a.txt is stored through Git's lfs filter, which runs only once the folder is trusted", refused.Message, StringComparison.Ordinal);
            GitResult committed = await git.CommitAsync([plain], "Two");
            Assert.True(committed.Succeeded, $"The commit in a folder not trusted failed: {committed.Message}");
            Assert.Equal("one\n", Encoding.UTF8.GetString(await git.BytesAtAsync("HEAD", file) ?? []));
            GitResult rolledBack = await git.RollbackAsync([Assert.Single((await git.StatusAsync()).Changes)]);
            Assert.False(rolledBack.Succeeded, "A file stored through the person's filter was rolled back in a folder not trusted, written out as git keeps it");
            Assert.Contains("a.txt is stored through Git's lfs filter, which runs only once the folder is trusted", rolledBack.Message, StringComparison.Ordinal);
            Assert.Equal("two\n", File.ReadAllText(file));
            Assert.False(File.Exists(Path.Combine(marks, "read")), "Git in a folder not trusted ran the person's filter, which would act on the repository's git-lfs settings");
            Assert.False(File.Exists(Path.Combine(marks, "hook")), "A commit in a folder not trusted ran the person's hook");
        }
        Git trusted = await OpenAsync(tree);
        // As long as what the index has, which git compares only through the filter.
        File.WriteAllText(file, "six\n");
        UntrustedGitTests.Touch(file);
        Assert.Null(Assert.Single((await trusted.StatusAsync()).Changes).Uncommittable);
        Assert.True(File.Exists(Path.Combine(marks, "read")), "The person's filter never ran at a status once trusted, so the test proves nothing of the folder not trusted");
        Assert.Contains("lfs.extension.x.clean", File.ReadAllText(Path.Combine(marks, "read")), StringComparison.Ordinal);
        GitResult stored = await trusted.CommitAsync([file], "Six");
        Assert.True(stored.Succeeded, $"The commit of the file stored through the filter failed once the folder was trusted: {stored.Message}");
        Assert.Equal("stored by the filter\n", await TestGit.RunAsync(tree, "cat-file", "blob", "HEAD:a.txt"));
        if (await UntrustedGitTests.WhyNoHooksFromConfigurationAsync(folder) is null)
        {
            Assert.True(File.Exists(Path.Combine(marks, "hook")), "The person's hook never ran at a commit once trusted, so the test proves nothing of the folder not trusted");
        }
    }

    [Fact]
    public async Task A_rollback_stash_unstash_checkout_merge_or_cherry_pick_writing_out_a_file_stored_through_a_filter_is_refused_until_the_folder_is_trusted()
    {
        string tree = Directory.CreateDirectory(Path.Combine(folder, "tree")).FullName;
        string picture = Path.Combine(tree, "picture.bin");
        string notes = Path.Combine(tree, "notes.txt");
        File.WriteAllText(picture, "one\n");
        File.WriteAllText(notes, "one\n");
        File.WriteAllText(Path.Combine(tree, ".gitattributes"), "*.bin filter=lfs\n");
        await TestGit.InitAsync(tree);
        // Made before the person's filter is configured, so no git of the test's own runs it.
        await TestGit.RunAsync(tree, "switch", "-q", "-c", "pictured");
        File.WriteAllText(picture, "pictured\n");
        await TestGit.RunAsync(tree, "commit", "-q", "-a", "-m", "The picture changed");
        string pictured = (await TestGit.RunAsync(tree, "rev-parse", "HEAD")).Trim();
        await TestGit.RunAsync(tree, "switch", "-q", "-c", "noted", "main");
        File.WriteAllText(notes, "noted\n");
        await TestGit.RunAsync(tree, "commit", "-q", "-a", "-m", "The notes changed");
        await TestGit.RunAsync(tree, "switch", "-q", "main");
        File.WriteAllText(picture, "stashed\n");
        await TestGit.RunAsync(tree, "stash", "push", "-q", "-m", "The picture set aside");
        // The person's filter, identity both ways, marking each run.
        string persons = Path.Combine(folder, "gitconfig");
        await TestGit.RunAsync(folder, "config", "--file", persons, "filter.lfs.clean", UntrustedGitTests.Marking(marks, "clean", "cat"));
        await TestGit.RunAsync(folder, "config", "--file", persons, "filter.lfs.smudge", UntrustedGitTests.Marking(marks, "smudge", "cat"));
        await TestGit.RunAsync(folder, "config", "--file", persons, "filter.lfs.required", "true");
        OwnEnvironment.Set("GIT_CONFIG_NOSYSTEM", null);
        OwnEnvironment.Set("GIT_CONFIG_SYSTEM", persons);
        const string Why = "The folder is not trusted: picture.bin is stored through Git's lfs filter, which runs only once the folder is trusted";

        using (WorkspaceTrust.Ask(tree))
        {
            Git git = await OpenAsync(tree);
            Assert.Equal(Why, await git.WhyNotCheckedOutAsync("pictured"));
            GitResult checkedOut = await git.CheckoutAsync("pictured");
            Assert.Equal((false, Why), (checkedOut.Succeeded, checkedOut.Message));
            Assert.Equal("main", await git.BranchAsync());
            Assert.Equal("one\n", File.ReadAllText(picture));
            GitResult merged = await git.MergeAsync("pictured");
            Assert.Equal((false, Why), (merged.Succeeded, merged.Message));
            GitResult picked = await git.CherryPickAsync(pictured);
            Assert.Equal((false, Why), (picked.Succeeded, picked.Message));
            Assert.Equal("one\n", File.ReadAllText(picture));
            // A branch whose files differ only where no filter stores them is checked out as ever.
            Assert.Null(await git.WhyNotCheckedOutAsync("noted"));
            GitResult noted = await git.CheckoutAsync("noted");
            Assert.True(noted.Succeeded, $"A checkout writing out no file stored through a filter was refused in a folder not trusted: {noted.Message}");
            Assert.Equal("noted\n", File.ReadAllText(notes));
            Assert.True((await git.CheckoutAsync("main")).Succeeded, "The checkout back to main failed");

            StashInfo stash = Assert.Single(await git.StashesAsync());
            Assert.Equal(Why, await git.WhyNotUnstashedAsync(stash));
            GitResult applied = await git.UnstashAsync(stash, pop: true);
            Assert.Equal((false, Why), (applied.Succeeded, applied.Message));
            Assert.Equal("one\n", File.ReadAllText(picture));
            Assert.Single(await git.StashesAsync());

            File.WriteAllText(picture, "two\n");
            File.WriteAllText(notes, "two\n");
            IReadOnlyList<FileChange> changes = (await git.StatusAsync()).Changes;
            FileChange changed = changes.Single(c => c.Path == picture);
            Assert.Equal(Why, changed.Unfiltered);
            Assert.Equal(Why, await git.WhyNotStashedAsync());
            GitResult stashed = await git.StashAsync("Both set aside");
            Assert.Equal((false, Why), (stashed.Succeeded, stashed.Message));
            Assert.Single(await git.StashesAsync());
            GitResult rolledBack = await git.RollbackAsync(changes);
            Assert.Equal((false, Why), (rolledBack.Succeeded, rolledBack.Message));
            Assert.Equal(("two\n", "two\n"), (File.ReadAllText(picture), File.ReadAllText(notes)));
            GitResult notesBack = await git.RollbackAsync([changes.Single(c => c.Path == notes)]);
            Assert.True(notesBack.Succeeded, $"The rollback of a file stored through no filter failed in a folder not trusted: {notesBack.Message}");
            Assert.Equal(("two\n", "one\n"), (File.ReadAllText(picture), File.ReadAllText(notes)));
            Assert.False(File.Exists(Path.Combine(marks, "clean")) || File.Exists(Path.Combine(marks, "smudge")), "Git in a folder not trusted ran the person's filter");
        }
        Git trusted = await OpenAsync(tree);
        FileChange filtered = Assert.Single((await trusted.StatusAsync()).Changes);
        Assert.Null(filtered.Unfiltered);
        Assert.Null(await trusted.WhyNotStashedAsync());
        GitResult rolled = await trusted.RollbackAsync([filtered]);
        Assert.True(rolled.Succeeded, $"The rollback of the file stored through the filter failed once the folder was trusted: {rolled.Message}");
        Assert.Equal("one\n", File.ReadAllText(picture));
        Assert.True(File.Exists(Path.Combine(marks, "smudge")), "The person's smudge filter never ran at a rollback once trusted, so the test proves nothing of the folder not trusted");
        GitResult unstashed = await trusted.UnstashAsync(Assert.Single(await trusted.StashesAsync()), pop: true);
        Assert.True(unstashed.Succeeded, $"The unstash of the file stored through the filter failed once the folder was trusted: {unstashed.Message}");
        Assert.Equal("stashed\n", File.ReadAllText(picture));
        GitResult restashed = await trusted.StashAsync("The picture again");
        Assert.True(restashed.Succeeded, $"The stash of the file stored through the filter failed once the folder was trusted: {restashed.Message}");
        Assert.True(File.Exists(Path.Combine(marks, "clean")), "The person's clean filter never ran at a stash once trusted, so the test proves nothing of the folder not trusted");
        GitResult switched = await trusted.CheckoutAsync("pictured");
        Assert.True(switched.Succeeded, $"The checkout writing out the file stored through the filter failed once the folder was trusted: {switched.Message}");
        Assert.Equal("pictured\n", File.ReadAllText(picture));
    }

    [Fact]
    public async Task A_file_git_lfs_stores_is_refused_a_commit_until_the_folder_is_trusted_and_then_goes_in_as_its_pointer()
    {
        string tree = Directory.CreateDirectory(Path.Combine(folder, "tree")).FullName;
        File.WriteAllText(Path.Combine(tree, "a.txt"), "one\n");
        File.WriteAllText(Path.Combine(tree, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
        await TestGit.InitAsync(tree);
        ChildResult lfs = await ChildProcesses.RunAsync(ChildProcesses.Command("git", ["lfs", "version"], tree), limit: TimeSpan.FromMinutes(1).Scaled(),
            cancel: TestContext.Current.CancellationToken, answers: [1]);
        Assert.SkipWhen(!lfs.Succeeded, $"git-lfs is not installed here, so no file is stored through it: {lfs.Error.Trim()}");
        // As `git lfs install --system` and Git for Windows set it.
        string persons = Path.Combine(folder, "gitconfig");
        await TestGit.RunAsync(folder, "config", "--file", persons, "filter.lfs.clean", "git-lfs clean -- %f");
        await TestGit.RunAsync(folder, "config", "--file", persons, "filter.lfs.smudge", "git-lfs smudge -- %f");
        await TestGit.RunAsync(folder, "config", "--file", persons, "filter.lfs.process", "git-lfs filter-process");
        await TestGit.RunAsync(folder, "config", "--file", persons, "filter.lfs.required", "true");
        OwnEnvironment.Set("GIT_CONFIG_NOSYSTEM", null);
        OwnEnvironment.Set("GIT_CONFIG_SYSTEM", persons);
        string picture = Path.Combine(tree, "picture.bin");
        File.WriteAllText(picture, "A picture's own bytes\n");

        using (WorkspaceTrust.Ask(tree))
        {
            Git git = await OpenAsync(tree);
            FileChange added = Assert.Single((await git.StatusAsync()).Changes);
            Assert.Equal((picture, ChangeKind.Unversioned, "lfs"), (added.Path, added.Kind, added.Filter));
            GitResult refused = await git.CommitAsync([picture], "A picture");
            Assert.False(refused.Succeeded, "A file git-lfs stores was committed whole in a folder not trusted, where git-lfs's pointer would have gone in");
            Assert.Contains("picture.bin is stored through Git's lfs filter", refused.Message, StringComparison.Ordinal);
            Assert.Null(await git.BytesAtAsync("HEAD", picture));
        }
        Git trusted = await OpenAsync(tree);
        Assert.Null(Assert.Single((await trusted.StatusAsync()).Changes).Filter);
        GitResult committed = await trusted.CommitAsync([picture], "A picture");
        Assert.True(committed.Succeeded, $"The commit of a file git-lfs stores failed once the folder was trusted: {committed.Message}");
        string stored = await TestGit.RunAsync(tree, "cat-file", "blob", "HEAD:picture.bin");
        Assert.StartsWith("version https://git-lfs.github.com/spec/v1", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_filter_and_hook_git_c_gave_above_Passenger_and_the_file_GIT_CONFIG_names_run_none_of_the_repositorys_in_a_folder_not_trusted()
    {
        string tree = Directory.CreateDirectory(Path.Combine(folder, "tree")).FullName;
        string own = Path.Combine(tree, "a.txt");
        string given = Path.Combine(tree, "b.txt");
        string plain = Path.Combine(tree, "c.txt");
        foreach (string file in (string[])[own, given, plain])
        {
            File.WriteAllText(file, "one\n");
        }
        File.WriteAllText(Path.Combine(tree, ".gitattributes"), "a.txt filter=own\nb.txt filter=given\n");
        await TestGit.InitAsync(tree);
        // The repository's own filter, which `git config` lists only where GIT_CONFIG names no file in place of every scope's.
        await TestGit.RunAsync(tree, "config", "filter.own.clean", UntrustedGitTests.Marking(marks, "own", "cat"));
        string hooks = Directory.CreateDirectory(Path.Combine(folder, "hooks")).FullName;
        string hook = Path.Combine(hooks, "pre-commit");
        File.WriteAllText(hook, $"#!/bin/sh\necho ran >> \"{Path.Combine(marks, "hook").Replace('\\', '/')}\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        string elsewhere = Path.Combine(folder, "elsewhere.gitconfig");
        File.WriteAllText(elsewhere, "[user]\n\tname = Elsewhere\n");
        // As `git -c` gives them to what it starts, each quoted as a shell quotes a word.
        OwnEnvironment.Set(GitVariables.ConfigParameters,
            $"{Quoted("filter.given.clean")}={Quoted(UntrustedGitTests.Marking(marks, "given", "cat"))} {Quoted(GitConfigKeys.CoreHooksPath)}={Quoted(hooks.Replace('\\', '/'))}");
        OwnEnvironment.Set(GitVariables.Config, elsewhere);
        foreach (string file in (string[])[own, given, plain])
        {
            File.WriteAllText(file, "two\n");
        }

        using (WorkspaceTrust.Ask(tree))
        {
            GitLookup found = await Git.OpenAsync(tree);
            Git git = found.Tree ?? throw new InvalidOperationException($"Git did not run in a folder not trusted while GIT_CONFIG named a file: {found.Refused}");
            Assert.Equal(3, (await git.StatusAsync()).Changes.Count);
            GitResult committed = await git.CommitAsync([plain], "Two");
            Assert.True(committed.Succeeded, $"The commit in a folder not trusted failed: {committed.Message}");
            Assert.False(File.Exists(Path.Combine(marks, "own")), "Git in a folder not trusted ran the repository's own filter");
            Assert.False(File.Exists(Path.Combine(marks, "given")), "Git in a folder not trusted ran a filter git -c gave above Passenger");
            Assert.False(File.Exists(Path.Combine(marks, "hook")), "A commit in a folder not trusted ran the hooks of the folder git -c gave above Passenger");
        }
        Git trusted = await OpenAsync(tree);
        File.WriteAllText(plain, "three\n");
        GitResult again = await trusted.CommitAsync([own, given, plain], "Three");
        Assert.True(again.Succeeded, $"The commit in the folder trusted failed: {again.Message}");
        foreach (string mark in (string[])["own", "given", "hook"])
        {
            Assert.True(File.Exists(Path.Combine(marks, mark)), $"The {mark} program never ran once the folder was trusted, so the test proves nothing of the folder not trusted");
        }

        static string Quoted(string word) => $"'{word.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    private static Task<Git> OpenAsync(string tree) => UntrustedGitTests.OpenAsync(tree);
}
