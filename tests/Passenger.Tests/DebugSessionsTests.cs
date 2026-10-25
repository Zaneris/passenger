using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Several programs debugged at once from one window, as a person does it: two programs that share a source file,
/// each debugged with Alt+F5 in a tab of its own in the Debug window, a breakpoint in the shared file stopping each, the
/// keys acting on the tab chosen while the other stays as it was, and Stop and Stop All ending one or every one. Only the
/// windows whose tests apply an edit through Hot Reload, which the server works out, take up the class's warm C# language
/// server. Debugged, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class DebugSessionsTests : IClassFixture<DebugSessionsTests.Pair>
{
    private readonly Pair pair;

    public DebugSessionsTests(Pair pair)
    {
        this.pair = pair;
        pair.Reset();
    }

    /// <summary>The shared file, as its breakpoint names it.</summary>
    private string Counter => Path.Combine(PathNames.Canonical(pair.Folder), "Shared", "Counter.cs");

    [AvaloniaFact]
    public void Two_programs_debugged_at_once_each_stop_in_the_file_they_share_and_step_stop_and_stop_all_act_on_the_tab_chosen()
    {
        MainWindow window = pair.Open(Counter, languageServer: false);
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.GoTo(new TextPosition(Pair.Next, 8));
            Ui.Run(window, AppActions.ToggleBreakpoint);

            DebugSession first = Debug(window, "First");
            Assert.Equal(DebugState.Paused, first.State);
            DebugSession second = Debug(window, "Second");
            Assert.Equal(DebugState.Paused, first.State);
            Assert.Same(second, window.Debugger.Session);
            Assert.Equal(["First", "Second"], window.Debugger.Tabs.Select(t => t.Name));
            Assert.Equal(["First", "Second"], Tabs(window).Select(t => t.Name));
            Assert.True(Tabs(window).Single(t => t.Name == "Second").Chosen, "The Debug window's tab of the program that stopped last is not the one chosen");
            Ui.WaitFor(() => window.BottomPanel.Debug.Frames.Count > 0 && window.BottomPanel.Debug.Frames[0].Line == Pair.Next + 1, "the second program's frames");
            // Said once the stop's frames and values are read, which take their own round trips.
            Ui.WaitFor(() => window.Status?.Contains("2 programs debugged, First and Second paused", StringComparison.Ordinal) == true,
                $"the status bar to say both programs are paused, not \"{window.Status}\"");
            Assert.Contains(new LineMark(Pair.Next, LineMarkKind.BreakpointBound), editor.LineMarks);
            Assert.NotSame(window.Debugger.Tabs[0].Console, window.Debugger.Tabs[1].Console);
            Assert.Same(window.Debugger.Tabs[1].Console, window.BottomPanel.Debug.Console);

            int firstThread = first.StoppedThread!.Value;
            Ui.Run(window, AppActions.StepOver);
            Ui.WaitFor(() => second.State == DebugState.Paused && window.Debugger.Stop?.Top?.Line == Pair.Next + 2, "the second program's step");
            Assert.Equal(DebugState.Paused, first.State);
            Assert.Equal(firstThread, first.StoppedThread);
            Assert.Same(second, window.Debugger.Session);
            Assert.Equal("next = {int} 1", Hover(window, editor, "return next", 8));

            Ui.Click(Tabs(window).Single(t => t.Name == "First").Button);
            Assert.Same(first, window.Debugger.Session);
            Ui.WaitFor(() => window.BottomPanel.Debug.Frames.Count > 0 && window.BottomPanel.Debug.Frames[0].Line == Pair.Next + 1, "the first program's frames");
            Ui.WaitFor(() => Value(window, "value") == "0", "the first program's values at its stop");
            Assert.True(window.Assist.HoverText is null, $"The card read at the second program's stop stayed as the first's tab was chosen: {window.Assist.HoverText}");
            Assert.Equal("next = {int} 0", Hover(window, editor, "return next", 9));
            Assert.Same(window.Debugger.Tabs[0].Console, window.BottomPanel.Debug.Console);
            Assert.Contains(new LineMark(Pair.Next, LineMarkKind.ExecutionPoint), editor.LineMarks);

            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => first.State == DebugState.Ended, "the first program to end");
            Ui.WaitFor(() => window.Debugger.Session == second, "the window to show the program still debugged");
            Assert.Equal(DebugState.Paused, second.State);
            Assert.True(window.Debugger.IsDebugging, "Stop ended every program, where it ends the one chosen");
            Assert.True(window.Debugger.Tabs[0].HasEnded && Tabs(window).Single(t => t.Name == "First").Greyed, "The ended program's tab was not kept greyed");

            DebugSession again = Debug(window, "First");
            Assert.Equal(["First", "Second"], window.Debugger.Tabs.Select(t => t.Name));
            Assert.Same(again, window.Debugger.Tabs[0].Session);
            Assert.False(Tabs(window).Single(t => t.Name == "First").Greyed, "The tab of the program debugged again stayed greyed");

            Assert.True(window.Perform(AppActions.StopAll), $"Stop All could not run: {Ui.Said(window)}");
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "every program to end");
            Assert.Equal(DebugState.Ended, again.State);
            Assert.Equal(DebugState.Ended, second.State);
            Assert.All(Tabs(window), t => Assert.True(t.Greyed, $"{t.Name}'s tab was not greyed as its program ended"));
            Assert.DoesNotContain(editor.LineMarks, m => m.Kind == LineMarkKind.ExecutionPoint);

            Ui.Click(Tabs(window).Single(t => t.Name == "Second").Close);
            Assert.Equal(["First"], window.Debugger.Tabs.Select(t => t.Name));
            Ui.Click(Tabs(window).Single().Close);
            Assert.Empty(window.Debugger.Tabs);
            Assert.Null(window.Debugger.Session);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Additional_launches_with_the_same_name_keep_separate_native_tabs_and_capture_output_from_the_start()
    {
        MainWindow window = pair.Open(Counter, languageServer: false);
        try
        {
            Passenger.App.Run.LaunchPlan plan = LoopingPlan("Same name");
            ConcurrentQueue<string> earlyOutput = new();
            DebugTab? configured = null;
            DebugLaunch first = Ui.Await(window.Debugger.StartAdditionalAsync(plan, configure: tab =>
            {
                configured = tab;
                Assert.Null(tab.Session);
                tab.ProgramOutput += (_, output) => earlyOutput.Enqueue(output.Output);
            }), "the first additional program");
            Assert.Same(configured, first.Tab);
            Ui.WaitFor(() => string.Concat(earlyOutput).Contains("looping", StringComparison.Ordinal), "the output subscribed before launch");

            DebugLaunch second = Ui.Await(window.Debugger.StartAdditionalAsync(plan), "another program with the same name");
            Assert.Equal(["Same name", "Same name"], window.Debugger.Tabs.Select(tab => tab.Name));
            Assert.NotSame(first.Tab, second.Tab);
            Assert.NotSame(first.Tab.Session, second.Tab.Session);
            Assert.NotSame(first.Tab.Console, second.Tab.Console);
            Assert.NotEqual(first.ProcessId, second.ProcessId);
            Assert.True(first.Tab.IsLive && second.Tab.IsLive);
            Assert.True(Processes.IsRunning(first.ProcessId) && Processes.IsRunning(second.ProcessId));
            Assert.Same(second.Tab, window.Debugger.Chosen);

            Task choosing = window.Debugger.ChooseAsync(first.Tab);
            Ui.WaitFor(() => choosing.IsCompleted, "choosing the first same-name program");
            choosing.GetAwaiter().GetResult();
            Assert.Same(first.Tab.Console, window.BottomPanel.Debug.Console);
            Task stopping = window.Debugger.StopAsync(second.Tab);
            Ui.WaitFor(() => stopping.IsCompleted, "stopping only the second same-name program");
            stopping.GetAwaiter().GetResult();
            Ui.WaitFor(() => !second.Tab.IsLive && !Processes.IsRunning(second.ProcessId), "the second program's exit");
            Assert.True(first.Tab.IsLive && Processes.IsRunning(first.ProcessId));
            Assert.Same(first.Tab, window.Debugger.Chosen);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Canceling_an_additional_start_before_or_during_adapter_start_cannot_stop_its_same_name_sibling(bool duringAdapterStart)
    {
        MainWindow window = pair.Open(Counter, languageServer: false);
        using CancellationTokenSource cancel = new();
        try
        {
            Passenger.App.Run.LaunchPlan plan = LoopingPlan("Sibling");
            DebugLaunch sibling = Ui.Await(window.Debugger.StartAdditionalAsync(plan), "the sibling to keep running");
            DebugTab? canceled = null;
            DebugSession? startingSession = null;
            Task<DebugLaunch> starting = window.Debugger.StartAdditionalAsync(plan, cancel.Token, tab =>
            {
                canceled = tab;
                if (duringAdapterStart)
                {
                    tab.StateChanged += (_, _) =>
                    {
                        if (tab.Session is { State: DebugState.Starting } session)
                        {
                            startingSession = session;
                            cancel.Cancel();
                        }
                    };
                }
                else
                {
                    cancel.Cancel();
                }
            });
            Ui.WaitFor(() => starting.IsCompleted, "the canceled additional launch's cleanup");
            Assert.ThrowsAny<OperationCanceledException>(() => starting.GetAwaiter().GetResult());
            Assert.NotNull(canceled);
            Assert.False(canceled.IsLive);
            if (duringAdapterStart)
            {
                Assert.NotNull(startingSession);
                Ui.WaitFor(() => startingSession.State == DebugState.Ended && !Processes.IsRunning(startingSession.AdapterProcessId), "the canceled adapter's exit");
            }
            else
            {
                Assert.Null(canceled.Session);
            }
            Assert.Contains(sibling.Tab, window.Debugger.Tabs);
            Assert.Same(sibling.Tab, Assert.Single(window.Debugger.Tabs, tab => tab.IsLive));
            Assert.True(Processes.IsRunning(sibling.ProcessId));
            Assert.Equal(DebugState.Running, sibling.Tab.Session!.State);
        }
        finally
        {
            window.Close();
        }
    }

    private static Passenger.App.Run.LaunchPlan LoopingPlan(string name) => new()
    {
        Name = name,
        Program = Debugging.Helpers.Debuggees.Program("LoopingConsoleApp"),
        Arguments = ["sleep"],
        WorkingDirectory = Path.GetDirectoryName(Debugging.Helpers.Debuggees.Program("LoopingConsoleApp"))!,
    };

    /// <summary>A program elsewhere (a container's) has its machine's process id, which may be another program's here: a
    /// session told so never ends a process of this machine by it. A launched program its adapter leaves running as it goes
    /// (let go here, as a crash leaves it) is ended by the IDE when the session is here; when the session is told its
    /// program is elsewhere, the program it launched, here only for the test, runs on.</summary>
    [AvaloniaFact]
    public void A_session_whose_program_is_elsewhere_never_ends_a_process_here_by_its_programs_id()
    {
        MainWindow window = pair.Open(Counter, languageServer: false);
        List<int> started = [];
        try
        {
            (int here, int hereAdapter) = LeftRunning(window, "Here", elsewhere: false);
            started.AddRange([here, hereAdapter]);
            // The IDE ends it on the window's thread as the adapter's end lands there: the wait runs that thread's loop.
            Ui.PumpUntil(() => !Processes.IsRunning(here), "a session here to end the program its adapter let go of, left running");
            DebugSession placed = window.Debugger.SessionNamed("Here")!;
            string endedHere = DebugSession.EndedLeftRunning(Path.GetFileName(placed.Program), here);
            Ui.WaitFor(() => window.Debugger.Output(placed).ToString().Contains(endedHere, StringComparison.Ordinal), $"the session here to say \"{endedHere}\"");

            (int program, int adapter) = LeftRunning(window, "Elsewhere", elsewhere: true);
            started.AddRange([program, adapter]);
            Assert.True(Processes.Gone(adapter), "The adapter that let the program go did not end");
            DebugSession elsewhere = window.Debugger.SessionNamed("Elsewhere")!;
            Ui.WaitFor(() => elsewhere.AdapterEnded.IsCompleted, "the adapter's end to reach the session, which ends a program left running");
            Assert.True(Processes.IsRunning(program), "A session whose program is elsewhere ended the process of this machine that has its program's id");
            Assert.DoesNotContain(DebugSession.EndedLeftRunning(Path.GetFileName(elsewhere.Program), program), window.Debugger.Output(elsewhere).ToString(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            started.ForEach(Processes.Kill);
        }
    }

    /// <summary>A program launched under the debugger, its session here or elsewhere, which its adapter then lets go of and
    /// goes, leaving it running; the program's process and the adapter's, once the session has ended.</summary>
    private static (int Program, int Adapter) LeftRunning(MainWindow window, string name, bool elsewhere)
    {
        Passenger.App.Run.LaunchPlan plan = new()
        {
            Name = name,
            Program = Debugging.Helpers.Debuggees.Program("LoopingConsoleApp"),
            Arguments = ["sleep"],
            WorkingDirectory = Path.GetDirectoryName(Debugging.Helpers.Debuggees.Program("LoopingConsoleApp"))!,
        };
        Task<string?> starting = window.Debugger.StartAsync(plan, elsewhere);
        Ui.WaitFor(() => starting.IsCompleted, $"{name}'s session to start");
        Assert.Null(starting.Result);
        DebugSession session = window.Debugger.SessionNamed(name)!;
        Assert.True(session.IsLocal != elsewhere, $"{name}'s session took its program for {(session.IsLocal ? "one of this machine's" : "another machine's")}");
        Ui.WaitFor(() => session.ProcessStarted.IsCompleted, $"{name}'s adapter to say which process it started");
        int program = session.ProcessStarted.Result;
        int adapter = session.AdapterProcessId;
        // In its loop, it writes no more: on Windows its output is a pipe of the adapter's, which a write would find broken.
        Ui.WaitFor(() => window.Debugger.Output(session).ToString().Contains("looping", StringComparison.Ordinal), $"{name}'s program to say it loops");
        Task letGo = session.DetachAsync();
        Ui.WaitFor(() => letGo.IsCompleted && session.State == DebugState.Ended, $"{name}'s session to let its program go and end");
        return (program, adapter);
    }

    /// <summary>A program started held for the debugger with DOTNET_MODIFIABLE_ASSEMBLIES=debug, as a container service's
    /// debug start will start one, attached to with Hot Reload: an edit made while it is paused goes into it as it resumes,
    /// and it runs the edited code to its end.</summary>
    [AvaloniaFact]
    public void An_attach_to_a_program_started_held_for_it_has_hot_reload_and_runs_an_edit_made_while_paused()
    {
        MainWindow window = pair.Open(Counter);
        Process? program = null;
        try
        {
            window.Build(Passenger.App.Run.BuildKind.Build);
            Ui.WaitFor(() => !window.IsBuilding, "the build");
            string built = Path.Combine(pair.Folder, "First", "bin", "Debug", TestProjects.Framework, OperatingSystem.IsWindows() ? "First.exe" : "First");
            Assert.True(File.Exists(built), $"The build made no {built}: {Ui.Said(window)}");
            string main = Path.Combine(PathNames.Canonical(pair.Folder), "First", "Program.cs");
            window.Debugger.Breakpoints.Toggle(main, 3);

            ProcessStartInfo start = ChildProcesses.Command(built, []);
            start.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
            start.Environment["DOTNET_MODIFIABLE_ASSEMBLIES"] = "debug";
            start.RedirectStandardOutput = true;
            program = ChildProcesses.Start(start);
            Task<string> output = program.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            Task<string?> attaching = window.Debugger.AttachAsync(program.Id, "First", hotReload: true);
            Ui.WaitFor(() => attaching.IsCompleted, "the debugger to attach");
            Assert.Null(attaching.Result);
            Ui.WaitFor(() => window.Debugger.Session is { State: DebugState.Paused } && window.Debugger.Stop is not null, "the program to stop at the breakpoint");
            // What a program attached to writes goes where it always went, as its tab's console says.
            Assert.Contains(DebugController.AttachedConsole, window.Debugger.Output(window.Debugger.Session).ToString(), StringComparison.Ordinal);
            Assert.True(window.Debugger.HotReload is not null && window.Debugger.WhyNotApply() is null, $"Hot Reload was not offered for the attach: {window.Debugger.WhyNotApply()}");

            window.OpenFile(Counter);
            Typing.Replace(window, "value + 1", "value + 2");
            window.Debugger.Breakpoints.Toggle(main, 3);
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the program to run on to its end");
            Assert.True(window.Debugger.LastApplied?.Result == HotReloadResult.Applied, $"The edit was not applied as the program resumed: {window.Debugger.LastApplied?.Said}");
            Assert.True(program.WaitForExit(TimeSpan.FromSeconds(30).Scaled()), "The program attached to did not end");
            Assert.Equal("first 6", output.Result.Trim());
        }
        finally
        {
            window.Close();
            if (program is not null)
            {
                Processes.Kill(program.Id);
                program.Dispose();
            }
        }
    }

    /// <summary>Hot Reload's apply is its own program's whichever tab the window shows: one resumed with an edit made while it
    /// was paused takes the edit and goes on, its console saying what the apply came to, though another program's stop takes
    /// the window while the edit is applied; the other stays paused where it was.</summary>
    [AvaloniaFact]
    public void A_program_resumed_with_an_edit_takes_it_and_goes_on_while_another_programs_stop_takes_the_window()
    {
        MainWindow window = pair.Open(Counter);
        try
        {
            string folder = PathNames.Canonical(pair.Folder);
            string firstMain = Path.Combine(folder, "First", "Program.cs");
            string secondMain = Path.Combine(folder, "Second", "Program.cs");
            // Each stops at its own call of Next, so the edit to Next is no active statement's.
            window.Debugger.Breakpoints.Toggle(firstMain, 3);
            window.Debugger.Breakpoints.Toggle(secondMain, 3);
            DebugSession first = Debug(window, "First");
            DebugSession second = Debug(window, "Second");
            Ui.Click(Tabs(window).Single(t => t.Name == "First").Button);
            Assert.Same(first, window.Debugger.Session);

            window.OpenFile(Counter);
            Ui.Settle();
            Typing.Replace(window, "value + 1", "value + 2");
            Assert.True(window.Debugger.HotReload is { Changed: true }, "The edit was not taken for one the first program is to take");
            // No breakpoint stops it again: it runs to its end in the edited code.
            window.Debugger.Breakpoints.Toggle(firstMain, 3);
            Assert.True(window.Perform(AppActions.Resume), $"Resume could not run: {window.Status}");
            // The second program's tab takes the window as the edit is applied, as its stop would.
            _ = window.Debugger.ChooseAsync(window.Debugger.Tabs.Single(t => t.Name == "Second"));

            Ui.WaitFor(() => first.State == DebugState.Ended,
                $"the first program to go on to its end with the edit (it is {first.State}, the status bar says \"{window.Status}\")");
            Assert.True(window.Debugger.LastApplied?.Result == HotReloadResult.Applied, $"The apply was not told as the first program's: {window.Debugger.LastApplied?.Said}");
            string written = window.Debugger.Output(first).ToString();
            Assert.True(written.Contains("Hot Reload:", StringComparison.Ordinal) && written.Contains("first 6", StringComparison.Ordinal),
                $"The first program's console does not say the apply and its edited total: {written}");
            Assert.Equal(DebugState.Paused, second.State);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An edit Hot Reload cannot apply restarts its own program, as Settings' always restart says, though another
    /// program's stop takes the window while it is applied: its tab is chosen again and its program built and debugged anew,
    /// stopping at its breakpoint, while the other stays paused where it was.</summary>
    [AvaloniaFact]
    public void A_rude_edit_restarts_its_own_program_though_another_programs_stop_takes_the_window_as_it_is_applied()
    {
        MainWindow window = pair.Open(Counter);
        try
        {
            string folder = PathNames.Canonical(pair.Folder);
            window.Debugger.Breakpoints.Toggle(Path.Combine(folder, "First", "Program.cs"), 3);
            window.Debugger.Breakpoints.Toggle(Path.Combine(folder, "Second", "Program.cs"), 3);
            DebugSession first = Debug(window, "First");
            DebugSession second = Debug(window, "Second");
            Ui.Click(Tabs(window).Single(t => t.Name == "First").Button);
            Assert.Same(first, window.Debugger.Session);

            window.OpenFile(Counter);
            Ui.Settle();
            // A parameter its callers leave out: their unchanged code would call the body the update deletes, so it restarts.
            Typing.Replace(window, "Next(int value)", "Next(int value, int step = 1)");
            Assert.True(window.Debugger.HotReload is { Changed: true }, "The edit was not taken for one the first program is to take");
            Assert.True(window.Perform(AppActions.Resume), $"Resume could not run: {window.Status}");
            // The second program's tab takes the window as the edit is applied, as its stop would.
            _ = window.Debugger.ChooseAsync(window.Debugger.Tabs.Single(t => t.Name == "Second"));

            Ui.WaitFor(() => window.LastHotReloadRestart is not null && window.Debugger.SessionNamed("First") is { State: DebugState.Paused } again && again != first,
                $"the first program to restart for the edit and stop at its breakpoint (the status bar says \"{window.Status}\")", TimeSpan.FromMinutes(2));
            Assert.True(window.Debugger.LastApplied?.Result == HotReloadResult.RestartNeeded, $"The edit was not told as needing a restart: {window.Debugger.LastApplied?.Said}");
            Assert.Equal(DebugState.Ended, first.State);
            Assert.Same(second, window.Debugger.SessionNamed("Second"));
            Assert.Equal(DebugState.Paused, second.State);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A program's output followed from outside (as a container run's log is) ends with a start that fails, rather
    /// than run for the window's life; and a session whose place stops under it (its container) ends as its program's end,
    /// said so, never as the debugger failing.</summary>
    [AvaloniaFact]
    public void A_failed_start_ends_the_output_it_followed_and_a_place_that_stopped_is_said_as_the_programs_end()
    {
        MainWindow window = pair.Open(Counter, languageServer: false);
        List<int> followers = [];
        EventHandler<ChildStart> started = (_, child) =>
        {
            if (child.Command.Contains("DebuggableConsoleApp", StringComparison.Ordinal))
            {
                lock (followers)
                {
                    followers.Add(child.ProcessId);
                }
            }
        };
        ChildProcesses.Started += started;
        Process? looping = null;
        try
        {
            // An attach to a process that is no more fails, and its output followed meanwhile is ended with it.
            using Process gone = ChildProcesses.Start(ChildProcesses.DotnetCommand(["--version"]));
            gone.WaitForExit();
            string? failed = Ui.Await(window.Debugger.AttachAsync(gone.Id, "Gone", place: Here(follow: true)), "the attach that cannot be done");
            Assert.NotNull(failed);
            int follower;
            lock (followers)
            {
                follower = Assert.Single(followers);
            }
            Ui.PumpUntil(() => !Processes.IsRunning(follower), $"the output followed for the failed attach ({failed}) to be ended", TimeSpan.FromSeconds(30));

            // A place whose adapter ends unasked with its place stopped: the program's end, said so, and no error of the IDE's.
            ProcessStartInfo start = ChildProcesses.DotnetCommand([Debugging.Helpers.Debuggees.Program("LoopingConsoleApp"), "sleep"]);
            start.RedirectStandardOutput = true;
            looping = ChildProcesses.Start(start);
            Assert.Null(Ui.Await(window.Debugger.AttachAsync(looping.Id, "Placed", place: Here(follow: false, running: () => Task.FromResult(false))), "the attach in its place"));
            DebugSession placed = window.Debugger.SessionNamed("Placed")!;
            Ui.WaitFor(() => placed.State == DebugState.Running, "the program attached to in its place to run");
            Processes.Kill(placed.AdapterProcessId);
            Ui.WaitFor(() => placed.State == DebugState.Ended, "the session to end with its adapter");
            Assert.Equal("Debugging Placed ended: here stopped", window.Debugger.SummaryOf(placed));
            Assert.Null(placed.Failure);
        }
        finally
        {
            ChildProcesses.Started -= started;
            window.Close();
            lock (followers)
            {
                followers.ForEach(Processes.Kill);
            }
            if (looping is not null)
            {
                Processes.Kill(looping.Id);
                looping.Dispose();
            }
        }
    }

    /// <summary>A program's output followed from outside, as a container service's log is into its Debug tab, shows to its
    /// last word once what follows it ends, as the container's end ends docker logs: a last line ended by a carriage return
    /// alone comes whole with it, the carriage return let go rather than held for good for a line feed that never comes.</summary>
    [AvaloniaFact]
    public void Output_followed_from_outside_shows_its_last_line_ended_by_a_carriage_return_once_it_ends()
    {
        MainWindow window = pair.Open(Counter, languageServer: false);
        Process? looping = null;
        try
        {
            ProcessStartInfo start = ChildProcesses.DotnetCommand([Debugging.Helpers.Debuggees.Program("LoopingConsoleApp"), "sleep"]);
            start.RedirectStandardOutput = true;
            looping = ChildProcesses.Start(start);
            // What a container's log ends with as the container ends: a line, then one ended by a carriage return alone.
            AdapterPlace place = Here(follow: false) with
            {
                Output = () => OperatingSystem.IsWindows()
                    ? ChildProcesses.Command("powershell", ["-NoProfile", "-Command", "[Console]::Out.Write('first words' + [char]10 + 'last words' + [char]13)"])
                    : ChildProcesses.Command("/bin/sh", ["-c", "printf 'first words\\nlast words\\r'"]),
            };
            Assert.Null(Ui.Await(window.Debugger.AttachAsync(looping.Id, "Followed", place: place), "the attach in its place"));
            DebugSession followed = window.Debugger.SessionNamed("Followed")!;
            Ui.WaitFor(() => window.Debugger.Output(followed).ToString().Contains("first words\nlast words\r", StringComparison.Ordinal),
                $"the followed output's last line in its console with the carriage return that ended it, not held for good: [{window.Debugger.Output(followed)}]");
            Task letGo = followed.DetachAsync();
            Ui.WaitFor(() => letGo.IsCompleted && followed.State == DebugState.Ended, "the session to let its program go and end");
        }
        finally
        {
            window.Close();
            if (looping is not null)
            {
                Processes.Kill(looping.Id);
                looping.Dispose();
            }
        }
    }

    /// <summary>A place whose adapter runs here, as a container's runs over docker exec: the IDE's own adapter given the flags
    /// a place's adapter is given, a program's output followed from outside when asked (DebuggableConsoleApp, which writes on
    /// for good), and whether the place still runs as given.</summary>
    private static AdapterPlace Here(bool follow, Func<Task<bool>>? running = null)
    {
        return new AdapterPlace(arguments => ChildProcesses.DotnetCommand([DebugSession.AdapterPath, .. arguments]), TestFolders.Create("place-symbols-"),
            Passenger.App.Containers.ContainerPaths.None, "here")
        {
            Output = follow ? () => ChildProcesses.DotnetCommand([Debugging.Helpers.Debuggees.Program("DebuggableConsoleApp")]) : null,
            Running = running,
        };
    }

    /// <summary>Debug with a configuration chosen, as Alt+F5 starts it; its program's session once it has stopped at the
    /// breakpoint, its tab chosen.</summary>
    internal static DebugSession Debug(MainWindow window, string configuration)
    {
        Assert.True(window.SelectConfiguration(configuration), $"No run configuration {configuration}: {string.Join(", ", window.Configurations.Select(c => c.Name))}");
        Ui.Run(window, AppActions.Debug);
        DebugSession session = window.Debugger.SessionNamed(configuration) ?? throw new InvalidOperationException($"Debug did not start {configuration}: {Ui.Said(window)}");
        Ui.WaitFor(() => session.State == DebugState.Paused && window.Debugger.Session == session && window.Debugger.Stop is not null,
            $"{configuration} to stop at the breakpoint (it is {session.State}, the status bar says \"{window.Status}\")");
        return session;
    }

    /// <summary>A value of the chosen frame's in Variables, as shown.</summary>
    private static string? Value(MainWindow window, string name) =>
        window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Name == name)?.Value;

    /// <summary>The Debug window's session tabs as drawn in its header: each one's name, whether it is the one chosen and
    /// greyed, its button and its ×.</summary>
    private static List<(string Name, bool Chosen, bool Greyed, ToggleButton Button, Button Close)> Tabs(MainWindow window)
    {
        window.ShowToolWindow(ToolWindowIds.Debug);
        Ui.Settle();
        return [.. window.DebugWindow.HeaderTools.GetVisualDescendants().OfType<ToggleButton>().Select(button =>
        {
            TextBlock name = button.GetVisualDescendants().OfType<TextBlock>().First();
            return (name.Text ?? string.Empty, button.IsChecked == true, name.Classes.Contains("muted"), button, button.GetVisualDescendants().OfType<Button>().First());
        })];
    }

    /// <summary>The card shown with the pointer resting on a letter of some text of the editor's, the last card gone first: a
    /// letter other than the last one rested on, since the pointer back where it last rested is no new rest.</summary>
    private static string Hover(MainWindow window, CodeEditor editor, string text, int letter)
    {
        window.MouseMove(new Point(0, 0), Avalonia.Input.RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is null, "the last card to go");
        int at = editor.Document.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal) + letter;
        window.MouseMove(editor.TranslatePoint(editor.PointOf(at) + new Point(1, editor.LineHeight / 2), window)!.Value, Avalonia.Input.RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is not null, $"the card on {text}");
        return window.Assist.HoverText!;
    }

    /// <summary>Two programs, First and Second, each adding one three times with the Counter their projects share from
    /// Shared/, and a solution of both.</summary>
    public sealed class Pair : SharedProject
    {
        /// <summary>Counter.Next's first line (from zero), where the breakpoint stops each program.</summary>
        public const int Next = 4;

        public Pair()
            : base("sessions-")
        {
            WriteTo(Folder);
            // The first builds make bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(PathOf(Path.Combine("First", "bin")));
            Directory.CreateDirectory(PathOf(Path.Combine("Second", "bin")));
            Prepare();
        }

        public static void WriteTo(string folder)
        {
            Directory.CreateDirectory(Path.Combine(folder, "Shared"));
            File.WriteAllText(Path.Combine(folder, "Shared", "Counter.cs"),
                "public static class Counter\n{\n    public static int Next(int value)\n    {\n        int next = value + 1;\n        return next;\n    }\n}\n");
            foreach (string name in (string[])["First", "Second"])
            {
                Directory.CreateDirectory(Path.Combine(folder, name));
                File.WriteAllText(Path.Combine(folder, name, $"{name}.csproj"),
                    $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n"
                    + "    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n  <ItemGroup>\n"
                    + "    <Compile Include=\"../Shared/Counter.cs\" Link=\"Counter.cs\" />\n  </ItemGroup>\n</Project>\n");
                File.WriteAllText(Path.Combine(folder, name, "Program.cs"),
                    $"int total = 0;\nfor (int i = 0; i < 3; i++)\n{{\n    total = Counter.Next(total);\n}}\nConsole.WriteLine($\"{name.ToLowerInvariant()} {{total}}\");\n");
            }
            File.WriteAllText(Path.Combine(folder, "Pair.slnx"),
                "<Solution>\n  <Project Path=\"First/First.csproj\" />\n  <Project Path=\"Second/Second.csproj\" />\n</Solution>\n");
        }
    }
}
