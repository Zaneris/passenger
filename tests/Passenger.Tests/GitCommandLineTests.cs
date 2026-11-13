using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>What the window hands git names the files and repository the person means, however many and however they
/// are named: thousands of long paths commit and roll back, a folder whose name ends in a space is its own repository,
/// and a\b.txt is not a/b.txt off Windows; and a repository's folder deleted under a window starts no git there.</summary>
public sealed class GitCommandLineTests : IDisposable
{
    private const string Original = "class Shop\n{\n    int count;\n}\n";

    private readonly string folder = TestFolders.Create("git-lines-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>Run as a window test is, on a dispatcher that goes when its run ends: the working tree's folder is deleted
    /// under the window, its status read, and the window closed with another asked for. No git starts in the folder gone,
    /// so none is said to be not installed, and none that failed to start there is left for the finalizer to report into
    /// whichever test ran then.</summary>
    [Fact]
    public async Task A_status_asked_for_in_a_deleted_folder_never_says_git_is_not_installed_nor_leaves_an_error_once_the_windows_loop_is_gone()
    {
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original);
        await TestGit.InitAsync(folder);
        string tree = PathNames.Canonical(folder);
        List<Exception> unheard = [];
        EventHandler<UnobservedTaskExceptionEventArgs> finalized = (_, e) =>
        {
            lock (unheard)
            {
                unheard.AddRange(e.Exception.InnerExceptions.Where(x => x.Message.Contains(tree, StringComparison.Ordinal)));
            }
        };
        TaskScheduler.UnobservedTaskException += finalized;
        try
        {
            (Task? Ended, bool Missing, Exception? Failed) run = await HeadlessUnitTestSession.GetOrStartForAssembly(typeof(GitCommandLineTests).Assembly).Dispatch(() =>
            {
                // As every window test starts and ends (TestsLeaveNothingBehind), its window closed however the run ends.
                BackgroundWork.Forget();
                OpenWindows.Watch();
                try
                {
                    MainWindow window = Ui.OpenFolder(folder, languageServer: false);
                    Ui.Landed(() => window.Vcs.Branch == "main", "the branch read as the working tree opened");
                    TestFolders.Delete(folder);
                    Task read = window.Vcs.RefreshAsync();
                    Ui.AwaitPumped(read, "the status asked for once the folder was gone");
                    bool missing = window.Vcs.GitMissing;
                    // Asked again as the window closes, never come back to.
                    UnawaitedWork.Dismiss(window.Vcs.RefreshAsync());
                    window.Close();
                    return (window.Ended, missing, (Exception?)null);
                }
                catch (Exception failed)
                {
                    OpenWindows.CloseAll();
                    return (null, false, failed);
                }
            }, TestContext.Current.CancellationToken);
            // Off the window's thread, where the session ends a run: a test ending there holds it in the runner's wait for the next.
            await Task.Yield();
            if (run.Failed is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(run.Failed);
            }
            Assert.False(run.Missing, "With the working tree's folder deleted, the window says git is not installed");
            Task ended = run.Ended ?? throw new InvalidOperationException("The window closed with no end to wait for");
            Assert.True(await Waiting.Within(ended, MainWindow.EndLimit + TimeSpan.FromSeconds(5).Scaled(), TestContext.Current.CancellationToken, scaled: false),
                "The window closed without ending what it started within its limit, so the IDE's process would not end");
            // A task nobody came back for is heard of when the collector finalizes it.
            for (int i = 0; i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Waiting.For(TimeSpan.FromMilliseconds(100), "for a task nobody came back for to be finalized and heard of, were there one");
            }
            lock (unheard)
            {
                Assert.True(unheard.Count == 0, "A git asked for in the deleted folder failed to start once the window's loop was gone, with nothing left to "
                    + $"hear it, so the IDE would report it as an error into whatever ran next: {string.Join("; ", unheard.Select(e => e.Message))}");
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= finalized;
        }
    }

    /// <summary>Commit and Rollback of 2,000 files whose paths together overrun the system's command line (Windows' 32,767
    /// characters, macOS's 1 MB, Linux's 2 MB) take every one of them.</summary>
    [AvaloniaFact]
    public void Commit_and_rollback_of_two_thousand_files_with_long_paths_take_them_all()
    {
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original);
        TestGit.Init(folder);
        // Each path within what the system takes, Windows' 260 characters among them.
        int depth = OperatingSystem.IsWindows() ? 0 : OperatingSystem.IsMacOS() ? 2 : 5;
        string deep = Path.Combine([folder, .. Enumerable.Range(0, depth).Select(i => new string((char)('a' + i), 200))]);
        Directory.CreateDirectory(deep);
        string[] files = [.. Enumerable.Range(0, 2000).Select(i => Path.Combine(deep, $"{new string('f', OperatingSystem.IsWindows() ? 80 : 140)}{i:D4}.txt"))];
        foreach (string file in files)
        {
            File.WriteAllText(file, "first\n");
        }

        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.Landed(() => window.Vcs.Changes.Count == files.Length, "the 2,000 new files listed");
        CommitResult made = Ui.Landed(window.Vcs.CommitAsync([.. window.Vcs.Changes], "Two thousand", amend: false, push: false), "the commit");
        Assert.True(made.Commit.Succeeded, made.Commit.Message);
        Assert.Equal(files.Length + 1, TestGit.Run(folder, "ls-files", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries).Length);

        foreach (string file in files)
        {
            File.WriteAllText(file, "changed\n");
        }
        Task read = window.Vcs.RefreshAsync();
        Ui.Landed(() => read.IsCompleted && window.Vcs.Changes.Count == files.Length, "the 2,000 files listed as changed");
        VcsOutcome rolled = Ui.Landed(window.Vcs.RollbackAsync([.. window.Vcs.Changes]), "the rollback");
        Assert.True(rolled.Succeeded, rolled.Message);
        Assert.All(files, file => Assert.Equal("first\n", File.ReadAllText(file)));
        Ui.Landed(() => window.Vcs.Changes.Count == 0, "no file listed as changed once rolled back");
        window.Close();
    }

    /// <summary>A folder "repo " opened beside a repository "repo" is the repository git names, never its sibling: a stash
    /// puts away its own change and leaves the sibling's.</summary>
    [AvaloniaFact]
    public void A_folder_whose_name_ends_in_a_space_is_its_own_repository_beside_one_named_without_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows takes no file name that ends in a space");
        string spaced = Path.Combine(folder, "repo ");
        string plain = Path.Combine(folder, "repo");
        foreach (string repository in (string[])[spaced, plain])
        {
            Directory.CreateDirectory(repository);
            File.WriteAllText(Path.Combine(repository, "Shop.cs"), Original);
            TestGit.Init(repository);
            File.WriteAllText(Path.Combine(repository, "Shop.cs"), $"{Original}// {Path.GetFileName(repository)}.\n");
        }

        MainWindow window = Ui.OpenFolder(spaced, languageServer: false);
        Ui.Landed(() => window.Vcs.Changes.Count == 1, "the spaced folder's change listed");
        Assert.Equal(PathNames.Canonical(spaced), window.Vcs.Repository?.Root);
        Assert.Equal(PathNames.Canonical(Path.Combine(spaced, "Shop.cs")), window.Vcs.Changes[0].Path);

        VcsOutcome stashed = Ui.Landed(window.Vcs.StashAsync("Spaced"), "the stash");
        Assert.True(stashed.Succeeded, stashed.Message);
        Assert.Equal(Original, File.ReadAllText(Path.Combine(spaced, "Shop.cs")));
        Assert.Equal($"{Original}// repo.\n", File.ReadAllText(Path.Combine(plain, "Shop.cs")));
        Assert.Contains("Spaced", TestGit.Run(spaced, "stash", "list"), StringComparison.Ordinal);
        Assert.True(TestGit.Run(plain, "stash", "list").Trim().Length == 0, "The stash was made in the sibling repository without the space");
        window.Close();
    }

    /// <summary>Off Windows a backslash is a letter of a file's name: with a\b.txt and a/b.txt both changed, Rollback of
    /// a\b.txt puts it back alone, and a/b.txt keeps its change.</summary>
    [AvaloniaFact]
    public void Rollback_of_a_file_whose_name_holds_a_backslash_puts_back_that_file_and_not_the_one_in_a_folder()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows takes a backslash for a separator, never a letter of a name");
        string backslashed = Path.Combine(folder, "a\\b.txt");
        string nested = Path.Combine(folder, "a", "b.txt");
        Directory.CreateDirectory(Path.Combine(folder, "a"));
        File.WriteAllText(backslashed, "backslash\n");
        File.WriteAllText(nested, "nested\n");
        TestGit.Init(folder);
        File.WriteAllText(backslashed, "backslash changed\n");
        File.WriteAllText(nested, "nested changed\n");

        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.Landed(() => window.Vcs.Changes.Count == 2, "both files listed as changed");
        FileChange chosen = Assert.Single(window.Vcs.Changes, c => c.Path == PathNames.Canonical(backslashed));
        VcsOutcome rolled = Ui.Landed(window.Vcs.RollbackAsync([chosen]), "the rollback");
        Assert.True(rolled.Succeeded, rolled.Message);
        Assert.Equal("backslash\n", File.ReadAllText(backslashed));
        Assert.True(File.ReadAllText(nested) == "nested changed\n", "Rollback of a\\b.txt put back a/b.txt");
        Ui.Landed(() => window.Vcs.Changes.Count == 1, "a/b.txt alone still listed as changed");
        Assert.Equal(PathNames.Canonical(nested), window.Vcs.Changes[0].Path);
        window.Close();
    }
}
