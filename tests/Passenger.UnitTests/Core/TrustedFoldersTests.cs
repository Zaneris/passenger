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
}
