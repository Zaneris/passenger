using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>What the window hands git names the files and repository the person means, however they are named: a
/// folder whose name ends in a space is its own repository, and a\b.txt is not a/b.txt off Windows.</summary>
public sealed class GitCommandLineTests : IDisposable
{
    private const string Original = "class Shop\n{\n    int count;\n}\n";

    private readonly string folder = TestFolders.Create("git-lines-");

    public void Dispose() => TestFolders.Delete(folder);

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
