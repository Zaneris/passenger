using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Godot game's configuration that starts several copies of it side by side, a server and its clients, each
/// with its own arguments: Debug starts each in a tab of its own with its own console, each a source in the Live Visual
/// Tree's chooser; a breakpoint only the server reaches stops it alone; Debug Another Copy starts one more beside them;
/// Stop ends them all; one copy restarts alone. Run starts each in a tab of the Run window of its own, Godot's errors folded
/// in each, restarted and stopped alone, and Run Another Copy shows a game in the Run console as the first copy's tab. The
/// game is GDScript alone, run headless with the run's user folders; without a Godot 4 each test skips saying why. It debugs
/// through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotCopiesTests : IClassFixture<GodotCopiesTests.Game>
{
    private const string Name = "Lobby on Godot";

    private readonly Game game;

    public GodotCopiesTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot?.WhyNotScripts is not null || game.Godot is null, game.Skipped);
    }

    /// <summary>Copies of a game run and debug each in its own tab and console, restarted alone, and Stop ends them
    /// all.</summary>
    [AvaloniaFact]
    public void Copies_of_a_game_run_and_debug_each_on_its_own()
    {
        Flow.Step(nameof(Three_copies_debug_each_in_its_own_tab_and_console_one_stops_alone_another_comes_beside_and_Stop_ends_them_all),
            Three_copies_debug_each_in_its_own_tab_and_console_one_stops_alone_another_comes_beside_and_Stop_ends_them_all);
        OpenWindows.CloseAll();
        Flow.Step(nameof(Restarting_one_debugged_copy_relaunches_it_alone_the_other_debugged_on),
            Restarting_one_debugged_copy_relaunches_it_alone_the_other_debugged_on);
        OpenWindows.CloseAll();
        Flow.Step(nameof(Two_copies_run_each_in_a_Run_window_tab_their_errors_folded_each_restarted_and_stopped_alone),
            Two_copies_run_each_in_a_Run_window_tab_their_errors_folded_each_restarted_and_stopped_alone);
        OpenWindows.CloseAll();
        Flow.Step(nameof(Another_copy_beside_a_game_in_the_Run_console_shows_that_game_as_the_first_tab_whose_Rerun_restarts_it_there),
            Another_copy_beside_a_game_in_the_Run_console_shows_that_game_as_the_first_tab_whose_Rerun_restarts_it_there);
    }

    /// <summary>Run of two copies runs each in a tab of the Run window of its own, never the Terminal window, its console its
    /// copy's lines alone with Godot's errors folded (the server's error, said every tenth frame, shown once and counted); the
    /// client's Rerun restarts it alone in its tab, the server running on in the same process, its Stop ends it alone, and
    /// Stop ends the server too. Set to run in the terminal, the configuration runs its copies each in a Terminal tab.</summary>
    private void Two_copies_run_each_in_a_Run_window_tab_their_errors_folded_each_restarted_and_stopped_alone()
    {
        MainWindow window = Open();
        try
        {
            Choose(window, 2, arguments: ["-- server noisy", "-- client"]);
            Ui.Launched(window, window.RunAsync(), "the two copies' start", TimeSpan.FromMinutes(2));
            string client = $"{Name} #2";
            Ui.WaitFor(() => window.RunParts.Select(p => p.Name).SequenceEqual([Name, client])
                    && Text(window.RunParts[0]).Contains("role server", StringComparison.Ordinal) && Text(window.RunParts[1]).Contains("role client", StringComparison.Ordinal),
                () => $"each copy in a tab of the Run window saying its own role ({Runs(window)}; {window.Status})", TimeSpan.FromMinutes(1));
            PartRun server = window.RunParts[0];
            PartRun other = window.RunParts[1];
            Assert.True(!Text(server).Contains("role client", StringComparison.Ordinal) && !Text(other).Contains("role server", StringComparison.Ordinal),
                $"A copy's tab holds another's lines: {Runs(window)}");
            Assert.True(window.RunInTerminal is null && window.Session.Games.Count == 2, $"The copies did not run as two games of the Run window: {window.Session.Games.Count} games");
            Ui.WaitFor(() => server.Console.Fold?.Entries is [{ Count: > 2 } entry] && entry.Header.StartsWith("ERROR: lobby trouble", StringComparison.Ordinal),
                () => $"the server's error, said every tenth frame, folded once with its count ({string.Join(", ", server.Console.Fold?.Entries.Select(e => $"{e.Header} x{e.Count}") ?? [])})", TimeSpan.FromMinutes(1));
            Assert.True(other.Console.Fold is { Entries.Count: 0 }, "The client's tab folds an error only the server says");

            int? serverProcess = server.ProcessId;
            int? clientProcess = other.ProcessId;
            Ui.Click(window.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Name == "runPart2Tab"));
            Ui.Click(Button(other.Console, "Rerun"));
            Ui.WaitFor(() => other.IsRunning && other.ProcessId != clientProcess && Text(other).Contains("role client", StringComparison.Ordinal),
                () => $"the client's Rerun to start it again in its tab ({Runs(window)}; {window.Status})", TimeSpan.FromMinutes(2));
            Assert.True(server.IsRunning && server.ProcessId == serverProcess && window.RunParts.Count == 2, $"The client's restart ended the server, which was to run on: {Runs(window)}");

            Ui.Click(Button(other.Console, "Stop"));
            Ui.WaitFor(() => !other.IsRunning && window.Session.Games.Count == 1, () => $"the client's Stop to end it alone ({Runs(window)})", TimeSpan.FromMinutes(1));
            Assert.True(server.IsRunning, $"The client's Stop ended the server: {Runs(window)}");
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.RunParts.All(p => !p.IsRunning) && !window.IsRunning && window.Session.Games.Count == 0,
                () => $"Stop to end the server too ({Runs(window)})", TimeSpan.FromMinutes(1));

            // The person's choice to run it in the terminal keeps its copies there, each a tab of the Terminal window.
            Assert.True(window.Session.Configurations.UseConsole(Name, ConsoleKind.Terminal), "The configuration could not be set to run in the terminal");
            Ui.Launched(window, window.RunAsync(), "the two copies' start in the terminal", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.TerminalPanel.Tabs.Count(t => (t.Name == Name || t.Name == client) && !t.HasEnded) == 2 && window.Session.Games.Count == 2,
                () => $"each copy in a tab of the Terminal window ({string.Join(", ", window.TerminalPanel.Tabs.Select(t => $"{t.Name} {(t.HasEnded ? "ended" : "running")}"))}; {window.Status})",
                TimeSpan.FromMinutes(1));
            Assert.True(window.RunParts.All(p => !p.IsRunning), $"A copy run in the terminal also has a tab of the Run window: {Runs(window)}");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Run Another Copy beside a game Run started in the Run console: that game becomes the first copy's tab, over the
    /// Run console, and the second copy a tab of its own; the console's Rerun restarts the first in place, in the console, the
    /// second running on in the same process.</summary>
    private void Another_copy_beside_a_game_in_the_Run_console_shows_that_game_as_the_first_tab_whose_Rerun_restarts_it_there()
    {
        MainWindow window = Open();
        try
        {
            Choose(window, 1);
            Ui.Launched(window, window.RunAsync(), "the game's start in the Run console", TimeSpan.FromMinutes(2));
            // One game alone takes no copy's arguments: a client.
            Ui.WaitFor(() => window.BottomPanel.Run.Document.Snapshot.ToString().Contains("role client", StringComparison.Ordinal),
                () => $"the game in the Run console to say its role ({window.Status})", TimeSpan.FromMinutes(1));
            Assert.Null(window.Actions.Run(AppActions.RunAnotherCopy)?.Shown);
            string client = $"{Name} #2";
            Ui.WaitFor(() => window.RunParts.Select(p => p.Name).SequenceEqual([Name, client]) && Text(window.RunParts[1]).Contains("role client", StringComparison.Ordinal),
                () => $"the game in the Run console and another copy beside it, each a tab ({Runs(window)}; {window.Status})", TimeSpan.FromMinutes(1));
            PartRun first = window.RunParts[0];
            PartRun second = window.RunParts[1];
            Assert.True(first.Console == window.BottomPanel.Run && first.IsRunning, $"The game in the Run console is not the first copy's tab over it: {Runs(window)}");

            int? firstProcess = first.ProcessId;
            int? secondProcess = second.ProcessId;
            Ui.Click(window.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Name == "runPart1Tab"));
            Ui.Click(window.BottomPanel.Run.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) is string tip && tip.StartsWith("Rerun", StringComparison.Ordinal) && b.IsEffectivelyVisible));
            Ui.WaitFor(() => first.IsRunning && first.ProcessId != firstProcess && window.BottomPanel.Run.Document.Snapshot.ToString().Contains("role client", StringComparison.Ordinal)
                    && window.Actions.Work.Of(AppActions.RunProgram) is null,
                () => $"the first copy's Rerun to start it again in the Run console ({Runs(window)}; {window.Status})", TimeSpan.FromMinutes(2));
            Assert.True(window.RunParts.Count == 2 && window.RunParts[0] == first && first.Console == window.BottomPanel.Run && second.IsRunning && second.ProcessId == secondProcess,
                $"The first copy's Rerun did not restart it alone in the Run console, the second running on: {Runs(window)}");
        }
        finally
        {
            End(window);
        }
    }

    private static Button Button(OutputView console, string tip) =>
        console.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) is string said && said == tip && b.IsEffectivelyVisible);

    private static string Text(PartRun part) => part.Runner.Shown().ToString();

    private static string Runs(MainWindow window) => string.Join(", ", window.RunParts.Select(r => $"{r.Name} ({r.Said}): {string.Join(" / ", Text(r).Split('\n').Where(l => l.Length > 0).TakeLast(2))}"));

    private void Three_copies_debug_each_in_its_own_tab_and_console_one_stops_alone_another_comes_beside_and_Stop_ends_them_all()
    {
        MainWindow window = Open();
        try
        {
            Choose(window, 3);
            window.OpenFile(Path.Combine(game.Folder, "main.gd"));
            CodeEditor editor = window.EditorArea.CodeEditor;
            int served = Array.FindIndex(editor.Document.Snapshot.ToString().Split('\n'), l => l.Contains("served += 1", StringComparison.Ordinal));
            editor.GoTo(new TextPosition(served, 1));
            Ui.Run(window, AppActions.ToggleBreakpoint);

            Ui.Launched(window, window.DebugAsync(), "the three copies' start under the debugger", TimeSpan.FromMinutes(2));
            string[] copies = [Name, $"{Name} #2", $"{Name} #3"];
            LiveTreeView tree = window.LiveTree;
            Ui.WaitFor(() => window.Session.Games.Count == 3 && tree.Sources.IsVisible && copies.All(c => tree.SourceNames.Any(s => s.StartsWith(c, StringComparison.Ordinal))),
                () => $"each copy in the Live Visual Tree's chooser ({string.Join(", ", tree.SourceNames)}; {Sessions(window)}; {window.Status})", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => Console(window, Name).Contains("role server", StringComparison.Ordinal)
                    && copies.Skip(1).All(c => Console(window, c).Contains("role client", StringComparison.Ordinal)),
                () => $"each copy's console to say its own role ({string.Join(" | ", copies.Select(c => $"{c}: {Tail(Console(window, c))}"))})", TimeSpan.FromMinutes(1));
            Assert.True(copies.Skip(1).All(c => !Console(window, c).Contains("role server", StringComparison.Ordinal)) && !Console(window, Name).Contains("role client", StringComparison.Ordinal),
                $"A copy's console holds another's lines: {string.Join(" | ", copies.Select(c => $"{c}: {Tail(Console(window, c))}"))}");

            Ui.WaitFor(() => Tab(window, Name)?.Session?.State == DebugState.Paused, () => $"the server to stop at its breakpoint ({Sessions(window)})", TimeSpan.FromMinutes(1));
            Assert.True(copies.Skip(1).All(c => Tab(window, c)?.Session?.State == DebugState.Running), $"A client stopped with the server: {Sessions(window)}");

            Assert.Null(window.Actions.Run(AppActions.DebugAnotherCopy)?.Shown);
            Ui.WaitFor(() => window.Session.Games.Count == 4 && Console(window, $"{Name} #4").Contains("role client", StringComparison.Ordinal),
                () => $"a fourth copy beside them, a client ({Sessions(window)}; {window.Status})", TimeSpan.FromMinutes(1));

            Ui.AwaitPumped(window.Debugger.ChooseAsync(Tab(window, $"{Name} #3")!), "the third copy's tab chosen");
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Session.Games.Count == 0 && !window.Debugger.Tabs.Any(t => t.IsLive),
                () => $"Stop on one copy to end every copy ({Sessions(window)}; {window.Session.Games.Count} games)", TimeSpan.FromMinutes(1));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Hot Reload's restart of one copy debugged beside another restarts it alone: its session stopped and the copy
    /// launched again in its place, its tab live with its own role again, the other's session debugged on as it was.</summary>
    private void Restarting_one_debugged_copy_relaunches_it_alone_the_other_debugged_on()
    {
        MainWindow window = Open();
        try
        {
            Choose(window, 2);
            Ui.Launched(window, window.DebugAsync(), "the two copies' start under the debugger", TimeSpan.FromMinutes(2));
            string client = $"{Name} #2";
            Ui.WaitFor(() => Tab(window, Name)?.Session?.State == DebugState.Running && Tab(window, client)?.Session?.State == DebugState.Running
                    && Console(window, client).Contains("role client", StringComparison.Ordinal),
                () => $"both copies debugged, the client saying its role ({Sessions(window)}; {Tail(Console(window, client))})", TimeSpan.FromMinutes(1));
            DebugSession server = Tab(window, Name)!.Session!;
            DebugSession before = Tab(window, client)!.Session!;

            Assert.Null(Ui.AwaitPumped(window.Builds.RestartDebugAsync(client), "the client copy's restart", TimeSpan.FromMinutes(2)));
            Ui.WaitFor(() => Tab(window, client)?.Session is { State: DebugState.Running } again && again != before && Console(window, client).Contains("role client", StringComparison.Ordinal),
                () => $"the client copy debugged again, saying its role ({Sessions(window)}; {Tail(Console(window, client))})", TimeSpan.FromMinutes(1));
            Assert.True(Tab(window, Name)?.Session == server && server.State == DebugState.Running, $"The server copy did not debug on as the client restarted: {Sessions(window)}");
            Assert.Equal(2, window.Session.Games.Count);
        }
        finally
        {
            End(window);
        }
    }

    private MainWindow Open()
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, GodotEnvironment = game.Homes };
        window.Show();
        window.OpenFolder(game.Folder);
        Ui.Settle();
        return window;
    }

    /// <summary>The game's configuration, run headless with the run's user folders, starting <paramref name="copies"/> copies: the
    /// first a server, the second a client, the rest clients too, given no arguments of their own; or each with its line of
    /// <paramref name="arguments"/>.</summary>
    private void Choose(MainWindow window, int copies, string name = Name, string[]? arguments = null)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Generated && c.Name == "Lobby"),
            () => $"the game's configuration ({string.Join(", ", window.Configurations.Select(c => c.Name))})", TimeSpan.FromMinutes(1));
        RunConfiguration generated = window.Configurations.Single(c => c.Generated && c.Name == "Lobby");
        window.SetConfigurations([generated with
        {
            Name = name, Generated = false, Arguments = "--headless", Environment = new(game.Homes), GodotCopies = copies, CopyArguments = [.. arguments ?? ["-- server", "-- client"]],
        }], name);
    }

    private static void End(MainWindow window)
    {
        if (window.Debugger.IsDebugging || window.IsRunning)
        {
            Assert.Null(window.Actions.Run(AppActions.StopAll)?.Shown);
            Ui.WaitFor(() => !window.Debugger.IsDebugging && !window.IsRunning, () => $"the copies to end ({Sessions(window)})", TimeSpan.FromMinutes(1));
        }
        window.Close();
    }

    private static DebugTab? Tab(MainWindow window, string name) => window.Debugger.Tabs.FirstOrDefault(t => t.Name == name && t.IsLive);

    private static string Console(MainWindow window, string name) =>
        Tab(window, name)?.Session is { } session ? window.Debugger.Output(session).ToString() : string.Empty;

    private static string Tail(string text) => string.Join(" / ", text.Split('\n').TakeLast(3));

    private static string Sessions(MainWindow window) =>
        string.Join(", ", window.Debugger.Tabs.Select(t => $"{t.Name}: {t.Session?.State.ToString() ?? "none"}"));

    /// <summary>A Godot 4 project of GDScript alone named Lobby at 20 frames a second: its Main node is the server where its
    /// user arguments say server, else a client, says so every tenth frame (with an error too where they say noisy), and
    /// counts frames served past the fortieth.</summary>
    public sealed class Game
    {
        public Game()
        {
            Godot = GodotExecutables.Find(null);
            Folder = PathNames.Canonical(TestFolders.Create("godot-lobby-"));
            Homes = GodotTests.Game.Home(Path.Combine(Folder, "..", Path.GetFileName(Folder) + "-home"));
            File.WriteAllText(Path.Combine(Folder, "project.godot"), "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Lobby\"\n"
                + "run/main_scene=\"res://main.tscn\"\nconfig/features=PackedStringArray(\"4.7\")\nrun/max_fps=20\n");
            File.WriteAllText(Path.Combine(Folder, "main.gd"), """
                extends Node

                var role := "server" if OS.get_cmdline_user_args().has("server") else "client"
                var frames := 0
                var served := 0


                func _process(_delta: float) -> void:
                	frames += 1
                	if frames % 10 == 0:
                		print("role ", role, " frame ", frames)
                		if OS.get_cmdline_user_args().has("noisy"):
                			push_error("lobby trouble")
                	if role == "server" and frames > 40:
                		served += 1

                """);
            File.WriteAllText(Path.Combine(Folder, "main.tscn"), "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://main.gd\" id=\"1\"]\n\n"
                + "[node name=\"Main\" type=\"Node\"]\nscript = ExtResource(\"1\")\n");
        }

        public string Folder { get; }

        public GodotFound? Godot { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4 to run these tests"
            : $"The Godot found cannot run a Godot 4 project: {Godot.WhyNotScripts}";

        public IReadOnlyDictionary<string, string> Homes { get; }
    }
}
