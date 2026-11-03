using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.Godot;
using Passenger.App.Mcp;
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

            private bool ending;

            public override void _Ready()
            {
                int answer = 6 * 7;
                GD.Print($"ready {answer}");
                GD.PushError("Spinner fell over");
                GetNode<Button>("Plain").Pressed += () => System.IO.File.WriteAllText(ProjectSettings.GlobalizePath("res://plain.txt"), "plain");
                try
                {
                    System.IO.File.WriteAllText(ProjectSettings.GlobalizePath("res://ready.txt"), $"ready {answer}");
                }
                catch (System.IO.IOException)
                {
                    // Instances Godot's Play starts at once write it together, which Windows refuses all but one.
                }
                ending = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "ends") >= 0;
            }

            public void OnToggled(bool on)
            {
                GD.Print($"toggled {on}");
            }

            public override void _Process(double delta)
            {
                frames++;
                // The instance asked to end, among several Godot's Play starts, quits once the test says so.
                if (ending && System.IO.File.Exists(ProjectSettings.GlobalizePath("res://end.txt")))
                {
                    ending = false;
                    GetTree().Quit();
                }
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

        [node name="Plain" type="Button" parent="."]
        offset_left = 120.0
        offset_top = 10.0
        offset_right = 220.0
        offset_bottom = 40.0
        text = "Plain"

        [connection signal="toggled" from="Ui/Press" to="." method="OnToggled"]

        """;

    /// <summary>The test's own plugin: Godot's Play, as its Play button runs it, each time the test asks (a file it writes),
    /// once Passenger's addon has connected, and Godot's Stop once the test asks for that.</summary>
    private const string Player = """
        @tool
        extends EditorPlugin


        var playing := false


        func _ready() -> void:
        	print("TEST: ready")
        	EditorInterface.get_selection().selection_changed.connect(_selected)


        ## A C# script made by Godot's own Attach Script dialog, from the template of a base class Passenger wrote, and attached
        ## to the main scene's node of that path, the scene saved.
        func _attach(base: String, path: String, node: String) -> void:
        	var dialog := ScriptCreateDialog.new()
        	EditorInterface.get_base_control().add_child(dialog)
        	dialog.config(base, path, false, false)
        	# Shown, as Attach Script shows it, the dialog checks what it is given and lists its templates.
        	dialog.popup_centered()
        	for menu in dialog.find_children("*", "OptionButton", true, false):
        		for i in menu.item_count:
        			if menu.get_item_text(i) == "C#":
        				menu.select(i)
        				menu.item_selected.emit(i)
        	# The dialog lists the language's templates as its checks next run.
        	await get_tree().process_frame
        	await get_tree().process_frame
        	for menu in dialog.find_children("*", "OptionButton", true, false):
        		for i in menu.item_count:
        			if menu.get_item_text(i) == base + ": Passenger (this project's style)":
        				menu.select(i)
        				menu.item_selected.emit(i)
        				print("TEST: template ", menu.get_item_text(i))
        	dialog.script_created.connect(func(script: Script) -> void:
        		EditorInterface.open_scene_from_path("res://main.tscn")
        		EditorInterface.get_edited_scene_root().get_node(node).set_script(script)
        		EditorInterface.save_scene()
        		print("TEST: attached ", script.resource_path))
        	await get_tree().process_frame
        	dialog.get_ok_button().pressed.emit()


        func _selected() -> void:
        	var root := EditorInterface.get_edited_scene_root()
        	for node in EditorInterface.get_selection().get_selected_nodes():
        		print("TEST: selection ", root.scene_file_path, " ", "." if node == root else str(root.get_path_to(node)))


        func _process(_delta: float) -> void:
        	if FileAccess.file_exists("res://hold.txt"):
        		DirAccess.remove_absolute(ProjectSettings.globalize_path("res://hold.txt"))
        		EditorInterface.open_scene_from_path("res://main.tscn")
        		EditorInterface.mark_scene_as_unsaved()
        		print("TEST: held")
        	if FileAccess.file_exists("res://open.txt"):
        		DirAccess.remove_absolute(ProjectSettings.globalize_path("res://open.txt"))
        		EditorInterface.open_scene_from_path("res://main.tscn")
        		print("TEST: opened")
        	if FileAccess.file_exists("res://select.txt"):
        		var node := FileAccess.get_file_as_string("res://select.txt").strip_edges()
        		DirAccess.remove_absolute(ProjectSettings.globalize_path("res://select.txt"))
        		EditorInterface.open_scene_from_path("res://main.tscn")
        		var root := EditorInterface.get_edited_scene_root()
        		EditorInterface.get_selection().clear()
        		EditorInterface.get_selection().add_node(root.get_node(node))
        		print("TEST: selected ", node)
        	if FileAccess.file_exists("res://attach.txt") and FileAccess.get_file_as_string("res://attach.txt").ends_with("\n"):
        		var words := FileAccess.get_file_as_string("res://attach.txt").strip_edges().split(" ")
        		DirAccess.remove_absolute(ProjectSettings.globalize_path("res://attach.txt"))
        		_attach(words[0], words[1], words[2])
        	if FileAccess.file_exists("res://undo.txt"):
        		DirAccess.remove_absolute(ProjectSettings.globalize_path("res://undo.txt"))
        		var undo := EditorInterface.get_editor_undo_redo()
        		var history := undo.get_history_undo_redo(undo.get_object_history_id(EditorInterface.get_edited_scene_root()))
        		var undone := history.get_current_action_name()
        		history.undo()
        		print("TEST: undid ", undone)
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
    private MainWindow Opened(bool addon = true, IReadOnlyList<(string Name, string Text)>? files = null)
    {
        Assert.SkipWhen(godot is not { WhyNot: null, Version: not null, Packages: not null }, godot is null
            ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build to run this test"
            : $"The Godot found cannot run a C# game: {godot.WhyNot}");
        GodotTests.Game.Make(folder, godot!.Version!, godot.Packages!, Script, Scene);
        foreach ((string name, string text) in files ?? [])
        {
            TestFolders.Write(folder, name, text);
        }
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
            // A plain button under the bare root, beyond the 64 by 64 the headless display gives the game.
            clicked = Ui.Await(new Passenger.App.Mcp.PassengerTools(window).AppClick(app: "Spinner in Godot", control: "#Plain"), "an agent's click on the plain button",
                TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => File.Exists(Path.Combine(folder, "plain.txt")), () => $"the click to press the plain button (the tool said: {clicked})", TimeSpan.FromSeconds(30));
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
            // Run Multiple Instances comes from the project's metadata; the second's argument has it quit once told.
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
            TestFolders.Write(folder, "end.txt", "end\n");
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

    private string ScenePath => Path.Combine(folder, "main.tscn");

    /// <summary>With Godot's editor holding unsaved edits in the scene, a rename whose edits reach the scene's connection asks
    /// the person first, saying so, and declined leaves the scene and the code as they were; an agent's is refused, naming
    /// what Godot holds, and asks nobody.</summary>
    [AvaloniaFact]
    public void A_rename_over_a_scene_Godot_holds_unsaved_asks_the_person_and_an_agents_is_refused()
    {
        MainWindow window = Opened();
        try
        {
            List<string> asked = [];
            window.Confirm = (title, text, _) =>
            {
                asked.Add($"{title}: {text}");
                return Task.FromResult(false);
            };
            StartEditor([]);
            Ui.WaitFor(() => window.GodotEditorsConnected == 1, () => $"Godot's editor to connect through the addon ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));
            TestFolders.Write(folder, "hold.txt", "hold\n");
            Ui.WaitFor(() => window.HeldByGodot(ScenePath)?.Contains("unsaved edits", StringComparison.Ordinal) == true,
                () => $"Godot's editor to say it holds the scene unsaved ({window.HeldByGodot(ScenePath) ?? "nothing held"}; Godot said: {Said()})", TimeSpan.FromMinutes(1));
            string scene = File.ReadAllText(ScenePath);

            CodeEditor code = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(code, "void OnTogg");
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == "OnToggled", () => $"Rename's box on the handler ({window.Assist.Renaming}; {window.Status})", TimeSpan.FromMinutes(1));
            window.KeyTextInput("OnFlipped");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => asked.Count > 0 && window.Status?.Contains("Godot's editor holds unsaved edits in main.tscn", StringComparison.Ordinal) == true,
                () => $"the rename to ask first and, declined, say why ({window.Status}; asked: {string.Join(" | ", asked)})", TimeSpan.FromMinutes(1));
            Assert.Contains("Godot's editor holds unsaved edits in main.tscn", asked[0], StringComparison.Ordinal);
            Ui.WaitFor(() => code.Document.Snapshot.ToString().Contains("void OnToggled(", StringComparison.Ordinal), "the declined rename's code edit to be taken back");
            Assert.Equal(scene, File.ReadAllText(ScenePath));

            int questions = asked.Count;
            AgentRenameRefused(window);
            Assert.Equal(questions, asked.Count);
            Assert.Equal(scene, File.ReadAllText(ScenePath));

            // Open here, the scene's edit is its document's, refused the same and taken back, its save never made.
            Assert.Null(window.EditorArea.Open(ScenePath));
            OpenDocument open = window.EditorArea.Find(ScenePath)!;
            AgentRenameRefused(window);
            Assert.Equal(questions, asked.Count);
            Ui.WaitFor(() => !open.IsModified && open.Text.Snapshot.ToString().Contains("method=\"OnToggled\"", StringComparison.Ordinal),
                () => $"the refused rename's edit of the open scene to be taken back ({window.Status})");
            Assert.Equal(scene, File.ReadAllText(ScenePath));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>An agent's rename of the scene's handler, refused saying what Godot holds.</summary>
    private static void AgentRenameRefused(MainWindow window)
    {
        Passenger.App.Mcp.AgentCall call = Passenger.App.Mcp.AgentCall.Begin(null, null, null, "rename", "Rename", "a test's agent", "the test", traced: false);
        try
        {
            Task<string> renamed = new Passenger.App.Mcp.PassengerTools(window).Rename("OnFlipped", symbol: "Main.OnToggled");
            Ui.WaitFor(() => renamed.IsCompleted, "an agent's rename to be answered", TimeSpan.FromMinutes(1));
            Assert.True(renamed.IsFaulted && renamed.Exception!.InnerException!.Message.Contains("save it in Godot first", StringComparison.Ordinal),
                $"An agent's rename over the scene Godot holds was not refused saying why: {(renamed.IsFaulted ? renamed.Exception!.InnerException!.Message : Ui.Await(renamed, "the rename's answer"))}");
        }
        finally
        {
            Ui.Await(call.DisposeAsync().AsTask().ContinueWith(_ => true, TaskScheduler.Default), "the agent's call to end");
        }
    }

    /// <summary>A scene Godot's editor has open and saved, written by Passenger (a rename reaching its connection), is read
    /// again there, so no question of a newer file comes; and a node selected in Godot's scene dock shows here, the scene
    /// shown at its node, which the outline follows.</summary>
    [AvaloniaFact]
    public void A_scene_Godot_has_open_is_read_again_after_a_write_here_and_its_selection_shows_in_the_outline()
    {
        MainWindow window = Opened();
        try
        {
            StartEditor([]);
            Ui.WaitFor(() => window.GodotEditorsConnected == 1, () => $"Godot's editor to connect through the addon ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));
            TestFolders.Write(folder, "open.txt", "open\n");
            Ui.WaitFor(() => window.GodotHasOpen(ScenePath), () => $"Godot's editor to say it has the scene open (Godot said: {Said()})", TimeSpan.FromMinutes(1));
            Assert.Null(window.HeldByGodot(ScenePath));

            CodeEditor code = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(code, "void OnTogg");
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == "OnToggled", () => $"Rename's box on the handler ({window.Assist.Renaming}; {window.Status})", TimeSpan.FromMinutes(1));
            window.KeyTextInput("OnFlipped");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => File.ReadAllText(ScenePath).Contains("method=\"OnFlipped\"", StringComparison.Ordinal),
                () => $"the rename to reach the scene's connection ({window.Status})", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => said.Any(l => l.Contains("res://main.tscn read again, as Passenger wrote it", StringComparison.Ordinal)),
                () => $"Godot's editor to read the scene again (Godot said: {Said()})", TimeSpan.FromMinutes(1));

            int press = Array.FindIndex(File.ReadAllText(ScenePath).Split('\n'), l => l.StartsWith("[node name=\"Press\"", StringComparison.Ordinal)) + 1;
            Ui.Run(window, AppActions.ShowStructure);
            TestFolders.Write(folder, "select.txt", "Ui/Press\n");
            Ui.WaitFor(() => window.EditorArea.Active is { } shown && PathNames.Same(shown.Path, ScenePath) && window.EditorArea.CodeEditor.CaretPosition.Line == press,
                () => $"the scene to show here at the node selected in Godot ({window.EditorArea.Active?.Path}:{window.EditorArea.CodeEditor.CaretPosition.Line}; Godot said: {Said()})",
                TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => window.StructureView.Selected?.Name == "Press", () => $"the outline to follow to the node ({window.StructureView.Selected?.Name})", TimeSpan.FromSeconds(30));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>An agent attached to the window acting in Godot's editor (godot_editor): refused, saying what opens one, while
    /// none is connected; then it opens the scene and reads it unasked, is asked before its first change, sets a node's
    /// property and adds a node through Godot's undo, and reads both back from the edited scene, its file as it was and the
    /// scene held unsaved for Passenger's writes; one undo in Godot takes the node added away, the next the property set.</summary>
    [AvaloniaFact]
    public void An_agents_godot_editor_changes_the_edited_scene_through_Godots_undo_and_reads_it_back()
    {
        MainWindow window = Opened();
        using Agent agent = new();
        try
        {
            window.ApplySettings(window.Settings with { McpServer = true });
            Assert.True(window.AgentSocket is not null, $"The window does not listen for agents: {window.Status}");
            McpClient client = agent.Attach();
            CallToolResult none = agent.Await(agent.Answer(client, McpTools.GodotEditor, new() { ["action"] = "read" }), "godot_editor with no editor connected");
            Assert.True(none.IsError == true && agent.Words(none).Contains("No Godot editor on", StringComparison.Ordinal),
                $"godot_editor with no editor connected did not say what opens one: {agent.Words(none)}");

            StartEditor([]);
            Ui.WaitFor(() => window.GodotEditorsConnected == 1, () => $"Godot's editor to connect through the addon ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));
            string scene = File.ReadAllText(ScenePath);
            JsonElement opened = agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "open", ["scene"] = "main.tscn" });
            Assert.Equal("res://main.tscn", opened.GetProperty("scene").GetString());
            Assert.Equal("\"Press\"", Stored(agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "read" }), "Ui/Press", "text"));

            agent.Asked(window, client, McpTools.GodotEditor, "act in Godot's editor",
                new() { ["action"] = "set", ["node"] = "Ui/Press", ["properties"] = new Dictionary<string, object?> { ["text"] = "Go" } });
            JsonElement added = agent.Unasked(window, client, McpTools.GodotEditor,
                new() { ["action"] = "add", ["node"] = "Ui", ["type"] = "Label", ["name"] = "Score", ["properties"] = new Dictionary<string, object?> { ["text"] = "0" } });
            Assert.Equal("Ui/Score", added.GetProperty("node").GetString());

            JsonElement read = agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "read" });
            Assert.Equal("\"Go\"", Stored(read, "Ui/Press", "text"));
            Assert.Equal("\"0\"", Stored(read, "Ui/Score", "text"));
            Assert.Equal("Label", Node(read, "Ui/Score")!.Value.GetProperty("type").GetString());
            Assert.Equal(scene, File.ReadAllText(ScenePath));
            // A Godot before 4.7 says nothing of what it holds unsaved.
            if (read.TryGetProperty("unsaved", out JsonElement unsaved))
            {
                Assert.True(unsaved.GetBoolean(), $"The edited scene was not said to hold unsaved changes: {read.GetRawText()}");
                Ui.WaitFor(() => window.HeldByGodot(ScenePath)?.Contains("unsaved edits", StringComparison.Ordinal) == true,
                    () => $"Passenger's writes to see the scene held unsaved ({window.HeldByGodot(ScenePath) ?? "nothing held"})");
            }

            TestFolders.Write(folder, "undo.txt", "undo\n");
            Ui.WaitFor(() => said.Any(l => l.Contains("TEST: undid Passenger: add Score", StringComparison.Ordinal)), () => $"Godot's undo of the node added (Godot said: {Said()})");
            read = agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "read" });
            Assert.Null(Node(read, "Ui/Score"));
            Assert.Equal("\"Go\"", Stored(read, "Ui/Press", "text"));
            TestFolders.Write(folder, "undo.txt", "undo\n");
            Ui.WaitFor(() => said.Any(l => l.Contains("TEST: undid Passenger: set text of Press", StringComparison.Ordinal)), () => $"Godot's undo of the property set (Godot said: {Said()})");
            Assert.Equal("\"Press\"", Stored(agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "read" }), "Ui/Press", "text"));
            Assert.DoesNotContain(said, l => l.Contains("SCRIPT ERROR", StringComparison.Ordinal));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>An agent's godot_editor selects a node unasked, the selection read back; moves a node, connects a signal and
    /// removes a node, each one step of Godot's undo, which takes each back in turn, the node moved back with its owner and
    /// connection; and plays the scene in the editor, the game debugged here.</summary>
    [AvaloniaFact]
    public void An_agents_godot_editor_selects_moves_connects_removes_and_plays_the_scene_debugged_here()
    {
        MainWindow window = Opened();
        using Agent agent = new();
        try
        {
            window.ApplySettings(window.Settings with { McpServer = true });
            StartEditor([]);
            Ui.WaitFor(() => window.GodotEditorsConnected == 1, () => $"Godot's editor to connect through the addon ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));
            McpClient client = agent.Attach();
            agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "select", ["scene"] = "res://main.tscn", ["node"] = "Ui/Press" });
            JsonElement read = agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "read" });
            Assert.Equal(["Ui/Press"], read.GetProperty("selected").EnumerateArray().Select(n => n.GetString()));

            agent.Asked(window, client, McpTools.GodotEditor, "act in Godot's editor", new() { ["action"] = "add", ["node"] = ".", ["type"] = "Panel", ["name"] = "Box" });
            agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "move", ["node"] = "Ui/Press", ["to"] = "Box", ["index"] = 0 });
            JsonElement connected = agent.Unasked(window, client, McpTools.GodotEditor,
                new() { ["action"] = "connect", ["node"] = "Box/Press", ["signal"] = "pressed", ["to"] = ".", ["method"] = "OnPressed" });
            Assert.Contains("has no method OnPressed yet", connected.GetProperty("said").GetString(), StringComparison.Ordinal);
            read = agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "read" });
            Assert.Null(Node(read, "Ui/Press"));
            Assert.Equal("\"Press\"", Stored(read, "Box/Press", "text"));
            Assert.Equal(["Box/Press toggled → . OnToggled", "Box/Press pressed → . OnPressed"], Connections(read));
            agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "remove", ["node"] = "Box" });
            Assert.Null(Node(agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "read" }), "Box"));

            foreach (string undone in (string[])["remove Box", "connect pressed to OnPressed", "move Press"])
            {
                TestFolders.Write(folder, "undo.txt", "undo\n");
                Ui.WaitFor(() => said.Any(l => l.Contains($"TEST: undid Passenger: {undone}", StringComparison.Ordinal)), () => $"Godot's undo of {undone} (Godot said: {Said()})");
            }
            read = agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "read" });
            Assert.Equal("\"Press\"", Stored(read, "Ui/Press", "text"));
            Assert.NotNull(Node(read, "Box"));
            Assert.Equal(["Ui/Press toggled → . OnToggled"], Connections(read));

            JsonElement played = agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "play" });
            Assert.Contains("plays in Godot's editor, debugged here", Text(played), StringComparison.Ordinal);
            Assert.True(window.Session.Game is { Relayed: true }, $"The game played is not relayed here: {Games(window)}");
            Assert.DoesNotContain(said, l => l.Contains("SCRIPT ERROR", StringComparison.Ordinal));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>An agent's godot_editor moves a node another's NodePath leads to, the path written again in the same step of
    /// Godot's undo, one undo putting both back; and its read of an instance lists only what the scene instancing it
    /// changes, not what the scene it instances sets.</summary>
    [AvaloniaFact]
    public void An_agents_move_rewrites_the_node_paths_to_the_node_and_its_read_of_an_instance_lists_only_its_own_changes()
    {
        MainWindow window = Opened();
        using Agent agent = new();
        try
        {
            TestFolders.Write(folder, "badge.tscn", "[gd_scene format=3]\n\n[node name=\"Badge\" type=\"Label\"]\ntext = \"Badge\"\nhorizontal_alignment = 1\n");
            window.ApplySettings(window.Settings with { McpServer = true });
            StartEditor([]);
            Ui.WaitFor(() => window.GodotEditorsConnected == 1, () => $"Godot's editor to connect through the addon ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));
            McpClient client = agent.Attach();
            agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "open", ["scene"] = "main.tscn" });
            agent.Asked(window, client, McpTools.GodotEditor, "act in Godot's editor", new() { ["action"] = "add", ["node"] = ".", ["type"] = "Label", ["name"] = "Score" });
            agent.Unasked(window, client, McpTools.GodotEditor,
                new() { ["action"] = "set", ["node"] = "Ui/Press", ["properties"] = new Dictionary<string, object?> { ["focus_neighbor_bottom"] = "../../Score" } });
            Assert.Equal("NodePath(\"../../Score\")", Stored(agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "read" }), "Ui/Press", "focus_neighbor_bottom"));

            agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "move", ["node"] = "Score", ["to"] = "Ui" });
            JsonElement read = agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "read" });
            Assert.NotNull(Node(read, "Ui/Score"));
            Assert.Equal("NodePath(\"../Score\")", Stored(read, "Ui/Press", "focus_neighbor_bottom"));
            TestFolders.Write(folder, "undo.txt", "undo\n");
            Ui.WaitFor(() => said.Any(l => l.Contains("TEST: undid Passenger: move Score", StringComparison.Ordinal)), () => $"Godot's undo of the move (Godot said: {Said()})");
            read = agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "read" });
            Assert.NotNull(Node(read, "Score"));
            Assert.Equal("NodePath(\"../../Score\")", Stored(read, "Ui/Press", "focus_neighbor_bottom"));

            // A remove empties the paths to the node, as Godot's own delete does, which its file then stores none of.
            agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "remove", ["node"] = "Score" });
            read = agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "read" });
            Assert.Null(Node(read, "Score"));
            Assert.False(Node(read, "Ui/Press")!.Value.GetProperty("properties").TryGetProperty("focus_neighbor_bottom", out JsonElement left),
                $"The path to the node removed was left as {left}");
            TestFolders.Write(folder, "undo.txt", "undo\n");
            Ui.WaitFor(() => said.Any(l => l.Contains("TEST: undid Passenger: remove Score", StringComparison.Ordinal)), () => $"Godot's undo of the remove (Godot said: {Said()})");
            read = agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "read" });
            Assert.NotNull(Node(read, "Score"));
            Assert.Equal("NodePath(\"../../Score\")", Stored(read, "Ui/Press", "focus_neighbor_bottom"));

            agent.Unasked(window, client, McpTools.GodotEditor,
                new() { ["action"] = "add", ["node"] = ".", ["type"] = "res://badge.tscn", ["name"] = "Badge", ["properties"] = new Dictionary<string, object?> { ["text"] = "Mine" } });
            JsonElement badge = Node(agent.Unasked(window, client, McpTools.GodotEditor, new() { ["action"] = "read" }), "Badge")!.Value;
            Assert.Equal("res://badge.tscn", badge.GetProperty("instance").GetString());
            Assert.Equal(["text"], badge.GetProperty("properties").EnumerateObject().Select(p => p.Name));
            Assert.Equal("\"Mine\"", badge.GetProperty("properties").GetProperty("text").GetString());
            Assert.DoesNotContain(said, l => l.Contains("SCRIPT ERROR", StringComparison.Ordinal));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>The connections of the edited scene godot_editor read.</summary>
    private static IEnumerable<string> Connections(JsonElement read) =>
        read.TryGetProperty("connections", out JsonElement connections) ? connections.EnumerateArray().Select(c => c.GetString()!) : [];

    private static string Text(JsonElement answer) => answer.ValueKind == JsonValueKind.String ? answer.GetString()! : answer.GetRawText();

    /// <summary>A node of the edited scene godot_editor read, by its path.</summary>
    private static JsonElement? Node(JsonElement read, string path) =>
        read.GetProperty("nodes").EnumerateArray().Where(n => n.GetProperty("path").GetString() == path).Select(n => (JsonElement?)n).FirstOrDefault();

    /// <summary>A property of a node of the edited scene, as its file would store it.</summary>
    private string? Stored(JsonElement read, string path, string property) =>
        Node(read, path) is { } node && node.TryGetProperty("properties", out JsonElement stored) && stored.TryGetProperty(property, out JsonElement value) ? value.GetString()
        : throw new InvalidOperationException($"The edited scene read holds no {property} of {path}: {read.GetRawText()}; Godot said: {Said()}");

    /// <summary>An agent attached to the test's window, on the SDK's own client, as the MCP tests' agents are.</summary>
    private sealed class Agent : McpRig
    {
        public McpClient Attach() => Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");

        public Task<CallToolResult> Answer(McpClient client, [IdentifierOf(typeof(McpTools))] string tool, Dictionary<string, object?> arguments) =>
            Waiting.Apart(() => Result(client, tool, arguments));

        public new T Await<T>(Task<T> task, string what) => base.Await(task, what);

        public string Words(CallToolResult result) => Text(result);

        public new JsonElement Asked(MainWindow window, McpClient client, [IdentifierOf(typeof(McpTools))] string tool, string what, Dictionary<string, object?>? arguments = null) =>
            base.Asked(window, client, tool, what, arguments);

        public new JsonElement Unasked(MainWindow window, McpClient client, [IdentifierOf(typeof(McpTools))] string tool, Dictionary<string, object?>? arguments = null) =>
            base.Unasked(window, client, tool, arguments);
    }

    /// <summary>Open in Godot Editor, with the editor running through the addon: on the script, its scene's node it is on; on
    /// the node path at the caret, the node it names; in the scene, the node whose section the caret is in; each shown in
    /// that one editor, no second one started, and the window here staying on the file it was on.</summary>
    [AvaloniaFact]
    public void Open_in_Godot_Editor_shows_the_scripts_node_the_caret_paths_node_and_the_scenes_in_the_editor_running()
    {
        MainWindow window = Opened();
        try
        {
            StartEditor([]);
            Ui.WaitFor(() => window.GodotEditorsConnected == 1, () => $"Godot's editor to connect through the addon ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));
            CodeEditor code = window.EditorArea.CodeEditor;
            string script = window.EditorArea.Active!.Path;
            code.GoTo(new TextPosition(1, 1));
            Assert.Null(window.Actions.Run(AppActions.GodotOpenInEditor));
            Ui.WaitFor(() => said.Any(l => l.Contains("TEST: selection res://main.tscn .", StringComparison.Ordinal)),
                () => $"Godot's editor to show the script's node ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(1));

            Ui.PlaceAfter(code, "GetNode<Button>(\"Ui/Pr");
            Assert.Null(window.Actions.Run(AppActions.GodotOpenInEditor));
            Ui.WaitFor(() => said.Any(l => l.Contains("TEST: selection res://main.tscn Ui/Press", StringComparison.Ordinal)),
                () => $"Godot's editor to show the node the caret's path names ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(1));
            Assert.Contains("shows Ui/Press in main.tscn", window.Status, StringComparison.Ordinal);
            Ui.Settle();
            Assert.True(PathNames.Same(window.EditorArea.Active!.Path, script), $"The window left the script for {window.EditorArea.Active.Path}, following what it asked Godot to select");

            window.OpenFile(ScenePath);
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "[node name=\"Ui\" type=");
            Assert.Null(window.Actions.Run(AppActions.GodotOpenInEditor));
            Ui.WaitFor(() => said.Any(l => l.Contains("TEST: selection res://main.tscn Ui", StringComparison.Ordinal) && !l.Contains("Ui/", StringComparison.Ordinal)),
                () => $"Godot's editor to show the scene's node at the caret ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(1));
            Assert.Equal(1, EditorsOn(folder));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Open in Godot Editor in a GDScript, its paths read by Passenger's GDScript parser: on <c>$Panel/Button</c> and
    /// on <c>%Button</c>, the button each names in the scene the script is attached in, selected in the editor running.</summary>
    [AvaloniaFact]
    public void Open_in_Godot_Editor_on_a_gdscripts_dollar_path_and_unique_name_shows_the_node_each_names()
    {
        MainWindow window = Opened(files:
        [
            ("hud.gd", "extends Control\n\n\nfunc _ready() -> void:\n\tvar button := $Panel/Button\n\tvar unique := %Button\n\tprint(button, unique)\n"),
            ("hud.tscn", "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://hud.gd\" id=\"1\"]\n\n[node name=\"Hud\" type=\"Control\"]\n"
                + "script = ExtResource(\"1\")\n\n[node name=\"Panel\" type=\"Panel\" parent=\".\"]\n\n[node name=\"Button\" type=\"Button\" parent=\"Panel\"]\nunique_name_in_owner = true\n"),
        ]);
        try
        {
            StartEditor([]);
            Ui.WaitFor(() => window.GodotEditorsConnected == 1, () => $"Godot's editor to connect through the addon ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));
            window.OpenFile(Path.Combine(folder, "hud.gd"));
            foreach (string after in (string[])["$Panel/But", "%But"])
            {
                said.Clear();
                Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
                Assert.Null(window.Actions.Run(AppActions.GodotOpenInEditor));
                Ui.WaitFor(() => said.Any(l => l.Contains("TEST: selection res://hud.tscn Panel/Button", StringComparison.Ordinal)),
                    () => $"Godot's editor to show the button {after} names ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(1));
            }
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Open in Godot Editor with no editor open: the folder's Godot Editor configuration starts one on the scene the
    /// caret's node path leads to, Godot's command line naming the scene alone, and the addon there, once it connects,
    /// selects the node.</summary>
    [AvaloniaFact]
    public void Open_in_Godot_Editor_with_none_open_starts_one_on_the_scene_and_the_addon_there_selects_the_node()
    {
        MainWindow window = Opened();
        try
        {
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "GetNode<Button>(\"Ui/Pr");
            Assert.Null(window.Actions.Run(AppActions.GodotOpenInEditor));
            Ui.WaitFor(() => window.Status?.Contains("where Passenger's addon selects Ui/Press once it is up", StringComparison.Ordinal) == true,
                () => $"the action to say the editor starts on the scene ({window.Status})", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => window.GodotEditorsConnected == 1, () => $"the editor started to connect through the addon ({window.Status}; {RunText(window)})", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => RunText(window).Contains("TEST: selection res://main.tscn Ui/Press", StringComparison.Ordinal),
                () => $"the addon to select the node once connected ({window.Status}; {RunText(window)})", TimeSpan.FromMinutes(1));
            Assert.Equal(1, EditorsOn(folder));
        }
        finally
        {
            End(window);
        }
    }

    private static string RunText(MainWindow window)
    {
        window.BottomPanel.Run.Flush();
        return window.BottomPanel.Run.Document.Snapshot.ToString();
    }

    /// <summary>C# script templates written for Godot in a project with a root namespace and file-scoped namespaces: Godot's own
    /// Attach Script dialog in a headless editor makes a Control's script from Passenger's template and attaches it to the
    /// scene's Ui, the script in the project's namespace as the project writes its C#, and the project builds with no warning;
    /// the project's .editorconfig changed, the window offers to write them again, writing nothing until that is taken.</summary>
    [AvaloniaFact]
    public void Csharp_script_templates_for_godot_give_a_script_its_dialog_makes_the_projects_namespace_and_style()
    {
        // The solution beside the project, as Godot makes one with its game: without it, Godot writes both again as it makes a C# script.
        MainWindow window = Opened(addon: false, files:
        [
            (".editorconfig", "root = true\n\n[*.cs]\nindent_style = space\nindent_size = 4\ncsharp_style_namespace_declarations = file_scoped:warning\n"),
            ("Spinner.sln", "Microsoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio 2012\nProject(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"Spinner\", "
                + "\"Spinner.csproj\", \"{2B1D8A1E-6E43-4C4A-9F5B-3F0E1C2D4A5B}\"\nEndProject\nGlobal\nEndGlobal\n"),
        ]);
        try
        {
            string csproj = Path.Combine(folder, "Spinner.csproj");
            TestFolders.Write(folder, "Spinner.csproj", File.ReadAllText(csproj).Replace("<EnableDynamicLoading>", "<RootNamespace>Spin.Game</RootNamespace>\n    <EnableDynamicLoading>", StringComparison.Ordinal));
            Ui.WaitFor(() => window.Projects.Any(p => p.Property(Passenger.Host.CarriedProperties.RootNamespace) == "Spin.Game"),
                () => $"the project's evaluation to take its root namespace ({window.Status})", TimeSpan.FromMinutes(2));
            window.Confirm = (title, _, _) => Task.FromResult(title == "Write C# Script Templates for Godot?");
            Assert.Null(window.Actions.Run(AppActions.GodotScriptTemplates));
            string template = Path.Combine(folder, GodotScriptTemplates.Folder, "Control", GodotScriptTemplates.FileName);
            Ui.WaitFor(() => File.Exists(template), () => $"the templates to be written ({window.Status})", TimeSpan.FromMinutes(1));
            Assert.All(GodotScriptTemplates.Bases, b => Assert.True(File.Exists(GodotScriptTemplates.PathOf(folder, b)), $"No template was written for {b}"));
            Assert.Contains("namespace Spin.Game;", File.ReadAllText(template), StringComparison.Ordinal);
            Assert.Contains("<Compile Remove=\"script_templates/**\" />", File.ReadAllText(csproj), StringComparison.Ordinal);

            StartEditor([]);
            Ui.WaitFor(() => said.Any(l => l.Contains("TEST: ready", StringComparison.Ordinal)), () => $"the test's plugin in Godot's editor (Godot said: {Said()})", TimeSpan.FromMinutes(2));
            TestFolders.Write(folder, "attach.txt", "Control res://Panel.cs Ui\n");
            Ui.WaitFor(() => said.Any(l => l.Contains("TEST: attached res://Panel.cs", StringComparison.Ordinal)), () => $"Godot's dialog to make and attach the script (Godot said: {Said()})", TimeSpan.FromMinutes(1));
            string made = File.ReadAllText(Path.Combine(folder, "Panel.cs"));
            Assert.True(made.Contains("namespace Spin.Game;\n", StringComparison.Ordinal) && made.Contains("public partial class Panel : Control\n{\n    public override void _Ready()\n", StringComparison.Ordinal),
                $"The script Godot made from the template is not in the project's namespace and style:\n{made}\nGodot said: {Said()}");
            Assert.Contains("res://Panel.cs", File.ReadAllText(ScenePath), StringComparison.Ordinal);
            ChildResult built = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["build", csproj, "-nologo", "-v:q"]), limit: Restore.Limit.Scaled())),
                "the game's build", Restore.Limit + TestFolders.Spare);
            Assert.True(built.ExitCode == 0 && built.Output.Contains(" 0 Warning(s)", StringComparison.Ordinal) && !built.Output.Contains(": warning ", StringComparison.Ordinal), $"The game built with the script made from the template said:\n{built.Output}\nits project:\n{File.ReadAllText(csproj)}");

            // The project's style changed: the window offers to write the templates again, leaving them as they were until asked.
            TestFolders.Write(folder, ".editorconfig", "root = true\n\n[*.cs]\nindent_style = tab\ncsharp_style_namespace_declarations = file_scoped:warning\n");
            const string Offer = "Passenger's C# script templates for Godot no longer fit the project's style";
            Ui.WaitFor(() => window.Status == Offer, () => $"the offer to write the templates in the project's new style ({window.Status})", TimeSpan.FromMinutes(1));
            Assert.True(File.ReadAllText(template).Contains("// meta-space-indent: 0", StringComparison.Ordinal), "The templates were written in the new style unasked");
            Assert.Equal("write them again", window.StatusPlace?.Name);
            window.StatusPlace!.Go();
            Ui.WaitFor(() => File.ReadAllText(template).Contains("\tpublic override void _Ready()", StringComparison.Ordinal),
                () => $"the templates to be written again with tabs ({window.Status}):\n{File.ReadAllText(template)}", TimeSpan.FromMinutes(1));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>How many Godot editors run on a folder: processes started with --editor that name it, but Windows' console
    /// wrapper, which starts the editor itself with the same words.</summary>
    private static int EditorsOn(string project)
    {
        int found = 0;
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId && DotnetProcesses.Describe(process.Id) is { } running
                    && running.CommandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("--editor")
                    && running.CommandLine.Contains(project, StringComparison.Ordinal) && !running.CommandLine.Contains("_console.exe", StringComparison.OrdinalIgnoreCase))
                {
                    found++;
                }
            }
        }
        return found;
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
        start.Environment[GodotAddonFiles.TraceVariable] = "1";
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        editor = ChildProcesses.Start(start);
        TestGodotEditors.Began(editor.Id);
        UnawaitedWork.Dismiss(ChildProcesses.Hear(editor, said.Enqueue, said.Enqueue));
    }

    private string Said() => string.Join(" | ", said.TakeLast(60));

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
