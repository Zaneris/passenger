using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Godot 4 game's GDScript as a person debugs it beside its C#, through the game's own debug connection: a
/// breakpoint in a script's <c>_process</c> stops the game with its locals in Variables, a step goes to the next line, an
/// evaluation answers in the frame, a script error stops it where it was raised; a saved script reaches the game Run runs
/// with Hot Reload. Godot runs headless, its user folders the run's. Without a Godot 4 .NET build each test skips saying why. Debugged, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotScriptDebuggingTests : IClassFixture<GodotScriptDebuggingTests.Game>
{
    private readonly Game game;

    public GodotScriptDebuggingTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot is null || game.Godot.WhyNot is not null, game.Skipped);
        game.Reset();
    }

    /// <summary>The scene's C# root, which says it is ready.</summary>
    private const string Main = """
        using Godot;

        public partial class Main : Node
        {
            public override void _Ready()
            {
                GD.Print("main ready");
            }
        }

        """;

    /// <summary>The GDScript of the root's child: each frame counts and doubles, every tenth says so, and every 25th fails at a
    /// null where the project holds a fail.txt.</summary>
    private const string Ticker = "extends Node\n\nvar counter := 0\n\n\nfunc _process(_delta: float) -> void:\n\tcounter += 1\n"
        + "\tvar doubled := twice(counter)\n\tif counter % 10 == 0:\n\t\tprint(\"ticker \", doubled)\n"
        + "\tif counter % 25 == 0 and FileAccess.file_exists(\"res://fail.txt\"):\n\t\tfail()\n\n\n"
        + "func twice(value: int) -> int:\n\treturn value * 2\n\n\nfunc fail() -> void:\n\tvar missing: Node = null\n\tprint(missing.get_name())\n";

    private const string Scene = """
        [gd_scene load_steps=3 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1"]
        [ext_resource type="Script" path="res://ticker.gd" id="2"]

        [node name="Main" type="Node"]
        script = ExtResource("1")

        [node name="Ticker" type="Node" parent="."]
        script = ExtResource("2")

        """;

    private string TickerPath => Path.Combine(PathNames.Canonical(game.Folder), "ticker.gd");

    /// <summary>A window on the project with a file open, no C# language server, and the Godot editor it starts for GDScript
    /// keeping its user folders the run's.</summary>
    private MainWindow Open(string file)
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, GodotEnvironment = game.Homes };
        window.Show();
        window.OpenFolder(game.Folder);
        Assert.Null(window.OpenFile(file));
        Ui.Settle();
        return window;
    }

    /// <summary>A breakpoint on _process's call of twice stops the game there in a GDScript tab of its own beside the game's,
    /// the breakpoint bound, Variables showing the frame's locals and self; Step Over goes to the next line, the local it set
    /// shown; an evaluation in the frame calls the script's own method; and the game, its breakpoint gone, runs on.</summary>
    [AvaloniaFact]
    public void A_breakpoint_in_a_scripts_process_stops_the_game_with_its_locals_a_step_goes_to_the_next_line_and_an_evaluation_answers()
    {
        MainWindow window = Open(TickerPath);
        try
        {
            Choose(window);
            CodeEditor editor = window.EditorArea.CodeEditor;
            int line = LineOf(editor, "var doubled := twice(counter)");
            editor.GoTo(new TextPosition(line, 1));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the game's build and start under the debugger", TimeSpan.FromMinutes(3));
            string tab = DebugController.ScriptsTab("Spinner on Godot");
            Ui.WaitFor(() => window.Debugger.Session is { Engine: DebugEngine.GDScript, State: DebugState.Paused } && Stopped(editor) == line,
                () => $"the game to stop at the breakpoint in ticker.gd's _process ({Sessions(window)}, {Ui.Said(window)}; {window.Debugger.Output()})", TimeSpan.FromMinutes(2));
            Assert.Contains(window.Debugger.Tabs, t => t.Name == tab && t.Companion?.Name == "Spinner on Godot");
            LineBreakpoint set = Assert.Single(window.Debugger.Breakpoints.In(TickerPath));
            Assert.Equal("bound", BreakpointStates.StateOf(set, window.Debugger.IsDebugging, muted: false));

            DebugModel view = window.BottomPanel.Debug;
            Ui.WaitFor(() => view.Tree.Rows.Any(r => r.Depth == 0 && r.Name == "_delta" && r.Type == "float") && view.Tree.Rows.Any(r => r.Depth == 0 && r.Name == "self"),
                () => $"the frame's locals and self in Variables ({Rows(view)})");
            Assert.Contains(view.Tree.Rows, r => r.Depth == 0 && r.Name == "doubled" && r.Value == "null");

            Ui.Run(window, AppActions.StepOver);
            Ui.WaitFor(() => window.Debugger.Session is { State: DebugState.Paused } && Stopped(editor) == line + 1,
                () => $"Step Over to go to the next line, {line + 2} ({Sessions(window)}, the editor at {Stopped(editor) + 1})");
            Ui.WaitFor(() => view.Tree.Rows.Any(r => r.Depth == 0 && r.Name == "doubled" && r.Value != "null"), () => $"the local the step set ({Rows(view)})");

            Ui.Run(window, AppActions.Evaluate);
            TextBox evaluate = window.DebugWindow.GetVisualDescendants().OfType<TextBox>().Single(b => b.IsKeyboardFocusWithin);
            window.KeyTextInput("twice(21)");
            Assert.Equal("twice(21)", evaluate.Text);
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => view.Tree.Rows.Any(r => r.Name == "twice(21)" && r.Value == "42"), () => $"the evaluation of the script's own method ({Rows(view)})");

            editor.GoTo(new TextPosition(line, 1));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => Console(window, "Spinner on Godot").Contains("ticker ", StringComparison.Ordinal) && window.Debugger.Tabs.First(t => t.Name == tab).Session?.State == DebugState.Running,
                () => $"the game to run on past the breakpoint taken away ({Sessions(window)}; {Console(window, "Spinner on Godot")})", TimeSpan.FromMinutes(1));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A script error stops the game where it was raised, as Godot's own editor stops it, the status bar saying the
    /// error; a step is refused there, and with GDScript Errors turned off the game runs on past the next.</summary>
    [AvaloniaFact]
    public void A_script_error_stops_the_game_where_it_was_raised_and_with_GDScript_Errors_off_it_runs_on()
    {
        File.WriteAllText(Path.Combine(game.Folder, "fail.txt"), "fail\n");
        MainWindow window = Open(TickerPath);
        try
        {
            Choose(window);
            CodeEditor editor = window.EditorArea.CodeEditor;
            int raised = LineOf(editor, "print(missing.get_name())");
            Ui.Launched(window, window.DebugAsync(), "the game's build and start under the debugger", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => window.Debugger.Session is { Engine: DebugEngine.GDScript, State: DebugState.Paused, StopReason: Passenger.Dap.StopReasons.Exception }
                && Stopped(editor) == raised, () => $"the game to stop at its script error ({Sessions(window)}, {Ui.Said(window)})", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => (window.Status ?? string.Empty).Contains("Cannot call method 'get_name' on a null value", StringComparison.Ordinal), () => $"the error in the status bar ({window.Status})");

            Assert.Equal(Passenger.App.Godot.GodotScriptAdapter.NoStepFromError, window.Actions.WhyNot(AppActions.StepOver, window.Actions.Context()));
            Assert.Null(window.Actions.WhyNot(AppActions.Resume, window.Actions.Context()));

            BreakpointStore store = window.Debugger.Breakpoints;
            store.Update(store.ScriptErrors, enabled: false, store.ScriptErrors.Suspend, null, null, null);
            Ui.Run(window, AppActions.Resume);
            int said = Console(window, "Spinner on Godot").Split("ticker ").Length;
            Ui.WaitFor(() => Console(window, "Spinner on Godot").Split("ticker ").Length > said + 6,
                () => $"the game to run on past its errors with GDScript Errors off ({Sessions(window)}; {Console(window, "Spinner on Godot")})", TimeSpan.FromMinutes(1));
            Assert.NotEqual(DebugState.Paused, window.Debugger.Tabs.First(t => t.Name == DebugController.ScriptsTab("Spinner on Godot")).Session?.State);
        }
        finally
        {
            window.Debugger.Breakpoints.Update(window.Debugger.Breakpoints.ScriptErrors, enabled: true, true, null, null, null);
            End(window);
        }
    }

    /// <summary>A GDScript file saved while Run runs the game with Hot Reload reaches the game: what the script prints after is
    /// the edit's, its counter going on from where it was, and the Run console says what Godot's reload keeps.</summary>
    [AvaloniaFact]
    public void A_saved_script_reaches_the_game_Run_runs_with_Hot_Reload_its_members_kept()
    {
        MainWindow window = Open(TickerPath);
        try
        {
            Choose(window);
            Ui.Launched(window, window.Builds.RunAsync(hotReload: true), "the game's build and start with Hot Reload on", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => Run(window).Contains("ticker 40", StringComparison.Ordinal), () => $"the script to run ({Run(window)})", TimeSpan.FromMinutes(1));
            Typing.Replace(window, "print(\"ticker \", doubled)", "print(\"tocker \", counter)");
            Ui.Run(window, AppActions.SaveAll);
            Ui.WaitFor(() => Run(window).Contains("tocker ", StringComparison.Ordinal), () => $"the game to print what the saved script prints ({window.Status}; {Run(window)})", TimeSpan.FromMinutes(1));
            string after = Run(window)[Run(window).IndexOf("tocker ", StringComparison.Ordinal)..].Split('\n')[0];
            Assert.True(int.Parse(after["tocker ".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture) > 20, $"The counter started again after the reload: {after}");
            Assert.Contains($"Hot Reload: {BuildRunController.ScriptReloadKeeps}", Run(window), StringComparison.Ordinal);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>The line (from zero) the editor shows the program stopped at, or -1.</summary>
    private static int Stopped(CodeEditor editor) => editor.LineMarks.FirstOrDefault(m => m.Kind == LineMarkKind.ExecutionPoint) is { } mark ? mark.Line : -1;

    private static string Sessions(MainWindow window) =>
        string.Join(", ", window.Debugger.Tabs.Select(t => $"{t.Name}: {t.Session?.State.ToString() ?? "none"}"));

    private static string Rows(DebugModel view) => string.Join("; ", view.Tree.Rows.Where(r => r.Depth == 0).Select(r => $"{r.Name} = {r.Value} ({r.Type})"));

    /// <summary>The game's generated configuration, Godot run headless with the run's user folders.</summary>
    private RunConfiguration Configured(MainWindow window)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Generated && c.Name == "Spinner"),
            () => $"the Godot project's configuration ({string.Join(", ", window.Configurations.Select(c => c.Name))})", TimeSpan.FromMinutes(1));
        RunConfiguration generated = window.Configurations.Single(c => c.Generated && c.Name == "Spinner");
        return generated with { Name = "Spinner on Godot", Generated = false, Arguments = "--headless", Environment = new(game.Homes) };
    }

    private void Choose(MainWindow window) => window.SetConfigurations([Configured(window)], "Spinner on Godot");

    private static int LineOf(CodeEditor editor, string code)
    {
        int index = Array.FindIndex(editor.Document.Snapshot.ToString().Split('\n'), l => l.Contains(code, StringComparison.Ordinal));
        Assert.True(index >= 0, $"The script has no line of {code}");
        return index;
    }

    private static string Console(MainWindow window, string tab) =>
        window.Debugger.Tabs.FirstOrDefault(t => t.Name == tab)?.Session is { } session ? window.Debugger.Output(session).ToString() : string.Empty;

    private static string Run(MainWindow window)
    {
        window.BottomPanel.Run.Flush();
        return window.BottomPanel.Run.Document.Snapshot.ToString();
    }

    private static void End(MainWindow window)
    {
        if (window.Debugger.IsDebugging || window.IsRunning)
        {
            Assert.Null(window.Actions.Run(AppActions.StopAll));
            Ui.WaitFor(() => !window.Debugger.IsDebugging && !window.IsRunning, () => $"the game to end ({Sessions(window)})", TimeSpan.FromMinutes(1));
        }
        window.Close();
    }

    /// <summary>A Godot 4 C# project whose scene's root runs C# and its child GDScript, on the Godot found, restored from
    /// its own packages.</summary>
    public sealed class Game : SharedProject
    {
        public Game()
            : base("godot-scripts-")
        {
            Godot = GodotExecutables.Find(null);
            Homes = GodotTests.Game.Home(Path.Combine(Folder, "..", Path.GetFileName(Folder) + "-home"));
            if (Godot is not { WhyNot: null, Version: { } version, Packages: { } packages })
            {
                return;
            }
            GodotTests.Game.Make(Folder, version, packages, Main, Scene);
            File.WriteAllText(PathOf("ticker.gd"), Ticker);
        }

        public GodotFound? Godot { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build to run these tests"
            : $"The Godot found cannot run a C# game: {Godot.WhyNot}";

        public IReadOnlyDictionary<string, string> Homes { get; }

        public override ValueTask InitializeAsync() => Godot is { WhyNot: null } ? base.InitializeAsync() : ValueTask.CompletedTask;
    }
}
