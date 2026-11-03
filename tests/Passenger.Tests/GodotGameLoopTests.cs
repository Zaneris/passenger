using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Godot;
using Passenger.App.LiveTree;
using Passenger.App.Telemetry;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A running Godot game held and picked from the Live Visual Tree: Select Element picks the node at a point among
/// its 2D nodes and controls or its 3D ones, the tree choosing it with its script's link; Pause Game Loop holds the game at
/// its frame, the debugger not breaking in, the frame count and the game's own output stopping there; Next Frame runs one
/// frame, and Resume lets it go on. The game is a project of GDScript alone at 20 frames a second, run headless with the
/// run's user folders: it draws no window, so a click at a point is given through Passenger's helper in the game, as an
/// agent's app_click gives it, and Godot picks there by what it reads without drawing (a control's rectangle, a 3D node's
/// collision shape; a 3D mesh alone it cannot). Without a Godot 4 each test skips saying why. Held to frames, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotGameLoopTests : IClassFixture<GodotGameLoopTests.Game>
{
    private readonly Game game;

    public GodotGameLoopTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot?.WhyNotScripts is not null || game.Godot is null, game.Skipped);
    }

    /// <summary>Select Element in 2D picks the control at a point, the tree choosing it with its script a link, and is off
    /// once it has; in 3D it picks the body at the middle of the camera's view, its group in Godot's editor picked for the
    /// shape under the point; a point with no node Godot picks there (the view's corner) is said so, with no collision shape there.</summary>
    [AvaloniaFact]
    public void Select_Element_picks_the_node_at_a_point_in_2D_and_in_3D_the_tree_choosing_it_with_its_scripts_link()
    {
        MainWindow window = Open();
        try
        {
            GodotGame running = Start(window);
            LiveTreeView tree = window.LiveTree;
            Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Main"), () => $"the game's nodes ({string.Join(", ", tree.Rows.Select(r => r.Element.Name))})", TimeSpan.FromMinutes(1));
            Assert.True(tree.SelectElement.IsVisible && tree.PickIn.IsVisible && tree.PicksIn3D,
                $"Select Element shows for a game whose tree holds 3D nodes, picking among them first (shown {tree.SelectElement.IsVisible}, chooser {tree.PickIn.IsVisible}, 3D {tree.PicksIn3D})");

            tree.PickIn.SelectedIndex = 0;
            Ui.Click(tree.SelectElement);
            Ui.WaitFor(() => running.Picking == GodotPicking.In2D, () => $"the game to pick among its 2D nodes ({running.Picking}; {window.Status})");
            Assert.True(tree.SelectElement.IsChecked, "Select Element did not stay on while the game picks");
            Click(running, 125, 125);
            Ui.WaitFor(() => tree.Details is { Name: "Box" } box && box.Source is { } script && PathNames.Same(script.File, Path.Combine(game.Folder, "box.gd")),
                () => $"the tree to choose the Box picked, its script a link ({tree.Details?.Name}: {tree.Details?.Source?.File}; {window.Status})", TimeSpan.FromMinutes(1));
            Assert.True(tree.SelectElement.IsChecked == false && running.Picking == GodotPicking.Off,
                $"Select Element stayed on once it picked a node, so the game would take no input of its own ({running.Picking})");

            tree.PickIn.SelectedIndex = 1;
            Ui.Click(tree.SelectElement);
            Ui.WaitFor(() => running.Picking == GodotPicking.In3D, () => $"the game to pick among its 3D nodes ({running.Picking}; {window.Status})");
            int picks = running.Picks;
            Click(running, 2, 2);
            Ui.WaitFor(() => running.Picks > picks, () => $"the game to say it picked nothing at the view's corner ({window.Status})", TimeSpan.FromMinutes(1));
            Assert.True(running.LastPick is null && running.Picking == GodotPicking.In3D && window.Status?.Contains("has no node there", StringComparison.Ordinal) == true,
                $"A point with no node to pick chose {running.LastPick?.Class}, or turned Select Element off, or was not said ({running.Picking}; {window.Status})");
            // The helper's own ray finds no shape there either, which a headless game's pick needs.
            Ui.WaitFor(() => window.Status?.Contains("no collision shape at this point: a headless game picks only nodes with one", StringComparison.Ordinal) == true,
                () => $"the miss to say the point held no collision shape ({window.Status})");
            (double width, double height) = View(running);
            Click(running, width / 2, height / 2);
            Ui.WaitFor(() => tree.Details is { Name: "Crate" } crate && crate.Source is { } script && PathNames.Same(script.File, Path.Combine(game.Folder, "crate.gd")),
                () => $"the tree to choose the Crate whose shape is at the middle of the {width}x{height} view, its script a link ({tree.Details?.Name}: {tree.Details?.Source?.File}; "
                    + $"{window.Status})", TimeSpan.FromMinutes(1));
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Pause Game Loop holds the game at its frame without breaking into the debugger: the line under the buttons
    /// says the frame, and the game prints no frame more however long it is held; Next Frame runs exactly one (the count and
    /// the game's own output one more, then still again), and Resume Game Loop lets it run on, the count moving with it.</summary>
    [AvaloniaFact]
    public void Pause_Game_Loop_holds_the_game_its_frame_count_stopping_and_Next_Frame_steps_it_by_one()
    {
        MainWindow window = Open();
        try
        {
            GodotGame running = Start(window);
            LiveTreeView tree = window.LiveTree;
            Ui.WaitFor(() => Printed(window) >= 20, () => $"the game to print its frames ({Run(window)})", TimeSpan.FromMinutes(1));
            Assert.True(tree.PauseLoop.IsVisible && tree.NextFrame.IsVisible && !tree.NextFrame.IsEnabled,
                $"Pause Game Loop and Next Frame show for a game, Next Frame greyed while it runs ({tree.PauseLoop.IsVisible}, {tree.NextFrame.IsVisible}, {tree.NextFrame.IsEnabled})");

            Ui.Click(tree.PauseLoop);
            Ui.WaitFor(() => running is { LoopHeld: true, Frame: not null } && tree.MonitorsLine?.StartsWith("Paused at frame ", StringComparison.Ordinal) == true,
                () => $"the game's loop to be held and its frame said ({running.LoopHeld}, {running.Frame}; {tree.MonitorsLine}; {window.Status})", TimeSpan.FromMinutes(1));
            Assert.True(tree.PauseLoop.IsChecked == true && tree.NextFrame.IsEnabled && window.Debugger.Session is null,
                $"Pause Game Loop is not pressed, Next Frame not enabled, or the debugger broke in ({tree.PauseLoop.IsChecked}, {tree.NextFrame.IsEnabled}, {window.Debugger.Session?.State})");
            long paused = running.Frame!.Value;
            Ui.Hold(TimeSpan.FromSeconds(1), "the frames printed before the hold to reach the console");
            int printed = Printed(window);
            Ui.Hold(TimeSpan.FromSeconds(1.5), "the held game to have printed thirty frames more, were it running");
            Assert.True(Printed(window) == printed && running.Frame == paused && tree.MonitorsLine!.Contains(Counted(paused), StringComparison.Ordinal),
                $"The held game ran on: it printed frame {Printed(window)} after {printed}, counted {running.Frame} after {paused} ({tree.MonitorsLine})");

            Ui.Click(tree.NextFrame);
            Ui.WaitFor(() => running.Frame == paused + 1 && tree.MonitorsLine?.StartsWith($"Paused at frame {Counted(paused + 1)}", StringComparison.Ordinal) == true,
                () => $"the count to step by one from {paused} ({running.Frame}; {tree.MonitorsLine}; {window.Status})", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => Printed(window) == printed + 1, () => $"the game to print one frame more than {printed} ({Printed(window)})", TimeSpan.FromMinutes(1));
            Ui.Hold(TimeSpan.FromSeconds(1.5), "the game to have run on past its one frame, were it not held again");
            Assert.True(Printed(window) == printed + 1 && running.LoopHeld, $"Next Frame ran more than one frame: the game printed frame {Printed(window)} after {printed}");

            Ui.Click(tree.PauseLoop);
            Ui.WaitFor(() => !running.LoopHeld && Printed(window) > printed + 10, () => $"the game to run on once resumed ({Printed(window)} after {printed}; {window.Status})", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => running.Frame > paused + 10 && tree.MonitorsLine?.StartsWith("Frame ", StringComparison.Ordinal) == true,
                () => $"the count to follow the running game ({running.Frame} after {paused}; {tree.MonitorsLine})", TimeSpan.FromMinutes(1));
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Passenger's helper, brought into a game by counts of its frames asked the moment its scene is up, several at
    /// once so they reach the game as its script is set, answers every one, on each of several starts of the game: a call that
    /// comes while the helper's members are still being made waits for them rather than going unanswered.</summary>
    [AvaloniaFact]
    public void The_helper_brought_in_by_counts_asked_at_once_answers_every_one_each_time_the_game_starts()
    {
        MainWindow window = Open();
        try
        {
            for (int start = 1; start <= 3; start++)
            {
                GodotGame running = Start(window);
                // The helper comes in beside the game's scene, which a game connects before it loads.
                Ui.WaitFor(() => running.Monitors is { Nodes: > 1 }, () => $"the game's scene to be up, start {start} ({running.Monitors}; {window.Status})", TimeSpan.FromMinutes(1));
                Task<long?>[] counts = [.. Enumerable.Range(0, 6).Select(_ => running.TryCountAsync())];
                long?[] answered = Ui.AwaitPumped(Task.WhenAll(counts), $"the counts asked as the helper came in, start {start}", TimeSpan.FromMinutes(1));
                Assert.True(answered.All(frame => frame >= 1),
                    $"On start {start} the helper did not answer every count asked as it came in: {string.Join(", ", answered.Select(frame => frame?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"))} ({window.Status})");
                Stop(window);
            }
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A game that frees Passenger's helper partway (Main frees the node of root's once told to) has it brought in
    /// again by the next call: the frame count is answered after the free, the values shown in main.gd climb on, and the game
    /// holds one helper, not a second made beside the first.</summary>
    [AvaloniaFact]
    public void A_game_that_frees_the_helper_has_it_brought_in_again_by_the_next_count_and_the_live_values()
    {
        MainWindow window = Open();
        try
        {
            GodotGame running = Start(window);
            window.OpenFile(Path.Combine(game.Folder, "main.gd"));
            OpenDocument script = window.EditorArea.Active!;
            int frames = Array.FindIndex(File.ReadAllText(Path.Combine(game.Folder, "main.gd")).Split('\n'), l => l.StartsWith("var frames", StringComparison.Ordinal));
            Ui.WaitFor(() => Frames(script, frames) is not null, () => $"the frame count's value in main.gd ({Hints(script)}; {window.GodotValues.Unread})", TimeSpan.FromMinutes(1));
            Assert.NotNull(Ui.AwaitPumped(running.TryCountAsync(), "the count before the helper is freed", TimeSpan.FromMinutes(1)));

            GodotNode before = Ui.AwaitPumped(running.SceneTreeAsync(), "the game's tree", TimeSpan.FromMinutes(1));
            GodotNode main = before.All().First(n => n.Name == "Main");
            double nodes = running.Monitors!.Nodes;
            Ui.AwaitPumped(running.SetPropertyAsync(main.Id, "free_helper", true), "Main told to free the helper", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => Run(window).Contains("helper freed", StringComparison.Ordinal), () => $"the game to free the helper ({Run(window)[^Math.Min(400, Run(window).Length)..]})", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => running.Monitors?.Nodes < nodes, () => $"the game's count of nodes to drop from {nodes} with the helper freed ({running.Monitors?.Nodes})", TimeSpan.FromMinutes(1));
            long? shown = Frames(script, frames);

            long? counted = Ui.AwaitPumped(running.TryCountAsync(), "the count after the helper was freed", TimeSpan.FromMinutes(1));
            Assert.True(counted >= 1, $"The count asked after the game freed the helper went unanswered ({window.Status}; {window.GodotValues.Unread})");
            Ui.WaitFor(() => Frames(script, frames) > shown, () => $"main.gd's frame count to climb past {shown} once the helper was brought in again ({Hints(script)}; {window.GodotValues.Unread})",
                TimeSpan.FromMinutes(1));
            GodotNode after = Ui.AwaitPumped(running.SceneTreeAsync(), "the game's tree with the helper back", TimeSpan.FromMinutes(1));
            string[] helpers = [.. after.Children.Where(c => c.Name.Contains(GodotAgent.NodeName, StringComparison.Ordinal) || c.Type.EndsWith("passenger_agent.gd", StringComparison.Ordinal))
                .Select(c => $"{c.Name} ({c.Type})")];
            Assert.True(helpers.Length == 1 && after.Children.Any(GodotAgent.Is), $"The game holds {helpers.Length} helpers once it was brought in again: {string.Join(", ", helpers)}");
            Assert.False(Run(window).Contains("passenger_agent.gd", StringComparison.Ordinal), $"Passenger's helper raised errors in the game:\n{Run(window)}");
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>The game's monitors charted in the Live Visual Tree once asked for: its frame time and frames a second, and
    /// the custom monitor its script adds, each a line under its heading with its latest value, more coming each second; an
    /// agent's telemetry of kind metrics reads the same; hidden again, nothing is charted.</summary>
    [AvaloniaFact]
    public void A_games_frame_time_and_its_custom_monitor_are_charted_as_they_come_and_an_agent_reads_them()
    {
        MainWindow window = Open();
        try
        {
            GodotGame running = Start(window);
            LiveTreeView tree = window.LiveTree;
            Ui.WaitFor(() => tree.MonitorsLine is not null, () => $"the game's monitors' line ({window.Status})", TimeSpan.FromMinutes(1));
            Assert.Null(tree.Charts);
            Ui.Click(tree.ChartsLink);
            Ui.WaitFor(() => tree.Charts is { } charts && charts.Charted.GetValueOrDefault("Frames") >= 2 && charts.Charted.GetValueOrDefault("Frame time") >= 2,
                () => $"the frame time and the game's Frames monitor to be charted, two readings each ({string.Join(", ", tree.Charts?.Charted.Select(c => $"{c.Key} {c.Value}") ?? [])}; {tree.Charts?.Said})",
                TimeSpan.FromMinutes(1));
            MetricsPane shown = tree.Charts!;
            Assert.True(double.TryParse(shown.ValueOf("Frames")?.Replace(",", string.Empty, StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture, out double frames) && frames > 0,
                $"The Frames monitor's value was to be the game's count of frames: {shown.ValueOf("Frames")}");
            Assert.EndsWith(" ms", shown.ValueOf("Frame time"), StringComparison.Ordinal);
            Assert.Contains("Frames a second", shown.Charted.Keys);

            Passenger.App.Mcp.TelemetryInfo read = Ui.AwaitPumped(new Passenger.App.Mcp.PassengerTools(window).GetTelemetry("metrics"), "an agent's read of the game's monitors", TimeSpan.FromMinutes(1));
            Assert.True(read.Metrics is { } metrics && metrics.Any(m => m is { Meter: "Game", Instrument: "Frames" }) && metrics.Any(m => m is { Meter: GodotMonitors.Engine, Instrument: "Frame time" }),
                $"An agent's metrics were to hold the game's Frames monitor and its frame time: {string.Join(", ", read.Metrics?.Select(m => $"{m.Meter}/{m.Instrument} {m.Value}") ?? [])}");

            Ui.Click(tree.ChartsLink);
            Assert.True(tree.Charts is null && shown.Charted.Count == 0, "Hidden, the charts were to be let go");
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>The frame count main.gd's line (from zero) shows, or null.</summary>
    private static long? Frames(OpenDocument document, int line) =>
        document.LiveValues.Where(a => a.Line == line).Select(a => a.Text).FirstOrDefault() is { } hint && hint.StartsWith("frames: ", StringComparison.Ordinal)
            && long.TryParse(hint["frames: ".Length..], System.Globalization.CultureInfo.InvariantCulture, out long count) ? count : null;

    private static string Hints(OpenDocument document) => string.Join("; ", document.LiveValues.Select(a => $"line {a.Line + 1}: {a.Text}"));

    /// <summary>A window on the project, no C# language server, the Godot editors it starts for GDScript keeping the run's user folders.</summary>
    private MainWindow Open()
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, GodotEnvironment = game.Homes };
        window.Show();
        window.OpenFolder(game.Folder);
        Ui.Settle();
        return window;
    }

    /// <summary>The game run, headless with the run's user folders, once it has connected to Passenger.</summary>
    private GodotGame Start(MainWindow window)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Generated && c.Name == "Arena"),
            () => $"the game's configuration ({string.Join(", ", window.Configurations.Select(c => c.Name))})", TimeSpan.FromMinutes(1));
        RunConfiguration generated = window.Configurations.Single(c => c.Generated && c.Name == "Arena");
        window.SetConfigurations([generated with { Name = "Arena on Godot", Generated = false, Arguments = "--headless", Environment = new(game.Homes) }], "Arena on Godot");
        Ui.Launched(window, window.RunAsync(), "the game's start", TimeSpan.FromMinutes(2));
        Ui.WaitFor(() => window.Session.Game is { State: LiveAppState.Connected }, () => $"the game to connect ({window.Session.Game?.State}; {window.Status})", TimeSpan.FromMinutes(1));
        return window.Session.Game!;
    }

    /// <summary>A left click at a point of the game's view, given through Passenger's helper in the game.</summary>
    private static void Click(GodotGame running, double x, double y) =>
        Ui.AwaitPumped(running.AskAsync("click", 0L, true, x, y, 1L, 1L, 0L), $"the click at {x},{y}", TimeSpan.FromMinutes(1));

    /// <summary>The size of the game's view as its root window has it, which a headless game's display gives.</summary>
    private static (double Width, double Height) View(GodotGame running) =>
        Ui.AwaitPumped(running.AskAsync("windows"), "the game's windows", TimeSpan.FromMinutes(1)) is [List<object?> windows]
            && windows.OfType<List<object?>>().FirstOrDefault() is { Count: >= 6 } root && root[4] is double width && root[5] is double height
            ? (width, height)
            : throw new InvalidOperationException("The game named no root window with a size");

    /// <summary>The last frame the game printed, or 0.</summary>
    private static int Printed(MainWindow window) =>
        Run(window).Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.StartsWith("frame ", StringComparison.Ordinal)) is { } line ? int.Parse(line[6..], System.Globalization.CultureInfo.InvariantCulture) : 0;

    private static string Counted(long frames) => frames.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private static string Run(MainWindow window)
    {
        window.BottomPanel.Run.Flush();
        return window.BottomPanel.Run.Document.Snapshot.ToString();
    }

    private static void Stop(MainWindow window)
    {
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.IsRunning && window.Session.Games.Count == 0, () => $"the game to end ({window.Status})");
    }

    private static void End(MainWindow window)
    {
        if (window.Debugger.IsDebugging || window.IsRunning)
        {
            Assert.Null(window.Actions.Run(AppActions.StopAll));
            Ui.WaitFor(() => !window.Debugger.IsDebugging && !window.IsRunning, () => $"the game to end ({window.Status})", TimeSpan.FromMinutes(1));
        }
        window.Close();
    }

    /// <summary>A Godot 4 project of GDScript alone named Arena at 20 frames a second: its Main node prints each frame it
    /// runs, and frees Passenger's helper once its free_helper is set; under it a ColorRect Box at 100,100 to 150,150 with a
    /// script of its own, and a 3D world whose camera looks at a Crate's collision box from ten units away, the Crate a group
    /// in Godot's editor with a script of its own.</summary>
    public sealed class Game
    {
        public Game()
        {
            Godot = GodotExecutables.Find(null);
            Folder = PathNames.Canonical(TestFolders.Create("godot-arena-"));
            Homes = GodotTests.Game.Home(Path.Combine(Folder, "..", Path.GetFileName(Folder) + "-home"));
            File.WriteAllText(Path.Combine(Folder, "project.godot"), "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Arena\"\n"
                + "run/main_scene=\"res://main.tscn\"\nconfig/features=PackedStringArray(\"4.7\")\nrun/max_fps=20\n");
            File.WriteAllText(Path.Combine(Folder, "main.gd"), "extends Node\n\nvar frames := 0\nvar free_helper := false\n\n\n"
                + "func _ready() -> void:\n\tPerformance.add_custom_monitor(\"Game/Frames\", func(): return frames)\n\n\n"
                + "func _process(_delta: float) -> void:\n\tframes += 1\n"
                + "\tprint(\"frame \", frames)\n\tif free_helper:\n\t\tfree_helper = false\n\t\tvar helper := get_tree().root.get_node_or_null(\"PassengerAgent\")\n"
                + "\t\tif helper != null:\n\t\t\thelper.queue_free()\n\t\t\tprint(\"helper freed\")\n");
            File.WriteAllText(Path.Combine(Folder, "box.gd"), "extends ColorRect\n");
            File.WriteAllText(Path.Combine(Folder, "crate.gd"), "extends StaticBody3D\n");
            File.WriteAllText(Path.Combine(Folder, "main.tscn"), """
                [gd_scene load_steps=5 format=3]

                [ext_resource type="Script" path="res://main.gd" id="1"]
                [ext_resource type="Script" path="res://box.gd" id="2"]
                [ext_resource type="Script" path="res://crate.gd" id="3"]

                [sub_resource type="BoxShape3D" id="shape"]
                size = Vector3(2, 2, 2)

                [node name="Main" type="Node"]
                script = ExtResource("1")

                [node name="Box" type="ColorRect" parent="."]
                offset_left = 100.0
                offset_top = 100.0
                offset_right = 150.0
                offset_bottom = 150.0
                script = ExtResource("2")

                [node name="World" type="Node3D" parent="."]

                [node name="Camera" type="Camera3D" parent="World"]
                transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 10)
                current = true

                [node name="Crate" type="StaticBody3D" parent="World"]
                script = ExtResource("3")
                metadata/_edit_group_ = true

                [node name="Shape" type="CollisionShape3D" parent="World/Crate"]
                shape = SubResource("shape")

                """);
        }

        public string Folder { get; }

        /// <summary>The Godot found, or null.</summary>
        public GodotFound? Godot { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4 to run these tests"
            : $"The Godot found cannot run a Godot 4 project: {Godot.WhyNotScripts}";

        public IReadOnlyDictionary<string, string> Homes { get; }
    }
}
