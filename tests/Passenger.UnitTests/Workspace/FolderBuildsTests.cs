using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A folder about to go waits for the design-time builds of its projects to land, and where a folder some
/// process works in cannot go (Windows) for the end of the host's round that built one, whose session leaves the process
/// that built it working in its folder; never for builds of projects elsewhere.</summary>
public sealed class FolderBuildsTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\work" : "/work";
    private static readonly string Lib = Path.Combine(Root, "Lib");
    private static readonly string LibProject = Path.Combine(Lib, "Lib.csproj");
    private static readonly string ShopProject = Path.Combine(Root, "Shop.csproj");

    [Fact]
    public void Where_working_folders_are_held_a_folder_whose_project_has_landed_waits_for_its_round_to_end()
    {
        TaskCompletionSource ended = new();
        DesignTimeRound round = new([ShopProject, LibProject], ended.Task);

        Task awaited = FolderBuilds.Awaited(Lib, [(ShopProject, new TaskCompletionSource().Task)], round, workingFoldersHeld: true);

        Assert.False(awaited.IsCompleted, "Lib would go as soon as its own build landed, while the round's build session still worked in its folder");
        ended.SetResult();
        Assert.True(awaited.IsCompleted, "Lib's delete still waited once the round that built it had ended");
    }

    [Fact]
    public void Where_working_folders_are_held_a_folder_waits_for_its_project_to_land_and_its_round_to_end()
    {
        TaskCompletionSource landed = new();
        TaskCompletionSource ended = new();
        DesignTimeRound round = new([LibProject], ended.Task);

        Task awaited = FolderBuilds.Awaited(Lib, [(LibProject, landed.Task)], round, workingFoldersHeld: true);

        landed.SetResult();
        Assert.False(awaited.IsCompleted, "Lib would go once its build landed, before the round's build session let go of its folder");
        ended.SetResult();
        Assert.True(awaited.IsCompleted);
    }

    [Fact]
    public void Where_working_folders_do_not_hold_a_folder_goes_once_its_projects_have_landed_whatever_the_round()
    {
        TaskCompletionSource landed = new();
        DesignTimeRound round = new([ShopProject, LibProject], new TaskCompletionSource().Task);

        Task awaited = FolderBuilds.Awaited(Lib, [(LibProject, landed.Task), (ShopProject, new TaskCompletionSource().Task)], round, workingFoldersHeld: false);

        Assert.False(awaited.IsCompleted, "Lib would go while its own design-time build still wrote its obj/");
        landed.SetResult();
        Assert.True(awaited.IsCompleted, "Lib waited for the round, or for Shop's build, where no folder a process works in is held");
    }

    [Fact]
    public void A_folder_waits_for_no_round_that_built_none_of_its_projects()
    {
        DesignTimeRound round = new([ShopProject], new TaskCompletionSource().Task);

        Task awaited = FolderBuilds.Awaited(Lib, [(ShopProject, new TaskCompletionSource().Task)], round, workingFoldersHeld: true);

        Assert.True(awaited.IsCompleted, "Lib waited for a round that built only Shop, beside it");
    }
}
