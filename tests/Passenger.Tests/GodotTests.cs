using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Godot 4 game in C# as a person works on it: its project (Godot.NET.Sdk, restored from the packages the Godot
/// found carries, never nuget.org) has run configurations for its game and for Godot's editor; Run starts the game on
/// that Godot, Debug stops at a breakpoint in a script's <c>_Process</c>, a Hot Reload edit reaches the game as it runs,
/// and Profile records it, its script's own time among the samples; Run and Debug take the external console. Godot runs
/// headless, its user folders the run's. Without a Godot 4 .NET build (GODOT, the PATH, Applications) each test skips
/// saying why. Debugged, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotTests : IClassFixture<GodotTests.Game>
{
    private readonly Game game;

    public GodotTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot is null || game.Godot.WhyNot is not null, game.Skipped);
        game.Reset();
    }

    /// <summary>The script: each frame spins a few milliseconds in a loop that calls nothing, which the profiler's sampler can
    /// stop in, every tenth says its greeting (a property the scene can set) and its number, and the twentieth raises an error.</summary>
    private const string Script = """
        using Godot;

        public partial class Main : Node
        {
            private int frames;

            private long spun;

            [Export]
            public string Greeting { get; set; } = "frame";

            public override void _Process(double delta)
            {
                frames++;
                Spin();
                if (frames % 10 == 0)
                {
                    Say(frames);
                }
                if (frames == 20)
                {
                    GD.PushError("Spinner fell over");
                }
            }

            private void Say(int frames)
            {
                GD.Print($"{Greeting} {frames}");
            }

            private void Spin()
            {
                long sum = 0;
                for (int i = 0; i < 2000000; i++)
                {
                    sum += i ^ frames;
                }
                spun = sum;
            }
        }

        """;

    private const string Scene = """
        [gd_scene load_steps=2 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1"]

        [node name="Main" type="Node"]
        script = ExtResource("1")

        """;

    private string ScriptPath => Path.Combine(PathNames.Canonical(game.Folder), "Main.cs");

    /// <summary>The project's generated configurations are its game and Godot's editor on it, each on the Godot found with
    /// --path naming the project, headless as the window nobody sees runs every Godot; Run of the game builds it and starts it there, its words in the Run console, Apply Hot
    /// Reload Changes saying it runs without Hot Reload; the editor's configuration says what Hot Reload is to Godot's editor.</summary>
    [AvaloniaFact]
    public void Run_starts_the_game_on_Godot_from_its_configuration_beside_one_for_Godots_editor()
    {
        MainWindow window = game.Open(null, languageServer: false);
        try
        {
            string folder = PathNames.Canonical(game.Folder);
            RunConfiguration played = Generated(window, "Spinner");
            RunConfiguration edited = Generated(window, $"Spinner: {RunConfiguration.GodotEditorName}");
            Assert.False(played.GodotEditor, "The game's configuration starts Godot's editor");
            Assert.True(edited.GodotEditor, "The editor's configuration starts the game, not Godot's editor");
            (LaunchPlan? editor, string? problem) = Ui.Await(window.Builds.ResolvePlanAsync(edited, null), "the editor's plan", TimeSpan.FromMinutes(1));
            Assert.True(editor is not null, $"Godot's editor has no plan: {problem}");
            Assert.Equal(game.Godot!.Executable, editor.Program);
            Assert.Equal([GodotExecutables.Headless, "--path", folder, "--editor"], editor.Arguments);
            // A window given no user folders for Godot keeps its Godots in the run's, never the person's.
            IReadOnlyDictionary<string, string> run = Passenger.App.Godot.GodotUserFolders.OfVariable()!;
            Assert.True(run.All(v => editor.Environment.GetValueOrDefault(v.Key) == v.Value),
                $"The generated configuration's Godot editor would keep its settings in the person's user folders: its environment is {string.Join(", ", editor.Environment)}");

            Choose(window, played);
            Ui.Launched(window, window.RunAsync(), "the game's build and start", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => Run(window).Contains("frame 10", StringComparison.Ordinal), () => $"the game to run its script ({Run(window)})", TimeSpan.FromMinutes(1));
            // Passenger listens for the game as its remote debugger, on loopback.
            Assert.Contains($"{game.Godot.Executable} --path {folder} {Passenger.App.Godot.GodotGame.RemoteDebug} tcp://127.0.0.1:", Run(window), StringComparison.Ordinal);
            Assert.Contains(" --headless\n", Run(window), StringComparison.Ordinal);
            Assert.StartsWith("This program runs without Hot Reload: Run it with Hot Reload on", window.Actions.WhyNot(AppActions.ApplyHotReloadChanges, window.Actions.Context()));
            Stop(window);

            RunConfigurationsDialog dialog = window.ShowRunConfigurations();
            Assert.True(dialog.Choose(edited.Name), $"Edit Configurations lists no {edited.Name}");
            Ui.WaitFor(() => dialog.Note.Contains($"Hot Reload: {GodotLaunch.EditorReloads}.", StringComparison.Ordinal),
                () => $"the editor's configuration to say what Hot Reload is to Godot's editor ({dialog.Note})");
            dialog.Close();
            Ui.Settle();
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>The editor's configuration opens the scene at hand in Godot's editor, and for the script at hand the scene
    /// it is attached in.</summary>
    [AvaloniaFact]
    public void Godots_editor_opens_the_scene_at_hand_or_the_one_the_script_at_hand_is_attached_in()
    {
        MainWindow window = game.Open(null, languageServer: false);
        try
        {
            string folder = PathNames.Canonical(game.Folder);
            RunConfiguration edited = Generated(window, $"Spinner: {RunConfiguration.GodotEditorName}");
            foreach (string atHand in (string[])["Main.cs", "main.tscn"])
            {
                Assert.Null(window.OpenFile(Path.Combine(folder, atHand)));
                Ui.Settle();
                (LaunchPlan? editor, string? problem) = Ui.Await(window.Builds.ResolvePlanAsync(edited, null), "the editor's plan", TimeSpan.FromMinutes(1));
                Assert.True(editor?.Arguments.SequenceEqual([GodotExecutables.Headless, "--path", folder, "--editor", "res://main.tscn"]) == true,
                    $"With {atHand} at hand, Godot's editor was to open res://main.tscn; {(editor is null ? $"it has no plan: {problem}" : $"it starts with {string.Join(' ', editor.Arguments)}")}");
            }
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Run with Hot Reload on starts the game held for a quiet debugger, nothing of a debug session shown: an edit to
    /// the method its _Process calls, applied by Apply Hot Reload Changes, is what the game then prints in the Run console.</summary>
    [AvaloniaFact]
    public void Run_with_Hot_Reload_on_applies_an_edit_to_the_running_game_through_a_quiet_debugger()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Choose(window, Generated(window, "Spinner"));
            Ui.Launched(window, window.Builds.RunAsync(hotReload: true), "the game's build and start with Hot Reload on", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => window.Debugger.Running.Any(r => r.Name == "Spinner on Godot") && Run(window).Contains("frame 10", StringComparison.Ordinal),
                () => $"the game to run with the debugger attached for Hot Reload ({window.Status}; {Run(window)})", TimeSpan.FromMinutes(1));
            Assert.Equal("Running Spinner on Godot with Hot Reload: a save applies its changes", window.Status);
            Assert.False(window.Debugger.IsDebugging, "Run with Hot Reload showed a debug session");
            Assert.DoesNotContain("dotnet watch", Run(window), StringComparison.Ordinal);
            Assert.Null(window.Actions.WhyNot(AppActions.ApplyHotReloadChanges, window.Actions.Context()));

            Typing.Replace(window, "{Greeting} {frames}", "tick {frames}");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.Debugger.LastApplied is not null, "Hot Reload to say what it applied");
            Assert.True(window.Debugger.LastApplied!.Result == HotReloadResult.Applied, $"The edit to the game's script was not applied: {window.Debugger.LastApplied.Said}");
            Ui.WaitFor(() => Run(window).Contains("tick ", StringComparison.Ordinal), () => $"the game to print what the edited method prints ({Run(window)})", TimeSpan.FromMinutes(1));
            Assert.False(window.Debugger.IsDebugging, "Hot Reload's apply showed a debug session");
            Stop(window);
            Ui.WaitFor(() => window.Debugger.Running.Count == 0, "the quiet session to end with the game");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Debug of the game stops at a breakpoint in its script's _Process, Godot having loaded the script's assembly
    /// itself; an edit to the script applied by Hot Reload is what the game prints once it goes on.</summary>
    [AvaloniaFact]
    public void A_breakpoint_in_Process_stops_the_game_and_a_Hot_Reload_edit_reaches_it_as_it_runs()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Choose(window, Generated(window, "Spinner"));
            CodeEditor editor = window.EditorArea.CodeEditor;
            int line = LineOf(editor, "frames++;");
            editor.GoTo(new TextPosition(line, 9));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the game's build and start under the debugger", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && editor.LineMarks.Any(m => m.Kind == LineMarkKind.ExecutionPoint && m.Line == line),
                () => $"the game to stop at the breakpoint in _Process ({window.Debugger.Session?.State}, {Ui.Said(window)}; {window.Debugger.Output()})", TimeSpan.FromMinutes(1));

            editor.GoTo(new TextPosition(line, 9));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Typing.Replace(window, "{Greeting} {frames}", "tick {frames}");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.Debugger.LastApplied is not null, "Hot Reload to say what it applied");
            Assert.True(window.Debugger.LastApplied!.Result == HotReloadResult.Applied, $"The edit to the game's script was not applied: {window.Debugger.LastApplied.Said}");
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => window.Debugger.Output().ToString().Contains("tick", StringComparison.Ordinal),
                () => $"the game to print what the edited script prints ({window.Debugger.Output()})", TimeSpan.FromMinutes(1));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A debugged game that ends is shown ended in the Debug window as it ends: its session over, and nothing shown
    /// debugged, while its debugger has yet to go, which it does after.</summary>
    [AvaloniaFact]
    public void A_debugged_game_that_ends_is_shown_ended_at_once_while_its_debugger_has_yet_to_go()
    {
        MainWindow window = game.Open(null, languageServer: false);
        try
        {
            Choose(window, Generated(window, "Spinner"));
            Ui.Launched(window, window.DebugAsync(), "the game's build and start under the debugger", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => window.Session.Games is [{ State: Passenger.App.LiveTree.LiveAppState.Connected, ProcessId: int }]
                && window.Debugger.SessionNamed("Spinner on Godot") is { State: DebugState.Running },
                () => $"the game to run under the debugger, connected ({Debugged(window)}; {Ui.Said(window)})", TimeSpan.FromMinutes(1));
            DebugSession session = window.Debugger.SessionNamed("Spinner on Godot")!;
            int adapter = session.AdapterProcessId;
            int played = window.Session.Games[0].ProcessId!.Value;
            // Whether the debugger still ran as the session was shown ended, read as it was.
            bool? debuggerRan = null;
            session.StateChanged += (_, _) =>
            {
                if (session.State == DebugState.Ended)
                {
                    debuggerRan ??= Processes.IsRunning(adapter);
                }
            };
            Processes.Kill(played);
            Ui.WaitFor(() => session.State == DebugState.Ended, () => $"the game's session to end with the game ({Debugged(window)}; {Running([("Spinner", played)])})",
                TimeSpan.FromMinutes(1));
            Assert.True(debuggerRan == true, "The game's session was shown ended only once its debugger had gone, not as the game ended");
            Assert.False(window.Debugger.IsDebugging, $"Something was still shown debugged once the game had ended: {Debugged(window)}");
            Ui.WaitFor(() => session.AdapterEnded.IsCompleted, "the game's debugger to go after", TimeSpan.FromMinutes(1));
            Assert.True(Processes.Gone(adapter), $"The game's debugger did not go once its session ended: {Processes.Describe(adapter)}");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A debugged game that quits by itself (its GDScript's get_tree().quit(), its tree's exit held a moment, so
    /// Godot's teardown outlasts what its .NET does) ends its session through its debugger's word that its debugging ended,
    /// with the game's own exit code: the session's fallback for an adapter that never says so is not used, and nothing is
    /// left running for Passenger to end.</summary>
    [AvaloniaFact]
    public void A_debugged_game_that_quits_ends_its_session_through_its_debuggers_word_with_its_own_exit_code()
    {
        string quitter = Path.Combine(game.Folder, "quitter.gd");
        File.WriteAllText(quitter, "extends Node\n\nvar frames := 0\n\n\nfunc _process(_delta: float) -> void:\n\tframes += 1\n\tif frames == 60:\n"
            + "\t\tprint(\"quitting\")\n\t\tget_tree().quit()\n\n\nfunc _exit_tree() -> void:\n\tOS.delay_msec(2500)\n");
        File.WriteAllText(Path.Combine(game.Folder, "main.tscn"), """
            [gd_scene load_steps=3 format=3]

            [ext_resource type="Script" path="res://Main.cs" id="1"]
            [ext_resource type="Script" path="res://quitter.gd" id="2"]

            [node name="Main" type="Node"]
            script = ExtResource("1")

            [node name="Quitter" type="Node" parent="."]
            script = ExtResource("2")

            """);
        MainWindow window = game.Open(null, languageServer: false);
        try
        {
            Choose(window, Generated(window, "Spinner"));
            Ui.Launched(window, window.DebugAsync(), "the game's build and start under the debugger", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => window.Debugger.SessionNamed("Spinner on Godot") is not null, () => $"the game's session ({Debugged(window)}; {Ui.Said(window)})", TimeSpan.FromMinutes(1));
            DebugSession session = window.Debugger.SessionNamed("Spinner on Godot")!;
            Ui.WaitFor(() => session.State == DebugState.Ended, () => $"the game to quit and its session to end ({Debugged(window)}; {window.Debugger.Output()})", TimeSpan.FromMinutes(2));
            string said = window.Debugger.Output().ToString();
            Assert.True(said.Contains("quitting", StringComparison.Ordinal), $"The game's session ended before it quit by itself:\n{said}");
            Assert.False(session.FallbackUsed, $"The game's session was ended without its debugger saying its debugging ended:\n{said}");
            Assert.True(session.ExitCode == 0, $"The game's exit code was {session.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "never said"}, not its own 0:\n{said}");
            Assert.False(said.Contains("was left running by the debugger", StringComparison.Ordinal), $"Passenger ended the game as it tore down:\n{said}");
        }
        finally
        {
            End(window);
            File.Delete(quitter);
        }
    }

    /// <summary>Run and Debug with the configuration's external console run the game in a tab of the Terminal window, which
    /// shows what its script prints rather than the Run and Debug windows' consoles (on Windows, where Godot's own .exe writes
    /// to no console, through Godot's console wrapper); under Debug a breakpoint in _Process still stops the game, the
    /// debugger holding Godot's own process; and Stop ends each there, the game with it.</summary>
    [AvaloniaFact]
    public void Run_and_Debug_in_the_external_console_show_what_the_game_prints_in_the_terminal_and_a_breakpoint_still_stops_it()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Choose(window, Generated(window, "Spinner"), ConsoleKind.Terminal);
            Ui.Launched(window, window.RunAsync(), "the game's build and start in the terminal", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => window.RunInTerminal is { } run && Terminal(run).Contains("frame 10", StringComparison.Ordinal),
                () => $"Run's game to print in the terminal ({Ui.Said(window)}; {(window.RunInTerminal is { } run ? Terminal(run) : "no terminal")})", TimeSpan.FromMinutes(1));
            TerminalRun ran = window.RunInTerminal!;
            Assert.DoesNotContain("frame 10", Run(window), StringComparison.Ordinal);
            int? played = window.Session.Games is [{ ProcessId: int id }] ? id : null;
            Stop(window);
            Ui.WaitFor(() => ran.Ended.IsCompleted && window.Session.Games.Count == 0, () => $"Run's game to end in the terminal ({window.Status}; {Running([("Run's", played)])})",
                TimeSpan.FromMinutes(1));
            Assert.True(Processes.Gone(ran.ProcessId) && (played is not int game || Processes.Gone(game)),
                $"Stop left Run's game running in the terminal: {Running([("the terminal's program", ran.ProcessId), ("the game", played)])}");

            Ui.Launched(window, window.DebugAsync(), "the game's build and start in the terminal under the debugger", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => window.Debugger.Session is { Engine: DebugEngine.Managed, Terminal: { } shown } && Terminal(shown).Contains("frame 10", StringComparison.Ordinal),
                () => $"the debugged game to print in the terminal ({Debugged(window)}; {Ui.Said(window)}; {(window.Debugger.Session?.Terminal is { } run ? Terminal(run) : "no terminal")})",
                TimeSpan.FromMinutes(1));
            TerminalRun debugged = window.Debugger.Session!.Terminal!;
            Assert.DoesNotContain("frame 10", window.Debugger.Output().ToString(), StringComparison.Ordinal);
            // The terminal took the keys as it showed the game.
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.Focus();
            int line = LineOf(editor, "frames++;");
            editor.GoTo(new TextPosition(line, 9));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && editor.LineMarks.Any(m => m.Kind == LineMarkKind.ExecutionPoint && m.Line == line),
                () => $"the game in the terminal to stop at the breakpoint in _Process ({Debugged(window)}, {Ui.Said(window)}; {window.Debugger.Output()})", TimeSpan.FromMinutes(1));
            int? held = window.Session.Games is [{ ProcessId: int heldId }] ? heldId : null;
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging && debugged.Ended.IsCompleted && window.Session.Games.Count == 0,
                () => $"the debugged game to end in the terminal with its session ({Debugged(window)}; {window.Status})", TimeSpan.FromMinutes(1));
            Assert.True(Processes.Gone(debugged.ProcessId) && (held is not int stopped || Processes.Gone(stopped)),
                $"Stop left the debugged game running in the terminal: {Running([("the terminal's program", debugged.ProcessId), ("the game", held)])}");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Profile starts the game under the profiler, and its snapshot holds the samples of the script's own loop.</summary>
    [AvaloniaFact]
    public void Profile_records_the_game_its_scripts_loop_among_the_samples()
    {
        MainWindow window = game.Open(null, languageServer: false);
        try
        {
            Choose(window, Generated(window, "Spinner"));
            Ui.Launched(window, window.ProfileAsync(), "the game's build and start under the profiler", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => Run(window).Contains("frame 30", StringComparison.Ordinal), () => $"the profiled game to run its script ({Run(window)})", TimeSpan.FromMinutes(1));
            Passenger.App.Profiling.SnapshotRead read = Ui.Await(window.SnapshotAsync(), "the game's snapshot", TimeSpan.FromMinutes(2));
            Assert.True(read.Snapshot is not null, read.Message);
            Assert.True(read.Snapshot.HotSpots.Any(h => h.Name.Contains("Main.Spin", StringComparison.Ordinal)),
                $"The game's snapshot holds no sample of its script's loop: {string.Join(", ", read.Snapshot.HotSpots.Take(10).Select(h => h.Name))}");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>The GDScript of <see cref="Profile_records_Godots_frames_beside_the_samples_naming_GDScripts_and_CSharps_functions_and_a_memory_snapshot_lists_what_the_script_made"/>:
    /// it makes a C# Ledger as it starts and keeps it, and each frame spins in a function of its own, then has the Ledger spin.</summary>
    private const string Wanderer = "extends Node\n\nvar ledger\nvar total := 0\n\nfunc _ready():\n\tledger = load(\"res://Ledger.cs\").new()\n\n"
        + "func _process(_delta):\n\twander()\n\ttotal += ledger.Tally(400000)\n\nfunc wander():\n\tvar sum := 0\n\tfor i in 60000:\n\t\tsum += i\n\ttotal = sum\n";

    /// <summary>Profile of a game whose GDScript and C# each spin in a function of their own records Godot's frames beside the
    /// samples: a memory snapshot lists the C# Ledger the GDScript made (refused saying why on macOS, where a Godot signed for
    /// release lets no program read its memory), and the Frames page names the GDScript function at its line with its calls
    /// and time, the C# loop the engine called and the C# method GDScript called with their sampled time, says the headless
    /// game drew nothing, and opens the script at the function's line.</summary>
    [AvaloniaFact]
    public void Profile_records_Godots_frames_beside_the_samples_naming_GDScripts_and_CSharps_functions_and_a_memory_snapshot_lists_what_the_script_made()
    {
        string wanderer = Path.Combine(game.Folder, "wander.gd");
        string ledger = Path.Combine(game.Folder, "Ledger.cs");
        File.WriteAllText(wanderer, Wanderer);
        File.WriteAllText(ledger, """
            using Godot;

            public partial class Ledger : RefCounted
            {
                public long Tally(int rounds)
                {
                    long sum = 0;
                    for (int i = 0; i < rounds; i++)
                    {
                        sum += i ^ rounds;
                    }
                    return sum;
                }
            }

            """);
        File.WriteAllText(Path.Combine(game.Folder, "main.tscn"), """
            [gd_scene load_steps=3 format=3]

            [ext_resource type="Script" path="res://Main.cs" id="1"]
            [ext_resource type="Script" path="res://wander.gd" id="2"]

            [node name="Main" type="Node"]
            script = ExtResource("1")

            [node name="Wanderer" type="Node" parent="."]
            script = ExtResource("2")

            """);
        MainWindow window = game.Open(null, languageServer: false);
        try
        {
            Choose(window, Generated(window, "Spinner"));
            Ui.Launched(window, window.ProfileAsync(), "the game's build and start under the profiler", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => Run(window).Contains("frame 300", StringComparison.Ordinal), () => $"the profiled game to run its scripts ({Run(window)})", TimeSpan.FromMinutes(1));

            Passenger.App.Profiling.ProfilerView view = window.ProfilerView;
            Avalonia.Controls.Button take = view.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Content as string == "Get Memory Snapshot");
            // Where only the runtime can write it, into the game's output (Linux's ptrace_scope), that is let.
            window.Confirm = (_, _, _) => Task.FromResult(true);
            Ui.Click(take, settle: false);
            Ui.WaitFor(() => view.Memory is not null || window.Status?.StartsWith("No memory snapshot", StringComparison.Ordinal) == true,
                "the game's memory snapshot", TimeSpan.FromMinutes(2));
            Assert.True(view.Memory is not null, window.Status);
            Assert.True(view.HeapTypes.Any(t => t.Name == "Ledger" && t.Count == 1),
                $"The game's memory snapshot lists no Ledger, which its GDScript made: {string.Join(", ", view.HeapTypes.Take(30).Select(t => $"{t.Name} {t.Count}"))}");
            // A Godot signed for release on macOS lets no program read its memory: its runtime's own heap dump is read, saying so.
            Assert.True((DotnetProcesses.WhyUnreadable(window.Profiling!.ProcessId) is null) == (view.Memory!.Lacks is null),
                $"The snapshot's words do not follow whether the game's memory could be read: {view.Memory.Lacks}");

            Passenger.App.Profiling.SnapshotRead read = Ui.Await(window.SnapshotAsync(), "the game's snapshot", TimeSpan.FromMinutes(2));
            Assert.True(read.Snapshot is { Game: not null, GameFrames.Count: > 100 }, $"{read.Message}: {read.Snapshot?.GameFrames.Count} of Godot's frames, {read.Snapshot?.Game?.Why}");
            view.ShowPage(Passenger.App.Profiling.ProfilerPage.Frames);
            Passenger.App.Profiling.GameFramesView frames = view.GamePage;
            Ui.WaitFor(() => frames.IsEffectivelyVisible && frames.Rows.Count > 0, "the Frames page to show Godot's frames");
            string listed = string.Join("; ", frames.Rows.Select(r => $"{r.Name} {r.Place} {r.Own:0.0} {r.All:0.0} {r.Calls}"));
            // Godot names a function by its body's first line.
            int wander = LineOf(Wanderer, "func wander") + 1;
            Passenger.App.Profiling.GameRow script = frames.Rows.FirstOrDefault(r => r.Function?.Name == "wander")
                ?? throw new Xunit.Sdk.XunitException($"The Frames page names no GDScript wander: {listed}");
            Assert.True(script is { Place: var place, Own: > 0, Calls: > 100 } && place == $"wander.gd:{wander}", $"GDScript's wander reads as {script.Place} {script.Own} ms, {script.Calls} calls: {listed}");
            Assert.True(frames.Rows.Any(r => r.Method?.Name.Contains("Main.Spin", StringComparison.Ordinal) == true && r.Own > 0),
                $"The Frames page names no time of the C# loop the engine called: {listed}");
            Assert.True(frames.Rows.Any(r => r.Method?.Name.Contains("Ledger.Tally", StringComparison.Ordinal) == true && r.All > 0),
                $"The Frames page names no time of the C# method GDScript called: {listed}");
            Assert.Contains("ran headless", frames.Summary, StringComparison.Ordinal);

            Ui.Click(Ui.Row(frames.Table, script));
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.EditorArea.Active?.Path is { } open && PathNames.Same(open, PathNames.Canonical(wanderer)),
                () => $"Enter on wander's row to open wander.gd, where {window.EditorArea.Active?.Path} is open ({window.Status})");
            Assert.Equal(wander - 1, window.EditorArea.CodeEditor.CaretPosition.Line);
        }
        finally
        {
            End(window);
            File.Delete(wanderer);
            File.Delete(ledger);
        }
    }

    /// <summary>The GDScript of <see cref="Profile_puts_GDScripts_and_CSharps_functions_in_one_call_tree_and_opens_the_slowest_frame_on_its_costliest_function"/>:
    /// each frame it spins in wander, then has the C# Ledger spin, which calls back into its crawl.</summary>
    private const string Crawler = "extends Node\n\nvar ledger\nvar total := 0\n\nfunc _ready():\n\tledger = load(\"res://Ledger.cs\").new()\n\n"
        + "func _process(_delta):\n\twander()\n\ttotal += ledger.Tally(200000, self)\n\nfunc wander():\n\tvar sum := 0\n\tfor i in 60000:\n\t\tsum += i\n\ttotal = sum\n\n"
        + "func crawl():\n\tvar sum := 0\n\tfor i in 40000:\n\t\tsum += i\n\treturn sum\n";

    /// <summary>Profile of a game whose GDScript calls C# and whose C# calls GDScript back puts both languages' functions in
    /// the one call tree beside the threads, GDScript's as Godot measured them, each with its own time and opening its script,
    /// and the C# sampled; the Frames page's link opens the slowest frame alone on the call tree, its costliest function chosen.</summary>
    [AvaloniaFact]
    public void Profile_puts_GDScripts_and_CSharps_functions_in_one_call_tree_and_opens_the_slowest_frame_on_its_costliest_function()
    {
        string crawler = Path.Combine(game.Folder, "wander.gd");
        string ledger = Path.Combine(game.Folder, "Ledger.cs");
        File.WriteAllText(crawler, Crawler);
        File.WriteAllText(ledger, """
            using Godot;

            public partial class Ledger : RefCounted
            {
                public long Tally(int rounds, GodotObject back)
                {
                    long sum = 0;
                    for (int i = 0; i < rounds; i++)
                    {
                        sum += i ^ rounds;
                    }
                    return sum + back.Call("crawl").AsInt64();
                }
            }

            """);
        File.WriteAllText(Path.Combine(game.Folder, "main.tscn"), """
            [gd_scene load_steps=3 format=3]

            [ext_resource type="Script" path="res://Main.cs" id="1"]
            [ext_resource type="Script" path="res://wander.gd" id="2"]

            [node name="Main" type="Node"]
            script = ExtResource("1")

            [node name="Wanderer" type="Node" parent="."]
            script = ExtResource("2")

            """);
        MainWindow window = game.Open(null, languageServer: false);
        try
        {
            Choose(window, Generated(window, "Spinner"));
            Ui.Launched(window, window.ProfileAsync(), "the game's build and start under the profiler", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => Run(window).Contains("frame 300", StringComparison.Ordinal), () => $"the profiled game to run its scripts ({Run(window)})", TimeSpan.FromMinutes(1));
            Passenger.App.Profiling.SnapshotRead read = Ui.Await(window.SnapshotAsync(), "the game's snapshot", TimeSpan.FromMinutes(2));
            Assert.True(read.Snapshot is { Game: not null, GameFrames.Count: > 100 }, $"{read.Message}: {read.Snapshot?.GameFrames.Count} of Godot's frames, {read.Snapshot?.Game?.Why}");

            Passenger.App.Profiling.ProfilerView view = window.ProfilerView;
            view.ShowPage(Passenger.App.Profiling.ProfilerPage.CallTree);
            Passenger.Profiler.CallNode scripts = view.Rows.Select(r => r.Node).FirstOrDefault(n => n.Name == Passenger.Profiler.GameProfile.GDScriptRoot)
                ?? throw new Xunit.Sdk.XunitException($"The call tree has no GDScript beside its threads: {string.Join(", ", view.Rows.Where(r => r.Depth == 0).Select(r => r.Node.Name))}");
            string named = string.Join("; ", scripts.Ordered.Select(n => $"{n.Name} {n.Module} {n.Self}/{n.Total}"));
            foreach ((string function, string line) in new[] { ("wander", "func wander"), ("crawl", "func crawl") })
            {
                Assert.True(scripts.Ordered.FirstOrDefault(n => n.Name == function) is { Script: not null, Self: > 0 } node && node.Module == $"wander.gd:{LineOf(Crawler, line) + 1}",
                    $"The call tree's GDScript lacks {function} at its line with its own time: {named}");
            }
            Assert.True(Nodes(view.Rows.Where(r => r.Depth == 0 && r.Node != scripts).Select(r => r.Node)).Any(n => n.Name.Contains("Ledger.Tally", StringComparison.Ordinal) && n.Self > 0),
                "The call tree beside the GDScript names no time of the C# method the GDScript called");

            view.ShowPage(Passenger.App.Profiling.ProfilerPage.Frames);
            Passenger.App.Profiling.GameFramesView frames = view.GamePage;
            Ui.WaitFor(() => frames.IsEffectivelyVisible && frames.FrameLink.IsEffectivelyVisible, "the Frames page's link to the slowest frame");
            Passenger.Profiler.GameFrame slowest = frames.Opened!;
            Assert.Equal(frames.Frames.Max(f => f.FrameMs), slowest.FrameMs);
            Assert.StartsWith("Open the slowest frame's calls in the call tree", frames.FrameLink.Content as string, StringComparison.Ordinal);
            Ui.Click(frames.FrameLink);
            Ui.WaitFor(() => view.Page == Passenger.App.Profiling.ProfilerPage.CallTree && view.Snapshot is { } shown && !ReferenceEquals(shown, shown.Whole) && view.ChosenCall is not null,
                () => $"the slowest frame's calls on the call tree ({view.Page}, {view.ChosenCall?.Node.Name})", TimeSpan.FromMinutes(1));
            Passenger.Profiler.CallNode chosen = view.ChosenCall!.Node;
            Passenger.Profiler.CallNode? costliest = Passenger.App.Profiling.ProfilerView.Costliest(view.Rows.Where(r => r.Depth == 0).Select(r => r.Node));
            Assert.True(costliest is not null && chosen == costliest && (chosen.Script is not null || chosen.Code is not null),
                $"The slowest frame opened on {chosen.Name} ({chosen.Self}), not its costliest function {costliest?.Name} ({costliest?.Self})");
            Assert.True(view.Snapshot!.GameFrames.Any(f => f.Number == slowest.Number), $"The call tree shows frames {string.Join(", ", view.Snapshot.GameFrames.Select(f => f.Number))}, not frame {slowest.Number}");
        }
        finally
        {
            End(window);
            File.Delete(crawler);
            File.Delete(ledger);
        }

        // Every node under some roots, the roots among them.
        static IEnumerable<Passenger.Profiler.CallNode> Nodes(IEnumerable<Passenger.Profiler.CallNode> roots) => roots.SelectMany(r => Nodes(r.Children).Prepend(r));
    }

    /// <summary>Run makes Passenger the game's remote debugger: the Live Visual Tree shows its scene tree, the Main node's
    /// script property typed there is what the game then prints, the error the script raises is in Problems at its line and
    /// linked from Godot's words in the Run console, and the window's status line reads the game's monitors.</summary>
    [AvaloniaFact]
    public void The_running_games_tree_shows_its_nodes_a_property_typed_there_changes_it_its_error_links_to_its_line_and_its_monitors_read()
    {
        MainWindow window = game.Open(null, languageServer: false);
        try
        {
            Choose(window, Generated(window, "Spinner"));
            Ui.Launched(window, window.RunAsync(), "the game's build and start", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => window.LiveTree.Rows.Any(r => r.Element.Name == "Main"),
                () => $"the Live Visual Tree to show the game's nodes ({window.LiveSource?.State}; {string.Join(", ", window.LiveTree.Rows.Select(r => r.Element.Name))})",
                TimeSpan.FromMinutes(1));
            Assert.Contains(window.LiveTree.Rows, r => r.Element.Name == "root" && r.Element.Type == "Window");

            Passenger.App.LiveTree.LiveTreeView tree = window.LiveTree;
            tree.Tree.SelectedItem = tree.Rows.First(r => r.Element.Name == "Main");
            Ui.WaitFor(() => tree.PropertyRows.Any(p => p.Name == "Greeting"), () => $"Main's properties ({tree.Details?.Type}: {string.Join(", ", tree.PropertyRows.Select(p => p.Name))})");
            Assert.Equal("frame", tree.PropertyRows.First(p => p.Name == "Greeting").Value);
            Assert.True(tree.Details!.Source is { } script && PathNames.Same(script.File, ScriptPath), $"Main's link goes to {tree.Details.Source?.File}, not its script");
            tree.PropertyList.SelectedItem = tree.PropertyRows.First(p => p.Name == "Greeting");
            tree.Edit(tree.PropertyRows.First(p => p.Name == "Greeting"));
            Ui.WaitFor(() => tree.Editing is { IsVisible: true }, "the Greeting's value to become a box to type in");
            tree.Editing!.Focus();
            tree.Editing.SelectAll();
            window.KeyTextInput("hello");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => Run(window).Contains("hello ", StringComparison.Ordinal), () => $"the game to print the greeting typed ({Run(window)})", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => tree.PropertyRows.Any(p => p.Name == "Greeting" && p.Value == "hello"), "the Greeting's row to show what the game now holds");

            int raised = LineOf(File.ReadAllText(ScriptPath), "GD.PushError");
            Ui.WaitFor(() => window.BottomPanel.Problems.GodotProblems.Any(p => p.Message == "Spinner fell over"),
                () => $"Problems' Godot tab to list the error ({string.Join("; ", window.BottomPanel.Problems.GodotProblems.Select(p => $"{p.Message} {p.Where}"))})");
            Passenger.App.Run.Problem error = window.BottomPanel.Problems.GodotProblems.First(p => p.Message == "Spinner fell over");
            Assert.True(PathNames.Same(error.Path!, ScriptPath) && error.Position.Line == raised - 1, $"The error is placed at {error.Path}:{error.Position.Line + 1}, not Main.cs:{raised}");
            string at = Run(window).Split('\n').First(l => l.Contains("(res://Main.cs:", StringComparison.Ordinal)).TrimEnd('\r');
            Passenger.App.ToolWindows.FileLink link = Assert.IsType<Passenger.App.ToolWindows.FileLink>(Assert.Single(window.BottomPanel.Run.Links.In(at)).Target);
            Assert.True(PathNames.Same(link.Path, ScriptPath) && link.Position.Line == raised - 1, $"Godot's line '{at}' links to {link.Path}:{link.Position.Line + 1}");

            Ui.WaitFor(() => window.Session.Game?.Monitors is { Objects: > 0, Nodes: >= 2 }, "the game's monitors");
            Ui.WaitFor(() => tree.MonitorsLine?.Contains(" FPS · ", StringComparison.Ordinal) == true, () => $"the Live Visual Tree to read the game's monitors ({tree.MonitorsLine})");
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>The Live Visual Tree follows the game as it changes while it is shown: the node the script renames after five
    /// seconds shows by its new name before the one it adds to the root after fifteen, neither asked for by Refresh, and
    /// the added one shows too.</summary>
    [AvaloniaFact]
    public void The_tree_follows_the_game_a_node_it_renames_and_one_it_adds_showing_without_Refresh()
    {
        File.WriteAllText(ScriptPath, """
            using Godot;

            public partial class Main : Node
            {
                private ulong started;
                private bool renamed;
                private bool added;

                public override void _Ready()
                {
                    started = Time.GetTicksMsec();
                }

                public override void _Process(double delta)
                {
                    ulong now = Time.GetTicksMsec() - started;
                    if (!renamed && now > 5000)
                    {
                        renamed = true;
                        Name = "Renamed";
                    }
                    if (!added && now > 15000)
                    {
                        added = true;
                        GetTree().Root.CallDeferred(Node.MethodName.AddChild, new Node { Name = "Added" });
                    }
                }
            }

            """);
        MainWindow window = game.Open(null, languageServer: false);
        try
        {
            Choose(window, Generated(window, "Spinner"));
            Ui.Launched(window, window.RunAsync(), "the game's build and start", TimeSpan.FromMinutes(3));
            Passenger.App.LiveTree.LiveTreeView tree = window.LiveTree;
            Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Main"),
                () => $"the Live Visual Tree to show the game's Main node ({window.LiveSource?.State}; {string.Join(", ", tree.Rows.Select(r => r.Element.Name))})", TimeSpan.FromMinutes(1));

            Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Renamed"),
                () => $"the node the game renamed to show by its new name ({string.Join(", ", tree.Rows.Select(r => r.Element.Name))})", TimeSpan.FromMinutes(1));
            Assert.True(!tree.Rows.Any(r => r.Element.Name is "Main" or "Added"),
                $"The rename was to show by itself, before the node added ten seconds later: {string.Join(", ", tree.Rows.Select(r => r.Element.Name))}");
            Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Added"),
                () => $"the node the game added to show ({string.Join(", ", tree.Rows.Select(r => r.Element.Name))})", TimeSpan.FromMinutes(1));
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A node's values of several parts are set from the properties pane in the form it shows them: a Node2D's
    /// position (a Vector2 of its transform) and a Transform2D the script exports, each read back as the game then holds it.</summary>
    [AvaloniaFact]
    public void A_nodes_position_and_a_transform_are_set_from_the_properties_pane()
    {
        File.WriteAllText(ScriptPath, """
            using Godot;

            public partial class Main : Node2D
            {
                [Export]
                public Transform2D Placed { get; set; } = Transform2D.Identity;
            }

            """);
        File.WriteAllText(Path.Combine(game.Folder, "main.tscn"), Scene.Replace("type=\"Node\"", "type=\"Node2D\"", StringComparison.Ordinal));
        MainWindow window = game.Open(null, languageServer: false);
        try
        {
            Choose(window, Generated(window, "Spinner"));
            Ui.Launched(window, window.RunAsync(), "the game's build and start", TimeSpan.FromMinutes(3));
            Passenger.App.LiveTree.LiveTreeView tree = window.LiveTree;
            Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Main"),
                () => $"the game's Main node ({string.Join(", ", tree.Rows.Select(r => r.Element.Name))})", TimeSpan.FromMinutes(1));
            tree.Tree.SelectedItem = tree.Rows.First(r => r.Element.Name == "Main");
            Ui.WaitFor(() => tree.PropertyRows.Any(p => p.Name == "position") && tree.PropertyRows.Any(p => p.Name == "Placed"),
                () => $"Main's position and Placed ({string.Join(", ", tree.PropertyRows.Select(p => p.Name))})");

            foreach ((string name, string typed) in (IEnumerable<(string, string)>)[("position", "(100, 50)"), ("Placed", "x: (1, 0), y: (0, 1), origin: (7, 8)")])
            {
                tree.PropertyList.SelectedItem = tree.PropertyRows.First(p => p.Name == name);
                tree.Edit(tree.PropertyRows.First(p => p.Name == name));
                Ui.WaitFor(() => tree.Editing is { IsVisible: true }, () => $"{name}'s value to become a box to type in ({window.Status})");
                tree.Editing!.Focus();
                tree.Editing.SelectAll();
                window.KeyTextInput(typed);
                Ui.Press(window, "ENTER");
                Ui.WaitFor(() => tree.PropertyRows.Any(p => p.Name == name && p.Value == typed),
                    () => $"{name}'s row to show {typed} as the game now holds it ({tree.PropertyRows.FirstOrDefault(p => p.Name == name)?.Value}; {window.Status})");
            }
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Two games launched at once (Debug on the main scene, and again on another) are each a source of the Live Visual Tree:
    /// the chooser offers both, the one started last shown first, and each chosen in turn shows its own nodes.</summary>
    [AvaloniaFact]
    public void Two_games_launched_are_each_a_source_the_Live_Visual_Tree_shows_chosen_in_turn()
    {
        File.WriteAllText(Path.Combine(game.Folder, "other.tscn"), "[gd_scene format=3]\n\n[node name=\"Other\" type=\"Node\"]\n");
        MainWindow window = game.Open(null, languageServer: false);
        IReadOnlyList<(string Name, int? Process)> games = [];
        try
        {
            RunConfiguration generated = Generated(window, "Spinner");
            RunConfiguration main = generated with { Name = "Main on Godot", Generated = false, Arguments = "--headless", Environment = new(game.Homes) };
            RunConfiguration other = main with { Name = "Other on Godot", Arguments = "--headless res://other.tscn", BuildBeforeLaunch = false };
            window.SetConfigurations([main, other], main.Name);
            Passenger.App.LiveTree.LiveTreeView tree = window.LiveTree;
            Ui.Launched(window, window.DebugAsync(), "the first game's build and start under the debugger", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Main"),
                () => $"the first game's Main node ({string.Join(", ", tree.Rows.Select(r => r.Element.Name))})", TimeSpan.FromMinutes(1));
            Assert.False(tree.Sources.IsVisible, "The chooser shows with one game running");

            window.SetConfigurations([main, other], other.Name);
            Ui.Launched(window, window.DebugAsync(), "the second game's start under the debugger", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => tree.Sources.IsVisible && tree.SourceNames.Count == 2 && tree.Rows.Any(r => r.Element.Name == "Other"),
                () => $"the chooser to offer both games, the second shown ({string.Join(", ", tree.SourceNames)}; {string.Join(", ", tree.Rows.Select(r => r.Element.Name))})", TimeSpan.FromMinutes(1));

            tree.Sources.SelectedIndex = 0;
            Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Main") && !tree.Rows.Any(r => r.Element.Name == "Other"),
                () => $"the first game's nodes once it is chosen ({string.Join(", ", tree.Rows.Select(r => r.Element.Name))})", TimeSpan.FromMinutes(1));
            Assert.True(window.Session.Game == window.Session.Games[0], "The game chosen in the Live Visual Tree is not the one agents' tools follow");
            tree.Sources.SelectedIndex = 1;
            Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Other") && !tree.Rows.Any(r => r.Element.Name == "Main"),
                () => $"the second game's nodes once it is chosen again ({string.Join(", ", tree.Rows.Select(r => r.Element.Name))})", TimeSpan.FromMinutes(1));
            games = [.. window.Session.Games.Select(g => (g.Name, g.ProcessId))];
            // Stop ends the chosen session, the game a moment before its debugger lets go of it: a Stop meanwhile is the same one's.
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Session.Games.Count == 1 && window.Session.Games[0].Name == main.Name && !window.Debugger.Tabs.Any(t => t.IsLive && t.Name.StartsWith(other.Name, StringComparison.Ordinal)),
                () => $"the second game and its sessions to end ({Debugged(window)}; {Running(games)})");
            Ui.WaitFor(() => !tree.Sources.IsVisible && tree.Rows.Any(r => r.Element.Name == "Main"),
                () => $"the Live Visual Tree to show the first game alone once the second ended ({string.Join(", ", tree.SourceNames)}; {Running(games)})", TimeSpan.FromMinutes(1));
        }
        finally
        {
            End(window, games);
            File.Delete(Path.Combine(game.Folder, "other.tscn"));
        }
    }

    /// <summary>Settings › Godot says the Godot found, with the version it answers and that it runs C#; a Godot too old for the
    /// project's Godot.NET.Sdk (a stand-in answering 4.6.1 to --version) is refused before Run starts anything, naming both.</summary>
    [AvaloniaFact]
    public void Settings_say_the_Godot_found_with_its_version_and_one_too_old_for_the_projects_sdk_is_refused_before_a_start()
    {
        MainWindow window = game.Open(null, languageServer: false);
        try
        {
            GodotFound godot = game.Godot!;
            string version = Ui.AwaitPumped(GodotExecutables.VersionAsync(godot), "the version the Godot found answers", TimeSpan.FromMinutes(1))!;
            Passenger.App.Settings.SettingsDialog dialog = window.ShowSettings();
            dialog.Page = Passenger.App.Settings.SettingsDialog.GodotPage;
            string said = $"Godot {version}, a .NET build, which runs C#, from {godot.From}: {godot.Executable}";
            Ui.WaitFor(() => dialog.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == said),
                () => $"Settings › Godot to say {said} ({string.Join(" | ", dialog.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text))})",
                TimeSpan.FromMinutes(1));
            dialog.Close();
            Ui.Settle();

            window.ApplySettings(window.Settings with { GodotExecutable = StandIn("4.6.1") });
            Choose(window, Generated(window, "Spinner"));
            string? refused = Ui.AwaitPumped(window.RunAsync(), "Run of the game on a Godot too old for it", TimeSpan.FromMinutes(3));
            // The project's Godot.NET.Sdk is the found Godot's package, spelled as NuGet spells it (4.8.0-beta.2 for 4.8 beta 2).
            string sdk = godot.Version!;
            Assert.Equal($"Spinner on Godot cannot run: it builds with Godot.NET.Sdk {sdk}, newer than the Godot 4.6.1 that runs it: run it on Godot {sdk} or newer, "
                + $"or build it with Godot.NET.Sdk 4.6.1", refused);
            Assert.False(window.IsRunning, "Run started the game on a Godot too old for its Godot.NET.Sdk");
        }
        finally
        {
            window.ApplySettings(window.Settings with { GodotExecutable = null });
            End(window);
        }
    }

    /// <summary>A stand-in Godot of a .NET build (a GodotSharp beside it) that answers --version with a version given, and
    /// does nothing else: its path.</summary>
    private static string StandIn(string version)
    {
        string folder = TestFolders.Create("old-godot-");
        Directory.CreateDirectory(Path.Combine(folder, "GodotSharp", "Api"));
        string answer = $"{version}.stable.mono.official.standin";
        if (OperatingSystem.IsWindows())
        {
            string batch = Path.Combine(folder, "godot.cmd");
            File.WriteAllText(batch, $"@echo {answer}\r\n");
            return batch;
        }
        string script = Path.Combine(folder, "godot");
        File.WriteAllText(script, $"#!/bin/sh\necho {answer}\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    /// <summary>A configuration the folder generated, by its name.</summary>
    private static RunConfiguration Generated(MainWindow window, string name)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Generated && c.Name == name),
            () => $"the Godot project's configuration {name} ({string.Join(", ", window.Configurations.Select(c => c.Name))})", TimeSpan.FromMinutes(1));
        return window.Configurations.Single(c => c.Generated && c.Name == name);
    }

    /// <summary>A copy of a generated configuration chosen, Godot run headless and its user folders the run's, never the
    /// person's, in the console given.</summary>
    private void Choose(MainWindow window, RunConfiguration generated, ConsoleKind console = ConsoleKind.Internal) =>
        window.SetConfigurations([generated with { Name = $"{generated.Name} on Godot", Generated = false, Arguments = "--headless", Environment = new(game.Homes), Console = console }],
            $"{generated.Name} on Godot");

    /// <summary>What a program run in a tab of the Terminal window has written there, its scrollback and its screen.</summary>
    private static string Terminal(TerminalRun run) =>
        string.Join('\n', run.Session.Screen.Scrollback.Select(l => l.Text()).Concat(run.Session.Screen.Lines.Select(l => l.Text())));

    private static int LineOf(string text, string code) => Array.FindIndex(text.Split('\n'), l => l.Contains(code, StringComparison.Ordinal)) + 1;

    private static int LineOf(CodeEditor editor, string code)
    {
        string[] lines = editor.Document.Snapshot.ToString().Split('\n');
        int index = Array.FindIndex(lines, l => l.Contains(code, StringComparison.Ordinal));
        Assert.True(index >= 0, $"The script has no line of {code}");
        return index + 1;
    }

    private static string Run(MainWindow window)
    {
        window.BottomPanel.Run.Flush();
        return window.BottomPanel.Run.Document.Snapshot.ToString();
    }

    private static void Stop(MainWindow window)
    {
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.IsRunning, "the game to end");
    }

    /// <summary>Stop of what still runs, then the window closed; a failure names each game given whose process still runs.</summary>
    private static void End(MainWindow window, IReadOnlyList<(string Name, int? Process)>? games = null)
    {
        if (window.Debugger.IsDebugging || window.IsRunning || window.Profiling is not null)
        {
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging && !window.IsRunning, () => $"the game to end ({Debugged(window)}; {window.Status}; {Running(games ?? [])})");
        }
        window.Close();
    }

    /// <summary>Each Debug tab's state, the chosen one marked.</summary>
    private static string Debugged(MainWindow window) => string.Join(", ", window.Debugger.Tabs.Select(t =>
        $"{t.Name}{(t.IsChosen ? " chosen" : string.Empty)}: {(t.IsLive ? t.Session?.State.ToString() ?? "opening" : "ended")}"));

    /// <summary>Whether each game's process still runs, by its id.</summary>
    private static string Running(IReadOnlyList<(string Name, int? Process)> games) => string.Join(", ", games.Select(g =>
        $"{g.Name}'s process {(g.Process is int id ? $"{id} {(DotnetProcesses.Runs(id) ? "still runs" : "has ended")}" : "unknown")}"));

    /// <summary>A Godot 4 C# project of one scene, its node's script <see cref="Script"/>, on the Godot found: its
    /// Godot.NET.Sdk the version that Godot carries, restored from that Godot's own packages alone.</summary>
    public sealed class Game : SharedProject
    {
        public Game()
            : base("godot-")
        {
            Godot = GodotExecutables.Find(null);
            Homes = Home(Path.Combine(Folder, "..", Path.GetFileName(Folder) + "-home"));
            if (Godot is not { WhyNot: null, Version: { } version, Packages: { } packages })
            {
                return;
            }
            Make(Folder, version, packages, Script, Scene);
        }

        /// <summary>Writes a Godot 4 C# project named Spinner into a folder: its project.godot (the scene its game starts in
        /// res://main.tscn), its project on the Godot.NET.Sdk of the Godot's version, its script Main.cs and scene, and a
        /// nuget.config whose one source is the Godot's own packages.</summary>
        internal static void Make(string folder, string version, string packages, string script, string scene)
        {
            string PathOf(string name) => Path.Combine(folder, name);
            string features = string.Join('.', version.Split('.').Take(2));
            File.WriteAllText(PathOf("project.godot"), $"""
                ; Engine configuration file.
                config_version=5

                [application]

                config/name="Spinner"
                run/main_scene="res://main.tscn"
                config/features=PackedStringArray("{features}", "C#")

                [dotnet]

                project/assembly_name="Spinner"

                """);
            File.WriteAllText(PathOf("Spinner.csproj"), $"<Project Sdk=\"Godot.NET.Sdk/{version}\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n"
                + "    <EnableDynamicLoading>true</EnableDynamicLoading>\n  </PropertyGroup>\n</Project>\n");
            File.WriteAllText(PathOf("Main.cs"), script);
            File.WriteAllText(PathOf("main.tscn"), scene);
            File.WriteAllText(PathOf("nuget.config"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="godot" value="{System.Security.SecurityElement.Escape(packages)}" />
                  </packageSources>
                </configuration>
                """);
            // Godot's build output goes under .godot, made now so a reset takes no folder the first build makes for one a test added.
            Directory.CreateDirectory(PathOf(Path.Combine(".godot", "mono", "temp")));
        }

        /// <summary>The Godot found, or null.</summary>
        public GodotFound? Godot { get; }

        /// <summary>Why each test skips, where it does.</summary>
        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build to run these tests"
            : $"The Godot found cannot run a C# game: {Godot.WhyNot}";

        /// <summary>The variables that give Godot user folders of the run's, where its game keeps its logs and its editor
        /// its settings: HOME on macOS and Linux (and XDG's on Linux), APPDATA and LOCALAPPDATA on Windows.</summary>
        public IReadOnlyDictionary<string, string> Homes { get; }

        public override ValueTask InitializeAsync() => Godot is { WhyNot: null } ? base.InitializeAsync() : ValueTask.CompletedTask;

        /// <summary>The variables that keep a Godot's user folders under <paramref name="home"/>.</summary>
        internal static Dictionary<string, string> Home(string home) => Passenger.App.Godot.GodotUserFolders.Under(home);
    }
}
