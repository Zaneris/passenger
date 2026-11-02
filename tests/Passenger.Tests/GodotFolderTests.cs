using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Navigation;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>A Godot project's .godot folder (its imports, its editor's state, Godot.NET.Sdk's build) is build output, as
/// bin and obj are: the Explorer dims it, Go to File and Find in Files pass over it, and a file made in it later is heard
/// as nothing of the person's, watched each folder on its own as on Linux or as a whole tree.</summary>
public sealed class GodotFolderTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_godot_folder_is_dimmed_as_build_output_and_passed_over_by_go_to_file_and_find_in_files(bool eachFolder)
    {
        string folder = TestFolders.Create("godot-cache-");
        TestFolders.Write(folder, GodotProject.FileName, "config_version=5\n");
        string source = TestFolders.Write(folder, Path.Combine("Scripts", "Needle.cs"), "// needle\n");
        TestFolders.Write(folder, Path.Combine(".godot", "mono", "temp", "obj", "NeedleGenerated.cs"), "// needle\n");
        TestFolders.Write(folder, Path.Combine(".godot", "editor", "needle.cfg"), "needle\n");
        MainWindow window = new() { Width = 1200, Height = 800, Watching = eachFolder ? FolderWatching.Inotify : FolderWatching.WholeTree, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        string root = PathNames.Canonical(folder);
        string expected = Path.Combine("Scripts", "Needle.cs");

        ExplorerNode cache = window.ExplorerView.Tree.Rows.Single(r => r.Path == Path.Combine(root, IgnoredFolders.GodotCache));
        Assert.True(cache.IsDimmed, "The Explorer shows the Godot project's .godot as the person's code, though it is Godot's cache and build output");

        Ui.Press(window, Ui.Shortcut(AppActions.GoToFile));
        window.KeyTextInput("needle");
        Ui.Settle();
        string[] offered = [.. Assert.IsType<GoToFilePopup>(window.Popup).Rows.Select(hit => hit.RelativePath)];
        Assert.True(offered.SequenceEqual([expected]), $"Go to File offers [{string.Join(", ", offered)}] for needle, where only {expected} is the person's");
        Ui.Press(window, "ESCAPE");

        FindInFilesPopup popup = window.ShowFindInFiles(replace: false)!;
        popup.Text = "needle";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Task searching = popup.SearchAsync();
        Ui.WaitFor(() => searching.IsCompleted && popup.Searching.IsCompleted && popup.Status != "Searching…", "the search for needle");
        string[] found = [.. popup.Matches.Select(m => Path.GetRelativePath(root, m.Path)).Distinct()];
        Assert.True(found.SequenceEqual([expected]), $"Find in Files found needle in [{string.Join(", ", found)}], where only {expected} is the person's");
        Ui.Press(window, "ESCAPE");

        TestFolders.Write(folder, Path.Combine(".godot", "imported", "later.cs"), "// later\n");
        TestFolders.Write(folder, Path.Combine("Scripts", "Later.cs"), "// later\n");
        Ui.WaitFor(() => window.FileIndex.Files.Contains(Path.Combine("Scripts", "Later.cs")), "the script made later in Go to File");
        Assert.True(!window.FileIndex.Files.Any(f => f.StartsWith(IgnoredFolders.GodotCache, StringComparison.Ordinal)),
            $"Go to File took in what Godot wrote under .godot: {string.Join(", ", window.FileIndex.Files.Where(f => f.StartsWith(IgnoredFolders.GodotCache, StringComparison.Ordinal)))}");
        Assert.True(File.Exists(source), "The project's own script went");
        window.Close();
    }
}
