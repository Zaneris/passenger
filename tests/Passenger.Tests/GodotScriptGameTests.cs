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
/// its game, importing its assets first where they have no .import; Debug is its GDScript session alone, a breakpoint in a
/// script stopping it and a member set from Variables changing what it prints; a saved script reaches the game Run runs
/// with Hot Reload; and Profile records Godot's own frames, saying the run has no samples, as Attach Profiler does; Debug
/// takes the configuration's external console. Godot runs headless, its user folders the run's. Without a Godot 4 each
/// test skips saying why. Debugged, so run alone.</summary>
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

    /// <summary>A game whose project stretches nothing draws at its window's size in pixels, so the Game window has Passenger's
    /// helper lay it out at its project's size and scale it to its window, and a place smaller than that still shows it whole.</summary>
    [AvaloniaFact]
    public void The_helper_has_a_game_that_stretches_nothing_laid_out_at_its_projects_size_and_scaled_to_its_window()
    {
        MainWindow window = Open();
        try
        {
            Choose(window);
            Ui.Launched(window, window.RunAsync(), "the game's start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.Session.Game is { State: Passenger.App.LiveTree.LiveAppState.Connected }, () => $"the game to connect ({window.Status})", TimeSpan.FromMinutes(1));
            Passenger.App.Godot.GodotGame game = window.Session.Game!;

            List<object?> said = Ui.Await(game.AskAsync(Passenger.App.Godot.GodotAgent.ScaleToWindow), "the helper's answer", TimeSpan.FromMinutes(1));

            Assert.True(said is [1L, 1152L, 648L], $"The game is left drawing at its window's size, so a smaller Game window would crop it: the helper said {string.Join(", ", said)}");
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A configuration that starts the game in Passenger's Game window runs it in a window of its own where it cannot
    /// draw there: a headless game has no picture, so the Game window says why, holds no game, and the game runs as ever.</summary>
    [AvaloniaFact]
    public void A_game_started_in_the_Game_window_that_runs_headless_runs_in_its_own_window_the_Game_window_saying_why()
    {
        MainWindow window = Open();
        try
        {
            Choose(window);
            RunConfiguration chosen = window.Configurations.Single(c => c.Name == "Ticker on Godot");
            window.SetConfigurations([chosen with { GodotInPane = true }], "Ticker on Godot");
            Ui.Launched(window, window.RunAsync(), "the game's start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => Run(window).Contains("tick 20", StringComparison.Ordinal), () => $"the game to say its word ({window.Status}; {Run(window)})", TimeSpan.FromMinutes(1));
            Assert.Equal(Refusals.GameHeadless("Ticker on Godot"), window.GamePane.Said);
            Assert.Null(window.GamePane.Game);
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Two text boxes, one holding the keys, the other freed once told, and a third taken out of the tree.</summary>
    private const string Boxes = "extends Node\n\nvar kept := LineEdit.new()\nvar freed := LineEdit.new()\nvar outside := LineEdit.new()\n"
        + "var drop := false:\n\tset(value):\n\t\tfreed.free()\n\t\tremove_child(outside)\n\t\tprint(\"dropped\")\n\n\n"
        + "func _ready() -> void:\n\tkept.name = \"Kept\"\n\tfreed.name = \"Freed\"\n\toutside.name = \"Outside\"\n"
        + "\tadd_child(kept)\n\tadd_child(freed)\n\tadd_child(outside)\n\tkept.grab_focus()\n";

    /// <summary>Input aimed at a control gone since it was found (freed, or taken out of the tree) is refused saying so, never
    /// given to what has the keys, whose text stays as it was; input aimed at nothing goes where the keys are, as ever.</summary>
    [AvaloniaFact]
    public void Input_aimed_at_a_control_gone_since_it_was_found_is_refused_and_reaches_no_other()
    {
        File.WriteAllText(TickerPath, Boxes);
        MainWindow window = Open();
        try
        {
            Choose(window);
            Ui.Launched(window, window.RunAsync(), "the game's start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.Session.Game is { State: Passenger.App.LiveTree.LiveAppState.Connected }, () => $"the game to connect ({window.Status})", TimeSpan.FromMinutes(1));
            Passenger.App.Godot.GodotGame running = window.Session.Game!;
            List<Passenger.App.Godot.GodotNode> nodes = [.. Ui.AwaitPumped(running.SceneTreeAsync(), "the game's tree", TimeSpan.FromMinutes(1)).All()];
            long Id(string name) => (long)nodes.First(n => n.Name == name).Id;
            (long freed, long outside) = (Id("Freed"), Id("Outside"));
            Ui.AwaitPumped(running.SetPropertyAsync(nodes.First(n => n.Name == "Main").Id, "drop", true), "the game told to drop its text boxes", TimeSpan.FromMinutes(1));
            Ui.PumpUntil(() => Run(window).Contains("dropped", StringComparison.Ordinal), "the game to drop its text boxes", TimeSpan.FromMinutes(1));

            foreach ((string call, object?[] arguments) in new (string, object?[])[]
            {
                ("type", [freed, "stale", false]),
                ("type", [outside, "stale", false]),
                ("key", [freed, new List<object?> { new List<object?> { "A", 0L } }]),
                ("click", [freed, false, 0.0, 0.0, 1L, 1L, 0L]),
            })
            {
                Task<List<object?>> asked = running.AskAsync(call, arguments);
                Ui.PumpUntil(() => asked.IsCompleted, $"the helper's answer to {call} at a control gone", TimeSpan.FromMinutes(1));
                Assert.True(asked.Exception?.InnerException?.Message == "the node is gone",
                    $"{call} at a control gone came to {(asked.IsCompletedSuccessfully ? $"[{string.Join(", ", asked.Result)}]" : asked.Exception?.InnerException?.Message)}");
            }

            List<object?> typed = Ui.AwaitPumped(running.AskAsync("type", 0L, "here", false), "typing with no control aimed at", TimeSpan.FromMinutes(1));
            Assert.Equal("here", typed[^1]);
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Run of a game whose asset has no .import beside it, which a game cannot load, imports the project first: the
    /// asset is written with none, opening the folder makes none (no Godot editor starts at a script's opening), and Run's
    /// import makes it for the game, which loads the asset.</summary>
    [AvaloniaFact]
    public void Run_of_a_game_whose_asset_has_no_import_file_imports_it_first_and_the_game_loads_it()
    {
        string icon = Path.Combine(PathNames.Canonical(game.Folder), "icon.svg");
        File.WriteAllText(icon, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\"><rect width=\"16\" height=\"16\" fill=\"#478cbf\"/></svg>\n");
        File.WriteAllText(TickerPath, "extends Node\n\n\nfunc _ready() -> void:\n\tprint(\"icon \", load(\"res://icon.svg\") != null)\n");
        File.Delete(icon + ".import");
        MainWindow window = Open();
        try
        {
            Assert.False(File.Exists(icon + ".import"), "Opening the game's folder made a .import beside icon.svg before Run");

            Choose(window);
            Ui.Launched(window, window.RunAsync(), "the game's import and start", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => Run(window).Contains("icon ", StringComparison.Ordinal), () => $"the game to say whether it loaded its icon ({window.Status}; {Run(window)})", TimeSpan.FromMinutes(1));
            Assert.Contains("icon true", Run(window), StringComparison.Ordinal);
            Assert.True(File.Exists(icon + ".import"), "Run's import of the game's assets left no .import beside icon.svg for the game");
            Stop(window);
        }
        finally
        {
            End(window);
            File.Delete(icon);
            File.Delete(icon + ".import");
        }
    }

    /// <summary>Run's import of a game whose editor plugin saves the project's settings leaves project.godot byte for byte as
    /// it was: Godot's save is its own by the safe save's file the folder's watcher hears, a word that may come only after the
    /// import has ended, and is put back before the game starts.</summary>
    [AvaloniaFact]
    public void Run_puts_back_the_project_settings_an_editor_plugin_saves_in_its_import()
    {
        string folder = PathNames.Canonical(game.Folder);
        string settings = Path.Combine(folder, "project.godot");
        string icon = Path.Combine(folder, "icon.svg");
        string marker = Path.Combine(folder, ".godot", "saver_saved");
        string original = File.ReadAllText(settings);
        File.WriteAllText(settings, original + "\n[editor_plugins]\n\nenabled=PackedStringArray(\"res://addons/saver/plugin.cfg\")\n");
        TestFolders.Write(folder, Path.Combine("addons", "saver", "plugin.cfg"), "[plugin]\n\nname=\"Saver\"\ndescription=\"Saves the settings.\"\nauthor=\"Tests\"\nversion=\"1.0\"\nscript=\"plugin.gd\"\n");
        TestFolders.Write(folder, Path.Combine("addons", "saver", "plugin.gd"), "@tool\nextends EditorPlugin\n\n\nfunc _enter_tree() -> void:\n\tProjectSettings.save()\n"
            + "\tFileAccess.open(\"res://.godot/saver_saved\", FileAccess.WRITE).store_string(\"saved\")\n");
        File.WriteAllText(icon, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\"><rect width=\"16\" height=\"16\" fill=\"#478cbf\"/></svg>\n");
        byte[] before = File.ReadAllBytes(settings);
        MainWindow window = Open();
        try
        {
            Choose(window);
            Ui.Launched(window, window.RunAsync(), "the game's import and start", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => Run(window).Contains("tick 20", StringComparison.Ordinal), () => $"the game to say its word ({window.Status}; {Run(window)})", TimeSpan.FromMinutes(1));
            Assert.True(File.Exists(marker), "The plugin never ran in Run's import, so nothing here saved project.godot");
            Assert.True(File.ReadAllBytes(settings).AsSpan().SequenceEqual(before), $"project.godot as it was, not as Godot's import saved it:\n{File.ReadAllText(settings)}");
            Stop(window);
        }
        finally
        {
            End(window);
            File.WriteAllText(settings, original);
            Directory.Delete(Path.Combine(folder, "addons"), recursive: true);
            File.Delete(marker);
            File.Delete(icon);
            File.Delete(icon + ".import");
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

    /// <summary>A script edited and left unsaved while Run runs the game with Hot Reload: Apply Hot Reload Changes, greyed
    /// while nothing is unsaved, is offered, and an agent's hot_reload saves the script, saying so, and the game then says
    /// the edit's words.</summary>
    [AvaloniaFact]
    public void An_agents_hot_reload_saves_the_games_unsaved_script_and_the_game_takes_the_edit()
    {
        MainWindow window = Open();
        try
        {
            Choose(window);
            Ui.Launched(window, window.Builds.RunAsync(hotReload: true), "the game's start with Hot Reload on", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => Run(window).Contains("tick 40", StringComparison.Ordinal), () => $"the script to run ({Run(window)})", TimeSpan.FromMinutes(1));
            Assert.Equal(DebugController.ScriptsReloadAsSaved("Ticker on Godot"), window.Actions.WhyNot(AppActions.ApplyHotReloadChanges, window.Actions.Context()));
            Typing.Replace(window, "print(word, \" \", counter)", "print(\"unsaved \", counter)");
            Assert.True(window.EditorArea.Active!.IsModified, "The script's edit was saved as it was typed");
            Assert.Null(window.Actions.WhyNot(AppActions.ApplyHotReloadChanges, window.Actions.Context()));

            Passenger.App.Mcp.AgentCall call = Passenger.App.Mcp.AgentCall.Begin(null, null, null, Passenger.App.Mcp.McpTools.HotReload, "Hot Reload", "a test's agent", "the test", traced: false);
            Passenger.App.Mcp.HotReloadInfo answer;
            try
            {
                answer = Ui.AwaitPumped(new Passenger.App.Mcp.PassengerTools(window).HotReload(), "an agent's hot_reload", TimeSpan.FromMinutes(1));
            }
            finally
            {
                Ui.Await(call.DisposeAsync().AsTask().ContinueWith(_ => true, TaskScheduler.Default), "the agent's call to end");
            }
            Assert.True(answer is { Result: "applied", Files: ["ticker.gd"] } && answer.Said == "Saved ticker.gd: Godot reloads it in Ticker on Godot",
                $"hot_reload with the game's script unsaved answered {answer.Result}: {answer.Said} ({string.Join(", ", answer.Files)})");
            Assert.False(window.EditorArea.Active!.IsModified, "hot_reload left the game's script unsaved");
            Ui.WaitFor(() => Run(window).Contains("unsaved ", StringComparison.Ordinal), () => $"the game to say what the saved script says ({window.Status}; {Run(window)})", TimeSpan.FromMinutes(1));
            Assert.Equal(DebugController.ScriptsReloadAsSaved("Ticker on Godot"), window.Actions.WhyNot(AppActions.ApplyHotReloadChanges, window.Actions.Context()));
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

    /// <summary>Debug with the configuration's external console runs the game in a tab of the Terminal window, which shows what
    /// it says rather than the Debug window's console, its GDScript session still stopping it at a breakpoint; Stop ends it
    /// there.</summary>
    [AvaloniaFact]
    public void Debug_with_the_external_console_runs_the_game_in_the_terminal_and_a_breakpoint_still_stops_it()
    {
        MainWindow window = Open();
        try
        {
            Choose(window, ConsoleKind.Terminal);
            Ui.Launched(window, window.DebugAsync(), "the game's start in the terminal under the debugger", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.Debugger.Session is { Engine: DebugEngine.GDScript, Terminal: { } shown } && TerminalText(shown).Contains("tick 20", StringComparison.Ordinal),
                () => $"the game to say its word in the terminal ({Sessions(window)}; {Ui.Said(window)}; {(window.Debugger.Session?.Terminal is { } run ? TerminalText(run) : "no terminal")})",
                TimeSpan.FromMinutes(1));
            TerminalRun terminal = window.Debugger.Session!.Terminal!;
            Assert.DoesNotContain("tick ", Console(window), StringComparison.Ordinal);

            // The terminal took the keys as it showed the game.
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.Focus();
            int line = LineOf(editor, "counter += 1");
            editor.GoTo(new TextPosition(line, 1));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Assert.True(window.Debugger.Breakpoints.In(TickerPath).Any(b => b.Line == line), $"No breakpoint was set at ticker.gd's line {line + 1}: {window.Status}");
            Ui.WaitFor(() => window.Debugger.Session is { Engine: DebugEngine.GDScript, State: DebugState.Paused } && Stopped(editor) == line,
                () => $"the game in the terminal to stop at the breakpoint in ticker.gd's _process ({Sessions(window)}, {Ui.Said(window)})", TimeSpan.FromMinutes(1));
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging && terminal.Ended.IsCompleted && window.Session.Games.Count == 0,
                () => $"the game in the terminal to end with its session ({Sessions(window)}; {window.Status})", TimeSpan.FromMinutes(1));
            Assert.True(Processes.Gone(terminal.ProcessId), $"Stop left the game running in the terminal: {Processes.Describe(terminal.ProcessId)}");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Attach Profiler to the game Run runs records its frames as Godot's own profiler measures them, its script's
    /// _process among them, and says the run has no samples: the game runs no .NET code, so no EventPipe session is asked of it.</summary>
    [AvaloniaFact]
    public void Attach_Profiler_to_the_game_Run_runs_records_its_frames_alone()
    {
        MainWindow window = Open();
        try
        {
            Choose(window);
            Ui.Launched(window, window.RunAsync(), "the game's start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => Run(window).Contains("tick 20", StringComparison.Ordinal) && window.Session.Games is [{ ProcessId: int }],
                () => $"the game to run, connected ({window.Status}; {Run(window)})", TimeSpan.FromMinutes(1));
            Assert.Null(Ui.Await(window.AttachProfilerAsync(), "the profiler's attach to the game", TimeSpan.FromMinutes(1)));
            Assert.Equal(Passenger.App.Profiling.ProfilerCommands.FramesAloneSaid("Ticker on Godot"), window.Status);
            int attached = Counted(Run(window));
            Ui.WaitFor(() => Counted(Run(window)) > attached + 200, () => $"the profiled game to run its script ({Run(window)})", TimeSpan.FromMinutes(1));
            Assert.Equal(Passenger.Profiler.ProfilingSession.NoHeapOfScripts, window.Actions.WhyNot(AppActions.MemorySnapshot, window.Actions.Context()));

            Passenger.App.Profiling.SnapshotRead read = Ui.Await(window.SnapshotAsync(), "the game's snapshot", TimeSpan.FromMinutes(2));
            Assert.True(read.Snapshot is { FramesAlone: true, Samples: 0, Game: not null, GameFrames.Count: > 100 },
                $"{read.Message}: {read.Snapshot?.GameFrames.Count} of Godot's frames, {read.Snapshot?.Samples} samples, {read.Snapshot?.Game?.Why}");
            Assert.True(window.IsRunning, $"The game ended with its profile, where an attached profiler leaves it running ({window.Status})");
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>The last count the game said (its word and its frame), or 0.</summary>
    private static int Counted(string said) => said.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("tick ", StringComparison.Ordinal))
        .Select(l => int.TryParse(l["tick ".Length..], System.Globalization.CultureInfo.InvariantCulture, out int count) ? count : 0).LastOrDefault();

    private static string TerminalText(TerminalRun run) =>
        string.Join('\n', run.Session.Screen.Scrollback.Select(l => l.Text()).Concat(run.Session.Screen.Lines.Select(l => l.Text())));

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

    /// <summary>A copy of the game's generated configuration chosen, Godot run headless with the run's user folders, in the
    /// console given.</summary>
    private void Choose(MainWindow window, ConsoleKind console = ConsoleKind.Internal)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Generated && c.Name == "Ticker"),
            () => $"the game's configuration ({string.Join(", ", window.Configurations.Select(c => c.Name))})", TimeSpan.FromMinutes(1));
        RunConfiguration generated = window.Configurations.Single(c => c.Generated && c.Name == "Ticker");
        window.SetConfigurations([generated with { Name = "Ticker on Godot", Generated = false, Arguments = "--headless", Environment = new(game.Homes), Console = console }],
            "Ticker on Godot");
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
