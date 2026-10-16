using System.Text.Json;
using Avalonia.Threading;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.App.Editors;
using Passenger.App.LiveTree;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Editor;
using Passenger.Inspector;
using Passenger.Text;

namespace Passenger.App.Debugging;

/// <summary>The debugger in the IDE: the breakpoints in the gutter, a session's stops shown where
/// they are (the execution line, the Debug window's frames and variables), the stepping actions, and
/// a value when the pointer rests on an expression while paused. A UI-thread object.</summary>
public sealed class DebugController : IDisposable
{
    private readonly EditorArea editors;
    private readonly DebugView view;
    private readonly List<string> watches = [];
    private readonly Dictionary<OpenDocument, EventHandler<DocumentChangedEventArgs>> following = [];

    /// <summary>Where the program stopped, at its built line: shown where that line's code now is.</summary>
    private (string Path, int Line)? executionPoint;

    /// <summary>The session's built lines, from its start to its end: the files' edits since the build.</summary>
    private BuiltLines? lines;

    private (string Expression, string Value)? lastResult;

    /// <summary>The frame chosen at the stop shown: its variables are the view's, a hover evaluates in it. None while the
    /// program runs, so nothing asks of a frame the debugger has let go.</summary>
    private DapStackFrame? frame;

    /// <summary>The exception the program stopped on, and the frame it was thrown in, shown at that frame's line.</summary>
    private (string Text, int FrameId)? stopException;

    /// <summary>Why the exception the program stopped on could not be described, when it could not.</summary>
    private string? undescribed;

    /// <summary>What a property breakpoint's stop changed and what changed it, as the inspector in the app said it.</summary>
    private string? propertyChange;

    /// <summary>The failed binding XAML Binding Errors stopped at, as the inspector's call there has it, and the frame
    /// shown at the stop, where its words are written over the line.</summary>
    private BindingStop? bindingStop;
    private int? bindingFrame;

    /// <summary>The failed binding the program is paused at, once the stop is read; null otherwise.</summary>
    public BindingStop? BindingStop => Stop is not null ? bindingStop : null;

    /// <summary>Counts the stops shown and the runs between them, so what a stop's requests bring back once the program has
    /// gone on is dropped rather than shown over what is now true.</summary>
    private int shown;

    /// <summary>The session's name, as the status bar names it ("Tests", a run configuration's).</summary>
    private string name = string.Empty;

    /// <summary>The data breakpoints whose refusal the status bar has said, so it says each once.</summary>
    private readonly HashSet<DataBreakpoint> refusalsTold = [];

    /// <summary>Clears the last stop's frames and values a moment after the program goes on, as Rider's views do: a step
    /// that stops again at once replaces them without the panes going blank between.</summary>
    private readonly DispatcherTimer clearing = new() { Interval = TimeSpan.FromMilliseconds(100) };

    /// <summary>Takes this debugger's handlers off the Debug window, which the next session's debugger has from then.</summary>
    private readonly Action letGo;

    private bool disposed;

    /// <summary>A folder's debugger over the window's editors and Debug window, which it has until it is disposed.</summary>
    public DebugController(EditorArea editors, DebugView view)
    {
        this.editors = editors;
        this.view = view;
        clearing.Tick += (_, _) =>
        {
            clearing.Stop();
            if (Session is not { State: DebugState.Paused })
            {
                view.ShowFrames([], null, [], null);
                view.Tree.Show([]);
            }
        };
        Breakpoints.Changed += (_, path) => UpdateMarks(path);
        Breakpoints.ExceptionsChanged += (_, _) => view.ShowMuted(Breakpoints.Muted);
        EventHandler<string> asked = (_, action) => ActionRequested?.Invoke(this, action);
        EventHandler<DapStackFrame> framed = (_, chosen) => _ = ChooseFrame(chosen, navigate: true);
        EventHandler<DapThread> threaded = (_, thread) => _ = ChooseThread(thread.Id);
        EventHandler<(string Expression, bool Watch)> evaluated = (_, request) => _ = Evaluate(request.Expression, request.Watch);
        EventHandler<(VariableNode Node, string Value)> set = (_, request) => _ = SetValue(request.Node, request.Value);
        EventHandler<VariableNode> watchData = (_, node) => _ = ToggleDataBreakpointAsync(node);
        EventHandler<string> unwatched = (_, expression) =>
        {
            watches.Remove(expression);
            _ = ShowVariables();
        };
        view.ActionRequested += asked;
        view.FrameChosen += framed;
        view.ThreadChosen += threaded;
        view.EvaluateRequested += evaluated;
        view.SetValueRequested += set;
        view.DataWatch = DataWatchAsync;
        view.DataBreakpointRequested += watchData;
        Breakpoints.DataChanged += (_, _) => TellRefusedData();
        view.WatchRemoved += unwatched;
        letGo = () =>
        {
            view.ActionRequested -= asked;
            view.FrameChosen -= framed;
            view.ThreadChosen -= threaded;
            view.EvaluateRequested -= evaluated;
            view.SetValueRequested -= set;
            view.DataBreakpointRequested -= watchData;
            view.WatchRemoved -= unwatched;
        };
        view.ShowMuted(Breakpoints.Muted);
    }

    /// <summary>Its folder left, the debugger lets go of the Debug window and the open files, its session ended first
    /// (<see cref="StopAsync"/> or <see cref="CloseAsync"/>): a late word from that session changes nothing shown.</summary>
    public void Dispose()
    {
        UiThread.Check();
        disposed = true;
        clearing.Stop();
        letGo();
        foreach (OpenDocument document in following.Keys.ToList())
        {
            Detach(document);
        }
    }

    public BreakpointStore Breakpoints { get; } = new();

    /// <summary>What the session's program has written, as the Debug window's console shows it now, what is on its way to
    /// it taken in first: what an agent reads of a debugged program, rather than the window's view.</summary>
    public TextSnapshot Output()
    {
        view.Console.Flush();
        return view.Console.Document.Snapshot;
    }

    public DebugSession? Session { get; private set; }

    public bool IsDebugging => Session is { State: not DebugState.Ended };

    /// <summary>The stop the program is paused at as the controller has read it (the stopped thread's frames, the exception
    /// it stopped on described), which the Debug window shows; null while it runs, before the stop is read, and after.</summary>
    public DebugStop? Stop { get; private set; }

    /// <summary>The session's state in the words the status bar is told as it changes (debugging, paused and on what,
    /// running, ended and how), or null with no session.</summary>
    public string? Summary => Session is { } session ? Said(session) : null;

    /// <summary>A toolbar button of the Debug window: an app action for the window to run.</summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>Something for the status bar.</summary>
    public event EventHandler<string>? Message;

    /// <summary>A place to show: a stop, or a frame chosen.</summary>
    public event EventHandler<(string Path, TextPosition Position)>? Navigate;

    /// <summary>The session started, stopped, ran on or ended.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Starts a program the debugger asks for in a tab of the Terminal window named for the session (the window's
    /// own), for a configuration that uses the terminal; without it such a program runs in the Console.</summary>
    public Func<string, RunInTerminalArguments, Task<Run.TerminalRun>>? RunInTerminal { get; init; }

    /// <summary>The folder open, which the Debug console names its files from.</summary>
    public string? Folder { get; init; }

    /// <summary>The C# language server whose Edit and Continue computes Hot Reload's updates, when it runs.</summary>
    public Func<Lsp.RoslynSession?> Server { get; init; } = () => null;

    /// <summary>The app's inspector, whose UI thread applies XAML changes while the program runs.</summary>
    public Func<LiveApp?> Inspector { get; init; } = () => null;

    /// <summary>Whether a launch is debugged with Hot Reload (Settings' Hot Reload while debugging): its code may change
    /// as it runs, and edits made while it is paused are applied as it goes on.</summary>
    public Func<bool> HotReloadOn { get; init; } = () => false;

    /// <summary>The session's Hot Reload, while it has one.</summary>
    public HotReload? HotReload => hotReload is not null && IsDebugging ? hotReload : null;

    private HotReload? hotReload;

    /// <summary>What the last Apply Changes came to, until the next.</summary>
    public HotReloadOutcome? LastApplied { get; private set; }

    /// <summary>An Apply Changes ended: the window shows its problems and says it.</summary>
    public event EventHandler<HotReloadOutcome>? Applied;

    /// <summary>Restarts the program for edits Hot Reload cannot apply, as Settings says (or asks, as dotnet watch does);
    /// true once it restarts, so a resume that applied first does not go on. The window's.</summary>
    public Func<HotReloadOutcome, Task<bool>> RestartFor { get; set; } = _ => Task.FromResult(false);

    /// <summary>The Apply Changes under way, which a second one waits for.</summary>
    private Task<HotReloadOutcome>? applying;

    /// <summary>Why Apply Changes cannot run: nothing debugged, a session without Hot Reload, or one that stopped taking
    /// changes.</summary>
    public string? WhyNotApply() => !IsDebugging ? NothingDebugged
        : hotReload is null ? (Session!.Attached
            ? "Hot Reload applies changes to a program the debugger launched, not to one it attached to"
            : "This program was launched with Hot Reload when debugging off (Settings, Hot Reload)")
        : hotReload.WhyNot;

    /// <summary>Rider's Apply Hot Reload Changes: the edits since the launch or the last apply, applied to the program
    /// paused or running. Applied, the files' lines are the program's from now (the built lines forget their edits), their
    /// breakpoints are bound again in the new code, and a paused program's stop is read again where it now is.</summary>
    public Task<HotReloadOutcome> ApplyChangesAsync() => ApplyChangesAsync(beforeResume: false);

    private Task<HotReloadOutcome> ApplyChangesAsync(bool beforeResume)
    {
        UiThread.Check();
        if (applying is { } active)
        {
            return active;
        }
        Task<HotReloadOutcome> started = ApplyOnceAsync(beforeResume);
        // A managed-only pre-resume apply with no C# edits can finish synchronously. Its finally already cleared applying;
        // caching that completed task afterward would keep every later XAML apply returning that old result.
        if (!started.IsCompleted)
        {
            applying = started;
        }
        return started;
    }

    /// <summary>Apply Changes as the action runs it: an edit that needs a restart restarts the program, as Settings says.</summary>
    public async Task ApplyAndRestartAsync()
    {
        if (await ApplyChangesAsync() is { Result: HotReloadResult.RestartNeeded } outcome)
        {
            await RestartAsked(outcome);
        }
    }

    /// <summary>The window's restart for edits Hot Reload cannot apply; declined, those edits are not asked about again until
    /// the next edit, as dotnet watch's No.</summary>
    private async Task<bool> RestartAsked(HotReloadOutcome outcome)
    {
        if (await RestartFor(outcome))
        {
            return true;
        }
        if (hotReload is not null)
        {
            hotReload.Changed = false;
        }
        return false;
    }

    private async Task<HotReloadOutcome> ApplyOnceAsync(bool beforeResume)
    {
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            if (WhyNotApply() is { } why || Session is not { } session || hotReload is not { } reload)
            {
                HotReloadOutcome refused = new(HotReloadResult.Failed, WhyNotApply() ?? NothingDebugged);
                Message?.Invoke(this, refused.Said);
                return refused;
            }
            Message?.Invoke(this, "Applying Hot Reload changes…");
            HotReloadOutcome outcome = beforeResume ? await reload.ApplyBeforeResumeAsync() : await reload.ApplyAsync();
            if (session != Session || disposed)
            {
                return outcome;
            }
            if (outcome.Files.Count > 0 || outcome.Result is HotReloadResult.Applied or HotReloadResult.NothingToApply)
            {
                // A mixed update can apply C# before the app refuses XAML. These files still run their new lines.
                // XAML property updates retain the original compiled element positions: they emit no new PDB lines.
                List<string> files = [.. outcome.Files.Where(p => Path.GetExtension(p).ToLowerInvariant() is not (".axaml" or ".xaml")).Select(PathNames.Canonical)];
                // The stop shown keeps its place in the text, then the files' lines are the program's.
                if (executionPoint is { } point && files.Contains(point.Path))
                {
                    executionPoint = (point.Path, Current(point.Path, point.Line));
                }
                foreach (string path in files)
                {
                    lines?.Reset(path);
                }
                await session.SendBreakpointsAgainAsync(files);
                if (outcome.Result == HotReloadResult.Applied && session.State == DebugState.Paused && session.StoppedThread is int thread)
                {
                    try
                    {
                        await ChooseThread(thread);
                    }
                    catch (DapException error)
                    {
                        Message?.Invoke(this, $"The debugger could not show the stop: {error.Reason}");
                    }
                }
                UpdateAllMarks();
            }
            LastApplied = outcome;
            view.Console.Append($"Hot Reload: {outcome.Said}\n", error: outcome.Result is not (HotReloadResult.Applied or HotReloadResult.NothingToApply or HotReloadResult.RegisteredForFutureViews));
            Message?.Invoke(this, outcome.Said);
            Applied?.Invoke(this, outcome);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return outcome;
        }
        finally
        {
            applying = null;
        }
    }

    /// <summary>Launches a plan's program under the debugger (after its build), its output in the Debug window's Console,
    /// or in a tab of the Terminal window when its configuration uses the terminal. Null once the session is open (the
    /// program may have ended already), else why it could not.</summary>
    public Task<string?> StartAsync(Run.LaunchPlan plan)
    {
        Func<RunInTerminalArguments, Task<Run.TerminalRun>>? inTerminal = plan.Console == Run.ConsoleKind.Terminal && RunInTerminal is { } run
            ? arguments => run(plan.Name, arguments)
            : null;
        bool hotReloadOn = HotReloadOn();
        return OpenAsync((built, log, output) => DebugSession.LaunchAsync(plan, Breakpoints, built, log, output, inTerminal, ColumnOf, hotReloadOn), plan.Name,
            launched: true, inTerminal: inTerminal is not null, hotReloadOn: hotReloadOn);
    }

    /// <summary>The column a breakpoint asks the debugger for: in Avalonia XAML, its attribute's or its line's first name's
    /// in the file's text (an open file's as it is now, else as saved); none elsewhere.</summary>
    private int? ColumnOf(LineBreakpoint breakpoint)
    {
        if (!XamlBreakpoints.Compiled(breakpoint.Path))
        {
            return null;
        }
        TextSnapshot? text = editors.Documents.FirstOrDefault(d => d.Path == breakpoint.Path)?.Text.Snapshot
            ?? (File.Exists(breakpoint.Path) ? TextSnapshot.From(File.ReadAllText(breakpoint.Path)) : null);
        return text is null ? null : XamlBreakpoints.Column(text, breakpoint.Line, breakpoint.Attribute);
    }

    /// <summary>Attaches the debugger to a running .NET process, as Attach to Process does: what it writes goes where it
    /// always went, as Rider's Console says. Null once the session is open, else why it could not.</summary>
    public Task<string?> AttachAsync(int processId, string name) =>
        OpenAsync((built, log, output) => DebugSession.AttachAsync(processId, Breakpoints, built, log, output, ColumnOf), name, launched: false);

    /// <summary>Starts a session, its output in the Console; a program it launched reads what is typed there, as in
    /// Rider's Debug console (one attached to has an input of its own, and one in the terminal reads the terminal's).</summary>
    private async Task<string?> OpenAsync(Func<BuiltLines, Action<string>, Action<OutputEvent>, Task<DebugSession>> open, string name, bool launched, bool inTerminal = false,
        bool hotReloadOn = false)
    {
        UiThread.Check();
        if (IsDebugging)
        {
            await StopAsync();
        }
        // Not the last session's while this one starts, nor once it could not: a caller asks Session whether it did.
        Session = null;
        hotReload = null;
        this.name = name;
        Stop = null;
        view.Clear();
        clearing.Stop();
        executionPoint = null;
        frame = null;
        stopException = null;
        undescribed = null;
        bindingStop = null;
        bindingFrame = null;
        lastResult = null;
        BuiltLines built = new();
        lines = built;
        view.SetState(DebugState.Starting);
        view.ShowDetachable(false);
        if (inTerminal)
        {
            view.Console.Append($"{name} runs in the Terminal window, which it reads from and writes to\n");
        }
        else if (!launched)
        {
            // Rider's words: what a program attached to writes goes where it always went.
            view.Console.Append("Console output is not available when the debugger is attached to a running process\n");
        }
        Message?.Invoke(this, $"Debugging {name}");
        try
        {
            ConsoleSink console = view.Console.Sink;
            DebugSession session = await open(built, line => view.Console.Append($"{line}\n"), output =>
            {
                ConsoleStream? stream = output.Category switch
                {
                    "stdout" => ConsoleStream.Output,
                    "stderr" => ConsoleStream.Error,
                    "telemetry" => null,
                    _ => ConsoleStream.System,
                };
                if (stream is { } into)
                {
                    console.Write(output.Output, into);
                }
            });
            Session = session;
            hotReload = hotReloadOn ? new HotReload(session, Server, Inspector) : null;
            view.Tree.Load = reference => session.VariablesAsync(reference);
            if (launched && !inTerminal)
            {
                view.Console.Input = new ConsoleInput(text => Send(session.InputAsync(text)), () => Send(session.CloseInputAsync()));
            }
            session.StateChanged += (_, _) => _ = OnStateChanged(session);
            await OnStateChanged(session);
            return null;
        }
        catch (Exception error) when (error is DapException or IOException or InvalidOperationException or TimeoutException)
        {
            lines = null;
            view.Console.Flush();
            view.SetState(DebugState.Ended);
            string failed = $"Could not debug {name}: {(error is DapException dap ? dap.Reason : error.Message)}";
            Message?.Invoke(this, failed);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return failed;
        }
    }

    /// <summary>Input for the program, which a session that has ended, or an adapter gone, may never take: its end says so.</summary>
    private static void Send(Task sending) =>
        _ = sending.ContinueWith(static sent => sent.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    public async Task StopAsync()
    {
        UiThread.Check();
        if (Session is { } session)
        {
            await session.StopAsync();
        }
    }

    /// <summary>Rider's Detach: the debugger lets go of the program it attached to, which runs on. Null once it has, else why
    /// it could not (nothing attached to).</summary>
    public async Task<string?> DetachAsync()
    {
        UiThread.Check();
        if (WhyNotDetach() is { } why)
        {
            return why;
        }
        await Session!.DetachAsync();
        return null;
    }

    /// <summary>Why Detach cannot run now: nothing is being debugged, or the debugger started the program, which ends with
    /// its session (its input and output are the adapter's).</summary>
    private string? WhyNotDetach() => !IsDebugging ? NothingDebugged
        : !Session!.Attached ? "The debugger started this program: Stop ends it, and Detach lets go only of a program attached to"
        : Session.State == DebugState.Starting ? "The debugger is still taking hold of the program"
        : null;

    /// <summary>Ends the session's program and adapter as the IDE closes (<see cref="DebugSession.CloseAsync"/>).</summary>
    public Task CloseAsync()
    {
        UiThread.Check();
        return Session?.CloseAsync() ?? Task.CompletedTask;
    }

    /// <summary>Its folder left with the window open: a program the debugger started is stopped, as Stop stops it, and one
    /// it attached to let go, running on, as the window's close lets it go.</summary>
    public Task LeaveAsync()
    {
        UiThread.Check();
        return WhyNotDetach() is null ? DetachAsync() : StopAsync();
    }

    /// <summary>The debugger's actions, on the active editor's caret and the session of the debugger <paramref name="current"/>
    /// gives, the window's session's whichever that is when one runs. Resume with nothing being debugged is the window's
    /// Debug, as F5 is in Visual Studio's keymap.</summary>
    public static void AddActions(ActionRegistry actions, Func<DebugController> current)
    {
        UiThread.Check();
        Add(AppActions.ToggleBreakpoint, "Toggle Breakpoint", (d, c) =>
        {
            // On an Avalonia XAML attribute, the attribute's own: it stops as that property is set.
            if (AttributeAtCaret(c) is { } attribute)
            {
                d.Breakpoints.ToggleAttribute(c.File!.Path, c.Caret.Line, attribute);
            }
            else
            {
                d.Breakpoints.Toggle(c.File!.Path, c.Caret.Line);
            }
        }, (_, c) => NeedsFile(c), new("Run", 2, 0));
        Add(AppActions.ToggleBreakpointEnabled, "Toggle Breakpoint Enabled", (d, c) => d.Breakpoints.ToggleEnabled(c.File!.Path, c.Caret.Line), (_, c) => NeedsFile(c), new("Run", 2, 1));
        Add(AppActions.Resume, "Resume Program", (d, c) =>
        {
            if (!d.IsDebugging)
            {
                actions.Run(AppActions.Debug, c);
                return;
            }
            _ = d.ResumeAsync(session => session.ContinueAsync(), "resume", applyXaml: true);
        }, (d, c) => d.IsDebugging ? d.NotPaused() : actions.WhyNot(AppActions.Debug, c), new("Run", 1, 0));
        Add(AppActions.Pause, "Pause Program", (d, c) => _ = d.Session!.PauseAsync(),
            (d, _) => !d.IsDebugging ? NothingDebugged : d.Session!.State == DebugState.Paused ? "The program is paused already" : null, new("Run", 1, 1));
        Add(AppActions.StepOver, "Step Over", (d, c) => _ = d.ResumeAsync(session => session.StepOverAsync(), "step over"), (d, _) => d.NotPaused(), new("Run", 1, 2));
        Add(AppActions.StepInto, "Step Into", (d, c) => _ = d.ResumeAsync(session => session.StepIntoAsync(), "step into"), (d, _) => d.NotPaused(), new("Run", 1, 3));
        Add(AppActions.SmartStepInto, "Smart Step Into", (d, c) => _ = d.SmartStepIntoAsync(), (d, _) => d.NotPaused(), new("Run", 1, 4));
        Add(AppActions.StepOut, "Step Out", (d, c) => _ = d.ResumeAsync(session => session.StepOutAsync(), "step out"), (d, _) => d.NotPaused(), new("Run", 1, 5));
        Add(AppActions.RunToCursor, "Run to Cursor", (d, c) => _ = d.ResumeAsync(session => session.RunToCursorAsync(c.File!.Path, c.Caret.Line), "run to the cursor"),
            (d, c) => NeedsFile(c) ?? d.NotPaused(), new("Run", 1, 6));
        Add(AppActions.SetNextStatement, "Set Next Statement", (d, c) => _ = d.SetNextStatementAsync(d.Session!, c.File!.Path, c.Caret.Line),
            (d, c) => NeedsFile(c) ?? d.NotPaused(), new("Run", 1, 7));
        Add(AppActions.ShowExecutionPoint, "Show Execution Point", (d, _) =>
        {
            (string path, int line) = d.executionPoint!.Value;
            d.Navigate?.Invoke(d, (path, new TextPosition(d.Current(path, line), 0)));
        }, (d, _) => d.NotPaused() ?? (d.executionPoint is null ? "Where the program is paused has no source" : null), new("Run", 1, 8));
        Add(AppActions.Evaluate, "Evaluate Expression...", (d, c) => d.view.FocusEvaluate(c.Symbol ?? string.Empty), (d, _) => d.NotPaused(), new("Run", 1, 9));
        Add(AppActions.AddToWatches, "Add to Watches", (d, c) => _ = d.Evaluate(c.Symbol!, watch: true),
            (d, c) => d.NotPaused() ?? (c.Symbol is { Length: > 0 } ? null : "No expression is at the caret"), null);
        // In Rider's Run menu after Stop, and on the Debug window's toolbar after it.
        Add(AppActions.Detach, "Detach", (d, c) => _ = d.DetachAsync(), (d, _) => d.WhyNotDetach(), new("Run", 0, 8));
        // Rider's toggle: every breakpoint stays as it is, and none stops the program until it is chosen again.
        actions.Add(new IdeAction(AppActions.MuteBreakpoints, "Mute Breakpoints", "Debug", _ => current().Breakpoints.SetMuted(!current().Breakpoints.Muted))
        {
            Menus = [new("Run", 2, 4)],
            IsChecked = _ => current().Breakpoints.Muted,
        });

        void Add(string id, string title, Action<DebugController, ActionContext> run, Func<DebugController, ActionContext, string?> whyNot, MenuPlace? menu) =>
            actions.Add(new IdeAction(id, title, "Debug", c => run(current(), c)) { WhyNot = c => whyNot(current(), c), Menus = menu is null ? [] : [menu] });
    }

    private const string NothingDebugged = "Nothing is being debugged";

    private static string? NeedsFile(ActionContext context) => context.File is null ? "No file is open" : null;

    /// <summary>The Avalonia XAML attribute the caret is on, whose own breakpoint Toggle Breakpoint sets; null elsewhere.</summary>
    public static string? AttributeAtCaret(ActionContext context) =>
        context.File is { } file && XamlBreakpoints.Compiled(file.Path)
            ? XamlBreakpoints.AttributeAt(file.Text.Snapshot, file.Text.Snapshot.GetOffset(context.Caret))
            : null;

    /// <summary>Why a step cannot go now: nothing is being debugged, or the program runs.</summary>
    private string? NotPaused() => !IsDebugging ? NothingDebugged : Session!.State != DebugState.Paused ? "The program is not paused" : null;

    /// <summary>Sets the paused program going as an action asks. One the debugger refuses leaves it paused where it was,
    /// the Debug window as it was, and the reason in the status bar. Edits made while it was paused are applied first, as
    /// Rider's "Changes will be applied on the next step or resume": one that needs a restart restarts it (or asks, as
    /// Settings says); one that does not compile is left, and the program goes on in the code it has, never stranded.</summary>
    private async Task ResumeAsync(Func<DebugSession, Task> resume, string action, bool applyXaml = false)
    {
        if (Session is not { } session)
        {
            return;
        }
        HotReload? reload = HotReload;
        bool xamlAfterResume = reload is { Changed: true, HasXamlChanges: true };
        if (HotReload is { Changed: true } && WhyNotApply() is null && session.State == DebugState.Paused)
        {
            if (await ApplyChangesAsync(beforeResume: xamlAfterResume) is { Result: HotReloadResult.RestartNeeded } outcome && await RestartAsked(outcome))
            {
                return;
            }
            if (session != Session || session.State != DebugState.Paused)
            {
                return;
            }
        }
        try
        {
            await resume(session);
            // A step/run-to-cursor can stop before the UI thread serves anything. XAML stays pending for Continue;
            // it must never queue a mutation behind that next stop or hold up the person's next step for its timeout.
            if (applyXaml && xamlAfterResume && session == Session && session.State == DebugState.Running && reload == HotReload
                && reload is { Changed: true, HasXamlChanges: true } && WhyNotApply() is null)
            {
                if (await ApplyChangesAsync() is { Result: HotReloadResult.RestartNeeded } outcome)
                {
                    await RestartAsked(outcome);
                }
            }
        }
        catch (DapException error) when (session == Session && session.State != DebugState.Ended)
        {
            Message?.Invoke(this, $"Could not {action}: {error.Reason}");
        }
        catch (DapException)
        {
            // The session ended meanwhile, which says why itself.
        }
    }

    /// <summary>Rider's Smart Step Into: the calls of the line the program is paused at, in the order they run, offered in
    /// its editor (each filled, the chosen one boxed, Rider's colours) to step straight into: the arrow keys or Tab move the
    /// choice, Enter or a click takes it, Escape leaves it. One call is stepped into at once; with none, it is Step Into.</summary>
    private async Task SmartStepIntoAsync()
    {
        UiThread.Check();
        if (Session is not { State: DebugState.Paused } session || Stop is not { Frames: [var top, ..] } || top.Source?.Path is not { } source)
        {
            await ResumeAsync(session => session.StepIntoAsync(), "step into");
            return;
        }
        int stop = shown;
        IReadOnlyList<StepInTarget> targets;
        try
        {
            targets = await session.StepInTargetsAsync(top.Id);
        }
        catch (DapException error)
        {
            Message?.Invoke(this, $"Smart Step Into: {error.Reason}");
            return;
        }
        if (stop != shown)
        {
            return;
        }
        string path = PathNames.Canonical(source);
        Navigate?.Invoke(this, (path, new TextPosition(Current(path, Math.Max(0, top.Line - 1)), Math.Max(0, top.Column - 1))));
        CodeEditor editor = editors.CodeEditor;
        List<(StepInTarget Target, TextRange Range)> placed = editors.Active?.Path == path ? Placed(targets, path, editor.Document.Snapshot) : [];
        if (placed.Count <= 1)
        {
            // Nothing to choose between: the one call there is, else Step Into, as Rider does.
            await ResumeAsync(session => placed.Count == 1 ? session.StepIntoTargetAsync(placed[0].Target.Id) : session.StepIntoAsync(), "step into");
            return;
        }
        editor.Focus();
        editor.OfferChoices([.. placed.Select(p => p.Range)], chosen =>
        {
            if (chosen is int index && Session == session && session.State == DebugState.Paused && stop == shown)
            {
                _ = ResumeAsync(paused => paused.StepIntoTargetAsync(placed[index].Target.Id), "smart step into");
            }
        });
        Message?.Invoke(this, "Smart Step Into: choose a call with the arrow keys or Tab, Enter to step into it, Escape to leave");
    }

    /// <summary>Where each target is in a file's text as it is now (from the built lines), those placed on one line each.</summary>
    private List<(StepInTarget, TextRange)> Placed(IReadOnlyList<StepInTarget> targets, string path, TextSnapshot text)
    {
        List<(StepInTarget, TextRange)> placed = [];
        foreach (StepInTarget target in targets)
        {
            if (target is not { Line: int line, Column: int column, EndLine: int endLine, EndColumn: int endColumn } || line != endLine)
            {
                continue;
            }
            int now = Current(path, line - 1);
            if (now < 0 || now >= text.LineCount)
            {
                continue;
            }
            LineSpan row = text.GetLine(now);
            int start = row.Start;
            int length = row.Length;
            if (column - 1 > length || endColumn - 1 > length || endColumn <= column)
            {
                continue;
            }
            placed.Add((target, TextRange.FromBounds(start + column - 1, start + endColumn - 1)));
        }
        return placed;
    }

    /// <summary>Rider's Set Next Statement: the paused thread goes on from a line (from zero) of the method it is
    /// in, skipping or repeating what lies between; null when it went, else why not.</summary>
    public async Task<string?> SetNextStatementAsync(DebugSession session, string path, int line)
    {
        UiThread.Check();
        string? problem = null;
        try
        {
            if (session.StoppedThread is not int thread)
            {
                problem = "No thread is paused";
            }
            else if (await session.GotoTargetAsync(path, Built(path, line) + 1) is not { } target)
            {
                problem = $"Line {line + 1} is not in the method the program is paused in";
            }
            else
            {
                await session.GotoAsync(thread, target);
            }
        }
        catch (DapException error)
        {
            problem = error.Reason;
        }
        if (problem is not null)
        {
            Message?.Invoke(this, $"Set Next Statement: {problem}");
        }
        return problem;
    }

    /// <summary>A file opened in a tab: its breakpoints, and the stop shown in it, follow its edits, and its gutter shows them.</summary>
    public void Attach(OpenDocument document)
    {
        UiThread.Check();
        EventHandler<DocumentChangedEventArgs> follow = (_, change) =>
        {
            // The built lines first: a breakpoint's move asks them what it now asks of the debugger.
            lines?.Follow(document.Path, change);
            if (hotReload is not null && change.Edits.Count > 0)
            {
                if (Path.GetExtension(document.Path).ToLowerInvariant() is ".cs" or ".razor" or ".cshtml")
                {
                    hotReload.Edited(document.Path, change.Before);
                }
                else if (Path.GetExtension(document.Path).ToLowerInvariant() is ".axaml" or ".xaml")
                {
                    hotReload.EditedXaml(document.Path, change.Before, document.Text.Snapshot);
                }
            }
            Breakpoints.Follow(document.Path, change);
            FollowStop(document, change);
            if (Breakpoints.In(document.Path).Any(b => b.Attribute is not null))
            {
                // An attribute's tint goes where the attribute now is, which an edit inside its line moves.
                UpdateMarks(document);
            }
        };
        following[document] = follow;
        document.Text.Changed += follow;
        document.Saved += OnSaved;
        UpdateMarks(document);
    }

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        if (following.Remove(document, out EventHandler<DocumentChangedEventArgs>? follow))
        {
            document.Text.Changed -= follow;
            document.Saved -= OnSaved;
        }
    }

    /// <summary>Whether a save while the program is debugged applies the edits (Settings' Apply changes on file save).</summary>
    public Func<bool> HotReloadOnSave { get; init; } = () => false;

    /// <summary>An apply a save asked for, run once the saves of one Save All are done.</summary>
    private bool applyOnSave;

    private void OnSaved(object? sender, EventArgs e)
    {
        if (applyOnSave || HotReload is not { Changed: true } || !HotReloadOnSave() || WhyNotApply() is not null)
        {
            return;
        }
        applyOnSave = true;
        Dispatcher.UIThread.Post(() =>
        {
            applyOnSave = false;
            _ = ApplyAndRestartAsync();
        });
    }

    /// <summary>The value of the expression under the pointer while paused (a.b.c up to the word
    /// hovered), in the chosen frame; null when there is none or it does not evaluate.</summary>
    public async Task<string?> ValueAt(int offset, CancellationToken cancellation)
    {
        UiThread.Check();
        if (Session is not { State: DebugState.Paused } session || frame is null || ExpressionAt(editors.CodeEditor.Document.Snapshot, offset) is not { } expression)
        {
            return null;
        }
        try
        {
            EvaluateResponse value = await session.EvaluateAsync(expression, frame.Id, "hover").WaitAsync(cancellation);
            return $"{expression} = {(value.Type is { Length: > 0 } type && value.Result != type ? $"{{{type}}} " : "")}{value.Result}";
        }
        catch (DapException)
        {
            return null;
        }
    }

    /// <summary>The member access chain ending at the word around an offset: "this.name" for "name"
    /// in "this.name.Length", as Rider evaluates a hover.</summary>
    public static string? ExpressionAt(TextSnapshot text, int offset)
    {
        if (offset >= text.Length || !IsWordChar(text[offset]))
        {
            if (offset == 0 || offset > text.Length || !IsWordChar(text[offset - 1]))
            {
                return null;
            }
            offset--;
        }
        int end = offset;
        while (end < text.Length && IsWordChar(text[end]))
        {
            end++;
        }
        int start = offset;
        while (true)
        {
            while (start > 0 && IsWordChar(text[start - 1]))
            {
                start--;
            }
            if (start > 1 && text[start - 1] == '.' && IsWordChar(text[start - 2]))
            {
                start--;
                continue;
            }
            break;
        }
        string expression = text.GetText(start, end - start);
        return expression.Length == 0 || char.IsAsciiDigit(expression[0]) || Keywords.Contains(expression) ? null : expression;
    }

    private static readonly HashSet<string> Keywords =
    [
        "var", "int", "long", "string", "bool", "double", "float", "decimal", "char", "byte", "object", "void", "class",
        "struct", "record", "interface", "enum", "namespace", "using", "public", "private", "protected", "internal",
        "static", "readonly", "const", "new", "return", "if", "else", "for", "foreach", "while", "do", "switch", "case",
        "break", "continue", "in", "is", "as", "await", "async", "true", "false", "null", "throw", "try", "catch", "finally",
    ];

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private async Task OnStateChanged(DebugSession session)
    {
        if (session != Session || disposed)
        {
            return;
        }
        // What the program wrote before it stopped or ended is shown before the stop or the end is.
        if (session.State == DebugState.Ended)
        {
            view.Console.Input = null;
            view.Console.Sink.End(ConsoleStream.Output);
            view.Console.Sink.End(ConsoleStream.Error);
            view.Console.Sink.End(ConsoleStream.System);
        }
        view.Console.Flush();
        view.SetState(session.State);
        view.ShowDetachable(session.Attached && session.State is DebugState.Running or DebugState.Paused);
        // Every stop is new and every run lets the last one's frames and values go: none is asked of again.
        shown++;
        frame = null;
        Stop = null;
        clearing.Stop();
        switch (session.State)
        {
            case DebugState.Paused:
                int stop = shown;
                await ShowStop(session);
                if (stop != shown)
                {
                    break;
                }
                Message?.Invoke(this, Said(session));
                break;
            case DebugState.Running:
                editors.CodeEditor.CancelChoices();
                executionPoint = null;
                stopException = null;
                bindingStop = null;
                bindingFrame = null;
                UpdateAllMarks();
                ClearInlineValues();
                clearing.Start();
                Message?.Invoke(this, Said(session));
                break;
            case DebugState.Ended:
                hotReload?.End();
                executionPoint = null;
                lines = null;
                UpdateAllMarks();
                ClearInlineValues();
                view.ShowFrames([], null, [], null);
                view.Tree.Show([]);
                view.SetState(DebugState.Ended);
                Message?.Invoke(this, Said(session));
                break;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A session's state in words: the status bar's as it changes, and <see cref="Summary"/>'s.</summary>
    private string Said(DebugSession session) => session.State switch
    {
        DebugState.Starting => $"Debugging {name}",
        DebugState.Paused when stopException is { } thrown => $"{name}: paused on {thrown.Text}",
        DebugState.Paused when undescribed is { } why => $"{name}: paused on an exception the debugger could not describe ({why})",
        DebugState.Paused when session.DataStop is ({ } watched, var change) =>
            $"{name}: Data Breakpoint Hit: {watched.Location} is changed{(change is null ? string.Empty : $" ({change})")}",
        DebugState.Paused when session.PropertyStop is { } property =>
            $"{name}: Property Breakpoint Hit: {property.Location} is changed{(propertyChange is null ? string.Empty : $" ({propertyChange})")}",
        DebugState.Paused when bindingStop is { } failed =>
            $"{name}: paused on a binding error{(BindingErrors.Where(failed.Problem) is { } where ? $" at {where}" : string.Empty)}: {BindingErrors.Stopped(failed.Problem.Failure)}",
        DebugState.Paused when session.BindingErrorStop => $"{name}: paused on a binding error the debugger could not describe",
        DebugState.Paused => $"{name}: paused ({session.StopReason})",
        DebugState.Running => $"{name}: running",
        _ when session.Detached => $"Detached from {name}, which runs on",
        _ => session.Failure is { } failure
            ? $"Debugging {name} ended: {failure}"
            : $"Debugging {name} ended{(session.ExitCode is int code ? $" with exit code {code}" : "")}",
    };

    /// <summary>Shows a stop: its exception described, then its thread's frames and the top one's values. An exception the
    /// debugger cannot describe (a getter of its own that never returns) still shows where the program stopped.</summary>
    private async Task ShowStop(DebugSession session)
    {
        if (session.StoppedThread is not int thread)
        {
            return;
        }
        int stop = shown;
        stopException = null;
        undescribed = null;
        propertyChange = null;
        bindingStop = null;
        bindingFrame = null;
        if (session.PropertyStop is not null)
        {
            try
            {
                // Said by the inspector's call the debugger stopped in: the values, and what set the new one.
                if ((await session.StackTraceAsync(thread)).FirstOrDefault() is { } changed)
                {
                    propertyChange = StringValue((await session.EvaluateAsync("change", changed.Id, "watch")).Result);
                }
            }
            catch (DapException)
            {
                // The stop shows without its words.
            }
            if (stop != shown)
            {
                return;
            }
        }
        if (session.BindingErrorStop)
        {
            try
            {
                bindingStop = await ReadBindingStop(session, thread);
            }
            catch (DapException)
            {
                // The stop shows without its words.
            }
            if (stop != shown)
            {
                return;
            }
            if (bindingStop is { } failed)
            {
                view.Console.Append(BindingErrors.StopLine(failed.Problem, Folder), error: true);
            }
        }
        if (session.StopReason == "exception")
        {
            try
            {
                ExceptionInfoResponse info = await session.ExceptionInfoAsync(thread);
                IReadOnlyList<DapStackFrame> stack = await session.StackTraceAsync(thread);
                DapStackFrame? thrown = stack.FirstOrDefault(f => f.Source?.Path is not null);
                stopException = (Describe(info), thrown?.Id ?? -1);
            }
            catch (DapException error)
            {
                undescribed = error.Reason;
            }
            if (stop != shown)
            {
                return;
            }
        }
        try
        {
            await ChooseThread(thread);
        }
        catch (DapException error)
        {
            Message?.Invoke(this, $"The debugger could not show the stop: {error.Reason}");
        }
    }

    /// <summary>The failed binding the program stopped at, as the inspector's call the debugger stopped in has it: the failure
    /// it gives as the protocol writes one, placed in its XAML, and the call's parameter, which Variables shows as
    /// <c>$bindingError</c>. Null when the call's frame does not say.</summary>
    private async Task<BindingStop?> ReadBindingStop(DebugSession session, int thread)
    {
        if ((await session.StackTraceAsync(thread)).FirstOrDefault() is not { } call)
        {
            return null;
        }
        EvaluateResponse error = await session.EvaluateAsync("bindingError", call.Id, "watch");
        if (StringValue((await session.EvaluateAsync("bindingError.Said", call.Id, "watch")).Result) is not { } said)
        {
            return null;
        }
        BindingFailure? failure;
        try
        {
            failure = JsonSerializer.Deserialize(said, WireJson.Default.BindingFailure);
        }
        catch (JsonException)
        {
            return null;
        }
        return failure is null ? null : new BindingStop(
            BindingErrors.Problem(failure, failure.Place is { } at ? BindingErrors.TextOf(at.File, editors.Documents) : null), error.Result, error.Type, error.VariablesReference);
    }

    /// <summary>A string as the debugger shows one, a C# literal, as it is; null for anything else (an error, null).</summary>
    public static string? StringValue(string shown) =>
        shown.Length > 1 && shown[0] == '"' && SyntaxFactory.ParseExpression(shown) is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal
            ? literal.Token.ValueText
            : null;

    private async Task ChooseThread(int thread)
    {
        if (Session is not { State: DebugState.Paused } session)
        {
            return;
        }
        int stop = shown;
        IReadOnlyList<DapThread> threads = await session.ThreadsAsync();
        IReadOnlyList<DapStackFrame> stack = await session.StackTraceAsync(thread);
        if (stop != shown)
        {
            return;
        }
        // A property breakpoint, and XAML Binding Errors, stop in the inspector's call, under Avalonia's: the user's code
        // that set the value or made the binding fail is where it stopped, as Just My Code shows it; with none (a style a
        // pointer set) the call's own frame, whose values say what happened, but no line of the inspector's.
        DapStackFrame? user = stack.FirstOrDefault(f => f.Source?.Path is not null && f.PresentationHint != "subtle");
        bool inspected = thread == session.StoppedThread && (session.PropertyStop is not null || session.BindingErrorStop);
        DapStackFrame? top = inspected ? user ?? stack.FirstOrDefault() : stack.FirstOrDefault(f => f.Source?.Path is not null) ?? stack.FirstOrDefault();
        bool shows = !inspected || top == user;
        executionPoint = thread == session.StoppedThread && shows && top?.Source?.Path is { } path ? (path, top.Line - 1) : executionPoint;
        if (thread == session.StoppedThread)
        {
            Stop = new DebugStop(thread, session.StopReason, stack, top, stopException?.Text) { Binding = bindingStop?.Problem };
            bindingFrame = bindingStop is not null && shows ? top?.Id : null;
        }
        view.ShowFrames(threads, threads.FirstOrDefault(t => t.Id == thread), stack, top);
        UpdateAllMarks();
        if (top is not null)
        {
            await ChooseFrame(top, navigate: shows);
        }
        if (thread == session.StoppedThread && !shows && bindingStop?.Problem.Problem is { Path: { } xaml } failedAt && File.Exists(xaml))
        {
            // No code of the app's made the binding fail (a template's, a style's): its XAML is where to look.
            Navigate?.Invoke(this, (xaml, failedAt.Position));
        }
    }

    private async Task ChooseFrame(DapStackFrame chosen, bool navigate)
    {
        if (Session is not { State: DebugState.Paused })
        {
            return;
        }
        frame = chosen;
        if (navigate && chosen.Source?.Path is { } path && File.Exists(path))
        {
            Navigate?.Invoke(this, (path, new TextPosition(Current(path, Math.Max(0, chosen.Line - 1)), Math.Max(0, chosen.Column - 1))));
        }
        await ShowVariables();
    }

    /// <summary>The chosen frame's variables, under its watches and the last result evaluated.</summary>
    private async Task ShowVariables()
    {
        UiThread.Check();
        if (Session is not { State: DebugState.Paused } session || frame is not { } current)
        {
            return;
        }
        List<VariableNode> rows = [];
        if (bindingStop is { } failed && Stop is { } stopped && stopped.Frames.Any(f => f.Id == current.Id))
        {
            // The failed binding above the frame's own values, where Rider lists $exception: the inspector call's parameter.
            rows.Add(new VariableNode(BindingStop.Name, failed.Value, failed.Type, failed.Reference, 0, VariableRole.Value));
        }
        foreach (string watch in watches)
        {
            rows.Add(await Evaluated(session, current, watch, VariableRole.Watch));
        }
        if (lastResult is { } result)
        {
            rows.Add(await Evaluated(session, current, result.Expression, VariableRole.Result));
        }
        Dictionary<string, string> values = [];
        try
        {
            IReadOnlyList<DapScope> scopes = await session.ScopesAsync(current.Id);
            foreach (DapScope scope in scopes)
            {
                IReadOnlyList<DapVariable> variables = await session.VariablesAsync(scope.VariablesReference);
                foreach (DapVariable variable in variables)
                {
                    values.TryAdd(variable.Name, variable.Value);
                }
                if (scopes.Count == 1)
                {
                    // One scope (the locals) needs no node of its own, as Rider shows them.
                    rows.AddRange(variables.Select(v => VariableNode.From(v, 0)));
                }
                else
                {
                    rows.Add(new VariableNode(scope.Name, string.Empty, null, scope.VariablesReference, 0, VariableRole.Scope));
                }
            }
        }
        catch (DapException error)
        {
            rows.Add(new VariableNode("error", error.Reason, null, 0, 0, VariableRole.Error));
        }
        if (frame == current)
        {
            view.Tree.Show(rows);
            ShowInlineValues(current, values);
        }
    }

    /// <summary>The chosen frame's values after the lines of its method, in its file and no other.</summary>
    private void ShowInlineValues(DapStackFrame current, IReadOnlyDictionary<string, string> values)
    {
        string? path = current.Source?.Path is { } source ? PathNames.Canonical(source) : null;
        foreach (OpenDocument document in editors.Documents)
        {
            if (document.Path != path)
            {
                document.LineAnnotations = [];
                continue;
            }
            int line = Current(document.Path, current.Line - 1);
            IReadOnlyList<LineAnnotation> inline = document.Syntax is { } syntax ? InlineValues.For(syntax.Current, line, values) : [];
            document.LineAnnotations = stopException is { } thrown && thrown.FrameId == current.Id
                ? [new LineAnnotation(line, thrown.Text, LineAnnotationKind.Exception), .. inline]
                : bindingStop is { } failed && bindingFrame == current.Id
                    ? [new LineAnnotation(line, $"Binding error{(BindingErrors.Where(failed.Problem) is { } where ? $" at {where}" : string.Empty)}: {BindingErrors.Stopped(failed.Problem.Failure)}", LineAnnotationKind.Exception), .. inline]
                    : inline;
        }
    }

    /// <summary>An exception as Rider's stop names it: its type without the namespace, and its message.</summary>
    public static string Describe(ExceptionInfoResponse info)
    {
        string type = info.Details?.TypeName ?? info.ExceptionId.Replace("CLR/", "", StringComparison.Ordinal);
        string shortType = type[(type.LastIndexOf('.') + 1)..];
        string? message = info.Details?.Message ?? info.Description;
        return string.IsNullOrEmpty(message) ? shortType : $"{shortType}: {message}";
    }

    private void ClearInlineValues()
    {
        foreach (OpenDocument document in editors.Documents)
        {
            document.LineAnnotations = [];
        }
    }

    private static async Task<VariableNode> Evaluated(DebugSession session, DapStackFrame frame, string expression, VariableRole role)
    {
        try
        {
            EvaluateResponse value = await session.EvaluateAsync(expression, frame.Id, role == VariableRole.Watch ? "watch" : "repl");
            return new VariableNode(expression, value.Result, value.Type, value.VariablesReference, 0, role);
        }
        catch (DapException error)
        {
            return new VariableNode(expression, error.Reason, null, 0, 0, role == VariableRole.Watch ? VariableRole.Watch : VariableRole.Error);
        }
    }

    /// <summary>Why no data breakpoint can watch a row of Variables that is no member of a value: a local, a watch, a result.</summary>
    public const string NotAMember = "A data breakpoint watches a field or property of an object, or an element of an array, and this is neither";

    /// <summary>What Rider's Set Data Breakpoint offers on a row of Variables, the debugger asked: to set one, to remove the
    /// one set on it (Remove Data Breakpoint), or neither and why (the system, the kind of member, the program running).</summary>
    public async Task<DataWatchOffer> DataWatchAsync(VariableNode node)
    {
        UiThread.Check();
        (DataBreakpointInfoResponse? info, string? whyNot) = await DataInfoAsync(node);
        return info?.DataId is { } dataId && Breakpoints.FindData(dataId) is not null
            ? new DataWatchOffer(DataWatchOffer.Remove, null)
            : new DataWatchOffer(DataWatchOffer.Set, whyNot);
    }

    /// <summary>Sets a data breakpoint on a row of Variables, or removes the one set on it, as Rider's Set Data Breakpoint
    /// and Remove Data Breakpoint do; why it cannot is said in the status bar.</summary>
    public async Task ToggleDataBreakpointAsync(VariableNode node)
    {
        UiThread.Check();
        // Held until the breakpoint is in the store, so a picture taken after the choice shows it.
        using IDisposable held = Editor.BackgroundWork.Scope();
        (DataBreakpointInfoResponse? info, string? whyNot) = await DataInfoAsync(node);
        if (info?.DataId is not { } dataId)
        {
            Message?.Invoke(this, $"Set Data Breakpoint: {whyNot}");
            return;
        }
        if (Breakpoints.FindData(dataId) is { } set)
        {
            Breakpoints.Remove(set);
            return;
        }
        Breakpoints.AddData(dataId, DataLocation(node));
    }

    /// <summary>What the debugger says of watching a row: its answer, or why it was not asked or could not answer.</summary>
    private async Task<(DataBreakpointInfoResponse? Info, string? WhyNot)> DataInfoAsync(VariableNode node)
    {
        if (Session is not { State: DebugState.Paused } session)
        {
            return (null, NotPaused() ?? "The program is not paused");
        }
        if (session.DataBreakpointsUnavailable is { } unavailable)
        {
            return (null, unavailable);
        }
        // The value the row is a member of, by the reference its members were listed by; a frame's own rows have none.
        if (node.Parent is not { Reference: > 0, Role: not VariableRole.Scope } owner)
        {
            return (null, NotAMember);
        }
        try
        {
            DataBreakpointInfoResponse info = await session.DataBreakpointInfoAsync(owner.Reference, node.Name);
            return (info, info.DataId is null ? info.Description : null);
        }
        catch (DapException error)
        {
            return (null, error.Reason);
        }
    }

    /// <summary>A watched member as Rider's Data Breakpoint Hit names it: the member on its object, and the object's type.</summary>
    private static string DataLocation(VariableNode node)
    {
        VariableNode owner = node.Parent is { Name: "Raw View", Parent: { } viewed } ? viewed : node.Parent!;
        string what = owner.Expression ?? owner.Name;
        return owner.Type is { Length: > 0 } type ? $"{node.Name} on {what} ({type})" : $"{node.Name} on {what}";
    }

    /// <summary>Says in the status bar, once each, why the debugger refused a data breakpoint (a fifth one, say).</summary>
    private void TellRefusedData()
    {
        refusalsTold.IntersectWith(Breakpoints.Data);
        foreach (DataBreakpoint refused in Breakpoints.Data.Where(d => d is { Status: BreakpointStatus.Invalid, Message: not null }))
        {
            if (refusalsTold.Add(refused))
            {
                Message?.Invoke(this, $"Data breakpoint on {refused.Location}: {refused.Message}");
            }
        }
    }

    /// <summary>Shows the chosen frame's variables and inline values again, as a value changed.</summary>
    public Task RefreshAsync() => ShowVariables();

    /// <summary>Set Value: what a row's expression names takes the value typed (a C# expression), and the frame's
    /// variables and inline values show what it now is; a refusal is said in the status bar.</summary>
    public async Task SetValue(VariableNode node, string value)
    {
        UiThread.Check();
        node.IsEditing = false;
        if (Session is not { State: DebugState.Paused } session || frame is not { } current || node.Expression is not { } expression)
        {
            return;
        }
        try
        {
            await session.SetExpressionAsync(expression, value, current.Id);
        }
        catch (DapException error)
        {
            Message?.Invoke(this, $"Could not set {expression}: {error.Reason}");
        }
        await ShowVariables();
    }

    private async Task Evaluate(string expression, bool watch)
    {
        if (watch)
        {
            if (!watches.Contains(expression))
            {
                watches.Add(expression);
            }
        }
        else
        {
            lastResult = (expression, string.Empty);
        }
        await ShowVariables();
    }

    private void UpdateAllMarks()
    {
        foreach (OpenDocument document in editors.Documents)
        {
            UpdateMarks(document);
        }
    }

    private void UpdateMarks(string path)
    {
        foreach (OpenDocument document in editors.Documents.Where(d => d.Path == path))
        {
            UpdateMarks(document);
        }
    }

    private void UpdateMarks(OpenDocument document)
    {
        bool debugging = IsDebugging;
        // Muted, a breakpoint shows as Rider's muted ones do, grey whatever the debugger said of it.
        bool muted = Breakpoints.Muted;
        TextSnapshot text = document.Text.Snapshot;
        List<LineMark> marks = [.. Breakpoints.In(document.Path).Select(b => new LineMark(b.Line, b switch
        {
            { Enabled: false } => LineMarkKind.BreakpointDisabled,
            _ when muted => LineMarkKind.Breakpoint,
            { Status: BreakpointStatus.Bound } when debugging => LineMarkKind.BreakpointBound,
            { Status: BreakpointStatus.Invalid } when debugging => LineMarkKind.BreakpointInvalid,
            _ => LineMarkKind.Breakpoint,
        }, Conditional: b.Condition is not null || b.HitCondition is not null, Tracepoint: !b.Suspend, Muted: muted,
            // An attribute's breakpoint tints the attribute, as Rider's inline breakpoints tint what they are on; nothing once
            // the attribute is edited away.
            Range: b.Attribute is { } attribute ? XamlBreakpoints.AttributeRange(text, b.Line, attribute) ?? new TextRange(0, 0) : null))];
        if (executionPoint is { } point && point.Path == document.Path)
        {
            marks.Add(new LineMark(Current(point.Path, point.Line), LineMarkKind.ExecutionPoint));
        }
        document.LineMarks = marks;
    }

    /// <summary>Carries the stop shown in a file through an edit to it, as Rider's execution point and inline values go
    /// with the text: the arrow from the built lines, the values a line at a time.</summary>
    private void FollowStop(OpenDocument document, DocumentChangedEventArgs change)
    {
        if (executionPoint is { } point && point.Path == document.Path)
        {
            UpdateMarks(document);
        }
        if (document.LineAnnotations.Count > 0)
        {
            document.LineAnnotations = [.. document.LineAnnotations.Select(a => a with { Line = TextEditing.MapLine(change.Before, change.Edits, change.After, a.Line) })];
        }
    }

    /// <summary>Where a built line (from zero) of a file is now, while a session runs: what the program's records of its build
    /// (an app's XAML places) are read against.</summary>
    public int CurrentLine(string path, int line) => Current(path, line);

    /// <summary>The built line a line (from zero) of a file has, while a session runs.</summary>
    public int BuiltLine(string path, int line) => Built(path, line);

    /// <summary>Whether a line (from zero) of a file is the build's, rather than written since the session began.</summary>
    public bool IsBuiltLine(string path, int line) => lines?.IsBuilt(path, line) ?? true;

    /// <summary>Where a built line of a file is now, while a session runs.</summary>
    private int Current(string path, int line) => lines?.ToCurrent(path, line) ?? line;

    /// <summary>The built line a line of a file has, while a session runs.</summary>
    private int Built(string path, int line) => lines?.ToBuilt(path, line) ?? line;
}

/// <summary>What Set Data Breakpoint offers on a row of Variables: its title (Rider's Set Data Breakpoint, or Remove Data
/// Breakpoint on a member watched), and why it cannot be chosen when it cannot.</summary>
public sealed record DataWatchOffer(string Title, string? WhyNot)
{
    public const string Set = "Set Data Breakpoint";
    public const string Remove = "Remove Data Breakpoint";
}

/// <summary>A stop as the debugger read it: the thread that stopped and why, its frames (the top one with source among
/// them), and the exception it stopped on, described as the stop's line shows it.</summary>
public sealed record DebugStop(int Thread, string? Reason, IReadOnlyList<DapStackFrame> Frames, DapStackFrame? Top, string? Exception)
{
    /// <summary>The failed binding XAML Binding Errors stopped at, placed in its XAML.</summary>
    public BindingProblem? Binding { get; init; }
}

/// <summary>A failed binding the program stopped at: the failure placed in its XAML (at the member its path misspells, else
/// its control's element), and the inspector call's parameter as the debugger shows it, which opens into its parts.</summary>
public sealed record BindingStop(BindingProblem Problem, string Value, string? Type, int Reference)
{
    /// <summary>Its row in Variables, as Rider names the exception stopped at $exception; the breakpoint's condition and log
    /// message read it by the same name.</summary>
    public const string Name = "$bindingError";
}
