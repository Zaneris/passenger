using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>What reading Git's state of a large binary file allocates, counted on every thread: the class runs alone, as
/// whatever a class beside the window tests allocates meanwhile would count as the IDE's.</summary>
[Collection(Alone.Name)]
[AboutMacOS]
public sealed class GitFileFormsBytesTests : IDisposable
{
    private readonly string folder = TestFolders.Create("git-forms-");

    public GitFileFormsBytesTests() => TestFolders.Prepare(() =>
    {
        File.WriteAllText(Path.Combine(folder, "README.md"), "Forms\n");
        TestGit.Init(folder);
    }, folder);

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>A changed 100 MB binary file costs what its probe and its listing do where Git's state is read: an agent's
    /// vcs_status, its diff of the file, the Commit window and its Show Diff (saying the binary files differ by their
    /// sizes) together allocate a small part of the file's size, where each read the file whole, several times over.</summary>
    [AvaloniaFact]
    public void A_changed_large_binary_file_costs_its_probe_not_its_size_in_vcs_status_the_commit_window_and_show_diff()
    {
        const int Size = 100 * 1024 * 1024;
        string asset = Path.Combine(folder, "asset.bin");
        WriteAsset(asset, Size, seed: 1);
        Git("add", "asset.bin");
        Git("commit", "-q", "-m", "Asset");
        WriteAsset(asset, Size + 1024, seed: 2);

        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.Landed(() => window.Vcs.Changes.Any(c => c.Name == "asset.bin"), "asset.bin listed as changed");
        Passenger.App.Mcp.PassengerTools agent = new(window);
        string cmd = OperatingSystem.IsMacOS() ? "meta" : "control";
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Passenger.App.Mcp.VcsStatus status = (Passenger.App.Mcp.VcsStatus)Ui.Await(agent.VcsStatus(), "an agent's vcs_status");
        object diff = Ui.Await(agent.VcsStatus("asset.bin"), "an agent's diff of asset.bin");
        window.Perform(AppActions.ShowCommit);
        Passenger.App.ToolWindows.CommitView commit = window.CommitView;
        Ui.Landed(() => commit.Rows.OfType<Passenger.App.ToolWindows.CommitRow>().Any(r => r.Change.Name == "asset.bin"), "the Commit window listing asset.bin");
        Avalonia.Controls.ListBox changes = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(commit).OfType<Avalonia.Controls.ListBox>().First(l => ReferenceEquals(l.ItemsSource, commit.Rows));
        Ui.Click(Ui.Row(changes, commit.Rows.OfType<Passenger.App.ToolWindows.CommitRow>().First(r => r.Change.Name == "asset.bin")));
        Ui.Press(window, $"{cmd} D");
        string said = $"Binary files differ: asset.bin is {Numbers.Said(Size + 1024)} bytes now and {Numbers.Said(Size)} bytes in the last commit";
        Ui.WaitFor(() => window.Status == said, () => $"the Commit window's Show Diff saying \"{said}\", where the status says \"{window.Status}\"");
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal(["asset.bin modified"], status.Changes.Select(c => $"{c.Path} {c.Kind}"));
        Assert.Equal("Binary files a/asset.bin and b/asset.bin differ", diff);
        OpenDiffs.None(window, "Show Diff on a binary file opened a diff");
        Assert.True(allocated < Size / 8, $"Reading Git's state of a changed {Size}-byte binary file allocated {allocated} bytes, so a large asset costs memory as its size, not its probe");
        window.Close();
    }

    /// <summary>A file of so many bytes, each run the same for its seed, a NUL among its first: binary, and kept small by
    /// git, which finds its pattern repeat.</summary>
    private static void WriteAsset(string path, int size, int seed)
    {
        byte[] chunk = new byte[1024 * 1024];
        for (int i = 0; i < chunk.Length; i++)
        {
            chunk[i] = (byte)((i * 7 + seed) % 251);
        }
        using FileStream file = new(path, FileMode.Create, FileAccess.Write);
        for (int written = 0; written < size; written += chunk.Length)
        {
            file.Write(chunk, 0, Math.Min(chunk.Length, size - written));
        }
    }

    private string Git(params string[] arguments) => TestGit.Run(folder, arguments);
}
