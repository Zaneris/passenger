using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Godot 4 project of GDScript alone, a project.godot and its scenes and scripts with no C# project, as a person
/// works on it: the game and Godot's editor are its run configurations and Build says there is nothing to build; Run starts
/// its game; Debug is its GDScript session alone, a breakpoint in a script stopping it and a member set from Variables
/// changing what it prints; a saved script reaches the game Run runs with Hot Reload; and Profile records Godot's own frames,
/// saying the run has no samples. Godot runs headless, its user folders the run's. Without a Godot 4 each test skips saying
/// why. Debugged, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotScriptGameTests : IClassFixture<GodotScriptGameTests.Game>
{
    private readonly Game game;

    public GodotScriptGameTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot?.WhyNotScripts is not null || game.Godot is null, game.Skipped);
        game.Reset();
    }

    /// <summary>The scene's one node's script: each frame counts, and every tenth says its word and the count.</summary>
    private const string Ticker = "extends Node\n\nvar word := \"tick\"\nvar counter := 0\n\n\nfunc _process(_delta: float) -> void:\n\tcounter += 1\n"
        + "\tif counter % 10 == 0:\n\t\tsay()\n\n\nfunc say() -> void:\n\tprint(word, \" \", counter)\n";

    private string TickerPath => Path.Combine(PathNames.Canonical(game.Folder), "ticker.gd");

    /// <summary>The game's and the editor's configurations are generated for the project.godot, Build is greyed saying there
    /// is nothing to build, and Run starts the game, which says its word.</summary>
    [AvaloniaFact]
    public void The_projects_game_and_editor_are_its_configurations_build_has_nothing_to_build_and_Run_starts_the_game()
    {
        MainWindow window = Open();
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Generated && c.Name == "Ticker") && window.Configurations.Any(c => c.Generated && c.Name == $"Ticker: {RunConfiguration.GodotEditorName}"),
                () => $"the game's and the editor's configurations ({string.Join(", ", window.Configurations.Select(c => c.Name))})", TimeSpan.FromMinutes(1));
            RunConfiguration generated = window.Configurations.Single(c => c.Generated && c.Name == "Ticker");
            Assert.True(generated.RunsScriptGame && PathNames.Same(generated.Project, Path.Combine(PathNames.Canonical(game.Folder), "project.godot")),
                $"The game's configuration runs {generated.Project}, where its project is the project.godot");
            Assert.Equal(Refusals.NothingToBuildOfScripts, window.Actions.WhyNot(AppActions.Build, window.Actions.Context()));

            Choose(window);
            Ui.Launched(window, window.RunAsync(), "the game's start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => Run(window).Contains("tick 20", StringComparison.Ordinal), () => $"the game to say its word ({window.Status}; {Run(window)})", TimeSpan.FromMinutes(1));
            Assert.DoesNotContain("Build", window.Status ?? string.Empty, StringComparison.Ordinal);
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Debug starts the game under its GDScript session alone, in one tab of the configuration's name: a breakpoint in
    /// _process stops it there, a local's Set Value is greyed saying Godot sets no local, and the script's word set from
    /// Variables is what the game says once it runs on.</summary>
    [AvaloniaFact]
    public void A_breakpoint_stops_the_game_debugged_alone_and_a_member_set_from_Variables_is_what_it_says_after()
    {
        MainWindow window = Open();
        try
        {
            Choose(window);
            CodeEditor editor = window.EditorArea.CodeEditor;
            int line = LineOf(editor, "counter += 1");
            editor.GoTo(new TextPosition(line, 1));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the game's start under the debugger", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.Debugger.Session is { Engine: DebugEngine.GDScript, State: DebugState.Paused } && Stopped(editor) == line,
                () => $"the game to stop at the breakpoint in ticker.gd's _process ({Sessions(window)}, {Ui.Said(window)})", TimeSpan.FromMinutes(2));
            Assert.True(window.Debugger.Tabs.Select(t => t.Name).SequenceEqual(["Ticker on Godot"]),
                $"Debug of a game of GDScript alone opened the tabs {Sessions(window)}, where its GDScript session is its one tab");

            Passenger.App.ToolWindows.DebugModel view = window.BottomPanel.Debug;
            Ui.WaitFor(() => Row(window, "_delta") is not null && Row(window, "self") is not null, () => $"the frame's local and self in Variables ({Rows(view)})");
            Assert.Equal(Passenger.App.Godot.GodotScriptAdapter.NoLocalSet, Row(window, "_delta")!.NoSetting);

            VariableNode word = Opened(window, "self", "word");
            Assert.True(word.NoSetting is null, $"The script's word cannot be set: {word.NoSetting}");
            SetInRow(window, word, "\"tock\"");
            Assert.True(window.Status?.StartsWith("Could not set", StringComparison.Ordinal) != true, $"Set Value on the script's word was refused: {window.Status}");
            Ui.WaitFor(() => Opened(window, "self", "word").Value == "\"tock\"", () => $"the word read back as set ({Opened(window, "self", "word").Value})");

            editor.GoTo(new TextPosition(line, 1));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => Console(window).Contains("tock ", StringComparison.Ordinal),
                () => $"the game to say the word set ({Sessions(window)}; {Console(window)})", TimeSpan.FromMinutes(1));
            Assert.DoesNotContain("tick ", Console(window)[Console(window).IndexOf("tock ", StringComparison.Ordinal)..], StringComparison.Ordinal);
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging && window.Session.Games.Count == 0, () => $"the game to end with its session ({Sessions(window)})", TimeSpan.FromMinutes(1));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A script saved while Run runs the game with Hot Reload reaches it: what it says after is the edit's, its count
    /// going on.</summary>
    [AvaloniaFact]
    public void A_saved_script_reaches_the_game_Run_runs_with_Hot_Reload()
    {
        MainWindow window = Open();
        try
        {
            Choose(window);
            Ui.Launched(window, window.Builds.RunAsync(hotReload: true), "the game's start with Hot Reload on", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => Run(window).Contains("tick 40", StringComparison.Ordinal), () => $"the script to run ({Run(window)})", TimeSpan.FromMinutes(1));
            Typing.Replace(window, "print(word, \" \", counter)", "print(\"edited \", counter)");
            Ui.Run(window, AppActions.SaveAll);
            Ui.WaitFor(() => Run(window).Contains("edited ", StringComparison.Ordinal), () => $"the game to say what the saved script says ({window.Status}; {Run(window)})", TimeSpan.FromMinutes(1));
            string after = Run(window)[Run(window).IndexOf("edited ", StringComparison.Ordinal)..].Split('\n')[0];
            Assert.True(int.Parse(after["edited ".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture) > 40, $"The count started again after the reload: {after}");
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Profile records the game's frames as Godot's own profiler measures them, its script's _process among them,
    /// and says the run has no samples, a game of GDScript alone running no .NET code.</summary>
    [AvaloniaFact]
    public void Profile_records_the_games_frames_alone_and_says_it_takes_no_samples()
    {
        MainWindow window = Open();
        try
        {
            Choose(window);
            Ui.Launched(window, window.ProfileAsync(), "the game's start under the profiler", TimeSpan.FromMinutes(2));
            Assert.Equal(Passenger.App.Profiling.ProfilerCommands.FramesAloneSaid("Ticker on Godot"), window.Status);
            Ui.WaitFor(() => Run(window).Contains("tick 200", StringComparison.Ordinal), () => $"the profiled game to run its script ({Run(window)})", TimeSpan.FromMinutes(1));
            Assert.Equal(Passenger.Profiler.ProfilingSession.NoHeapOfScripts, window.Actions.WhyNot(AppActions.MemorySnapshot, window.Actions.Context()));

            Passenger.App.Profiling.SnapshotRead read = Ui.Await(window.SnapshotAsync(), "the game's snapshot", TimeSpan.FromMinutes(2));
            Assert.True(read.Snapshot is { FramesAlone: true, Samples: 0, Game: not null, GameFrames.Count: > 100 },
                $"{read.Message}: {read.Snapshot?.GameFrames.Count} of Godot's frames, {read.Snapshot?.Samples} samples, {read.Snapshot?.Game?.Why}");
            Assert.Contains("no samples", read.Message, StringComparison.Ordinal);
            Passenger.App.Profiling.GameFramesView frames = window.ProfilerView.GamePage;
            Ui.WaitFor(() => frames.IsEffectivelyVisible && frames.Rows.Count > 0, "the Frames page to show Godot's frames");
            Assert.True(frames.Rows.Any(r => r.Function?.Name == "_process" && r.Calls > 100),
                $"The Frames page names no _process of ticker.gd: {string.Join("; ", frames.Rows.Select(r => $"{r.Name} {r.Place} {r.Calls}"))}");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A window on the project with its script open, no C# language server.</summary>
    private MainWindow Open()
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, GodotEnvironment = game.Homes };
        window.Show();
        window.OpenFolder(game.Folder);
        Assert.Null(window.OpenFile(TickerPath));
        Ui.Settle();
        return window;
    }

    /// <summary>A copy of the game's generated configuration chosen, Godot run headless with the run's user folders.</summary>
    private void Choose(MainWindow window)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Generated && c.Name == "Ticker"),
            () => $"the game's configuration ({string.Join(", ", window.Configurations.Select(c => c.Name))})", TimeSpan.FromMinutes(1));
        RunConfiguration generated = window.Configurations.Single(c => c.Generated && c.Name == "Ticker");
        window.SetConfigurations([generated with { Name = "Ticker on Godot", Generated = false, Arguments = "--headless", Environment = new(game.Homes) }], "Ticker on Godot");
    }

    private static int LineOf(CodeEditor editor, string code)
    {
        int index = Array.FindIndex(editor.Document.Snapshot.ToString().Split('\n'), l => l.Contains(code, StringComparison.Ordinal));
        Assert.True(index >= 0, $"The script has no line of {code}");
        return index;
    }

    /// <summary>The line (from zero) the editor shows the program stopped at, or -1.</summary>
    private static int Stopped(CodeEditor editor) => editor.LineMarks.FirstOrDefault(m => m.Kind == LineMarkKind.ExecutionPoint) is { } mark ? mark.Line : -1;

    private static string Sessions(MainWindow window) =>
        string.Join(", ", window.Debugger.Tabs.Select(t => $"{t.Name}: {t.Session?.State.ToString() ?? "none"}"));

    private static string Rows(Passenger.App.ToolWindows.DebugModel view) => string.Join("; ", view.Tree.Rows.Where(r => r.Depth == 0).Select(r => $"{r.Name} = {r.Value} ({r.Type})"));

    private static VariableNode? Row(MainWindow window, string name) => window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == name);

    /// <summary>The row reached by opening each row named in turn, from one of the frame's.</summary>
    private static VariableNode Opened(MainWindow window, params string[] path)
    {
        VariableNode row = Row(window, path[0]) ?? throw new InvalidOperationException($"The frame lists no {path[0]}");
        foreach (string name in path.Skip(1))
        {
            Task opened = window.BottomPanel.Debug.Tree.Expand(row);
            Ui.WaitFor(() => opened.IsCompleted, $"{row.Name}'s rows");
            row = row.Children?.FirstOrDefault(c => c.Name == name)
                ?? throw new InvalidOperationException($"{row.Name} lists {string.Join(", ", (row.Children ?? []).Select(c => c.Name))}, without {name}");
        }
        return row;
    }

    /// <summary>Set Value in a row, as a person sets it: F2 on the row, the value typed, Enter.</summary>
    private static void SetInRow(MainWindow window, VariableNode row, string value)
    {
        VariableNode top = window.BottomPanel.Debug.Tree.Rows.First(r => r.Depth == 0);
        Ui.Click(Ui.Row(window.DebugWindow.VariablesList, row), settle: false);
        Ui.Press(window, "F2");
        Assert.True(row.IsEditing, $"F2 on {row.Name} did not open its value for editing");
        window.KeyTextInput(value);
        Ui.Press(window, "Enter");
        Ui.WaitFor(() => window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0) is { } again && again != top, "the variables shown again once the value was set");
    }

    private static string Console(MainWindow window) =>
        window.Debugger.Tabs.FirstOrDefault(t => t.Name == "Ticker on Godot")?.Session is { } session ? window.Debugger.Output(session).ToString() : string.Empty;

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
        if (window.Debugger.IsDebugging || window.IsRunning || window.Profiling is not null)
        {
            Assert.Null(window.Actions.Run(AppActions.StopAll));
            Ui.WaitFor(() => !window.Debugger.IsDebugging && !window.IsRunning, () => $"the game to end ({Sessions(window)})", TimeSpan.FromMinutes(1));
        }
        window.Close();
    }

    /// <summary>A Godot 4 project of GDScript alone named Ticker: its project.godot (the scene its game starts in
    /// res://main.tscn), the scene, and its one node's script, written again before each test.</summary>
    public sealed class Game
    {
        public Game()
        {
            Godot = GodotExecutables.Find(null);
            Folder = TestFolders.Create("godot-gdscript-");
            Homes = GodotTests.Game.Home(Path.Combine(Folder, "..", Path.GetFileName(Folder) + "-home"));
            File.WriteAllText(Path.Combine(Folder, "project.godot"), "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Ticker\"\n"
                + "run/main_scene=\"res://main.tscn\"\nconfig/features=PackedStringArray(\"4.7\")\n");
            File.WriteAllText(Path.Combine(Folder, "main.tscn"), "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://ticker.gd\" id=\"1\"]\n\n"
                + "[node name=\"Main\" type=\"Node\"]\nscript = ExtResource(\"1\")\n");
        }

        public string Folder { get; }

        /// <summary>The Godot found, or null.</summary>
        public GodotFound? Godot { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4 to run these tests"
            : $"The Godot found cannot run a Godot 4 project: {Godot.WhyNotScripts}";

        public IReadOnlyDictionary<string, string> Homes { get; }

        /// <summary>The script as it was, before each test.</summary>
        public void Reset() => File.WriteAllText(Path.Combine(Folder, "ticker.gd"), Ticker);
    }
}
