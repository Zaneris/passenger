using Passenger.Core;

namespace Passenger.UnitTests.Core;

public sealed class TrustedFoldersTests
{
    private static readonly string Top = Path.GetFullPath(Path.Combine(Path.DirectorySeparatorChar.ToString(), "trusted-folders-tests"));

    private static string At(params string[] names) => Path.Combine([Top, .. names]);

    [Fact]
    public void A_folder_trusted_trusts_every_folder_and_file_under_it_and_nothing_beside_it()
    {
        TrustedFolders trusted = TrustedFolders.None.With([At("work", "app")]);

        Assert.True(trusted.Trusts(At("work", "app")), "The folder trusted was not trusted itself");
        Assert.True(trusted.Trusts(At("work", "app", "src", "Program.cs")), "A file under the folder trusted was not trusted");
        Assert.False(trusted.Trusts(At("work", "app2")), "A folder beside the one trusted, its name beginning alike, was trusted");
        Assert.False(trusted.Trusts(At("work")), "The folder above the one trusted was trusted");
        Assert.False(TrustedFolders.None.Trusts(At("work", "app")), "A folder was trusted before anything was");
    }

    [Fact]
    public void A_parent_trusted_takes_the_place_of_the_folders_under_it_and_a_folder_under_a_trusted_one_adds_nothing()
    {
        TrustedFolders children = TrustedFolders.None.With([At("work", "app"), At("work", "lib"), At("other")]);

        TrustedFolders parent = children.With([At("work")]);

        Assert.Equal([At("other"), At("work")], parent.Roots);
        Assert.Same(parent, parent.With([At("work", "app", "src")]));
        Assert.Same(parent, parent.With(["relative", string.Empty]));
    }

    [Fact]
    public void Where_every_folder_is_trusted_a_folder_held_to_the_persons_answer_is_trusted_only_once_it_or_a_parent_is()
    {
        TrustedFolders everything = TrustedFolders.None with { Everything = true, Asked = [At("work", "app")] };

        Assert.True(everything.Trusts(At("elsewhere")), "A folder no one asked of was not trusted where every folder is");
        Assert.False(everything.Trusts(At("work", "app", "src")), "A folder under one held to the person's answer was trusted before they trusted it");
        Assert.True(everything.With([At("work")]).Trusts(At("work", "app", "src")), "The parent trusted did not trust the folder held to the person's answer");
    }

    [Fact]
    public void A_trusted_workspaces_folders_beyond_its_own_are_trusted_while_it_lends_them_and_no_longer_once_it_lets_them_go()
    {
        object workspace = new();
        TrustedFolders lent = TrustedFolders.None.With([At("repo", "src")]).Lending(workspace, [At("repo", "tests"), At("repo", "tests")]);

        Assert.True(lent.Trusts(At("repo", "tests", "Shop.Tests", "Shop.Tests.csproj")), "A folder the trusted workspace lent was not trusted");
        Assert.False(lent.Trusts(At("repo", "docs")), "A folder beside those lent was trusted");
        Assert.Single(lent.Lent);
        Assert.False(lent.Lending(workspace, []).Trusts(At("repo", "tests")), "A folder stayed trusted once the workspace that lent it let it go");
        Assert.True(lent.Lending(new object(), []).Trusts(At("repo", "tests")), "Another workspace letting its folders go took back this one's");
    }

    [Fact]
    public void A_workspace_lends_a_folder_beside_its_own_never_a_disks_root_the_home_folder_a_folder_holding_it_or_its_own()
    {
        string open = At("repo", "app");
        string home = PathNames.Canonical(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        Assert.True(WorkspaceTrust.Lends(At("repo", "lib"), open), "A project's folder beside the open folder could not be lent");
        Assert.True(WorkspaceTrust.Lends(Path.Combine(home, "code", "lib"), open), "A project's folder in the home folder could not be lent");
        Assert.False(WorkspaceTrust.Lends(Path.GetPathRoot(open)!, open), "A disk's root could be lent");
        Assert.False(WorkspaceTrust.Lends(At("repo"), open), "The folder holding the open folder could be lent");
        Assert.False(WorkspaceTrust.Lends(Top, open), "A folder above the open folder could be lent");
        Assert.False(WorkspaceTrust.Lends(open, open), "The open folder could be lent to itself");
        Assert.False(WorkspaceTrust.Lends(home, open), "The home folder could be lent");
        Assert.False(WorkspaceTrust.Lends(Path.GetDirectoryName(home)!, open), "The folder holding the home folder could be lent");
        Assert.False(WorkspaceTrust.Lends("relative", open), "A relative path could be lent");
    }

    [Fact]
    public void Why_a_workspace_lends_a_folder_nothing_names_the_disks_root_the_open_folder_or_the_home_folder_it_is_or_holds()
    {
        string open = At("repo", "app");
        string home = PathNames.Canonical(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        Assert.Null(WorkspaceTrust.WhyNotLent(At("repo", "lib"), open));
        Assert.Equal(Unlent.Root, WorkspaceTrust.WhyNotLent(Path.GetPathRoot(open)!, open));
        Assert.Equal(Unlent.HoldsOpen, WorkspaceTrust.WhyNotLent(At("repo"), open));
        Assert.Equal(Unlent.HoldsOpen, WorkspaceTrust.WhyNotLent(open, open));
        Assert.Equal(Unlent.HoldsHome, WorkspaceTrust.WhyNotLent(home, open));
        Assert.Equal(Unlent.Relative, WorkspaceTrust.WhyNotLent("relative", open));
        if (Path.GetDirectoryName(home) is { } above && Path.GetDirectoryName(above) is not null)
        {
            Assert.Equal(Unlent.HoldsHome, WorkspaceTrust.WhyNotLent(above, open));
        }
    }

    [Fact]
    public void A_folder_in_a_Git_repository_is_trusted_as_the_repositorys_top_and_one_in_none_as_itself()
    {
        string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);
        try
        {
            string repository = Directory.CreateDirectory(Path.Join(root, "repo")).FullName;
            Directory.CreateDirectory(Path.Join(repository, ".git"));
            string app = Directory.CreateDirectory(Path.Join(repository, "src", "App")).FullName;
            string plain = Directory.CreateDirectory(Path.Join(root, "plain", "App")).FullName;

            Assert.Equal(repository, WorkspaceTrust.FolderToTrust(app));
            Assert.Equal(repository, WorkspaceTrust.FolderToTrust(repository));
            Assert.Equal(repository, WorkspaceTrust.FolderToTrust(Path.Join(repository, "samples", "New")));
            Assert.Equal(plain, WorkspaceTrust.FolderToTrust(plain));
            Assert.Null(WorkspaceTrust.RepositoryOf(plain));
            Assert.Null(WorkspaceTrust.RepositoryOf("relative"));
            Assert.False(WorkspaceTrust.TrustsAlone(Path.Join(repository, "samples", "New")), "A new folder in a Git repository was trusted alone, without the repository");
            Assert.True(WorkspaceTrust.TrustsAlone(repository) && WorkspaceTrust.TrustsAlone(plain), "A repository's top, or a folder in none, was not trusted alone");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_git_file_naming_a_git_folder_makes_a_worktrees_top_and_a_submodule_takes_its_superprojects_top()
    {
        string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);
        try
        {
            string worktree = Directory.CreateDirectory(Path.Join(root, "worktree")).FullName;
            File.WriteAllText(Path.Join(worktree, ".git"), "gitdir: /elsewhere/repo/.git/worktrees/worktree\n");
            string superproject = Directory.CreateDirectory(Path.Join(root, "super")).FullName;
            Directory.CreateDirectory(Path.Join(superproject, ".git", "modules", "sub"));
            string submodule = Directory.CreateDirectory(Path.Join(superproject, "lib", "sub")).FullName;
            File.WriteAllText(Path.Join(submodule, ".git"), "gitdir: ../../.git/modules/sub\n");
            string odd = Directory.CreateDirectory(Path.Join(root, "odd", "App")).FullName;
            File.WriteAllText(Path.Join(root, "odd", ".git"), "not a repository\n");

            Assert.Equal(worktree, WorkspaceTrust.FolderToTrust(Directory.CreateDirectory(Path.Join(worktree, "src")).FullName));
            Assert.Equal(superproject, WorkspaceTrust.FolderToTrust(Directory.CreateDirectory(Path.Join(submodule, "src")).FullName));
            Assert.Equal(superproject, WorkspaceTrust.FolderToTrust(submodule));
            Assert.Equal(odd, WorkspaceTrust.FolderToTrust(odd));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Trusting_a_folder_in_a_Git_repository_trusts_the_repository_whose_trust_taken_back_takes_back_the_folder()
    {
        string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);
        string repository = Directory.CreateDirectory(Path.Join(root, "repo")).FullName;
        try
        {
            Directory.CreateDirectory(Path.Join(repository, ".git"));
            string app = Directory.CreateDirectory(Path.Join(repository, "src", "App")).FullName;
            string tools = Directory.CreateDirectory(Path.Join(repository, "tools")).FullName;

            WorkspaceTrust.Trust(app);

            Assert.True(WorkspaceTrust.Trusts(repository), "Trusting a folder in a Git repository left the repository, whose build files its projects take in, untrusted");
            Assert.True(WorkspaceTrust.Trusts(tools), "A folder beside the one trusted, in its Git repository, was not trusted with the repository");
            WorkspaceTrust.TakeBack(repository);
            Assert.False(WorkspaceTrust.Trusts(app), "The folder trusted with its repository stayed trusted once the repository's trust was taken back");
        }
        finally
        {
            WorkspaceTrust.TakeBack(repository);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_folder_taken_back_is_trusted_no_longer_with_every_folder_under_it_while_a_parent_still_trusted_keeps_it()
    {
        TrustedFolders trusted = TrustedFolders.None.With([At("work", "app"), At("other")]);

        TrustedFolders taken = trusted.Without(At("work", "app"));

        Assert.False(taken.Trusts(At("work", "app", "src")), "A folder under the one taken back stayed trusted");
        Assert.True(taken.Trusts(At("other")), "Taking one folder back took back another beside it");
        Assert.Same(taken, taken.Without(At("work", "app")));
        Assert.Empty(TrustedFolders.None.With([At("other", "a"), At("other", "b")]).Without(At("other")).Roots);
        Assert.True(TrustedFolders.None.With([At("work")]).Without(At("work", "app")).Trusts(At("work", "app")), "A folder whose parent stays trusted was taken back");
    }
}
