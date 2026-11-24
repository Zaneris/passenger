using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Settings;

namespace Passenger.Tests;

/// <summary>A tool window serving one kind of project shows only while the open folder holds one: a plain console solution
/// shows none of them, on a stripe or in a place a layout opens them in, each greyed in View › Tool Windows saying why, as
/// the actions working in it are, its place in the layout kept; a Godot project come brings the Game window and gone takes it away; and every tool window
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
        [ToolWindowIds.Coverage] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Modules] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.DependencyMap] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Database] = ToolWindowServes.EveryFolder,
        [ToolWindowIds.Clones] = ToolWindowServes.EveryFolder,
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

    /// <summary>Each tool window declares what it serves, a console solution shows none it lacks and View greys each
    /// saying why, and a Godot project brings the Game window as it comes and takes it as it goes, as one flow, each
    /// step the check one test made in an instance of its own.</summary>
    [AvaloniaFact]
    public void Tool_windows_show_only_for_what_the_folder_holds_and_come_and_go_with_it()
    {
        Flow.StepApart<ToolWindowServesTests>("every tool window declares what it serves as the sweep decided", t => t.Every_tool_window_declares_what_it_serves_as_the_sweep_decided());
        Flow.StepApart<ToolWindowServesTests>("a plain console solution shows no window of a kind of project it lacks and View greys each saying why", t => t.A_plain_console_solution_shows_no_window_of_a_kind_of_project_it_lacks_and_View_greys_each_saying_why());
        Flow.StepApart<ToolWindowServesTests>("a Godot project come brings the Game window and gone takes it away", t => t.A_Godot_project_come_brings_the_Game_window_and_gone_takes_it_away());
        Flow.StepApart<ToolWindowServesTests>("Under titled tabs a console solution names no Game window and a Godot project come brings its tab", t => t.Under_titled_tabs_a_console_solution_names_no_Game_window_and_a_Godot_project_come_brings_its_tab());
        Flow.StepApart<ToolWindowServesTests>("a theme chosen over an arranged layout leaves a window serving nothing where it stood", t => t.A_theme_chosen_over_an_arranged_layout_leaves_a_window_serving_nothing_where_it_stood());
    }

    private void Every_tool_window_declares_what_it_serves_as_the_sweep_decided()
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

    private void A_plain_console_solution_shows_no_window_of_a_kind_of_project_it_lacks_and_View_greys_each_saying_why()
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
            // What works in such a window greys as the window does, saying the same: Find Action and an agent's ide_actions read it.
            IdeAction[] within = [.. window.Actions.All.Where(a => a.Group == "Aspire" || a.Id == AppActions.EndpointsRefresh)];
            Assert.True(within.Length > 10, $"Only {within.Length} actions work in the Aspire and Endpoints windows");
            foreach (IdeAction action in within)
            {
                string why = action.Group == "Aspire" ? Refusals.NoAppHost : Refusals.NoWebRequests;
                Assert.True(window.Actions.WhyNot(action.Id, window.Actions.Context()) == why,
                    $"{action.Title} says \"{window.Actions.WhyNot(action.Id, window.Actions.Context())}\" in a plain console solution, not \"{why}\" as its window does");
            }
            Assert.True(StripeButton(window, ToolWindowIds.Run) is not null, "Run, every folder's, left its stripe");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A Godot project comes as a person brings one: its folder moved in whole (as a copy or a clone lands, which the
    /// system may tell as the folder alone), or its project.godot written; and goes as its file or its whole folder is
    /// deleted.</summary>
    private void A_Godot_project_come_brings_the_Game_window_and_gone_takes_it_away()
    {
        string folder = ConsoleSolution();
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            DockHost dock = window.DockHost;
            Assert.True(StripeButton(window, ToolWindowIds.Game) is null, "The Game window's icon is on a stripe before any Godot project came");
            string made = TestFolders.Create("godot-game-");
            string project = TestFolders.Write(made, "project.godot", ProjectGodot);
            TestFolders.Write(made, "main.gd", "extends Node\n");
            string game = Path.Combine(folder, "game");
            Directory.Move(made, game);
            project = Path.Combine(game, Path.GetFileName(project));
            Came(window, "its folder moved in whole");
            NativeMenu tools = Ui.Submenu(Ui.Submenu(window.MenuBar, "View"), MainMenu.ToolWindows);
            Assert.True(tools.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Game").IsEnabled, "View › Tool Windows greys Game with a Godot project in the folder");
            dock.Show(ToolWindowIds.Game);
            Ui.Settle();
            Assert.True(dock.IsShown(ToolWindowIds.Game), "The Game window did not show with a Godot project in the folder");

            File.Delete(project);
            Went(window, "its project.godot deleted");
            Assert.True(dock.Capture().Windows[ToolWindowIds.Game].Shown, "The Game window's place, open as its project went, was not kept");

            File.WriteAllText(project, ProjectGodot);
            Came(window, "its project.godot written");
            Assert.True(dock.IsShown(ToolWindowIds.Game), "The Game window, open as its project went, did not come back open where it stood");

            Directory.Delete(game, recursive: true);
            Went(window, "its whole folder deleted");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Under titled tabs, as Cubicle names the tool windows, a console solution names no window of a kind of project
    /// it lacks by a tab along an edge or at a place's foot, and a Godot project come brings the Game window's tab.</summary>
    private void Under_titled_tabs_a_console_solution_names_no_Game_window_and_a_Godot_project_come_brings_its_tab()
    {
        string folder = ConsoleSolution();
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ApplySettings(window.Settings with { Look = SourcedTheme.BuiltIn("Cubicle") });
            Ui.Settle();
            Assert.True(window.DockHost.TitledTabs && StripeButton(window, ToolWindowIds.Run) is null, "Cubicle did not name the tool windows by titled tabs");
            Assert.True(Tab(window, ToolWindowIds.Run) is not null, "Run, every folder's, has no tab under titled tabs");
            string[] tabbed = [.. OfAKind.Select(k => k.Id).Where(id => Tab(window, id) is not null)];
            Assert.True(tabbed.Length == 0, $"A plain console solution names {string.Join(", ", tabbed)} by a tab under titled tabs");

            TestFolders.Write(folder, "game/project.godot", ProjectGodot);
            Ui.WaitFor(() => Tab(window, ToolWindowIds.Game) is not null,
                () => $"the Game window's tab once a Godot project came, where it says {window.DockHost[ToolWindowIds.Game].Unserved}");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>A theme chosen over a layout the person arranged moves, window by window, only those serving the folder: one
    /// serving nothing that the theme places elsewhere keeps its place for when it serves.</summary>
    private void A_theme_chosen_over_an_arranged_layout_leaves_a_window_serving_nothing_where_it_stood()
    {
        string folder = ConsoleSolution();
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            DockHost dock = window.DockHost;
            dock.Move(dock[ToolWindowIds.Terminal], ToolAnchor.RightBottom);
            Ui.Settle();
            ToolAnchor game = dock[ToolWindowIds.Game].Anchor;
            string name = $"Placing {Guid.NewGuid():N}";
            string json = $$$"""{"format": 1, "name": "{{{name}}}", "layout": {"places": {"leftBottom": ["game", "find"]}} }""";
            Assert.Null(KeptThemes.Keep(ThemeSource.Added, false, name, System.Text.Encoding.UTF8.GetBytes(json)));
            window.ApplySettings(window.Settings with { Look = new SourcedTheme(ThemeSource.Added.Name, name) });
            Ui.Settle();
            Assert.True(dock[ToolWindowIds.Find].Anchor == ToolAnchor.LeftBottom && dock[ToolWindowIds.Terminal].Anchor == ToolAnchor.RightBottom,
                $"The theme left Find in {dock[ToolWindowIds.Find].Anchor}, where it places it at the left, or took the Terminal the person moved to {dock[ToolWindowIds.Terminal].Anchor}");
            Assert.True(dock[ToolWindowIds.Game].Anchor == game, $"The Game window, serving nothing here, went to {dock[ToolWindowIds.Game].Anchor} as the theme places it, from {game}");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>A window's tab under titled tabs, along an edge or at a place's foot, as a person sees it.</summary>
    private static Border? Tab(MainWindow window, string id) =>
        window.DockHost.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains(StyleClasses.Tab) && Equals(b.Tag, id) && b.IsEffectivelyVisible);

    /// <summary>A Godot project's file, of a game of GDScript alone.</summary>
    private const string ProjectGodot = "config_version=5\n\n[application]\n\nconfig/name=\"Hop\"\n";

    private static void Came(MainWindow window, string how) =>
        Ui.WaitFor(() => StripeButton(window, ToolWindowIds.Game) is not null,
            () => $"the Game window's icon once a Godot project came, {how}, where it says {window.DockHost[ToolWindowIds.Game].Unserved}, the folder's games are "
                + $"[{string.Join(", ", window.WorkspaceModel?.GodotGames ?? [])}] and its index lists [{string.Join(", ", window.FileIndex.Files)}]");

    private static void Went(MainWindow window, string how)
    {
        Ui.WaitFor(() => StripeButton(window, ToolWindowIds.Game) is null, () => $"the Game window's icon gone with the Godot project, {how}");
        Assert.False(window.DockHost.IsShown(ToolWindowIds.Game), $"The Game window still shows with no Godot project in the folder, {how}");
        Assert.Equal(Refusals.NoGodotProject, window.DockHost[ToolWindowIds.Game].Unserved);
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

    /// <summary>A window's stripe icon as a person sees it.</summary>
    private static ToggleButton? StripeButton(MainWindow window, string id) =>
        window.DockHost.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(b => Equals(b.Tag, id) && b.IsEffectivelyVisible);
}
