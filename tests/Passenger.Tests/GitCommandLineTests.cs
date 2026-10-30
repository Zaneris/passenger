using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>What the window hands git names the files and repository the person means, however many and however they
/// are named: thousands of long paths commit and roll back, a folder whose name ends in a space is its own repository,
/// and a\b.txt is not a/b.txt off Windows.</summary>
public sealed class GitCommandLineTests : IDisposable
{
    private const string Original = "class Shop\n{\n    int count;\n}\n";

    private readonly string folder = TestFolders.Create("git-lines-");

    public void Dispose() => TestFolders.Delete(folder);

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
