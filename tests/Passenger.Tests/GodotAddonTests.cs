using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Passenger's addon in a headless Godot editor on a small C# game of the test's own: installed by its action into
/// the project and enabled there, it connects the editor to the window, and the game played as Godot's Play plays it
/// (EditorInterface.play_main_scene, from a plugin of the test's in the same editor, never a window) is built here and
/// stops at a breakpoint in its first scene's _Ready, held at its start until the debugger attached; Stop lets it go on
/// in Godot. A build error stops the next Play, the error in Problems. Godot runs headless, its user folders the run's.
/// Without a Godot 4 .NET build each test skips saying why. Debugged, so run alone.</summary>
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
                System.IO.File.WriteAllText(ProjectSettings.GlobalizePath("res://ready.txt"), $"ready {answer}");
                GetTree().Quit();
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
    /// once Passenger's addon has connected.</summary>
    private const string Player = """
        @tool
        extends EditorPlugin


        func _process(_delta: float) -> void:
        	if not FileAccess.file_exists("res://play.txt") or EditorInterface.is_playing_scene():
        		return
        	for plugin in get_parent().get_children():
        		if plugin.get_script() != null and plugin.get_script().resource_path == "res://addons/passenger/passenger.gd" and plugin.get("welcomed"):
        			DirAccess.remove_absolute(ProjectSettings.globalize_path("res://play.txt"))
        			print("TEST: playing")
        			EditorInterface.play_main_scene()
        			return

        """;

    [AvaloniaFact]
    public void Godots_Play_through_the_addon_builds_here_stops_at_a_breakpoint_in_Ready_and_a_build_error_stops_it_in_Problems()
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
        try
        {
            window.Confirm = (_, _, _) => Task.FromResult(true);
            Assert.Null(window.Actions.Run(AppActions.GodotAddon));
            Ui.WaitFor(() => window.Status?.Contains("Passenger's addon is installed", StringComparison.Ordinal) == true,
                () => $"the addon to be installed ({window.Status})");
            string enabled = File.ReadAllText(project);
            Assert.True(File.Exists(Path.Combine(folder, "addons", "passenger", "passenger.gd")) && File.Exists(Path.Combine(folder, "addons", "passenger", "plugin.cfg"))
                && enabled.Contains("enabled=PackedStringArray(\"res://addons/plays/plugin.cfg\", \"res://addons/passenger/plugin.cfg\")", StringComparison.Ordinal),
                $"The addon's files were to be written and enabled beside the test's plugin:\n{enabled}");

            window.OpenFile(Path.Combine(folder, "Main.cs"));
            CodeEditor code = window.EditorArea.CodeEditor;
            int line = Line(code, "int answer = 6 * 7;");
            code.GoTo(new TextPosition(line, 13));
            Ui.Run(window, AppActions.ToggleBreakpoint);

            StartEditor();
            Ui.WaitFor(() => window.GodotEditorsConnected == 1, () => $"Godot's editor to connect through the addon ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(2));

            TestFolders.Write(folder, "play.txt", "play\n");
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && code.LineMarks.Any(m => m.Kind == LineMarkKind.ExecutionPoint && m.Line == line),
                () => $"the game Godot's Play started to stop at the breakpoint in _Ready ({window.Debugger.Session?.State}; {window.Status}; Godot said: {Said()})",
                TimeSpan.FromMinutes(3));
            Assert.Equal("Spinner in Godot", window.Debugger.Tabs.Single(t => t.IsLive).Name);
            string ready = Path.Combine(folder, "ready.txt");
            Assert.False(File.Exists(ready), "The game went past its breakpoint before the debugger let it go on");
            // Stop lets the game go, Godot's to end: it goes on from the breakpoint, writes its answer and quits itself.
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging && File.Exists(ready),
                () => $"Stop to let the game go on, write its answer and end ({window.Status}; Godot said: {Said()})", TimeSpan.FromMinutes(1));
            Assert.Equal("ready 42", File.ReadAllText(ready));

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
            if (window.Debugger.IsDebugging)
            {
                Ui.Run(window, AppActions.Stop);
                Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugger to let the game go");
            }
            window.Close();
        }
    }

    /// <summary>A headless Godot editor on the project, its user folders the run's and Passenger's state the IDE's; on Windows
    /// through Godot's console wrapper, as its own .exe writes to no console.</summary>
    private void StartEditor()
    {
        ProcessStartInfo start = ChildProcesses.Command(GodotExecutables.InTerminal(godot!.Executable), ["--headless", "--editor", "--path", folder], folder);
        foreach ((string name, string value) in GodotTests.Game.Home(Path.Combine(folder, "..", Path.GetFileName(folder) + "-home")))
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

    private static int Line(CodeEditor editor, string code)
    {
        int index = Array.FindIndex(editor.Document.Snapshot.ToString().Split('\n'), l => l.Contains(code, StringComparison.Ordinal));
        Assert.True(index >= 0, $"The script has no line of {code}");
        return index + 1;
    }
}
