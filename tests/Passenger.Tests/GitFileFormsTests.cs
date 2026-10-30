using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Files git keeps in another form than the disk's, over real working trees: one a filter smudges (as LFS and
/// git-crypt do) and one in Windows-1252, each compared, blamed and rolled back as the disk has it.</summary>
public sealed class GitFileFormsTests : IDisposable
{
    private readonly string folder = TestFolders.Create("git-forms-");

    public GitFileFormsTests() => TestFolders.Prepare(() =>
    {
        File.WriteAllText(Path.Combine(folder, "README.md"), "Forms\n");
        TestGit.Init(folder);
    }, folder);

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>A file a filter keeps lower case in git and upper case on disk, and a Windows-1252 file, both as committed:
    /// neither shows a change, Annotate blames each line to its commit, and Rollback Lines on the 1252 file puts its letter
    /// back as itself, saved in Windows-1252 rather than as UTF-8 around a replacement character.</summary>
    [AvaloniaFact]
    public void Filtered_and_Windows_1252_files_as_committed_show_no_change_blame_to_their_commit_and_roll_back_in_their_encoding()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        byte[] legacy = System.Text.Encoding.GetEncoding(1252).GetBytes("first line\ncafé au lait\nlast line\n");
        Git("config", "filter.up.smudge", "tr a-z A-Z");
        Git("config", "filter.up.clean", "tr A-Z a-z");
        File.WriteAllText(Path.Combine(folder, ".gitattributes"), "*.dat filter=up\n");
        File.WriteAllText(Path.Combine(folder, "data.dat"), "alpha\nbeta\ngamma\n");
        File.WriteAllBytes(Path.Combine(folder, "Legacy.txt"), legacy);
        Git("add", "-A");
        Git("commit", "-q", "-m", "Forms");
        string commit = Git("rev-parse", "HEAD").Trim();
        // Checked out again, the file is as its smudge writes it.
        File.Delete(Path.Combine(folder, "data.dat"));
        Git("checkout", "--", "data.dat");
        Assert.Equal("ALPHA\nBETA\nGAMMA\n", File.ReadAllText(Path.Combine(folder, "data.dat")));

        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        foreach (string name in (string[])["data.dat", "Legacy.txt"])
        {
            window.OpenFile(Path.Combine(folder, name));
            Ui.Settle();
            OpenDocument file = window.EditorArea.Active!;
            FileDiff diff = Ui.Landed(window.Vcs.DiffAsync(file), $"{name} compared with the last commit")!;
            Assert.True(diff.Hunks.Count == 0 && file.ChangeMarks.Count == 0,
                $"{name}, as committed, shows {diff.Hunks.Count} changes against the last commit's text:\n{diff.Head}");
            Ui.Await(window.ToggleAnnotationsAsync(file), $"{name}'s blame");
            IReadOnlyList<BlameLine> blame = window.Vcs.BlameOf(file)!;
            Assert.True(blame.Count == 3 && blame.All(l => l.Hash == commit),
                $"{name}'s lines, as committed, blame to [{string.Join(", ", blame.Select(l => l.IsCommitted ? l.Short : "not committed"))}]");
        }

        OpenDocument shown = window.EditorArea.Active!;
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "caf");
        window.KeyTextInput("f");
        Ui.Landed(() => shown.ChangeMarks.Any(m => m.Line == 1), "a change bar on the line typed in");
        window.Perform(AppActions.RollbackLines);
        Ui.Landed(() => shown.Text.Snapshot.ToString() == "first line\ncafé au lait\nlast line\n", "the line rolled back as committed");
        window.Perform(AppActions.Save);
        Ui.Settle();
        Assert.True(File.ReadAllBytes(Path.Combine(folder, "Legacy.txt")).AsSpan().SequenceEqual(legacy),
            $"Rollback Lines and a save left Legacy.txt as {Convert.ToHexString(File.ReadAllBytes(Path.Combine(folder, "Legacy.txt")))}, not its Windows-1252 bytes");
        window.Close();
    }

    /// <summary>A binary file changed since the last commit has no lines to compare: Show Diff says the binary files
    /// differ, by their sizes, and opens no diff of what reads as no text on one side and lines deleted on the other.</summary>
    [AvaloniaFact]
    public void Show_diff_of_a_changed_binary_file_says_the_binary_files_differ_by_their_sizes()
    {
        byte[] image = [0x89, (byte)'P', (byte)'N', (byte)'G', 0, 1, 2, 3];
        File.WriteAllBytes(Path.Combine(folder, "image.png"), image);
        Git("add", "image.png");
        Git("commit", "-q", "-m", "Image");
        File.WriteAllBytes(Path.Combine(folder, "image.png"), [.. image, 4, 5]);

        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.Landed(() => window.Vcs.Changes.Any(c => c.Name == "image.png"), "image.png listed as changed");
        DiffWindow? diff = Ui.Landed(window.ShowDiffAsync(window.Vcs.Changes.Single(c => c.Name == "image.png").Path), "Show Diff on image.png");
        Assert.True(diff is null, $"Show Diff on a binary file opened a diff of {diff?.View.Left.Document.Snapshot.Length} and {diff?.View.Right.Document.Snapshot.Length} characters");
        Assert.Equal("Binary files differ: image.png is 10 bytes now and 8 bytes in the last commit", window.Status);
        window.Close();
    }

    /// <summary>A file whose only change is its line separators, and (where git keeps modes) one whose only change is its
    /// mode, each listed as modified: Show Diff says which separators changed on how many lines, and the mode's change,
    /// where it said no differences.</summary>
    [AvaloniaFact]
    public void Show_diff_of_a_file_changed_only_in_its_line_separators_or_its_mode_says_what_changed()
    {
        File.WriteAllText(Path.Combine(folder, "lines.txt"), "one\ntwo\nthree\n");
        File.WriteAllText(Path.Combine(folder, "run.sh"), "echo run\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "Lines and a script");
        File.WriteAllText(Path.Combine(folder, "lines.txt"), "one\r\ntwo\r\nthree\r\n");
        bool modes = !OperatingSystem.IsWindows();
        if (modes)
        {
            File.SetUnixFileMode(Path.Combine(folder, "run.sh"), File.GetUnixFileMode(Path.Combine(folder, "run.sh")) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.Landed(() => window.Vcs.Changes.Count == (modes ? 2 : 1), "lines.txt and run.sh listed as changed");
        DiffWindow lines = Ui.Landed(window.ShowDiffAsync(Changed(window, "lines.txt")), "Show Diff on lines.txt")!;
        Ui.Landed(() => lines.View.Said == "Line separators changed: 3 lines from LF to CRLF", $"Show Diff saying lines.txt's separators changed, where it says \"{lines.View.Said}\"");
        lines.Close();
        if (modes)
        {
            DiffWindow script = Ui.Landed(window.ShowDiffAsync(Changed(window, "run.sh")), "Show Diff on run.sh")!;
            Ui.Landed(() => script.View.Said == "File mode changed from 100644 to 100755: now executable", $"Show Diff saying run.sh's mode changed, where it says \"{script.View.Said}\"");
            script.Close();
        }
        window.Close();
    }

    /// <summary>On macOS a committed file whose name holds an accented letter decomposed (as the Finder writes it), edited
    /// since: git names it composed, yet it shows its change bar and the Explorer's colour of a modified file as any other
    /// changed file does, and is listed by the name the disk has.</summary>
    [AvaloniaFact]
    public void A_changed_file_named_with_a_decomposed_accent_shows_its_change_bar_and_its_explorer_colour()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Only macOS's git composes the names its volumes keep decomposed");
        string path = Path.Combine(folder, "cafe\u0301.txt");
        File.WriteAllText(path, "one\ntwo\nthree\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "Café");
        File.WriteAllText(path, "one\nTWO\nthree\n");

        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile(path);
        Ui.Settle();
        OpenDocument file = window.EditorArea.Active!;
        Assert.Equal("cafe\u0301.txt", file.Name);
        Ui.Landed(() => file.ChangeMarks.Any(m => m.Line == 1 && m.Kind == ChangeMarkKind.Modified), "a change bar on café.txt's second line");
        Assert.Equal([file.Path], window.Vcs.Changes.Select(c => c.Path));
        Passenger.App.Explorer.ExplorerNode row = window.ExplorerView.Tree.Rows.First(r => r.Path == file.Path);
        Assert.True(row.Change == ChangeKind.Modified, $"The Explorer's café.txt is coloured as {row.Change?.ToString() ?? "unchanged"}");
        window.Close();
    }

    /// <summary>A file whose only change is a UTF-8 byte order mark added, and one saved again as UTF-16, each listed as
    /// modified: Show Diff and an agent's vcs_status with its path say the mark added and the encoding changed, where
    /// each said no differences, so neither change is committed unseen.</summary>
    [AvaloniaFact]
    public void Show_diff_and_vcs_status_of_a_file_changed_only_in_its_byte_order_mark_or_its_encoding_say_what_changed()
    {
        File.WriteAllText(Path.Combine(folder, "Marked.cs"), "class Marked\n{\n}\n", new System.Text.UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "Notes: café\n", new System.Text.UTF8Encoding(false));
        Git("add", "-A");
        Git("commit", "-q", "-m", "Marked and notes");
        File.WriteAllText(Path.Combine(folder, "Marked.cs"), "class Marked\n{\n}\n", new System.Text.UTF8Encoding(true));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "Notes: café\n", System.Text.Encoding.Unicode);

        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.Landed(() => window.Vcs.Changes.Count == 2, "Marked.cs and notes.txt listed as changed");
        Passenger.App.Mcp.PassengerTools agent = new(window);
        foreach ((string name, string said) in new[] { ("Marked.cs", "Byte order mark added"), ("notes.txt", "Encoding changed from UTF-8 to UTF-16 LE") })
        {
            DiffWindow diff = Ui.Landed(window.ShowDiffAsync(Changed(window, name)), $"Show Diff on {name}")!;
            Ui.Landed(() => diff.View.Said == said, $"Show Diff saying \"{said}\" of {name}, where it says \"{diff.View.Said}\"");
            diff.Close();
            Assert.Equal(said, Ui.Await(agent.VcsStatus(name), $"vcs_status of {name}"));
        }
        window.Close();
    }

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
        const string Said = "Binary files differ: asset.bin is 104858624 bytes now and 104857600 bytes in the last commit";
        Ui.WaitFor(() => window.Status == Said, () => $"the Commit window's Show Diff saying the binary files differ, where the status says \"{window.Status}\"");
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal(["asset.bin modified"], status.Changes.Select(c => $"{c.Path} {c.Kind}"));
        Assert.Equal("Binary files a/asset.bin and b/asset.bin differ", diff);
        Assert.True(window.Frontmost is not DiffWindow, "Show Diff on a binary file opened a diff");
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

    private static string Changed(MainWindow window, string name) => window.Vcs.Changes.Single(c => c.Name == name).Path;

    private string Git(params string[] arguments) => TestGit.Run(folder, arguments);
}
