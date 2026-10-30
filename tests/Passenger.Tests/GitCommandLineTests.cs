using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>What the window hands git names the files and repository the person means, however they are named: a
/// folder whose name ends in a space is its own repository.</summary>
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
}
