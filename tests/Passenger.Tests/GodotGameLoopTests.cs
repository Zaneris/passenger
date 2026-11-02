using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Godot;
using Passenger.App.LiveTree;
using Passenger.App.Run;
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
    /// shape under the point; a point with no node Godot picks there (the view's corner) is said so.</summary>
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
    /// runs; under it a ColorRect Box at 100,100 to 150,150 with a script of its own, and a 3D world whose camera looks at a
    /// Crate's collision box from ten units away, the Crate a group in Godot's editor with a script of its own.</summary>
    public sealed class Game
    {
        public Game()
        {
            Godot = GodotExecutables.Find(null);
            Folder = PathNames.Canonical(TestFolders.Create("godot-arena-"));
            Homes = GodotTests.Game.Home(Path.Combine(Folder, "..", Path.GetFileName(Folder) + "-home"));
            File.WriteAllText(Path.Combine(Folder, "project.godot"), "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Arena\"\n"
                + "run/main_scene=\"res://main.tscn\"\nconfig/features=PackedStringArray(\"4.7\")\nrun/max_fps=20\n");
            File.WriteAllText(Path.Combine(Folder, "main.gd"), "extends Node\n\nvar frames := 0\n\n\nfunc _process(_delta: float) -> void:\n\tframes += 1\n\tprint(\"frame \", frames)\n");
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
