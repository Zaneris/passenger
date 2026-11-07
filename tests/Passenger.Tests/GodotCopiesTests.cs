using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Godot game's configuration that starts several copies of it side by side, a server and its clients, each with
/// its own arguments: Debug starts each in a tab of its own with its own console, each a source in the Live Visual Tree's
/// chooser; a breakpoint only the server reaches stops it alone; Debug Another Copy starts one more beside them; Run runs
/// each in a tab of the Terminal window, and Run Another Copy one more; Stop ends them all; one copy restarts alone. The game is GDScript alone, run headless with the run's user
/// folders; without a Godot 4 each test skips saying why. It debugs through the window, so run alone.</summary>
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

    [AvaloniaFact]
    public void Three_copies_debug_each_in_its_own_tab_and_console_one_stops_alone_another_comes_beside_and_Stop_ends_them_all()
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

            Assert.Null(window.Actions.Run(AppActions.DebugAnotherCopy));
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
    [AvaloniaFact]
    public void Restarting_one_debugged_copy_relaunches_it_alone_the_other_debugged_on()
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

    /// <summary>Run of a configuration of two copies runs each in a tab of the Terminal window of its own, named for its
    /// place, each printing its own role there and a source in the Live Visual Tree's chooser; Run Another Copy runs a third
    /// beside them; Stop ends all three.</summary>
    [AvaloniaFact]
    public void Two_copies_run_each_in_its_own_terminal_tab_another_runs_beside_and_Stop_ends_them_all()
    {
        MainWindow window = Open();
        try
        {
            Choose(window, 2);
            Ui.Launched(window, window.RunAsync(), "the two copies' start", TimeSpan.FromMinutes(2));
            LiveTreeView tree = window.LiveTree;
            Ui.WaitFor(() => window.RunCopies.Select(r => r.Name).SequenceEqual([Name, $"{Name} #2"]) && window.Session.Games.Count == 2 && tree.SourceNames.Count == 2,
                () => $"both copies running, each in the chooser ({string.Join(", ", window.RunCopies.Select(r => r.Name))}; {string.Join(", ", tree.SourceNames)}; {window.Status})",
                TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => Terminal(window.RunCopies[0]).Contains("role server", StringComparison.Ordinal) && Terminal(window.RunCopies[1]).Contains("role client", StringComparison.Ordinal),
                () => $"each copy's tab to show its own role ({Tail(Terminal(window.RunCopies[0]))} | {Tail(Terminal(window.RunCopies[1]))})", TimeSpan.FromMinutes(1));
            Assert.False(Terminal(window.RunCopies[1]).Contains("role server", StringComparison.Ordinal), $"The client's tab holds the server's lines: {Terminal(window.RunCopies[1])}");
            Assert.True(window.IsRunning && window.Shown(window.Configurations.Single(c => c.Name == Name)).Contains("(running)", StringComparison.Ordinal),
                "The configuration is not shown running while its copies run");

            Assert.Null(window.Actions.Run(AppActions.RunAnotherCopy));
            Ui.WaitFor(() => window.RunCopies.Count == 3 && window.RunCopies[2].Name == $"{Name} #3" && Terminal(window.RunCopies[2]).Contains("role client", StringComparison.Ordinal),
                () => $"a third copy run beside them, a client ({string.Join(", ", window.RunCopies.Select(r => r.Name))}; {window.Status})", TimeSpan.FromMinutes(1));

            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.RunCopies.Count == 0 && window.Session.Games.Count == 0 && !window.IsRunning,
                () => $"Stop to end every copy ({string.Join(", ", window.RunCopies.Select(r => r.Name))}; {window.Session.Games.Count} games)", TimeSpan.FromMinutes(1));
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

    /// <summary>A person's configuration named as a copy of another would be is its own game: Debug starts it as the first of
    /// its own copies, and Debug Another Copy finds it and counts on from it.</summary>
    [AvaloniaFact]
    public void A_configuration_named_as_a_copy_is_debugged_as_its_own_and_Debug_Another_Copy_counts_on_from_it()
    {
        const string Own = "Lobby #2";
        MainWindow window = Open();
        try
        {
            Choose(window, 1, Own);
            Ui.Launched(window, window.DebugAsync(), "the game's start under the debugger", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => Tab(window, Own)?.Session?.State == DebugState.Running, () => $"the game to run ({Sessions(window)}; {window.Status})", TimeSpan.FromMinutes(1));

            Assert.Null(window.Actions.Run(AppActions.DebugAnotherCopy));
            Ui.WaitFor(() => Tab(window, $"{Own} #2")?.Session?.State == DebugState.Running, () => $"its second copy beside it ({Sessions(window)}; {window.Status})", TimeSpan.FromMinutes(1));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>The game's configuration, run headless with the run's user folders, starting <paramref name="copies"/> copies: the
    /// first a server, the second a client, the rest clients too, given no arguments of their own.</summary>
    private void Choose(MainWindow window, int copies, string name = Name)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Generated && c.Name == "Lobby"),
            () => $"the game's configuration ({string.Join(", ", window.Configurations.Select(c => c.Name))})", TimeSpan.FromMinutes(1));
        RunConfiguration generated = window.Configurations.Single(c => c.Generated && c.Name == "Lobby");
        window.SetConfigurations([generated with
        {
            Name = name, Generated = false, Arguments = "--headless", Environment = new(game.Homes), GodotCopies = copies, CopyArguments = ["-- server", "-- client"],
        }], name);
    }

    private static void End(MainWindow window)
    {
        if (window.Debugger.IsDebugging || window.IsRunning)
        {
            Assert.Null(window.Actions.Run(AppActions.StopAll));
            Ui.WaitFor(() => !window.Debugger.IsDebugging && !window.IsRunning, () => $"the copies to end ({Sessions(window)})", TimeSpan.FromMinutes(1));
        }
        window.Close();
    }

    /// <summary>What a program run in a tab of the Terminal window has written there, its scrollback and its screen.</summary>
    private static string Terminal(TerminalRun run) =>
        string.Join('\n', run.Session.Screen.Scrollback.Select(l => l.Text()).Concat(run.Session.Screen.Lines.Select(l => l.Text())));

    private static DebugTab? Tab(MainWindow window, string name) => window.Debugger.Tabs.FirstOrDefault(t => t.Name == name && t.IsLive);

    private static string Console(MainWindow window, string name) =>
        Tab(window, name)?.Session is { } session ? window.Debugger.Output(session).ToString() : string.Empty;

    private static string Tail(string text) => string.Join(" / ", text.Split('\n').TakeLast(3));

    private static string Sessions(MainWindow window) =>
        string.Join(", ", window.Debugger.Tabs.Select(t => $"{t.Name}: {t.Session?.State.ToString() ?? "none"}"));

    /// <summary>A Godot 4 project of GDScript alone named Lobby at 20 frames a second: its Main node is the server where its
    /// user arguments say server, else a client, says so every tenth frame, and counts frames served past the fortieth.</summary>
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
