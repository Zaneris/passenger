using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Git in a folder the person has not trusted runs none of the programs its repository names, with real git: a
/// filter whose name holds an '=', a submodule's own filter (its configuration in .git/modules) as a status, a stash and a
/// rollback reach it, and a remote's upload-pack and receive-pack as a fetch, a push and a partial clone's read would run
/// them; each runs once the folder is trusted, so it was there to run.</summary>
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

    /// <summary>A shell command for git's configuration that marks <paramref name="name"/> ran, then does <paramref name="then"/>.</summary>
    private string Marking(string name, string then) => $"sh -c 'echo ran >> \"{Path.Combine(marks, name).Replace('\\', '/')}\"; {then}'";

    private bool Ran(string name) => File.Exists(Path.Combine(marks, name));

    /// <summary>A file given a time after its last, so a status reads its content again, through any filter it names.</summary>
    private static void Touch(string path) => File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path) + TimeSpan.FromMinutes(1));

    private static async Task<Git> OpenAsync(string tree) =>
        (await Git.OpenAsync(tree)).Tree ?? throw new InvalidOperationException($"No working tree was found at {tree}");
}
