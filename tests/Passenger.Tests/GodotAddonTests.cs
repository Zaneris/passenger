using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
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
        if (editor is { } running)
        {
            ChildProcesses.Kill(running);
            running.Dispose();
        }
        TestFolders.Delete(folder);
    }

    private const string Script = """
        using Godot;

        public partial class Main : Node
        {
            public override void _Ready()
            {
                int answer = 6 * 7;
                GD.Print($"ready {answer}");
                GD.PushError("Spinner fell over");
                System.IO.File.WriteAllText(ProjectSettings.GlobalizePath("res://ready.txt"), $"ready {answer}");
            }
        }

        """;

    private const string Scene = """
        [gd_scene load_steps=2 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1"]

        [node name="Main" type="Node"]
        script = ExtResource("1")

        """;

    /// <summary>The test's own plugin: Godot's Play, as its Play button runs it, each time the test asks (a file it writes),
    /// once Passenger's addon has connected, and Godot's Stop once the test asks for that.</summary>
    private const string Player = """
        @tool
        extends EditorPlugin


        func _process(_delta: float) -> void:
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
    private MainWindow Opened()
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
        window.Confirm = (_, _, _) => Task.FromResult(true);
        Assert.Null(window.Actions.Run(AppActions.GodotAddon));
        Ui.WaitFor(() => window.Status?.Contains("Passenger's addon is installed", StringComparison.Ordinal) == true, () => $"the addon to be installed ({window.Status})");
        string enabled = File.ReadAllText(project);
        Assert.True(File.Exists(Path.Combine(folder, "addons", "passenger", "passenger.gd")) && File.Exists(Path.Combine(folder, "addons", "passenger", "plugin.cfg"))
            && enabled.Contains("enabled=PackedStringArray(\"res://addons/plays/plugin.cfg\", \"res://addons/passenger/plugin.cfg\")", StringComparison.Ordinal),
            $"The addon's files were to be written and enabled beside the test's plugin:\n{enabled}");
        window.OpenFile(Path.Combine(folder, "Main.cs"));
        return window;
    }

    private static void End(MainWindow window)
    {
        if (window.Debugger.IsDebugging)
        {
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugger to let the game go");
        }
        window.Close();
    }

    /// <summary>A headless Godot editor on the project, its user folders the run's and Passenger's state the IDE's, with the
    /// variables given, which the games it plays inherit; on Windows through Godot's console wrapper, as its own .exe writes
    /// to no console.</summary>
    private void StartEditor(IReadOnlyList<(string Name, string Value)> variables)
    {
        ProcessStartInfo start = ChildProcesses.Command(GodotExecutables.InTerminal(godot!.Executable), ["--headless", "--editor", "--path", folder], folder);
        foreach ((string name, string value) in GodotTests.Game.Home(Path.Combine(folder, "..", Path.GetFileName(folder) + "-home")).Select(v => (v.Key, v.Value)).Concat(variables))
        {
            start.Environment[name] = value;
        }
        start.Environment[Passenger.App.State.StateStore.Variable] = Passenger.App.State.StateStore.DefaultRoot;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        editor = ChildProcesses.Start(start);
        UnawaitedWork.Dismiss(ChildProcesses.Hear(editor, said.Enqueue, said.Enqueue));
    }

    private string Said() => string.Join(" | ", said.TakeLast(40));

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
