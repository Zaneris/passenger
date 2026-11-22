using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.Tests;

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

    /// <summary>The person's own filters and hooks in a folder not trusted as one flow, each step the check one test
    /// made in an instance of its own: their required filter and hook run at a status, a commit or a rollback only once
    /// the folder is trusted, a file stored through a filter refused being written out by a rollback, stash, unstash,
    /// checkout, merge or cherry-pick until then, and a filter and hook git -c gave above Passenger and the file
    /// GIT_CONFIG names running none of the repository's.</summary>
    [Fact]
    public async Task The_persons_own_filters_and_hooks_and_those_given_above_Passenger_run_in_a_folder_only_once_it_is_trusted()
    {
        await Flow.StepApartAsync("the persons own required filter and hook run at a status a commit or a rollback only once the folder is trusted", () => new UntrustedGitPersonsFilterTests(), t => t.The_persons_own_required_filter_and_hook_run_at_a_status_a_commit_or_a_rollback_only_once_the_folder_is_trusted());
        await Flow.StepApartAsync("a rollback stash unstash checkout merge or cherry pick writing out a file stored through a filter is refused until the folder is trusted", () => new UntrustedGitPersonsFilterTests(), t => t.A_rollback_stash_unstash_checkout_merge_or_cherry_pick_writing_out_a_file_stored_through_a_filter_is_refused_until_the_folder_is_trusted());
        await Flow.StepApartAsync("a filter and hook git c gave above Passenger and the file GIT CONFIG names run none of the repositorys in a folder not trusted", () => new UntrustedGitPersonsFilterTests(), t => t.A_filter_and_hook_git_c_gave_above_Passenger_and_the_file_GIT_CONFIG_names_run_none_of_the_repositorys_in_a_folder_not_trusted());
    }

    private async Task The_persons_own_required_filter_and_hook_run_at_a_status_a_commit_or_a_rollback_only_once_the_folder_is_trusted()
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

    private async Task A_rollback_stash_unstash_checkout_merge_or_cherry_pick_writing_out_a_file_stored_through_a_filter_is_refused_until_the_folder_is_trusted()
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

    private async Task A_filter_and_hook_git_c_gave_above_Passenger_and_the_file_GIT_CONFIG_names_run_none_of_the_repositorys_in_a_folder_not_trusted()
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
