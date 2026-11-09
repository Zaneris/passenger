using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What git's status lists in a test runner's own output is found by its folders, read where they lie: each
/// unversioned file of a status is asked, so the question costs no string however deep the file.</summary>
public class IgnoredFoldersTests
{
    [Fact]
    public void A_file_is_found_in_a_test_runners_output_by_its_folders_read_in_place()
    {
        string runner = IgnoredFolders.GdUnit4Runner;
        string deep = Path.Join("/work", "game", "src", "levels", "forest", "enemies", "bosses", "Troll.cs");
        string inRunner = Path.Join("/work", "game", runner, "reports", "run", "report.xml");
        Assert.True(IgnoredFolders.InToolOutput(inRunner), "A report the test runner wrote counts as the person's work");
        Assert.True(IgnoredFolders.InToolOutput(Path.Join(runner, "Scene.cs")), "A file at the top of the runner's folder counts as the person's work");
        Assert.False(IgnoredFolders.InToolOutput(deep), "A file of the person's own is taken for the runner's output");
        Assert.False(IgnoredFolders.InToolOutput(Path.Join("/work", "game", runner)), "A file named as the runner's folder is taken for what it holds");
        Assert.False(IgnoredFolders.InToolOutput(Path.Join("/work", "game", runner + "s", "Scene.cs")), "A folder whose name begins with the runner's is taken for it");

        bool any = false;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            any |= IgnoredFolders.InToolOutput(deep) | IgnoredFolders.InToolOutput(inRunner);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(any);
        Assert.True(allocated == 0, $"Asking 2,000 times whether a file is the runner's allocated {allocated:N0} bytes, which a status of thousands of files pays each time it is read");
    }
}
