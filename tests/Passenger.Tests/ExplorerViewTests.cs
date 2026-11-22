using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Navigation;
using Passenger.App.Settings;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>The Explorer's view, the solution's projects or the folder's files: the view a person switches to, by its
/// switch, Find Action or a key of their own, is kept with the folder, which opens in it again, after a restart too, while
/// another folder opens in its own; a folder never switched opens in the Solution view, as before the view was kept.</summary>
[SameOnEverySystem]
public sealed class ExplorerViewTests : IDisposable
{
    private readonly string shop = PathNames.Canonical(TestFolders.Create("explorer-view-shop-"));
    private readonly string tool = PathNames.Canonical(TestFolders.Create("explorer-view-tool-"));
    private readonly string loose = PathNames.Canonical(TestFolders.Create("explorer-view-loose-"));
    private readonly string settings = TestFolders.Create("explorer-view-settings-");

    public ExplorerViewTests()
    {
        foreach ((string folder, string name) in (ReadOnlySpan<(string, string)>)[(shop, "Shop"), (tool, "Tool")])
        {
            TestFolders.Write(folder, $"{name}/{name}.csproj", TestProjects.Console());
            TestFolders.Write(folder, $"{name}/Program.cs", $"System.Console.WriteLine(\"{name}\");\n");
            TestFolders.Write(folder, $"{name}.slnx", $"<Solution>\n  <Project Path=\"{name}/{name}.csproj\" />\n</Solution>\n");
            TestFolders.Write(folder, "scripts/build.sh", "echo build\n");
        }
        TestFolders.Write(loose, "notes.md", "# Notes\n");
    }

    public void Dispose()
    {
        TestFolders.Delete(shop);
        TestFolders.Delete(tool);
        TestFolders.Delete(loose);
        TestFolders.Delete(settings);
    }

    /// <summary>The files view chosen comes back as the folder opens again and after a restart, each folder in the view
    /// chosen last in it, and a folder an older Passenger kept in the solution view, as one flow, each step the check
    /// one test made in an instance of its own.</summary>
    [AvaloniaFact]
    public void Each_folder_opens_in_the_Explorer_view_chosen_last_in_it()
    {
        Flow.StepApart<ExplorerViewTests>("the files view chosen by the switch comes back as the folder opens again and after a restart", t => t.The_files_view_chosen_by_the_switch_comes_back_as_the_folder_opens_again_and_after_a_restart());
        Flow.StepApart<ExplorerViewTests>("each folder opens in the view chosen last in it and one never switched in the solution view", t => t.Each_folder_opens_in_the_view_chosen_last_in_it_and_one_never_switched_in_the_solution_view());
        Flow.StepApart<ExplorerViewTests>("a folders state an older passenger wrote without the view opens in the solution view", t => t.A_folders_state_an_older_passenger_wrote_without_the_view_opens_in_the_solution_view());
    }

    private void The_files_view_chosen_by_the_switch_comes_back_as_the_folder_opens_again_and_after_a_restart()
    {
        MainWindow window = Keeping(shop);
        Assert.Equal(ExplorerViewKind.Solution, Shown(window));
        Ui.Click(window.ExplorerView.ViewSwitchButton);
        Assert.Equal(ExplorerViewKind.Files, Shown(window));

        Assert.Null(window.CloseSolution());
        Ui.Settle();
        window.OpenFolder(shop);
        Ui.Settle();
        Assert.True(Shown(window) == ExplorerViewKind.Files, "Closed and opened again, the folder's Explorer went back to the Solution view the person switched away from");
        window.Close();

        MainWindow restarted = Keeping(shop);
        Assert.True(Shown(restarted) == ExplorerViewKind.Files, "After a restart the folder's Explorer opened in the Solution view, not the Files view the person left it in");
        restarted.Close();
    }

    private void Each_folder_opens_in_the_view_chosen_last_in_it_and_one_never_switched_in_the_solution_view()
    {
        MainWindow window = Keeping(shop);
        Ui.Click(window.ExplorerView.ViewSwitchButton);
        Assert.Equal(ExplorerViewKind.Files, Shown(window));

        window.OpenFolder(tool);
        Ui.Settle();
        Assert.True(Shown(window) == ExplorerViewKind.Solution, "A folder never switched opened in the Files view another folder was left in");
        window.OpenFolder(shop);
        Ui.Settle();
        Assert.True(Shown(window) == ExplorerViewKind.Files, "Opened again after another folder, the folder lost the Files view it was left in");

        Ui.Click(window.ExplorerView.ViewSwitchButton);
        window.OpenFolder(tool);
        Ui.Settle();
        Ui.Click(window.ExplorerView.ViewSwitchButton);
        window.OpenFolder(shop);
        Ui.Settle();
        Assert.True(Shown(window) == ExplorerViewKind.Solution, "Switched back to the Solution view, the folder opened again in the Files view");
        window.OpenFolder(tool);
        Ui.Settle();
        Assert.True(Shown(window) == ExplorerViewKind.Files, "The second folder lost the Files view it was left in");
        window.Close();
    }

    private void A_folders_state_an_older_passenger_wrote_without_the_view_opens_in_the_solution_view()
    {
        TestFolders.Write(ProjectState.FolderOf(settings, shop), "workspace.json", JsonSerializer.Serialize(new
        {
            version = 1,
            recentFiles = new[] { Path.Combine(shop, "Shop", "Program.cs") },
            unloadedProjects = Array.Empty<string>(),
            godotEditorOffered = false,
        }));
        MainWindow window = Keeping(shop);
        Assert.True(Shown(window) == ExplorerViewKind.Solution, "A folder whose state an older Passenger wrote, with no view in it, opened in the Files view");
        Ui.Click(window.ExplorerView.ViewSwitchButton);
        Reopen(window);
        Assert.True(Shown(window) == ExplorerViewKind.Files, "A folder whose state an older Passenger wrote did not keep the view switched to since");
        window.Close();
    }

    /// <summary>The view the Explorer shows, as its rows and its switch's words both say it, failing where they differ.</summary>
    private static ExplorerViewKind Shown(MainWindow window)
    {
        ExplorerViewKind shown = window.ExplorerView.ViewShown;
        string? words = window.ExplorerView.ViewSwitchButton.GetVisualDescendants().OfType<TextBlock>().Single().Text;
        Assert.True(words == shown.ToString(), $"The Explorer shows its {shown} view while its switch says {words}");
        return shown;
    }

    /// <summary>The folder open closed and opened again in the same window.</summary>
    private static void Reopen(MainWindow window)
    {
        string folder = window.Folder!;
        Assert.Null(window.CloseSolution());
        Ui.Settle();
        window.OpenFolder(folder);
        Ui.Settle();
    }

    /// <summary>A window keeping its state in the test's own folder, as a person's does, with a folder open in it.</summary>
    private MainWindow Keeping(string folder)
    {
        MainWindow window = new() { Width = 1000, Height = 700, StartsLanguageServer = false, StateFolder = settings };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        Ui.WaitFor(() => window.ExplorerView.Tree.Rows.Count > 0, "the Explorer's rows");
        return window;
    }
}
