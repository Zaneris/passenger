using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.Godot;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Passenger's addon in a headless Godot editor on a small C# game of the test's own: installed by its action into
/// the project and enabled there, it connects the editor to the window, and the game played as Godot's Play plays it
/// (EditorInterface.play_main_scene, from a plugin of the test's in the same editor, never a window) is built here and
/// stops at a breakpoint in its first scene's _Ready, held at its start until the debugger attached; Stop lets it go on
/// in Godot, and the game, relayed through the editor's session, is a source of the Live Visual Tree, its errors pinned. A
/// game the debugger cannot reach runs on undebugged. A build error stops the next Play, the error in Problems. Godot runs
/// headless, its user folders the run's. Without a Godot 4 .NET build each test skips saying why. Debugged, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotAddonTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("godot-addon-"));

    private readonly GodotFound? godot = GodotExecutables.Find(null);

    private readonly ConcurrentQueue<string> said = new();

    private Process? editor;

    public void Dispose()
    {
        EndEditor();
        TestFolders.Delete(folder);
    }

    /// <summary>The test's Godot editor ended, then any game it was starting as it went, which may outlive it.</summary>
    private void EndEditor()
    {
        if (editor is not { } running)
        {
            return;
        }
        editor = null;
        int id = running.Id;
        ChildProcesses.Kill(running);
        Assert.True(Processes.Gone(id), $"The test's Godot editor (process {id}) still runs after it was ended");
        running.Dispose();
        TestGodotEditors.EndGamesOf(id);
    }

    private const string Script = """
        using Godot;

        public partial class Main : Node
        {
            private int frames;

            public override void _Ready()
            {
                int answer = 6 * 7;
                GD.Print($"ready {answer}");
                GD.PushError("Spinner fell over");
                System.IO.File.WriteAllText(ProjectSettings.GlobalizePath("res://ready.txt"), $"ready {answer}");
                // The instance asked to end, among several Godot's Play starts, quits a moment after it began.
                if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "ends") >= 0)
                {
                    GetTree().CreateTimer(4.0).Timeout += () => GetTree().Quit();
                }
            }

            public override void _Process(double delta)
            {
                frames++;
                if (GetNode<Button>("Ui/Press").ButtonPressed && !System.IO.File.Exists(ProjectSettings.GlobalizePath("res://pressed.txt")))
                {
                    System.IO.File.WriteAllText(ProjectSettings.GlobalizePath("res://pressed.txt"), "pressed");
                }
            }
        }

        """;

    private const string Scene = """
        [gd_scene load_steps=2 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1"]

        [node name="Main" type="Node"]
        script = ExtResource("1")

        [node name="Ui" type="Control" parent="."]
        layout_mode = 3
        anchors_preset = 0
        offset_right = 400.0
        offset_bottom = 300.0

        [node name="Press" type="Button" parent="Ui"]
        layout_mode = 0
        offset_left = 10.0
        offset_top = 10.0
        offset_right = 110.0
        offset_bottom = 40.0
        toggle_mode = true
        text = "Press"

        """;

    /// <summary>The test's own plugin: Godot's Play, as its Play button runs it, each time the test asks (a file it writes),
    /// once Passenger's addon has connected, and Godot's Stop once the test asks for that.</summary>
    private const string Player = """
        @tool
        extends EditorPlugin


        var playing := false


        func _ready() -> void:
        	print("TEST: ready")


        func _process(_delta: float) -> void:
        	if EditorInterface.is_playing_scene() != playing:
        		playing = EditorInterface.is_playing_scene()
        		print("TEST: play began" if playing else "TEST: play ended")
        	if FileAccess.file_exists("res://stop.txt"):
        		DirAccess.remove_absolute(ProjectSettings.globalize_path("res://stop.txt"))
        		print("TEST: stopping")
        		EditorInterface.stop_playing_scene()
        		return
        	if not FileAccess.file_exists("res://play.txt") or EditorInterface.is_playing_scene():
        		return
        	for plugin in get_parent().get_children():
        		if plugin.get_script() != null and plugin.get_script().resource_path == "res://addons/passenger/passenger.gd" and plugin.get("welcomed"):
        			DirAccess.remove_absolute(ProjectSettings.globalize_path("res://play.txt"))
        			print("TEST: playing")
        			EditorInterface.play_main_scene()
        			return

        """;

    private string Ready => Path.Combine(folder, "ready.txt");

    /// <summary>The game played stops at the breakpoint in _Ready, held at its start until the debugger took it; Stop lets it
    /// go on in Godot, and the game, relayed through the editor's session, shows its tree in the Live Visual Tree and its
    /// error pinned to its line; Godot's Stop ends it there. Then a build error stops the next Play, the error in Problems.</summary>
    [AvaloniaFact]
    public void Godots_Play_through_the_addon_stops_at_a_breakpoint_in_Ready_shows_its_tree_and_pinned_error_and_a_build_error_stops_it()
    {
        MainWindow window = Opened();
        try
        {
            CodeEditor code = window.EditorArea.CodeEditor;
            int line = Line(code, "int answer = 6 * 7;");
            int raised = Line(code, "GD.PushError");
            code.GoTo(new TextPosition(line, 13));
            Ui.Run(window, AppActions.ToggleBreakpoint);

            StartEditor([]);
            Ui.WaitFor(() => window.GodotEditorsConnected == 1, () => $"Godot's editor to connect through the addon ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));

            TestFolders.Write(folder, "play.txt", "play\n");
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && code.LineMarks.Any(m => m.Kind == LineMarkKind.ExecutionPoint && m.Line == line),
                () => $"the game Godot's Play started to stop at the breakpoint in _Ready ({window.Debugger.Session?.State}; {Games(window)}; {window.Status}; Godot said: {Said()})",
                TimeSpan.FromMinutes(3));
            Assert.Equal("Spinner in Godot", window.Debugger.Tabs.Single(t => t.IsLive).Name);
            Assert.False(File.Exists(Ready), "The game went past its breakpoint before the debugger let it go on");
            // Stop lets the game go, Godot's to end: it goes on from the breakpoint and writes its answer.
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging && File.Exists(Ready),
                () => $"Stop to let the game go on and write its answer ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(1));
            Assert.Equal("ready 42", File.ReadAllText(Ready));

            Assert.True(window.Session.Game is { Relayed: true, Name: "Spinner in Godot" }, $"The game chosen is {window.Session.Game?.Name}, not the one Godot's editor plays");
            Ui.WaitFor(() => window.LiveTree.Rows.Any(r => r.Element.Name == "Main"),
                () => $"the Live Visual Tree to show the played game's nodes ({window.LiveSource?.State}; {string.Join(", ", window.LiveTree.Rows.Select(r => r.Element.Name))})",
                TimeSpan.FromMinutes(1));
            OpenDocument script = window.EditorArea.Active!;
            Ui.WaitFor(() => script.ErrorPins.Any(p => p.Line == raised - 1 && p.Last == "Spinner fell over"),
                () => $"the game's error to be pinned to Main.cs:{raised} ({string.Join("; ", script.ErrorPins.Select(p => $"line {p.Line + 1} {p.Last}"))})");
            TestFolders.Write(folder, "stop.txt", "stop\n");
            Ui.WaitFor(() => window.Session.Games.Count == 0, () => $"the game to end with Godot's Stop ({window.Session.Games.Count} running; Godot said: {Said()})", TimeSpan.FromMinutes(1));

            Typing.Replace(window, "GD.Print(", "GD.Prin(");
            TestFolders.Write(folder, "play.txt", "play\n");
            Ui.WaitFor(() => said.Any(l => l.Contains("An EditorPlugin build callback failed", StringComparison.Ordinal)),
                () => $"Godot's Play to stop at the failed build ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));
            Assert.True(window.BottomPanel.Problems.Problems.Any(p => p.Severity == Passenger.App.Run.ProblemSeverity.Error && p.Path is { } path
                && PathNames.Same(path, Path.Combine(folder, "Main.cs")) && p.Message.Contains("Prin", StringComparison.Ordinal)),
                $"The build error was to be in Problems:{Ui.BuildErrors(window)}");
            Assert.False(window.Debugger.IsDebugging, "A game was debugged though its build failed");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A game held at its start whose runtime turns its debugger off (DOTNET_EnableDiagnostics_Debugger=0 in the
    /// editor it inherits from) is let go by Passenger, which reads that through its diagnostic port before attaching: it runs
    /// on undebugged past the breakpoint, the window and Godot's Output saying why, and no Debug tab opens for it.</summary>
    [AvaloniaFact]
    public void A_played_game_the_debugger_cannot_reach_is_let_go_to_run_on_saying_why()
    {
        MainWindow window = Opened();
        try
        {
            CodeEditor code = window.EditorArea.CodeEditor;
            code.GoTo(new TextPosition(Line(code, "int answer = 6 * 7;"), 13));
            Ui.Run(window, AppActions.ToggleBreakpoint);

            StartEditor([("DOTNET_EnableDiagnostics_Debugger", "0")]);
            Ui.WaitFor(() => window.GodotEditorsConnected == 1, () => $"Godot's editor to connect through the addon ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));

            TestFolders.Write(folder, "play.txt", "play\n");
            Ui.WaitFor(() => File.Exists(Ready) && said.Any(l => l.Contains("Spinner in Godot runs undebugged", StringComparison.Ordinal)),
                () => $"the game to be let go and run on undebugged ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(3));
            Assert.Contains("DOTNET_EnableDiagnostics_Debugger=0", window.Status, StringComparison.Ordinal);
            Assert.False(window.Debugger.Tabs.Any(t => t.IsLive), $"A Debug tab opened for a game the debugger cannot reach: {string.Join(", ", window.Debugger.Tabs.Select(t => t.Name))}");
            TestFolders.Write(folder, "stop.txt", "stop\n");
            Ui.WaitFor(() => window.Session.Games.Count == 0, () => $"the game to end with Godot's Stop ({window.Session.Games.Count} running; Godot said: {Said()})", TimeSpan.FromMinutes(1));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A window keeping its state on the test's game, the addon installed by its action and enabled beside the test's
    /// player, Main.cs open; skips without a Godot 4 .NET build.</summary>
    private MainWindow Opened(bool addon = true)
    {
        Assert.SkipWhen(godot is not { WhyNot: null, Version: not null, Packages: not null }, godot is null
            ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build to run this test"
            : $"The Godot found cannot run a C# game: {godot.WhyNot}");
        GodotTests.Game.Make(folder, godot!.Version!, godot.Packages!, Script, Scene);
        string project = Path.Combine(folder, GodotProject.FileName);
        // Its games headless, as no window may open; the test's player enabled beside the addon the install enables.
        File.AppendAllText(project, "\n[editor]\n\nrun/main_run_args=\"--headless\"\n\n[editor_plugins]\n\nenabled=PackedStringArray(\"res://addons/plays/plugin.cfg\")\n");
        TestFolders.Write(folder, "addons/plays/plugin.cfg", "[plugin]\n\nname=\"Plays\"\ndescription=\"\"\nauthor=\"\"\nversion=\"1\"\nscript=\"plays.gd\"\n");
        TestFolders.Write(folder, "addons/plays/plays.gd", Player);
        Restore.Run(Path.Combine(folder, "Spinner.csproj"));

        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = TestFolders.Create("godot-addon-state-"), Autosaves = false };
        window.Show();
        window.ApplySettings(window.Settings with { GodotExecutable = godot.Executable });
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        // Asked nothing but the install: an offer to open Godot's editor would open one with a window.
        window.Confirm = (title, _, _) => Task.FromResult(title.StartsWith("Install", StringComparison.Ordinal));
        if (!addon)
        {
            window.OpenFile(Path.Combine(folder, "Main.cs"));
            return window;
        }
        Assert.Null(window.Actions.Run(AppActions.GodotAddon));
        Ui.WaitFor(() => window.Status?.Contains("Passenger's addon is installed", StringComparison.Ordinal) == true, () => $"the addon to be installed ({window.Status})");
        string enabled = File.ReadAllText(project);
        Assert.True(File.Exists(Path.Combine(folder, "addons", "passenger", "passenger.gd")) && File.Exists(Path.Combine(folder, "addons", "passenger", "plugin.cfg"))
            && enabled.Contains("enabled=PackedStringArray(\"res://addons/plays/plugin.cfg\", \"res://addons/passenger/plugin.cfg\")", StringComparison.Ordinal),
            $"The addon's files were to be written and enabled beside the test's plugin:\n{enabled}");
        window.OpenFile(Path.Combine(folder, "Main.cs"));
        return window;
    }

    /// <summary>Run in Godot Editor through the addon: Run has the editor play the game, built here, undebugged and relayed,
    /// and Stop ends it in the editor; Debug has it play held at its start, stopped at the breakpoint in _Ready, and Stop
    /// ends that play in the editor too, not only the debugger's hold of it.</summary>
    [AvaloniaFact]
    public void Run_in_Godot_Editor_plays_the_game_in_the_editor_through_the_addon_and_Stop_ends_it_there()
    {
        MainWindow window = Opened();
        try
        {
            InEditor(window);
            StartEditor([]);
            Ui.WaitFor(() => window.GodotEditorsConnected == 1, () => $"Godot's editor to connect through the addon ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));

            Ui.Launched(window, window.RunAsync(), "the play in Godot's editor", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => File.Exists(Ready) && window.Session.Game is { Relayed: true, State: Passenger.App.LiveTree.LiveAppState.Connected },
                () => $"the editor to play the game, relayed here ({Games(window)}; {window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));
            Assert.False(window.Debugger.Tabs.Any(t => t.IsLive), "Run debugged the game the editor played");
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Session.Games.Count == 0 && said.Any(l => l.Contains("TEST: play ended", StringComparison.Ordinal)),
                () => $"Stop to end the play in the editor ({Games(window)}; Godot said: {Said()})", TimeSpan.FromMinutes(1));

            File.Delete(Ready);
            said.Clear();
            CodeEditor code = window.EditorArea.CodeEditor;
            int line = Line(code, "int answer = 6 * 7;");
            code.GoTo(new TextPosition(line, 13));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the play in Godot's editor under the debugger", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && code.LineMarks.Any(m => m.Kind == LineMarkKind.ExecutionPoint && m.Line == line),
                () => $"the game the editor played to stop at the breakpoint in _Ready ({Games(window)}; {window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging && window.Session.Games.Count == 0 && said.Any(l => l.Contains("TEST: play ended", StringComparison.Ordinal)),
                () => $"Stop to end the play in the editor ({Games(window)}; Godot said: {Said()})", TimeSpan.FromMinutes(1));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Run in Godot Editor where the editor is open without the addon: the game is built here and played through
    /// the editor's IDE messaging, which Stop stops there; and the editor takes the assemblies of a build here on its own.</summary>
    [AvaloniaFact]
    public void Run_in_Godot_Editor_without_the_addon_plays_through_IDE_messaging_and_the_editor_reloads_after_a_build()
    {
        MainWindow window = Opened(addon: false);
        try
        {
            InEditor(window);
            StartEditor([], verbose: true);
            Ui.WaitFor(() => File.Exists(GodotIdeMessaging.MetaPath(folder)) && said.Any(l => l.Contains("TEST: ready", StringComparison.Ordinal)),
                () => $"Godot's editor to open and serve its IDE messaging (Godot said: {Said()})", TimeSpan.FromMinutes(2));

            Ui.Launched(window, window.RunAsync(), "the play in Godot's editor", TimeSpan.FromMinutes(2));
            Assert.Contains("plays in Godot's editor", window.Status, StringComparison.Ordinal);
            Ui.WaitFor(() => File.Exists(Ready), () => $"the editor to play the game ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => said.Any(l => l.Contains("TEST: play ended", StringComparison.Ordinal)),
                () => $"Stop to end the play in the editor (Godot said: {Said()})", TimeSpan.FromMinutes(1));

            int reloads = said.Count(l => l.Contains(".NET: Reloading assemblies", StringComparison.Ordinal));
            Typing.Replace(window, "ready {answer}", "ready again {answer}");
            Ui.Await(window.BuildAsync(), "the build", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => said.Count(l => l.Contains(".NET: Reloading assemblies", StringComparison.Ordinal)) > reloads,
                () => $"the editor to reload the game's assemblies after the build (Godot said: {Said()})", TimeSpan.FromMinutes(1));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Run in Godot Editor with no editor open on the project says so, naming the configuration that opens one.</summary>
    [AvaloniaFact]
    public void Run_in_Godot_Editor_with_no_editor_open_says_so_and_names_what_opens_one()
    {
        MainWindow window = Opened(addon: false);
        try
        {
            InEditor(window);
            string? refused = Ui.Await(window.RunAsync(), "Run in Godot Editor", TimeSpan.FromMinutes(1));
            Assert.Equal("Godot's editor is not open on Spinner: Spinner: Godot Editor opens it, then run Spinner in Godot's editor again", refused);
            Assert.False(window.IsRunning, "Something ran with no editor open");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A game Godot's Play starts, relayed here, shows its members' values at their lines as one Passenger starts does,
    /// and an agent's click through app_click reaches its button.</summary>
    [AvaloniaFact]
    public void A_relayed_games_values_show_at_their_lines_and_an_agents_click_reaches_it()
    {
        MainWindow window = Opened();
        try
        {
            OpenDocument script = window.EditorArea.Active!;
            int frames = Line(window.EditorArea.CodeEditor, "private int frames;");
            StartEditor([]);
            Ui.WaitFor(() => window.GodotEditorsConnected == 1, () => $"Godot's editor to connect through the addon ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));
            TestFolders.Write(folder, "play.txt", "play\n");
            Ui.WaitFor(() => File.Exists(Ready) && window.Session.Game is { Relayed: true, State: Passenger.App.LiveTree.LiveAppState.Connected },
                () => $"the played game to be relayed here ({Games(window)}; Godot said: {Said()})", TimeSpan.FromMinutes(2));

            Ui.WaitFor(() => script.LiveValues.Any(v => v.Line == frames - 1 && v.Text.StartsWith("frames: ", StringComparison.Ordinal)),
                () => $"the frame count's value at its line ({string.Join("; ", script.LiveValues.Select(v => $"line {v.Line + 1}: {v.Text}"))}; the last read: {window.GodotValues.Unread ?? "answered"}; "
                    + $"IDE: {string.Join(" | ", Ui.Shared(IdeLog.PathOf(IdeLog.Ide)).Split('\n').Where(l => l.Contains("Godot:", StringComparison.Ordinal) || l.Contains("xception", StringComparison.Ordinal)).TakeLast(10))}; Godot said: {Said()})", TimeSpan.FromMinutes(1));
            string clicked = Ui.Await(new Passenger.App.Mcp.PassengerTools(window).AppClick(app: "Spinner in Godot", control: "#Press"), "an agent's click on the game's button",
                TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => File.Exists(Path.Combine(folder, "pressed.txt")), () => $"the click to press the game's button (the tool said: {clicked})", TimeSpan.FromSeconds(30));
            TestFolders.Write(folder, "stop.txt", "stop\n");
            Ui.WaitFor(() => window.Session.Games.Count == 0, () => $"the game to end with Godot's Stop ({Games(window)}; Godot said: {Said()})", TimeSpan.FromMinutes(1));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Three instances Godot's Play starts at once (its Run Multiple Instances) are each relayed and debugged through
    /// their own session; the one given a user argument to quit ended, the other two are still relayed and debugged, each by
    /// its own session's debugger, whatever place the editor now gives their debuggers.</summary>
    [AvaloniaFact]
    public void Three_instances_played_are_each_relayed_and_debugged_and_the_others_stay_so_once_the_second_ends()
    {
        MainWindow window = Opened();
        try
        {
            // Godot's editor keeps its Run Multiple Instances in the project's metadata, which it reads as it opens.
            // The second is given a user argument that has it quit itself a moment after it began.
            TestFolders.Write(folder, ".godot/editor/project_metadata.cfg", "[debug_options]\n\nmultiple_instances_enabled=true\nrun_instance_count=3\n"
                + "run_instances_config=[{\"arguments\": \"\", \"features\": \"\", \"override_args\": false, \"override_features\": false}, "
                + "{\"arguments\": \"++ ends\", \"features\": \"\", \"override_args\": false, \"override_features\": false}, "
                + "{\"arguments\": \"\", \"features\": \"\", \"override_args\": false, \"override_features\": false}]\n");
            StartEditor([]);
            Ui.WaitFor(() => window.GodotEditorsConnected == 1, () => $"Godot's editor to connect through the addon ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));
            TestFolders.Write(folder, "play.txt", "play\n");
            string[] names = ["Spinner in Godot", "Spinner in Godot #2", "Spinner in Godot #3"];
            Ui.WaitFor(() => names.All(n => window.Session.Games.Any(g => g.Name == n && g.State == Passenger.App.LiveTree.LiveAppState.Connected && g.ProcessId is int))
                && names.All(n => window.Debugger.Tabs.Any(t => t.Name == n && t.Session is { State: DebugState.Running })),
                () => $"three instances relayed and debugged ({Games(window)}; tabs {Tabs(window)}; Godot said: {Said()})", TimeSpan.FromMinutes(3));
            Dictionary<string, int> processes = window.Session.Games.ToDictionary(g => g.Name, g => g.ProcessId!.Value);
            Assert.Equal(3, processes.Values.Distinct().Count());
            int ending = processes.Single(p => DotnetProcesses.Describe(p.Value)?.CommandLine.Contains(" ends", StringComparison.Ordinal) == true).Value;
            string ended = processes.Single(p => p.Value == ending).Key;
            string[] others = [.. names.Where(n => n != ended)];
            Ui.WaitFor(() => !window.Session.Games.Any(g => g.Name == ended) && !window.Debugger.Tabs.Any(t => t.Name == ended && t.IsLive),
                () => $"the second instance to end here ({Games(window)}; tabs {Tabs(window)}; its process {(DotnetProcesses.Runs(ending) ? "runs" : "has gone")}; Godot said: {Said()})",
                TimeSpan.FromMinutes(1));
            // Long enough for a message of each of the others to come through its own session again.
            Ui.Hold(TimeSpan.FromSeconds(3), "the first and third instances' monitors to come again, once a second each");
            foreach (string name in others)
            {
                GodotGame game = Assert.Single(window.Session.Games, g => g.Name == name);
                Assert.True(game.State == Passenger.App.LiveTree.LiveAppState.Connected && game.ProcessId == processes[name] && Processes.IsRunning(processes[name]),
                    $"{name} is no longer relayed as the game it was ({Games(window)})");
                Assert.True(window.Debugger.Tabs.Any(t => t.Name == name && t.IsLive), $"{name} is no longer debugged");
            }
            Assert.NotNull(Ui.Await(window.Session.Games.Single(g => g.Name == others[^1]).SceneTreeAsync(), "the last instance's tree through its own session"));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>The editor ended while it plays leaves no game behind: every process naming it as its editor ends with it.</summary>
    [AvaloniaFact]
    public void An_editor_ended_while_it_plays_leaves_no_game_naming_it()
    {
        MainWindow window = Opened();
        try
        {
            StartEditor([]);
            Ui.WaitFor(() => window.GodotEditorsConnected == 1, () => $"Godot's editor to connect through the addon ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));
            TestFolders.Write(folder, "play.txt", "play\n");
            Ui.WaitFor(() => window.Session.Game is { Relayed: true, ProcessId: int }, () => $"the editor to play the game ({Games(window)}; Godot said: {Said()})", TimeSpan.FromMinutes(2));
            int editorId = editor!.Id;
            int game = window.Session.Game!.ProcessId!.Value;

            EndEditor();
            Assert.True(Processes.Gone(game), $"The game (process {game}) still runs after its editor ended");
            Assert.Empty(TestGodotEditors.EndGamesOf(editorId));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>The window's configuration made the generated game's, played in Godot's editor.</summary>
    private static void InEditor(MainWindow window)
    {
        RunConfiguration generated = window.Session.Configurations.All.First(c => c.Generated && !c.GodotEditor && c.Name == "Spinner");
        window.SetConfigurations([generated with { Name = "Spinner in Godot's editor", Generated = false, GodotInEditor = true }], "Spinner in Godot's editor");
    }

    private void End(MainWindow window)
    {
        if (window.Debugger.IsDebugging)
        {
            Assert.Null(window.Actions.Run(AppActions.StopAll));
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugger to let every game go");
        }
        window.Close();
        EndEditor();
    }

    /// <summary>A headless Godot editor on the project, its user folders the run's and Passenger's state the IDE's, with the
    /// variables given, which the games it plays inherit; on Windows through Godot's console wrapper, as its own .exe writes
    /// to no console.</summary>
    private void StartEditor(IReadOnlyList<(string Name, string Value)> variables, bool verbose = false)
    {
        ProcessStartInfo start = ChildProcesses.Command(GodotExecutables.InTerminal(godot!.Executable),
            ["--headless", "--editor", .. verbose ? new[] { "--verbose" } : [], "--path", folder], folder);
        foreach ((string name, string value) in GodotTests.Game.Home(Path.Combine(folder, "..", Path.GetFileName(folder) + "-home")).Select(v => (v.Key, v.Value)).Concat(variables))
        {
            start.Environment[name] = value;
        }
        start.Environment[Passenger.App.State.StateStore.Variable] = Passenger.App.State.StateStore.DefaultRoot;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        editor = ChildProcesses.Start(start);
        TestGodotEditors.Began(editor.Id);
        UnawaitedWork.Dismiss(ChildProcesses.Hear(editor, said.Enqueue, said.Enqueue));
    }

    private string Said() => string.Join(" | ", said.TakeLast(40));

    /// <summary>Each Debug tab, with its session's state.</summary>
    private static string Tabs(MainWindow window) =>
        string.Join(", ", window.Debugger.Tabs.Select(t => $"{t.Name} {(t.IsLive ? t.Session?.State.ToString() ?? "opening" : "ended")}"));

    /// <summary>Each game the window has, with its state and the process it said.</summary>
    private static string Games(MainWindow window) => window.Session.Games.Count == 0 ? "no game"
        : string.Join(", ", window.Session.Games.Select(g => $"{g.Name} {g.State} process {g.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unsaid"}"));

    private static int Line(CodeEditor editor, string code)
    {
        int index = Array.FindIndex(editor.Document.Snapshot.ToString().Split('\n'), l => l.Contains(code, StringComparison.Ordinal));
        Assert.True(index >= 0, $"The script has no line of {code}");
        return index + 1;
    }
}
