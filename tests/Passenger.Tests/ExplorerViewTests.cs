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

    [AvaloniaFact]
    public void The_files_view_chosen_by_the_switch_comes_back_as_the_folder_opens_again_and_after_a_restart()
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

    [AvaloniaFact]
    public void Each_folder_opens_in_the_view_chosen_last_in_it_and_one_never_switched_in_the_solution_view()
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

    /// <summary>Also: an agent's switch shows, but is not kept as the person's choice; a folder with no solution or
    /// project, whose Explorer has only its files, says why it cannot switch.</summary>
    [AvaloniaFact]
    public void Switch_explorer_view_runs_from_a_persons_own_key_and_find_action_each_switch_kept_as_the_folders_view()
    {
        MainWindow window = Keeping(shop);
        string keys = OperatingSystem.IsMacOS() ? "meta alt shift K" : "control alt shift K";
        window.ApplySettings(window.Settings with { Shortcuts = new() { [AppActions.SwitchExplorerView] = [keys] } });
        try
        {
            Shortcut bound = Assert.Single(window.Actions.Keymap.ShortcutsFor(AppActions.SwitchExplorerView));
            Assert.True(ToolTip.GetTip(window.ExplorerView.ViewSwitchButton) is string tip && tip.Contains(bound.Display(), StringComparison.Ordinal),
                $"The view switch's tip does not name the key the person gave Switch Explorer View, {bound.Display()}: '{ToolTip.GetTip(window.ExplorerView.ViewSwitchButton)}'");
            Ui.Press(window, bound.First.Notation());
            Assert.Equal(ExplorerViewKind.Files, Shown(window));
            Reopen(window);
            Assert.True(Shown(window) == ExplorerViewKind.Files, "The Files view switched to by the person's key was not kept with the folder");

            Assert.True(window.Perform(AppActions.FindAction));
            Ui.Settle();
            ActionPopup popup = Assert.IsType<ActionPopup>(window.Popup);
            popup.Query = "Solution View";
            Ui.Settle();
            int row = popup.Rows.ToList().FindIndex(a => a.Id == AppActions.SwitchExplorerView);
            Assert.True(row >= 0, $"Find Action does not list Switch Explorer View for 'Solution View': [{string.Join(", ", popup.Rows.Select(a => a.Title))}]");
            for (int down = 0; down < row; down++)
            {
                Ui.Press(window, "DOWN");
            }
            Ui.Press(window, "ENTER");
            Assert.Equal(ExplorerViewKind.Solution, Shown(window));
            Reopen(window);
            Assert.True(Shown(window) == ExplorerViewKind.Solution, "The Solution view switched to from Find Action was not kept with the folder");

            Assert.Null(window.Actions.RunForAgent(AppActions.SwitchExplorerView));
            Ui.Settle();
            Assert.Equal(ExplorerViewKind.Files, Shown(window));
            Reopen(window);
            Assert.True(Shown(window) == ExplorerViewKind.Solution, "An agent's switch to the Files view was kept as the view the person opens the folder in");

            window.OpenFolder(loose);
            Ui.Settle();
            Assert.Equal(Refusals.NoSolutionView, window.Actions.WhyNot(AppActions.SwitchExplorerView, window.Actions.Context()));
            Ui.Press(window, bound.First.Notation());
            Assert.True(window.Status == Refusals.NoSolutionView && window.ExplorerView.ViewShown == ExplorerViewKind.Files && !window.ExplorerView.ViewSwitchButton.IsVisible,
                $"In a folder of files alone, Switch Explorer View said '{window.Status}', the Explorer showing its {window.ExplorerView.ViewShown} view");
        }
        finally
        {
            // The keymap the views without a window read is the process's; the next test starts on the platform's own.
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_folders_state_an_older_passenger_wrote_without_the_view_opens_in_the_solution_view()
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
