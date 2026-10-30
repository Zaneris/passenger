using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
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

    private string Git(params string[] arguments) => TestGit.Run(folder, arguments);
}
