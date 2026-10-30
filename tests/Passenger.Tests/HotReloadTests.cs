using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App.Editors;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Hot Reload while debugging (Edit and Continue), as a person uses it: a program launched with Debug, its code
/// edited in the editor, paused or running, and Apply Hot Reload Changes (Alt+F10) or the next resume putting the edits into
/// the program, which then prints what the new code prints; an edit the runtime cannot take named at its line in Problems'
/// Hot Reload tab, nothing applied, and the program restarted with its breakpoints. The tests share one project
/// (<see cref="Greetings"/>): restored once, its warm language server and the build the last test left, each test's window
/// never saving what it typed. Debugged, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class HotReloadTests : IClassFixture<HotReloadTests.Greetings>, IClassFixture<HotReloadTests.Plugins>, IClassFixture<HotReloadTests.Spins>
{
    private readonly Greetings project;
    private readonly Plugins plugins;
    private readonly Spins spins;

    public HotReloadTests(Greetings project, Plugins plugins, Spins spins)
    {
        this.project = project;
        this.plugins = plugins;
        this.spins = spins;
        project.Reset();
        plugins.Reset();
        spins.Reset();
    }

    /// <summary>Greets once every 100 ms, numbered, until stopped.</summary>
    private const string Program = "for (int i = 1; ; i++)\n{\n    Greeter.Greet(i);\n    Thread.Sleep(100);\n}\n";

    private const string Greeter = "static class Greeter\n{\n    public static void Greet(int i)\n    {\n        Console.WriteLine($\"Hello {i}\");\n    }\n}\n";

    private string ProgramPath => Path.Combine(PathNames.Canonical(project.Folder), "Program.cs");

    private string GreeterPath => Path.Combine(PathNames.Canonical(project.Folder), "Greeter.cs");

    /// <summary>An edit applied while paused before a call is what that call runs; then, paused in the edited method, a step
    /// after another edit to it applies that one with no Apply, runs the paused statement in the new code and stops at the
    /// statement the edit added.</summary>
    [AvaloniaFact]
    public void An_edit_applied_while_paused_runs_from_the_next_call_and_a_step_after_an_edit_goes_through_the_edited_code()
    {
        MainWindow window = Open();
        try
        {
            window.OpenFile(ProgramPath);
            window.EditorArea.CodeEditor.GoTo(new TextPosition(2, 4));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Debug);
            Paused(window, "the program to stop at the breakpoint");

            window.OpenFile(GreeterPath);
            Replace(window, "Hello", "Howdy");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            HotReloadOutcome applied = Applied(window);
            Assert.True(applied.Result == HotReloadResult.Applied, $"The edit should apply while paused: {applied.Said}");
            Assert.Contains(GreeterPath, applied.Files);

            window.OpenFile(ProgramPath);
            window.EditorArea.CodeEditor.GoTo(new TextPosition(2, 4));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Resume);
            // The call the program was paused before is the first to run the new code.
            Ui.WaitFor(() => Output(window).Contains("Howdy 2", StringComparison.Ordinal), $"the program to print what the edited code prints ({Output(window)})");
            Assert.Contains("Howdy 1", Output(window), StringComparison.Ordinal);
            Assert.DoesNotContain("Hello", Output(window), StringComparison.Ordinal);

            window.OpenFile(GreeterPath);
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.GoTo(new TextPosition(4, 8));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Paused(window, "the program to stop in Greet");
            string? greeting = null;
            Ui.WaitFor(() => (greeting = window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == "i")?.Value) is not null,
                "the paused greeting's number in Variables");
            HotReloadOutcome before = window.Debugger.LastApplied!;
            Replace(window, "        Console.WriteLine($\"Howdy {i}\");", "        Console.WriteLine($\"Hi {i}\");\n        Console.WriteLine(\"added\");");
            Ui.Run(window, AppActions.StepOver);
            Ui.WaitFor(() => window.Debugger.LastApplied != before && editor.LineMarks.Any(m => m.Kind == LineMarkKind.ExecutionPoint && m.Line == 5),
                $"the step to stop at the added statement ({window.Debugger.LastApplied?.Said}; marks {string.Join(", ", editor.LineMarks)})");
            Assert.Equal(HotReloadResult.Applied, window.Debugger.LastApplied!.Result);
            Assert.Contains($"Hi {greeting}", Output(window), StringComparison.Ordinal);
            Ui.Run(window, AppActions.StepOver);
            Ui.WaitFor(() => Output(window).Contains("added", StringComparison.Ordinal), $"the added statement to run ({Output(window)})");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>An edit typed while the debugger starts the program, its runtime held at its start, is one Apply Hot Reload
    /// Changes applies once the program runs: the program then greets as the edited code does.</summary>
    [AvaloniaFact]
    public void An_edit_made_while_the_debugger_starts_the_program_is_applied_once_it_runs()
    {
        using Debugging.Helpers.StartHold hold = new();
        MainWindow window = Open();
        int program = 0;
        try
        {
            window.SetConfigurations([new Passenger.App.Run.RunConfiguration
            {
                Name = "Greetings held",
                Project = Path.Combine(PathNames.Canonical(project.Folder), "Greetings.csproj"),
                Environment = new Dictionary<string, string>(hold.Environment),
            }], "Greetings held");
            Task<int> held = hold.Held(TimeSpan.FromMinutes(3));
            // Not settled: the start holds the window's background work for as long as the debugger waits for the program.
            Assert.True(window.Perform(AppActions.Debug), $"Debug did not start: {Ui.Said(window)}");
            program = Ui.AwaitPumped(held, "the build, then the program's runtime to come to the hold", TimeSpan.FromMinutes(3));
            DebugSession starting = window.Debugger.Session ?? throw new InvalidOperationException($"No session while the program was held: {window.Status}");
            Assert.Equal(DebugState.Starting, starting.State);
            window.OpenFile(GreeterPath);
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.Focus();
            editor.Select(new TextRange(editor.Document.Snapshot.ToString().IndexOf("Hello", StringComparison.Ordinal), "Hello".Length));
            window.KeyTextInput("Howdy");
            Assert.Contains("Howdy {i}", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

            Task letGo = hold.LetGo();
            Ui.PumpUntil(() => letGo.IsCompleted && starting.State != DebugState.Starting, "the program to run", TimeSpan.FromSeconds(60));
            Ui.WaitFor(() => Output(window).Contains("Hello 2", StringComparison.Ordinal), $"the program to greet as it was built ({Output(window)})");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            HotReloadOutcome applied = Applied(window);
            Assert.True(applied.Result == HotReloadResult.Applied, $"The edit made while the program started was not applied: {applied.Said}");
            Assert.Contains(GreeterPath, applied.Files);
            Ui.WaitFor(() => Output(window).Contains("Howdy", StringComparison.Ordinal), $"the program to greet as the edited code does ({Output(window)})");
        }
        finally
        {
            End(window);
            Processes.Kill(program);
        }
    }

    /// <summary>With Hot Reload on save, a saved edit reaches the debugged program with no Apply; a breakpoint on the line the
    /// edit added then stops it there, its new local in Variables and the evaluator compiling against the method as the update
    /// made it.</summary>
    [AvaloniaFact]
    public void With_apply_on_save_a_saved_edit_reaches_the_program_and_a_breakpoint_on_a_line_it_added_stops_there()
    {
        MainWindow window = Open();
        window.ApplySettings(window.Settings with { HotReloadOnSave = true });
        try
        {
            Ui.Run(window, AppActions.Debug);
            Ui.WaitFor(() => Output(window).Contains("Hello 2", StringComparison.Ordinal), "the program to greet");
            window.OpenFile(GreeterPath);
            Replace(window, "        Console.WriteLine($\"Hello {i}\");", "        string said = $\"Howdy {i}\";\n        Console.WriteLine(said);");
            Ui.Run(window, AppActions.Save);
            Ui.WaitFor(() => Output(window).Contains("Howdy", StringComparison.Ordinal), $"the saved edit to reach the program ({Output(window)})");
            Assert.Equal(HotReloadResult.Applied, window.Debugger.LastApplied!.Result);

            // Line 6, the WriteLine the edit moved down a line, is the program's code now.
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.GoTo(new TextPosition(5, 8));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Paused(window, "the program to stop at the breakpoint on the edited code");
            Assert.Contains(new LineMark(5, LineMarkKind.ExecutionPoint), editor.LineMarks);
            Assert.Contains(new LineMark(5, LineMarkKind.BreakpointBound), editor.LineMarks);
            Ui.WaitFor(() => window.BottomPanel.Debug.Tree.Rows.Any(r => r.Name == "said" && r.Value.Contains("Howdy", StringComparison.Ordinal)),
                "the edited method's new local in Variables");
            Dap.EvaluateResponse length = Ui.Await(window.Debugger.Session!.EvaluateAsync("said.Substring(0, 5) + i.ToString()", window.BottomPanel.Debug.Frames[0].Id, "watch"),
                "an expression in the edited method");
            Assert.StartsWith("\"Howdy", length.Result, StringComparison.Ordinal);
        }
        finally
        {
            window.ApplySettings(window.Settings with { HotReloadOnSave = false });
            End(window);
        }
    }

    /// <summary>The comment above the field holds breaks Roslyn counts and the editor does not: the edit is named at
    /// the editor's line 4, not the compiler's 8, which only the console's message in a compiler's shape names, as a
    /// compiler would. Then, with always restart off, the next rude edit asks, and Never stops the asking.</summary>
    [AvaloniaFact]
    public void A_rude_edit_is_named_at_its_line_and_restarts_the_program_with_it_and_with_always_restart_off_asks_until_never()
    {
        project.Write("Greeter.cs", $"static class Greeter\n{{\n    /* Pasted:{(char)0x2028}a{(char)0x2029}b{(char)0x85}c\rd */\n    static int count;\n\n"
            + "    public static void Greet(int i)\n    {\n        count++;\n        Console.WriteLine($\"Hello {i} ({count})\");\n    }\n}\n");
        MainWindow window = Open();
        try
        {
            window.OpenFile(ProgramPath);
            window.EditorArea.CodeEditor.GoTo(new TextPosition(2, 4));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Debug);
            Paused(window, "the program to stop at the breakpoint");
            DebugSession first = window.Debugger.Session!;

            window.OpenFile(GreeterPath);
            // A field's type the runtime cannot change, and a body it can: neither is applied.
            Replace(window, "static int count;", "static long count;");
            Replace(window, "Hello", "Howdy");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.LastHotReloadRestart is not null, "the program to be restarted for the edit");
            HotReloadOutcome outcome = window.Debugger.LastApplied ?? throw new InvalidOperationException("Hot Reload said nothing");
            Assert.True(outcome.Result == HotReloadResult.RestartNeeded, $"Changing a field's type needs a restart: {outcome.Said}");
            Passenger.App.Run.Problem rude = Assert.Single(outcome.Problems, p => p.Severity == Passenger.App.Run.ProblemSeverity.Error);
            Assert.Equal(GreeterPath, rude.Path);
            Assert.Equal(3, rude.Position.Line);
            Assert.StartsWith("ENC", rude.Code, StringComparison.Ordinal);
            Assert.Contains(rude, window.BottomPanel.Problems.HotReloadProblems!);
            Assert.Contains("Greeter.cs:4", outcome.Said, StringComparison.Ordinal);

            Ui.WaitFor(() => window.Debugger.Session is { } next && next != first && next.State == DebugState.Paused, "the restarted program to stop at the breakpoint");
            string message = $"{GreeterPath}(8,{rude.Position.Column + 1}): error {rude.Code}";
            Ui.WaitFor(() => window.BottomPanel.Debug.Console.Document.Snapshot.ToString().Contains(message, StringComparison.Ordinal),
                "the console's message for the edit, in a compiler's shape at the compiler's line 8");
            Assert.DoesNotContain("Howdy", Output(window), StringComparison.Ordinal);
            window.OpenFile(ProgramPath);
            window.EditorArea.CodeEditor.GoTo(new TextPosition(2, 4));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => Output(window).Contains("Howdy 2 (2)", StringComparison.Ordinal), $"the restarted program to run the edits ({Output(window)})");
            Assert.Contains("Howdy 1 (1)", Output(window), StringComparison.Ordinal);
            TestContext.Current.TestOutputHelper?.WriteLine($"Hot Reload restart for a rude edit took {window.LastHotReloadRestart!.Value.TotalSeconds:0.0} s");

            window.ApplySettings(window.Settings with { RestartOnRudeEdit = false });
            DebugSession restarted = window.Debugger.Session!;
            TimeSpan? restart = window.LastHotReloadRestart;
            window.OpenFile(GreeterPath);
            Replace(window, "static long count;", "static int count;");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            ChoiceDialog asked = Asked(window);
            Assert.Contains(MainWindow.RestartQuestion, Said(asked), StringComparison.Ordinal);
            Assert.Contains("Updating the type of field requires restarting the application", Said(asked), StringComparison.Ordinal);
            Ui.ClickButton(asked, "No");
            Assert.Same(restarted, window.Debugger.Session);
            Assert.Equal(DebugState.Running, restarted.State);
            Assert.Equal(restart, window.LastHotReloadRestart);

            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.ClickButton(Asked(window), "Never");
            HotReloadOutcome told = window.Debugger.LastApplied!;
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.Debugger.LastApplied != told, "the third Apply to say what it came to");
            Ui.Settle();
            Assert.Empty(window.OwnedWindows.OfType<ChoiceDialog>());
            Assert.Same(restarted, window.Debugger.Session);
            Assert.Equal(HotReloadResult.RestartNeeded, window.Debugger.LastApplied!.Result);
        }
        finally
        {
            window.ApplySettings(window.Settings with { RestartOnRudeEdit = true });
            End(window);
        }
    }

    /// <summary>A file saved since the build the program runs, its time left before that build's so the build before Debug
    /// takes the build as up to date: an edit to it reaches nothing, and Hot Reload says why in Roslyn's words after "No
    /// managed code changes to apply", in the status bar, a link to Problems' Hot Reload tab, and in the Debug console.</summary>
    [AvaloniaFact]
    public void An_edit_to_a_file_saved_since_the_build_the_program_runs_applies_nothing_and_says_why_in_Roslyns_words()
    {
        MainWindow window = Open();
        try
        {
            window.Build(Passenger.App.Run.BuildKind.Build);
            Ui.WaitFor(() => !window.IsBuilding, "the build");
            DateTime was = File.GetLastWriteTimeUtc(GreeterPath);
            File.WriteAllText(GreeterPath, Greeter.Replace("Hello", "Hi", StringComparison.Ordinal));
            File.SetLastWriteTimeUtc(GreeterPath, was);
            Ui.Run(window, AppActions.Debug);
            Ui.WaitFor(() => Output(window).Contains("Hello 2", StringComparison.Ordinal), $"the program built before the save to greet as built ({Output(window)})");

            window.OpenFile(GreeterPath);
            Replace(window, "Hi", "Howdy");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            HotReloadOutcome outcome = Applied(window);
            Assert.True(outcome.Result == HotReloadResult.NothingToApply, $"An edit to a file of a stale build should apply nothing: {outcome.Said}");
            Passenger.App.Run.Problem stale = outcome.Problems.FirstOrDefault(p => p.Code == "ENC1008")
                ?? throw new Xunit.Sdk.XunitException($"Roslyn did not say the project is stale: [{string.Join("; ", outcome.Problems.Select(p => $"{p.Code} {p.Message}"))}]");
            Assert.Equal(GreeterPath, stale.Path);
            Assert.True(outcome.Said == $"No managed code changes to apply: {stale.Message.TrimEnd('.')}.", $"Hot Reload did not say Roslyn's reason for applying nothing: {outcome.Said}");

            Ui.WaitFor(() => window.Status == outcome.Said, $"the status bar to say why nothing was applied, not \"{window.Status}\"");
            window.DockHost.Hide(ToolWindowIds.Problems);
            Ui.Click(window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == "StatusMessage"));
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Problems) && window.BottomPanel.Problems.Shown == ProblemsTab.HotReload,
                "A click on the status bar's words did not show Problems' Hot Reload tab");
            Assert.Contains(stale, window.BottomPanel.Problems.HotReloadProblems!);
            Assert.Contains($"Hot Reload: {outcome.Said}", window.BottomPanel.Debug.Console.Document.Snapshot.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Howdy", Output(window), StringComparison.Ordinal);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A module the program loads after an update has been applied (a plugin's, which nothing it was built with
    /// references) takes the edits made to its project from then on: Hot Reload applies them and the program prints what the
    /// edited plugin prints.</summary>
    [AvaloniaFact]
    public void A_module_loaded_after_an_update_takes_the_edits_to_its_project_from_then_on()
    {
        MainWindow window = plugins.Open(null);
        try
        {
            Ui.Run(window, AppActions.Debug);
            Ui.WaitFor(() => Output(window).Contains("Hello 2", StringComparison.Ordinal), $"the program to greet ({Output(window)})");
            window.OpenFile(plugins.PathOf(Path.Combine("App", "Greeter.cs")));
            Replace(window, "Hello", "Howdy");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            HotReloadOutcome first = Applied(window);
            Assert.True(first.Result == HotReloadResult.Applied, $"The program's own edit should apply: {first.Said}");
            Ui.WaitFor(() => Output(window).Contains("Howdy", StringComparison.Ordinal), $"the program to greet as edited ({Output(window)})");

            Ui.TypeToDebugged(window, "load");
            Ui.WaitFor(() => Output(window).Contains("Plugin ", StringComparison.Ordinal), $"the program to load its plugin ({Output(window)})");
            string speaker = Path.Combine(PathNames.Canonical(plugins.Folder), "Plugin", "Speaker.cs");
            window.OpenFile(speaker);
            Replace(window, "Plugin {i}", "Plugged {i}");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.Debugger.LastApplied != first, "Hot Reload to say what it applied of the plugin's edit");
            HotReloadOutcome later = window.Debugger.LastApplied!;
            Assert.True(later.Result == HotReloadResult.Applied, $"The edit to the plugin loaded after the first update should apply: {later.Said}");
            Assert.Contains(speaker, later.Files);
            Ui.WaitFor(() => Output(window).Contains("Plugged", StringComparison.Ordinal), $"the program to print what the edited plugin prints ({Output(window)})");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>An edit to a method that spins through a long loop, applied again and again as the program runs, now while
    /// the loop is under way, now while the program sleeps between spins: every update reaches the program, the next call
    /// running the new code and a spin under way going on in it at full speed to its end, and the runtime offers that
    /// spin's frame the new code a few times at most, never at every statement it runs.</summary>
    [AvaloniaFact]
    public void An_edit_applied_again_and_again_while_its_method_spins_in_a_loop_reaches_the_loop_and_never_leaves_it_crawling()
    {
        const int Applies = 8;
        string engineLog = Path.Combine(TestFolders.Create("hotreload-engine-"), "engine.log");
        string? kept = Environment.GetEnvironmentVariable(Passenger.Dap.AdapterEnvironment.EngineLogVariable);
        Environment.SetEnvironmentVariable(Passenger.Dap.AdapterEnvironment.EngineLogVariable, engineLog);
        MainWindow window = spins.Open(null);
        try
        {
            Ui.Run(window, AppActions.Debug);
            Ui.WaitFor(() => Ticks(window).Count >= 2, () => $"the program to spin ({Output(window)})");
            window.OpenFile(spins.PathOf("Spinner.cs"));
            for (int adds = 3; adds < 3 + Applies; adds++)
            {
                int seen = Ticks(window).Count;
                Ui.WaitFor(() => Ticks(window).Count > seen, () => $"a spin to end ({Output(window)})");
                bool spinning = adds % 2 == 1;
                if (spinning)
                {
                    Ui.Hold(TimeSpan.FromMilliseconds(Spins.SleepMs + 100), "the sleep after the spin to end, the next spin under way");
                }
                Replace(window, $"+ {adds - 1};", $"+ {adds};");
                HotReloadOutcome outcome = Ui.Await(window.Debugger.ApplyChangesAsync(), $"Hot Reload to apply + {adds}", TimeSpan.FromMinutes(1));
                Assert.True(outcome.Result == HotReloadResult.Applied, $"The edit to the spinning method should apply: {outcome.Said}");
                int before = Ticks(window).Count;
                Ui.WaitFor(() => Ticks(window).Skip(before).Contains(adds),
                    () => $"the program to go on in the new code (+ {adds}), applied {(spinning ? "mid-spin" : "between spins")} ({string.Join(" | ", Output(window).Split('\n').TakeLast(6))})");
                // Within three spins of the update, counted, not timed: one under way, or one told late, may end in the old code.
                int spun = Ticks(window).Skip(before).TakeWhile(t => t != adds).Count();
                Assert.True(spun <= 2, $"+ {adds}, applied {(spinning ? "mid-spin" : "between spins")}, came only after {spun} spins in the old code: {string.Join(", ", Ticks(window).Skip(before))}");
            }
            string log = Ui.Shared(engineLog);
            int offered = log.Split('\n').Count(l => l.Contains("Event: FunctionRemapOpportunity", StringComparison.Ordinal));
            string[] remaps = [.. log.Split('\n').Where(l => l.StartsWith("Remap of", StringComparison.Ordinal) || l.Contains("] Remap of", StringComparison.Ordinal))];
            TestContext.Current.TestOutputHelper?.WriteLine($"{offered} remap opportunities, {remaps.Length} remaps:\n{string.Join('\n', remaps)}");
            Assert.True(offered <= 3 * Applies, $"The runtime offered the new code {offered} times for {Applies} updates: a frame left in the old code crawled through it a stop at a time");
            Assert.True(remaps.Length > 0 && remaps.All(r => r.TrimEnd().EndsWith(": 0x00000000", StringComparison.Ordinal)),
                $"A spin under way as an update was applied should go on in the new code: {string.Join("; ", remaps)}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Passenger.Dap.AdapterEnvironment.EngineLogVariable, kept);
            End(window);
        }
    }

    /// <summary>After a step and a resume, an edit to the loop the program runs, applied as it runs, takes the loop's frame to
    /// the new code at the first statement the update left as it was: the next greeting is the edited one, its number
    /// following on, and the runtime offers the new code a few times, never at every statement of another pass through the
    /// old code.</summary>
    [AvaloniaFact]
    public void After_a_step_an_edit_applied_as_the_program_runs_goes_on_in_the_new_code_from_the_next_iteration()
    {
        string engineLog = Path.Combine(TestFolders.Create("hotreload-engine-"), "engine.log");
        string? kept = Environment.GetEnvironmentVariable(Passenger.Dap.AdapterEnvironment.EngineLogVariable);
        Environment.SetEnvironmentVariable(Passenger.Dap.AdapterEnvironment.EngineLogVariable, engineLog);
        MainWindow window = Open();
        try
        {
            window.OpenFile(ProgramPath);
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.GoTo(new TextPosition(2, 4));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Debug);
            Paused(window, "the program to stop at the greeting");
            Ui.Run(window, AppActions.StepOver);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && editor.LineMarks.Contains(new LineMark(3, LineMarkKind.ExecutionPoint)),
                $"the step to stop at the sleep (marks {string.Join(", ", editor.LineMarks)})");
            editor.GoTo(new TextPosition(2, 4));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => Output(window).Contains("Hello 3", StringComparison.Ordinal), $"the program to greet on ({Output(window)})");

            Replace(window, "Greeter.Greet(i);", "Greeter.Greet(-i);");
            HotReloadOutcome outcome = Ui.Await(window.Debugger.ApplyChangesAsync(), "Hot Reload to apply the edit to the loop", TimeSpan.FromMinutes(1));
            Assert.True(outcome.Result == HotReloadResult.Applied, $"The edit to the loop should apply: {outcome.Said}");
            Ui.WaitFor(() => Greeted(window).Any(g => g < 0), $"the program to greet as edited ({Output(window)})");
            List<int> greetings = Greeted(window);
            int edited = greetings.FindIndex(g => g < 0);
            Assert.True(edited > 0 && greetings[edited - 1] == -greetings[edited] - 1,
                $"The first greeting of the new code should follow on from the last of the old: {string.Join(", ", greetings)}");

            string log = Ui.Shared(engineLog);
            string[] offers = [.. log.Split('\n').Where(l => l.Contains("The old code runs on from", StringComparison.Ordinal) || l.Contains("Remap of", StringComparison.Ordinal))];
            int offered = log.Split('\n').Count(l => l.Contains("Event: FunctionRemapOpportunity", StringComparison.Ordinal));
            TestContext.Current.TestOutputHelper?.WriteLine($"{offered} remap opportunities:\n{string.Join('\n', offers)}");
            // At the update the loop's frame is in Sleep, rarely in Greet: three offers, or five, before the loop's i++ takes it.
            Assert.True(offered is > 0 and <= 5,
                $"The runtime offered the new code {offered} times: the frame went on through the old code, its thread taken as still stepping:\n{string.Join('\n', offers)}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Passenger.Dap.AdapterEnvironment.EngineLogVariable, kept);
            End(window);
        }
    }

    /// <summary>Paused at the sleep, two lines typed above the greeting and applied leave the loop's frame in its code from
    /// before: Set Next Statement onto a line the edit added is refused, saying why, and onto the greeting, two lines down
    /// now, stops there, the program then greeting again, whole, before going on in the new code.</summary>
    [AvaloniaFact]
    public void After_lines_applied_above_while_paused_set_next_statement_goes_to_a_moved_line_which_runs_whole()
    {
        MainWindow window = Open();
        try
        {
            window.OpenFile(ProgramPath);
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.GoTo(new TextPosition(3, 4));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Debug);
            Paused(window, "the program to stop at the sleep");
            Ui.WaitFor(() => Greeted(window).Count == 1, () => $"the first greeting ({Output(window)})");

            Replace(window, "Greeter.Greet(i);", "Console.WriteLine(\"first\");\nConsole.WriteLine(\"second\");\nGreeter.Greet(i);");
            HotReloadOutcome outcome = Ui.Await(window.Debugger.ApplyChangesAsync(), "Hot Reload to apply the lines added", TimeSpan.FromMinutes(1));
            Assert.True(outcome.Result == HotReloadResult.Applied, $"The lines added should apply: {outcome.Said}");
            Assert.Contains(new LineMark(5, LineMarkKind.ExecutionPoint), editor.LineMarks);

            editor.GoTo(new TextPosition(2, 4));
            Ui.Run(window, AppActions.SetNextStatement);
            Ui.WaitFor(() => window.Status?.Contains("Hot Reload changed the method", StringComparison.Ordinal) == true,
                () => $"Set Next Statement onto a line the old code lacks to be refused, saying why (status: {window.Status})");
            Assert.Contains(new LineMark(5, LineMarkKind.ExecutionPoint), editor.LineMarks);

            editor.GoTo(new TextPosition(4, 4));
            Ui.Run(window, AppActions.SetNextStatement);
            Ui.WaitFor(() => editor.LineMarks.Contains(new LineMark(4, LineMarkKind.ExecutionPoint)),
                () => $"the arrow at the greeting Set Next Statement went back to (marks {string.Join(", ", editor.LineMarks)}; status: {window.Status})");
            editor.GoTo(new TextPosition(5, 4));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => Greeted(window).Count >= 3, () => $"the program to greet on ({Output(window)})");
            Assert.True(Greeted(window).Take(3).SequenceEqual([1, 1, 2]),
                $"The greeting Set Next Statement went back to should run whole, then the new code: {Output(window)}");
            Assert.Contains("Hello 1\nfirst\nsecond\nHello 2", Output(window).Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Two lines typed above the greeting while an apply's update of another edit is being made, after Roslyn
    /// compiled that edit, are not the program's yet: a breakpoint on the greeting, two lines down, binds to the greeting's
    /// code, and Continue applies the two lines before the program goes on, which prints them and stops at the greeting.</summary>
    [AvaloniaFact]
    public void An_edit_typed_while_an_apply_works_is_applied_by_the_next_continue_and_a_breakpoint_below_it_binds_to_its_line()
    {
        MainWindow window = Open();
        try
        {
            window.OpenFile(ProgramPath);
            window.EditorArea.CodeEditor.GoTo(new TextPosition(2, 4));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Debug);
            Paused(window, "the program to stop before its first greeting");

            window.OpenFile(GreeterPath);
            CodeEditor editor = window.EditorArea.CodeEditor;
            Replace(window, "Hello", "Howdy");
            TaskCompletionSource emitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task held = window.CSharpService!.Session!.Server.HoldNextAnswer(Passenger.Lsp.LspMethods.DispatchExtensionMessage, Emits, emitted.Task);
            try
            {
                Ui.Run(window, AppActions.ApplyHotReloadChanges, settle: false);
                Ui.PumpUntil(() => held.IsCompleted, "Roslyn's update of the first edit, made and held", TimeSpan.FromMinutes(1));
                editor.Focus();
                editor.GoTo(new TextPosition(3, 5));
                Ui.Press(window, "ENTER", settle: false);
                window.KeyTextInput("Console.WriteLine(\"second\");");
                Ui.Press(window, "ENTER", settle: false);
                window.KeyTextInput("Console.WriteLine(\"third\");");
            }
            finally
            {
                emitted.SetResult();
            }
            HotReloadOutcome first = Applied(window);
            Assert.True(first.Result == HotReloadResult.Applied, $"The first edit should apply: {first.Said}");
            // The update showed the stop again, in Program.cs.
            window.OpenFile(GreeterPath);
            editor = window.EditorArea.CodeEditor;
            Assert.Equal("static class Greeter\n{\n    public static void Greet(int i)\n    {\n        Console.WriteLine(\"second\");\n        Console.WriteLine(\"third\");\n"
                + "        Console.WriteLine($\"Howdy {i}\");\n    }\n}\n", editor.Document.Snapshot.ToString());

            editor.GoTo(new TextPosition(6, 8));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.WaitFor(() => editor.LineMarks.Contains(new LineMark(6, LineMarkKind.BreakpointBound)),
                () => $"the breakpoint on the greeting, below the lines the program does not run yet, to bind to the greeting (marks {string.Join(", ", editor.LineMarks)})");

            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && editor.LineMarks.Contains(new LineMark(6, LineMarkKind.ExecutionPoint)),
                () => $"Continue to apply the two lines and the program to stop at the greeting (applied: {window.Debugger.LastApplied?.Said}; marks {string.Join(", ", editor.LineMarks)}; output: {Output(window)})");
            Assert.True(Output(window).Replace("\r\n", "\n", StringComparison.Ordinal).Contains("second\nthird\n", StringComparison.Ordinal),
                $"The program should run the lines typed while the first edit was applied before stopping at the greeting: {Output(window)}");
            Assert.DoesNotContain("Howdy", Output(window), StringComparison.Ordinal);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Whether a request to the language server asks Roslyn for Hot Reload's update.</summary>
    private static bool Emits(object parameters) =>
        parameters is Passenger.Lsp.RoslynExtension.DispatchWorkspaceMessageParams { MessageName: Passenger.Roslyn.Extension.HotReloadHandlers.Emit };

    /// <summary>The number of each greeting the program printed, in order.</summary>
    private static List<int> Greeted(MainWindow window) =>
        [.. Output(window).Split('\n').Select(l => l.Trim().Split(' ')).Where(w => w is ["Hello", _]).Select(w => int.Parse(w[1], System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>What each spin the program printed returned, in order.</summary>
    private static List<int> Ticks(MainWindow window) =>
        [.. Output(window).Split('\n').Select(l => l.Trim().Split(' ')).Where(w => w is ["tick", _, _, _]).Select(w => int.Parse(w[2], System.Globalization.CultureInfo.InvariantCulture))];

    private static ChoiceDialog Asked(MainWindow window)
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<ChoiceDialog>().Any(), "Hot Reload to ask whether to restart");
        return window.OwnedWindows.OfType<ChoiceDialog>().Single();
    }

    private static string Said(Avalonia.Controls.Window dialog) =>
        string.Join('\n', dialog.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text));

    /// <summary>Waits for the program to stop and the stop to be shown, its file brought forward, before the test types.</summary>
    private static void Paused(MainWindow window, string what)
    {
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.Debugger.Stop is not null, what);
        Ui.Settle();
    }

    private static HotReloadOutcome Applied(MainWindow window)
    {
        Ui.WaitFor(() => window.Debugger.LastApplied is not null, "Hot Reload to say what it applied");
        return window.Debugger.LastApplied!;
    }

    private static string Output(MainWindow window) => window.Debugger.Output().ToString();

    /// <summary>Selects the first place a text is written in the editor and types another over it, as a person does.</summary>
    private static void Replace(MainWindow window, string old, string typed) => Typing.Replace(window, old, typed);

    /// <summary>A window on the project, on the server the last test's left, which never saves what its test typed: what is
    /// on disk stays what the build was made from, but for what a build or a save the test makes writes.</summary>
    private MainWindow Open() => project.Open(null);

    private static void End(MainWindow window)
    {
        if (window.Debugger.IsDebugging)
        {
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        }
        window.Close();
    }

    /// <summary>A program that greets for ever, Greeter's one method printing each greeting.</summary>
    public sealed class Greetings : SharedProject
    {
        public Greetings()
            : base("hotreload-")
        {
            File.WriteAllText(PathOf("Greetings.csproj"),
                TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>", "<Nullable>enable</Nullable>"));
            File.WriteAllText(PathOf("Program.cs"), Program);
            File.WriteAllText(PathOf("Greeter.cs"), Greeter);
            // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(PathOf("bin"));
        }
    }

    /// <summary>A program that spins for ever, each spin a loop of millions of iterations, printing what it returns and how
    /// long it took, then sleeping.</summary>
    public sealed class Spins : SharedProject
    {
        public const int SleepMs = 300;

        public Spins()
            : base("hotreload-spins-")
        {
            File.WriteAllText(PathOf("Spins.csproj"),
                TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>", "<Nullable>enable</Nullable>"));
            File.WriteAllText(PathOf("Program.cs"), $$"""
                for (int i = 1; ; i++)
                {
                    System.Diagnostics.Stopwatch spun = System.Diagnostics.Stopwatch.StartNew();
                    long value = Spinner.Spin();
                    Console.WriteLine($"tick {i} {value} {spun.ElapsedMilliseconds}");
                    Thread.Sleep({{SleepMs}});
                }

                """);
            File.WriteAllText(PathOf("Spinner.cs"), """
                static class Spinner
                {
                    public static long Spin()
                    {
                        long spin = 0;
                        for (int k = 0; k < 400_000_000; k++)
                        {
                            spin += k % 7;
                        }
                        return spin % 1 + 2;
                    }
                }

                """);
            Directory.CreateDirectory(PathOf("bin"));
        }
    }

    /// <summary>A program that greets for ever and, once "load" is typed into its console, loads a plugin from its build,
    /// which nothing it is built with references (built first all the same), and has it speak at every greeting.</summary>
    public sealed class Plugins : SharedProject
    {
        public Plugins()
            : base("hotreload-plugins-")
        {
            File.WriteAllText(PathOf("Plugins.slnx"), "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Plugin/Plugin.csproj\" />\n</Solution>\n");
            Directory.CreateDirectory(PathOf("App"));
            Directory.CreateDirectory(PathOf("Plugin"));
            File.WriteAllText(PathOf(Path.Combine("App", "App.csproj")), TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>", "<Nullable>enable</Nullable>")
                .With("  <ItemGroup>\n    <ProjectReference Include=\"../Plugin/Plugin.csproj\" ReferenceOutputAssembly=\"false\" />\n  </ItemGroup>\n"));
            File.WriteAllText(PathOf(Path.Combine("App", "Program.cs")), """
                using System.Reflection;

                ManualResetEventSlim asked = new();
                new Thread(() =>
                {
                    while (Console.ReadLine() is { } line)
                    {
                        if (line == "load")
                        {
                            asked.Set();
                        }
                    }
                }) { IsBackground = true }.Start();
                MethodInfo? speak = null;
                for (int i = 1; ; i++)
                {
                    Greeter.Greet(i);
                    if (speak is null && asked.IsSet)
                    {
                        string plugin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Plugin", "bin", "Debug", "net10.0", "Plugin.dll"));
                        speak = Assembly.LoadFrom(plugin).GetType("Speaker")!.GetMethod("Speak")!;
                    }
                    speak?.Invoke(null, [i]);
                    Thread.Sleep(100);
                }

                """);
            File.WriteAllText(PathOf(Path.Combine("App", "Greeter.cs")), Greeter);
            File.WriteAllText(PathOf(Path.Combine("Plugin", "Plugin.csproj")), TestProjects.Library("<ImplicitUsings>enable</ImplicitUsings>", "<Nullable>enable</Nullable>"));
            File.WriteAllText(PathOf(Path.Combine("Plugin", "Speaker.cs")),
                "public static class Speaker\n{\n    public static void Speak(int i)\n    {\n        Console.WriteLine($\"Plugin {i}\");\n    }\n}\n");
            Directory.CreateDirectory(PathOf(Path.Combine("App", "bin")));
            Directory.CreateDirectory(PathOf(Path.Combine("Plugin", "bin")));
        }

        /// <summary>Restored by the real path, by which App's reference reaches Plugin and the solution's restore would not.</summary>
        public override ValueTask InitializeAsync() => new(PrepareAsync(byRealPath: true));
    }
}

/// <summary>Code typed over code in the editor as a person types it: the first place a text is written selected, the new
/// text typed over it a line at a time, Enter between lines (the editor indents the next as it does), so the file reads
/// as the edit was meant, every line where it should be.</summary>
internal static class Typing
{
    public static void Replace(MainWindow window, string old, string typed)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        int at = editor.Document.Snapshot.ToString().IndexOf(old, StringComparison.Ordinal);
        Assert.True(at >= 0, $"The file should have '{old}' to replace: {editor.Document.Snapshot}");
        editor.Focus();
        editor.Select(new TextRange(at, old.Length));
        string[] lines = typed.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                Ui.Press(window, "Enter");
            }
            string line = i > 0 ? lines[i].TrimStart() : lines[i];
            if (line.Length > 0)
            {
                window.KeyTextInput(line);
            }
        }
        Ui.Settle();
        string text = editor.Document.Snapshot.ToString();
        foreach (string line in lines.Where(l => l.Trim().Length > 0))
        {
            Assert.True(text.Contains(line.Trim(), StringComparison.Ordinal), $"The edit should have typed '{line.Trim()}': {text}");
        }
    }
}
