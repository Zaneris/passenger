using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;

namespace Passenger.Tests;

/// <summary>A tool window serving one kind of project shows only while the open folder holds one: a plain console solution
/// shows none of them, on a stripe or in a place a layout opens them in, each greyed in View › Tool Windows saying why, its
/// place in the layout kept; a Godot project come brings the Game window and gone takes it away; and every tool window
/// declares what it serves.</summary>
public sealed class ToolWindowServesTests
{
    /// <summary>What each tool window serves, as the sweep decided it: a window of one kind of project, or every folder's.</summary>
    private static readonly Dictionary<string, ToolWindowServes> Sweep = new(StringComparer.Ordinal)
    {
        [ToolWindowIds.Explorer] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Structure] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Bookmarks] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Commit] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Run] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Debug] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Tests] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Build] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Problems] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Find] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Git] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.NuGet] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Terminal] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.LocalHistory] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Containers] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Profiler] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Hierarchy] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Endpoints] = ToolWindowServes.WebRequests,
        [ToolWindowIds.Requests] = ToolWindowServes.WebApps,
        [ToolWindowIds.LiveTree] = ToolWindowServes.InspectedApps,
        [ToolWindowIds.Game] = ToolWindowServes.GodotProjects,
        [ToolWindowIds.Aspire] = ToolWindowServes.AppHosts,
        [ToolWindowIds.Migrations] = ToolWindowServes.EfCore,
        [ToolWindowIds.StepLibrary] = ToolWindowServes.StepDefinitions,
    };

    /// <summary>The windows of one kind of project, each with why a plain console solution has none of it.</summary>
    private static readonly (string Id, string Why)[] OfAKind =
    [
        (ToolWindowIds.Game, Refusals.NoGodotProject),
        (ToolWindowIds.Aspire, Refusals.NoAppHost),
        (ToolWindowIds.StepLibrary, Refusals.NoStepDefinitions),
        (ToolWindowIds.Endpoints, Refusals.NoWebRequests),
        (ToolWindowIds.Requests, Refusals.NoWebApp),
        (ToolWindowIds.Migrations, Refusals.NoEfCore),
        (ToolWindowIds.LiveTree, Refusals.NoInspectedApp),
    ];

    [AvaloniaFact]
    public void Every_tool_window_declares_what_it_serves_as_the_sweep_decided()
    {
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        Ui.Settle();
        try
        {
            Assert.Equal(ToolWindowIds.All.Order(StringComparer.Ordinal), window.DockHost.Windows.Select(w => w.Id).Order(StringComparer.Ordinal));
            Assert.Equal(ToolWindowIds.All.Order(StringComparer.Ordinal), Sweep.Keys.Order(StringComparer.Ordinal));
            foreach (ToolWindow each in window.DockHost.Windows)
            {
                Assert.True(ReferenceEquals(Sweep[each.Id], each.Serves),
                    $"The {each.Title} window serves {each.Serves.Kinds}, where the sweep decided it serves {Sweep[each.Id].Kinds}: decide it here");
            }
            // A window of a kind of project says why in Refusals' words wherever it greys.
            HashSet<string> words = [.. typeof(Refusals).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!)];
            foreach (ToolWindowServes rule in Sweep.Values.Distinct().Where(r => r != ToolWindowServes.EveryFolder))
            {
                Assert.True(rule.WhyNot(FolderHolds.None) is { } why && words.Contains(why), $"A window serving {rule.Kinds} greys without Refusals' words");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_plain_console_solution_shows_no_window_of_a_kind_of_project_it_lacks_and_View_greys_each_saying_why()
    {
        string folder = ConsoleSolution();
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            DockHost dock = window.DockHost;
            Ui.WaitFor(() => window.Projects is [{ IsEvaluated: true }], () => $"the console project evaluated, where {string.Join(", ", window.Projects.Select(p => $"{p.Name} {p.Problem}"))}");
            // A layout opening one in each of their places (a theme's, or one kept from a folder that had them) shows none.
            string[] opened = [ToolWindowIds.Game, ToolWindowIds.Aspire, ToolWindowIds.Requests];
            DockLayout opening = dock.Capture();
            foreach (string id in opened)
            {
                opening.Windows[id] = opening.Windows[id] with { Shown = true };
            }
            dock.Apply(opening);
            Ui.Settle();
            NativeMenu tools = Ui.Submenu(Ui.Submenu(window.MenuBar, "View"), MainMenu.ToolWindows);
            foreach ((string id, string why) in OfAKind)
            {
                ToolWindow shown = dock[id];
                Assert.True(StripeButton(window, id) is null, $"{shown.Title}'s icon is on a stripe in a plain console solution");
                Assert.False(dock.IsShown(id), $"{shown.Title} shows in a plain console solution");
                Assert.False(shown.IsBuilt && dock.GetVisualDescendants().Contains(shown.Content), $"{shown.Title}'s view is in its place in a plain console solution");
                NativeMenuItem item = tools.Items.OfType<NativeMenuItem>().Single(i => i.Header == shown.Title);
                Assert.False(item.IsEnabled, $"View › Tool Windows offers {shown.Title} in a plain console solution");
                Assert.Equal(why, item.ToolTip);
                Assert.Equal(why, window.Actions.WhyNot(shown.Action!, window.Actions.Context()));
                Assert.True(!opened.Contains(id) || dock.Capture().Windows[id].Shown, $"{shown.Title}'s place, open in the layout, was not kept for when it serves");
            }
            Assert.All(dock.Windows.Where(w => !OfAKind.Any(k => k.Id == w.Id)), w =>
                Assert.True(tools.Items.OfType<NativeMenuItem>().Single(i => i.Header == w.Title).IsEnabled, $"View › Tool Windows greys {w.Title} in a plain console solution"));
            Assert.True(StripeButton(window, ToolWindowIds.Run) is not null, "Run, every folder's, left its stripe");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_Godot_project_come_brings_the_Game_window_and_gone_takes_it_away()
    {
        string folder = ConsoleSolution();
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            DockHost dock = window.DockHost;
            Assert.True(StripeButton(window, ToolWindowIds.Game) is null, "The Game window's icon is on a stripe before any Godot project came");
            string game = TestFolders.Write(folder, "game/project.godot", "config_version=5\n\n[application]\n\nconfig/name=\"Hop\"\n");
            Ui.WaitFor(() => StripeButton(window, ToolWindowIds.Game) is not null, () => $"the Game window's icon once a Godot project came, where it says {dock[ToolWindowIds.Game].Unserved}");
            NativeMenu tools = Ui.Submenu(Ui.Submenu(window.MenuBar, "View"), MainMenu.ToolWindows);
            Assert.True(tools.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Game").IsEnabled, "View › Tool Windows greys Game with a Godot project in the folder");
            dock.Show(ToolWindowIds.Game);
            Ui.Settle();
            Assert.True(dock.IsShown(ToolWindowIds.Game), "The Game window did not show with a Godot project in the folder");

            File.Delete(game);
            Ui.WaitFor(() => StripeButton(window, ToolWindowIds.Game) is null, "the Game window's icon gone with the Godot project");
            Assert.False(dock.IsShown(ToolWindowIds.Game), "The Game window still shows with no Godot project in the folder");
            Assert.Equal(Refusals.NoGodotProject, dock[ToolWindowIds.Game].Unserved);
            Assert.True(dock.Capture().Windows[ToolWindowIds.Game].Shown, "The Game window's place, open as its project went, was not kept");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A solution of one console app, which no tool window of a kind of project serves.</summary>
    private static string ConsoleSolution()
    {
        string folder = TestFolders.Create("plain-console-");
        TestFolders.Write(folder, "Hello/Hello.csproj", TestProjects.Console());
        TestFolders.Write(folder, "Hello/Program.cs", "System.Console.WriteLine(\"Hello\");\n");
        TestFolders.Write(folder, "Hello.slnx", "<Solution>\n  <Project Path=\"Hello/Hello.csproj\" />\n</Solution>\n");
        return folder;
    }

    private static ToggleButton? StripeButton(MainWindow window, string id) =>
        window.DockHost.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(b => Equals(b.Tag, id));
}
