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
/// and Profile records it, its script's own time among the samples. Godot runs headless, its user folders the run's.
/// Without a Godot 4 .NET build (GODOT, the PATH, Applications) each test skips saying why. Debugged, so run alone.</summary>
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
    /// --path naming the project; Run of the game builds it and starts it there, its words in the Run console, Apply Hot
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
            Assert.Equal(["--path", folder, "--editor"], editor.Arguments);

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
                Assert.True(editor?.Arguments.SequenceEqual(["--path", folder, "--editor", "res://main.tscn"]) == true,
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
            // Stop ends the chosen session; the other is ended in turn.
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Session.Games.Count < 2, "the second game to end");
        }
        finally
        {
            End(window);
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
            Assert.Equal($"Spinner on Godot cannot run: it builds with Godot.NET.Sdk {version}, newer than the Godot 4.6.1 that runs it: run it on Godot {version} or newer, "
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
    /// person's.</summary>
    private void Choose(MainWindow window, RunConfiguration generated) =>
        window.SetConfigurations([generated with { Name = $"{generated.Name} on Godot", Generated = false, Arguments = "--headless", Environment = new(game.Homes) }],
            $"{generated.Name} on Godot");

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

    private static void End(MainWindow window)
    {
        if (window.Debugger.IsDebugging || window.IsRunning || window.Profiling is not null)
        {
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging && !window.IsRunning, "the game to end");
        }
        window.Close();
    }

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
        internal static Dictionary<string, string> Home(string home)
        {
            string full = Directory.CreateDirectory(Path.GetFullPath(home)).FullName;
            return OperatingSystem.IsWindows()
                ? new() { ["APPDATA"] = Path.Combine(full, "Roaming"), ["LOCALAPPDATA"] = Path.Combine(full, "Local") }
                : new()
                {
                    ["HOME"] = full,
                    ["XDG_CONFIG_HOME"] = Path.Combine(full, ".config"),
                    ["XDG_DATA_HOME"] = Path.Combine(full, ".local", "share"),
                    ["XDG_CACHE_HOME"] = Path.Combine(full, ".cache"),
                };
        }
    }
}
