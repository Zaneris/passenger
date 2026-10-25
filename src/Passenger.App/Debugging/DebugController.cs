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
/// a value when the pointer rests on an expression while paused. Several programs may be debugged at once, a session and a
/// tab of the Debug window each: every breakpoint goes to each, and the window shows the chosen tab's, a stop in any program
/// choosing its tab. A UI-thread object.</summary>
public sealed class DebugController : IDisposable
{
    /// <summary>The words in the console of a program attached to, whose output goes where it always went.</summary>
    public const string AttachedConsole = "Console output is not available when the debugger is attached to a running process";

    private readonly EditorArea editors;
    private readonly DebugModel view;
    private readonly List<string> watches = [];
    private readonly Dictionary<OpenDocument, EventHandler<DocumentChangedEventArgs>> following = [];

    /// <summary>Where the program stopped, at its built line: shown where that line's code now is.</summary>
    private (string Path, int Line)? executionPoint;

    /// <summary>The chosen session's built lines, from its start to its end: the files' edits since the build.</summary>
    private BuiltLines? lines => chosen?.Lines;

    /// <summary>The tab the Debug window shows, whose session the actions act on; null before the first session.</summary>
    private DebugTab? chosen;

    /// <summary>The last expression the person evaluated (not a watch), as it came out, and the stop it was evaluated at
    /// (<see cref="shown"/>): listed under the watches while that stop is shown, never evaluated again unasked.</summary>
    private (int Stop, VariableNode Row)? lastResult;

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

    /// <summary>Cancelled as the stop shown goes (the program goes on, another tab is chosen, the folder is left), so what was
    /// read at it and still shows (a hover card's value) goes with it.</summary>
    private CancellationTokenSource stopShown = new();

    /// <summary>The chosen session's name, as the status bar names it ("Tests", a run configuration's).</summary>
    private string name => chosen?.Name ?? string.Empty;

    /// <summary>The data breakpoints whose refusal the status bar has said, so it says each once.</summary>
    private readonly HashSet<DataBreakpoint> refusalsTold = [];

    /// <summary>Clears the last stop's frames and values a moment after the program goes on: a step that stops again at once
    /// replaces them without the panes going blank between.</summary>
    private readonly Debounce clearing;

    /// <summary>Takes this debugger's handlers off the Debug window, which the next session's debugger has from then.</summary>
    private readonly Action letGo;

    private bool disposed;
    private bool ending;

    /// <summary>A folder's debugger over the window's editors and Debug window, which it has until it is disposed.</summary>
    public DebugController(EditorArea editors, DebugModel view)
    {
        this.editors = editors;
        this.view = view;
        // The window's Debug tabs are this folder's from now: the last folder's, ended or ending, are let go.
        view.ForgetSessions();
        clearing = new Debounce(TimeSpan.FromMilliseconds(100), () =>
        {
            if (Session is not { State: DebugState.Paused })
            {
                view.ShowFrames([], null, [], null);
                view.Tree.Show([]);
            }
            return Task.CompletedTask;
        });
        Breakpoints.Changed += (_, path) => UpdateMarks(path);
        Breakpoints.ExceptionsChanged += (_, _) => view.ShowMuted(Breakpoints.Muted);
        EventHandler<string> asked = (_, action) => ActionRequested?.Invoke(this, action);
        EventHandler<DapStackFrame> framed = (_, chosen) => IdeErrors.Observe(ChooseFrame(chosen, navigate: true));
        EventHandler<DapThread> threaded = (_, thread) => IdeErrors.Observe(ChooseThread(thread.Id));
        EventHandler<(string Expression, bool Watch)> evaluated = (_, request) => IdeErrors.Observe(Evaluate(request.Expression, request.Watch));
        EventHandler<(VariableNode Node, string Value)> set = (_, request) => IdeErrors.Observe(SetValue(request.Node, request.Value));
        EventHandler<VariableNode> watchData = (_, node) => IdeErrors.Observe(ToggleDataBreakpointAsync(node));
        EventHandler<VariableNode> textOpened = (_, node) => TextOpened?.Invoke(this, node.Value);
        EventHandler<DebugTab> tabChosen = (_, tab) => IdeErrors.Observe(ChooseAsync(tab));
        EventHandler<DebugTab> tabClosed = (_, tab) => IdeErrors.Observe(CloseTabAsync(tab));
        EventHandler<string> unwatched = (_, expression) =>
        {
            watches.Remove(expression);
            IdeErrors.Observe(ShowVariables());
        };
        view.ActionRequested += asked;
        view.FrameChosen += framed;
        view.ThreadChosen += threaded;
        view.EvaluateRequested += evaluated;
        view.SetValueRequested += set;
        view.DataWatch = DataWatchAsync;
        view.PlaceOf = PlaceOf;
        view.DataBreakpointRequested += watchData;
        view.TextOpened += textOpened;
        Breakpoints.DataChanged += (_, _) => TellRefusedData();
        view.WatchRemoved += unwatched;
        view.SessionChosen += tabChosen;
        view.SessionClosed += tabClosed;
        letGo = () =>
        {
            view.SessionChosen -= tabChosen;
            view.SessionClosed -= tabClosed;
            view.ActionRequested -= asked;
            view.FrameChosen -= framed;
            view.ThreadChosen -= threaded;
            view.EvaluateRequested -= evaluated;
            view.SetValueRequested -= set;
            view.DataBreakpointRequested -= watchData;
            view.TextOpened -= textOpened;
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
        shown++;
        clearing.Stop();
        NextShown();
        letGo();
        foreach (DebugTab tab in view.Sessions)
        {
            StopFollowing(tab);
        }
        foreach (OpenDocument document in following.Keys.ToList())
        {
            Detach(document);
        }
    }

    public BreakpointStore Breakpoints { get; } = new();

    /// <summary>What a session's program has written (the chosen one's by default), as its console in the Debug window has it
    /// now, what is on its way to it taken in first.</summary>
    public TextSnapshot Output(DebugSession? session = null)
    {
        OutputView console = (session is null ? chosen : TabOf(session))?.Console ?? view.Console;
        console.Flush();
        return console.Document.Snapshot;
    }

    /// <summary>The chosen tab's session, which the window shows and its actions act on.</summary>
    public DebugSession? Session => chosen?.Session;

    /// <summary>Whether any program is being debugged.</summary>
    public bool IsDebugging => view.Sessions.Any(t => t.IsLive && t.Session is not null);

    /// <summary>The programs debugged, each with its tab in the Debug window, in the order they started.</summary>
    public IReadOnlyList<DebugTab> Tabs => view.Sessions;

    /// <summary>The tab the Debug window shows.</summary>
    public DebugTab? Chosen => chosen;

    /// <summary>The session of the tab a program's name gives (a run configuration's), the last started for it.</summary>
    public DebugSession? SessionNamed(string name) => view.Sessions.FirstOrDefault(t => t.Name == name)?.Session;

    /// <summary>The tab a session is in, while it has one.</summary>
    public DebugTab? TabOf(DebugSession session) => view.Sessions.FirstOrDefault(t => t.Session == session);

    /// <summary>The session debugging an inspected app, while its tab is there; null for an app Run or Profile started.</summary>
    public DebugSession? SessionOf(LiveApp app) => view.Sessions.FirstOrDefault(t => t.App == app)?.Session;

    /// <summary>The apps of the tabs there are, each with its tab, in their order.</summary>
    public IEnumerable<(DebugTab Tab, LiveApp App)> Apps => view.Sessions.Where(t => t.App is not null).Select(t => (t, t.App!));

    /// <summary>The stop the program is paused at as the controller has read it (the stopped thread's frames, the exception
    /// it stopped on described), which the Debug window shows; null while it runs, before the stop is read, and after.</summary>
    public DebugStop? Stop { get; private set; }

    /// <summary>The chosen session's state in the words the status bar is told as it changes (debugging, paused and on what,
    /// running, ended and how), and how many programs are debugged and which are paused when there are several; null with
    /// no session.</summary>
    public string? Summary => chosen is { Session: not null } tab ? Told(tab) : null;

    /// <summary>A session's state as <see cref="Summary"/> words it, its tab chosen or not.</summary>
    public string? SummaryOf(DebugSession session) => TabOf(session) is { } tab ? Told(tab) : null;

    /// <summary>A toolbar button of the Debug window: an app action for the window to run.</summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>Something for the status bar.</summary>
    public event EventHandler<string>? Message;

    /// <summary>A place to show: a stop, or a frame chosen.</summary>
    public event EventHandler<(string Path, TextPosition Position)>? Navigate;

    /// <summary>An EF Core query's SQL read in Variables or the hover card, to open whole in a read-only editor tab.</summary>
    public event EventHandler<string>? TextOpened;

    /// <summary>The session started, stopped, ran on or ended.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Another tab is chosen, or none is, or a tab came or went: what follows the chosen tab's app looks again.</summary>
    public event EventHandler? TabsChanged;
    /// <summary>The native tab chosen in the Debug window changed.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Starts a program the debugger asks for in a tab of the Terminal window named for the session (the window's
    /// own), for a configuration that uses the terminal; without it such a program runs in the Console.</summary>
    public Func<string, RunInTerminalArguments, Task<Run.TerminalRun>>? RunInTerminal { get; init; }

    /// <summary>The folder open, which the Debug console names its files from.</summary>
    public string? Folder { get; init; }

    /// <summary>The C# language server whose Edit and Continue computes Hot Reload's updates, when it runs.</summary>
    public Func<Lsp.RoslynSession?> Server { get; init; } = () => null;

    /// <summary>Whether a launch is debugged with Hot Reload (Settings' Hot Reload while debugging): its code may change
    /// as it runs, and edits made while it is paused are applied as it goes on.</summary>
    public Func<bool> HotReloadOn { get; init; } = () => false;

    /// <summary>The chosen session's Hot Reload, while it has one.</summary>
    public HotReload? HotReload => chosen is { HotReload: { } reload, IsLive: true, Session: not null } ? reload : null;

    private HotReload? hotReload => chosen?.HotReload;

    /// <summary>What the last Apply Changes came to, until the next.</summary>
    public HotReloadOutcome? LastApplied { get; private set; }

    /// <summary>An Apply Changes ended: the window shows its problems and says it.</summary>
    public event EventHandler<HotReloadOutcome>? Applied;

    /// <summary>Restarts the program for edits Hot Reload cannot apply, as Settings says (or asks); true once it restarts,
    /// so a resume that applied first does not go on. The window's.</summary>
    public Func<HotReloadOutcome, Task<bool>> RestartFor { get; set; } = _ => Task.FromResult(false);

    /// <summary>The Apply Changes under way, which another waits for: one at a time.</summary>
    private Task<Applying>? applying;

    /// <summary>Why Apply Changes cannot run: nothing debugged, a session without Hot Reload, or one that stopped taking
    /// changes.</summary>
    public string? WhyNotApply() => NotLive()
        ?? (Session!.State == DebugState.Starting ? StillStarting
        : hotReload is null ? (Session!.Attached
            ? "Hot Reload applies changes to a program the debugger launched, not to one it attached to"
            : "This program was launched with Hot Reload when debugging off (Settings, Hot Reload)")
        : hotReload.WhyNot);

    /// <summary>Apply Hot Reload Changes to the chosen program debugged with Hot Reload and its group, restarting none.</summary>
    public async Task<HotReloadOutcome> ApplyChangesAsync() => (await ApplyAsync(Debugged(), null)).Outcome;

    /// <summary>The window's restart for edits Hot Reload cannot apply: as Settings say, or asked
    /// (<see cref="RestartFor"/>, <see cref="RestartRunningFor"/>).</summary>
    public HotReloadRestart SettingsRestart => (outcome, running) => running ? RestartRunningFor(outcome) : RestartFor(outcome);

    /// <summary>The chosen program debugged with Hot Reload, then the others of its group (a container run's services debugged
    /// together), which take its edits with it; none when Apply Changes cannot run on it.</summary>
    private List<IHotReloadTarget> Debugged() => WhyNotApply() is null && chosen is { } applied
        ? [applied, .. view.Sessions.Where(t => t != applied && applied.Group is { } group && t.Group == group
            && t is { IsLive: true, HotReload.WhyNot: null, Session: not null })]
        : [];

    /// <summary>A restart for edits Hot Reload cannot apply to the program of <paramref name="applied"/>'s tab, made as
    /// <paramref name="restart"/> says, the tab chosen again first should another program's stop have taken the window
    /// meanwhile (the restart acts on the chosen), while it is still debugged and its folder open; declined, those edits are
    /// not asked about again until the next edit.</summary>
    private async Task<bool> RestartAsked(HotReloadOutcome outcome, DebugTab? applied, HotReloadRestart restart)
    {
        if (applied is null || !applied.IsLive || disposed || ending || !view.Sessions.Contains(applied))
        {
            return false;
        }
        await ChooseAsync(applied);
        if (applied == chosen && applied.IsLive && !disposed && !ending && await restart(outcome, false))
        {
            return true;
        }
        if (applied.HotReload is { } reload)
        {
            reload.Changed = false;
        }
        return false;
    }

    /// <summary>Apply Hot Reload Changes to <paramref name="targets"/>, the one path every apply takes (a save's, the action's,
    /// an agent's, a resume's): one at a time, so one asked while another is under way waits for it and then applies only
    /// what still has edits, sending no program a second update for the same edits. Each target's console says what its
    /// apply came to; those that need a restart for their edits are restarted as <paramref name="restart"/> says (none when
    /// null). Answers what they came to, said as one.</summary>
    internal async Task<HotReloadApplied> ApplyAsync(IReadOnlyList<IHotReloadTarget> targets, HotReloadRestart? restart)
    {
        UiThread.Check();
        Applying? before = null;
        while (applying is { } under)
        {
            before = await under;
        }
        if (before is not null)
        {
            targets = [.. targets.Where(target => target.HotReload is { Changed: true })];
            if (targets.Count == 0)
            {
                return new HotReloadApplied(before.Outcome, false);
            }
        }
        Task<Applying> mine = ApplyOnceAsync(targets);
        applying = mine;
        Applying done;
        try
        {
            done = await mine;
        }
        finally
        {
            if (applying == mine)
            {
                applying = null;
            }
        }
        if (restart is null)
        {
            return new HotReloadApplied(done.Outcome, false);
        }
        bool restarted = done.Services is { Result: HotReloadResult.RestartNeeded } rude && await RestartRunningAsked(rude, restart);
        if (done.Program is { Result: HotReloadResult.RestartNeeded } edit)
        {
            restarted |= await RestartAsked(edit, done.Tab, restart);
        }
        return new HotReloadApplied(done.Outcome, restarted);
    }

    /// <summary>What an apply came to: said as one, and apart for the run's services and the tab's program, each restarted
    /// as its own.</summary>
    private sealed record Applying(HotReloadOutcome Outcome, HotReloadOutcome? Services, DebugTab? Tab, HotReloadOutcome? Program);

    private async Task<Applying> ApplyOnceAsync(IReadOnlyList<IHotReloadTarget> targets)
    {
        using IDisposable held = BackgroundWork.Scope();
        if (targets.Count == 0)
        {
            HotReloadOutcome refused = new(HotReloadResult.Failed, WhyNotApply() ?? NothingDebugged);
            Message?.Invoke(this, refused.Said);
            return new Applying(refused, null, null, null);
        }
        Message?.Invoke(this, "Applying Hot Reload changes…");
        List<(IHotReloadTarget Target, DebugSession? Session, HotReloadOutcome Outcome)> each = [];
        foreach (IHotReloadTarget target in targets)
        {
            DebugSession? session = (target as DebugTab)?.Session;
            each.Add((target, session, target.HotReload is { } reload ? await reload.ApplyAsync()
                : new HotReloadOutcome(HotReloadResult.Failed, $"{target.Name} is not debugged with Hot Reload")));
        }
        List<(DebugTab Tab, DebugSession? Session, HotReloadOutcome Outcome)> tabs = [.. each.Where(e => !e.Target.Quiet).Select(e => ((DebugTab)e.Target, e.Session, e.Outcome))];
        List<(IHotReloadTarget Target, HotReloadOutcome Outcome)> quiet = [.. each.Where(e => e.Target.Quiet).Select(e => (e.Target, e.Outcome))];
        bool marked = false;
        foreach ((DebugTab tab, DebugSession? session, HotReloadOutcome its) in tabs)
        {
            marked |= await TakenAsync(tab, session, its);
        }
        if (marked)
        {
            UpdateAllMarks();
        }
        HotReloadOutcome? services = quiet.Count > 0 ? Together([.. quiet.Select(q => (q.Target.Name, q.Outcome))]) : null;
        HotReloadOutcome? program = tabs is [var lone] ? lone.Outcome : tabs.Count > 0 ? Together([.. tabs.Select(t => (t.Tab.Name, t.Outcome))]) : null;
        HotReloadOutcome outcome = services is { } both && program is { } and
            ? Together([("the run's services", both), (tabs[0].Tab.Name, and)])
            : services ?? program!;
        foreach ((IHotReloadTarget target, HotReloadOutcome its) in quiet)
        {
            target.Say($"Hot Reload: {its.Said}", Unapplied(its));
        }
        // The program applied to is its own tab's, whichever is chosen now; one ended or restarted meanwhile is told nothing.
        DebugTab? applied = tabs.Count > 0 ? tabs[0].Tab : null;
        bool gone = applied is not null && (disposed || applied.Session != tabs[0].Session || !applied.IsLive);
        if (applied is IHotReloadTarget own && program is { } said && !gone)
        {
            own.Say($"Hot Reload: {said.Said}", Unapplied(said));
        }
        if (!gone || quiet.Count > 0)
        {
            LastApplied = outcome;
            Message?.Invoke(this, outcome.Said);
            Applied?.Invoke(this, outcome);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        return new Applying(outcome, services, applied, program);

        static bool Unapplied(HotReloadOutcome its) => its.Result is not (HotReloadResult.Applied or HotReloadResult.NothingToApply);
    }

    /// <summary>A tab's program that took an apply: the files' lines are its program's from now (the built lines forget their
    /// edits), their breakpoints are bound again in the new code, and a paused program's stop shown is read again where it
    /// now is. True when it took the edits, its marks to be drawn again.</summary>
    private async Task<bool> TakenAsync(DebugTab tab, DebugSession? applied, HotReloadOutcome its)
    {
        if (its.Result is not (HotReloadResult.Applied or HotReloadResult.NothingToApply) || disposed || tab.Session is not { } session || session != applied || !tab.IsLive)
        {
            return false;
        }
        List<string> files = [.. its.Files.Select(PathNames.Canonical)];
        // The stop shown keeps its place in the text, then the files' lines are the program's.
        bool shown = tab == chosen;
        (string Path, int Line)? shownAt = shown && executionPoint is { } point && files.Contains(point.Path) ? (point.Path, Current(point.Path, point.Line)) : null;
        foreach (string path in files)
        {
            tab.Lines?.Reset(path);
        }
        if (shownAt is { } now)
        {
            executionPoint = (now.Path, Built(now.Path, now.Line));
        }
        await session.SendBreakpointsAgainAsync(files);
        if (its.Result == HotReloadResult.Applied && session.State == DebugState.Paused && session.StoppedThread is int thread && tab == chosen && !disposed)
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
        return true;
    }

    /// <summary>Launches a plan's program under the debugger (after its build), its output in its tab's Console in the Debug
    /// window, or in a tab of the Terminal window when its configuration uses the terminal, beside any other program
    /// debugged; the plan's last session, still debugged, is stopped first. With <paramref name="elsewhere"/> the program
    /// runs on another machine (<see cref="DebugSession.Elsewhere"/>). Null once the session is open (the program may have
    /// ended already), else why it could not.</summary>
    public Task<string?> StartAsync(Run.LaunchPlan plan, bool elsewhere = false, AdapterPlace? place = null, LiveApp? app = null)
        => StartInTabAsync(plan, elsewhere, place, app: app);

    private Task<string?> StartInTabAsync(Run.LaunchPlan plan, bool elsewhere = false, AdapterPlace? place = null, DebugTab? tab = null, LiveApp? app = null)
    {
        Func<RunInTerminalArguments, Task<Run.TerminalRun>>? inTerminal = !elsewhere && place is null && plan.Console == Run.ConsoleKind.Terminal && RunInTerminal is { } run
            ? arguments => run(plan.Name, arguments)
            : null;
        bool hotReloadOn = HotReloadOn();
        return OpenAsync((built, log, output, starting) => DebugSession.LaunchAsync(plan, Breakpoints, built, log, output, inTerminal, ColumnOf, hotReloadOn, starting, elsewhere, place),
            plan.Name, launched: true, inTerminal: inTerminal is not null, hotReloadOn: hotReloadOn, tab: tab, app: app, plan: plan);
    }

    /// <summary>The column a breakpoint asks the debugger for: in Avalonia XAML, its attribute's or its line's first name's
    /// in the file's text (an open file's as it is now, else as saved); none elsewhere.</summary>
    private int? ColumnOf(LineBreakpoint breakpoint)
    {
        if (!XamlBreakpoints.Compiled(breakpoint.Path))
        {
            return null;
        }
        TextSnapshot? text = CSharp.ServerPlaces.TextOf(editors, breakpoint.Path);
        return text is null ? null : XamlBreakpoints.Column(text, breakpoint.Line, breakpoint.Attribute);
    }

    /// <summary>Attaches the debugger to a running .NET process beside any other program debugged, its output going
    /// where it always went. With <paramref name="hotReload"/> the program was started held with
    /// DOTNET_MODIFIABLE_ASSEMBLIES=debug, so Hot Reload is offered as for a launch while Settings has it on;
    /// <paramref name="elsewhere"/> runs it on another machine, <paramref name="place"/> the adapter where it is, and an
    /// inspected <paramref name="app"/> is held by its tab. Null once the session is open, else why it could not.</summary>
    public Task<string?> AttachAsync(int processId, string name, bool hotReload = false, bool elsewhere = false, AdapterPlace? place = null, string? group = null,
        LiveApp? app = null)
    {
        bool hotReloadOn = hotReload && HotReloadOn();
        return OpenAsync((built, log, output, starting) => DebugSession.AttachAsync(processId, Breakpoints, built, log, output, ColumnOf, starting, hotReloadOn, elsewhere, place), name,
            launched: false, hotReloadOn: hotReloadOn, place: place, group: group, app: app);
    }

    /// <summary>Launches another program in a native tab, with hooks installed before its adapter starts.</summary>
    public Task<DebugLaunch> StartAdditionalAsync(Run.LaunchPlan plan, CancellationToken cancellation = default, Action<DebugTab>? configure = null)
        => OpenAdditionalAsync(plan.Name, tab => StartInTabAsync(plan, tab: tab), null, cancellation, configure);

    /// <summary>Attaches another process in a native tab without replacing a sibling session.</summary>
    public Task<DebugLaunch> AttachAdditionalAsync(int pid, string name, CancellationToken cancellation = default, Action<DebugTab>? configure = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return OpenAdditionalAsync(name, tab => OpenAsync((built, log, output, starting) =>
            DebugSession.AttachAsync(pid, Breakpoints, built, log, output, ColumnOf, starting), name, launched: false, tab: tab), pid, cancellation, configure);
    }

    private async Task<DebugLaunch> OpenAdditionalAsync(string name, Func<DebugTab, Task<string?>> open, int? attachedProcessId,
        CancellationToken cancellation, Action<DebugTab>? configure)
    {
        UiThread.Check();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (ending)
        {
            throw new InvalidOperationException("The workspace is ending");
        }
        cancellation.ThrowIfCancellationRequested();
        DebugTab tab = Place(name, replace: false);
        Task<string?>? opening = null;
        try
        {
            tab.RestartFor = _ =>
            {
                tab.Console.Append("Hot Reload: This resource needs restart; restart it through its owner\n", error: true);
                return Task.FromResult(false);
            };
            configure?.Invoke(tab);
            cancellation.ThrowIfCancellationRequested();
            opening = open(tab);
            if (await opening.WaitAsync(cancellation) is { } failure)
            {
                throw new InvalidOperationException(failure);
            }
            DebugSession session = tab.Session ?? throw new InvalidOperationException("The debugging session did not start");
            int processId = await session.ProcessStarted.WaitAsync(Waits.Of(TimeSpan.FromSeconds(30)), cancellation);
            if (attachedProcessId is int expected && processId != expected)
            {
                throw new InvalidOperationException("The debugger attached to a different process");
            }
            cancellation.ThrowIfCancellationRequested();
            if (ending || disposed || tab.OpeningStopped || !view.Sessions.Contains(tab))
            {
                throw new InvalidOperationException("The workspace or debugging session ended during launch");
            }
            return new DebugLaunch(tab, processId);
        }
        catch
        {
            tab.OpeningStopped = true;
            if (opening is not null)
            {
                Send(opening);
            }
            try
            {
                await StopAsync(tab);
                if (opening is not null)
                {
                    // The start ends before its caller hears it was given up, so what the start says of its end is said first.
                    await ((Task)opening).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
                }
            }
            finally
            {
                if (tab.Session is { State: not DebugState.Ended } session)
                {
                    await session.CloseAsync();
                }
                if (tab.Session is null)
                {
                    tab.HasEnded = true;
                    tab.OnStateChanged();
                }
            }
            throw;
        }
    }

    /// <summary>Starts a session in a tab of its own, its output in the tab's Console, which a program it launched reads
    /// what is typed into. The tab is chosen, and its session <see cref="Session"/> from its first moment, so Stop is there
    /// as soon as a session begins: its start waits for the program's runtime as long as that takes. A start that failed
    /// leaves its tab ended, with no session. Tabs of one <paramref name="group"/> (a container run's services) take Hot
    /// Reload's changes together; an inspected <paramref name="app"/> is held by its tab.</summary>
    private async Task<string?> OpenAsync(Func<BuiltLines, Action<string>, Action<OutputEvent>, Action<DebugSession>, Task<DebugSession>> open, string name, bool launched,
        bool inTerminal = false, bool hotReloadOn = false, AdapterPlace? place = null, string? group = null, DebugTab? tab = null, LiveApp? app = null,
        Run.LaunchPlan? plan = null)
    {
        UiThread.Check();
        // A start that comes after its folder was left would open in the next folder's Debug window, which is not its own.
        if (disposed)
        {
            return $"Could not debug {name}: its folder was closed";
        }
        if (ending)
        {
            return "The workspace is ending";
        }
        // A program debugged again takes its last session's place, which is let go first.
        if (tab is null && view.Sessions.FirstOrDefault(t => t.Name == name && t.IsLive) is { } again)
        {
            await EndAsync(again);
            if (disposed)
            {
                return $"Could not debug {name}: its folder was closed";
            }
        }
        if (disposed || ending)
        {
            return "The workspace ended during launch";
        }
        tab ??= Place(name);
        tab.Group = group ?? tab.Group;
        tab.App = app ?? tab.App;
        tab.Plan = plan;
        BuiltLines built = new(path => CSharp.ServerPlaces.TextOf(editors, path));
        tab.Lines = built;
        await ChooseAsync(tab);
        if (disposed || ending || tab.OpeningStopped || !view.Sessions.Contains(tab))
        {
            tab.HasEnded = true;
            tab.OnStateChanged();
            return $"Debugging {name} ended before launch completed";
        }
        view.SetState(DebugState.Starting);
        view.ShowDetachable(false);
        OutputView shown = tab.Console;
        if (inTerminal)
        {
            shown.Append($"{name} runs in the Terminal window, which it reads from and writes to\n");
        }
        else if (!launched && place?.Output is { } follow)
        {
            await FollowAsync(follow, shown, tab);
        }
        else if (!launched)
        {
            shown.Append($"{AttachedConsole}\n");
        }
        Message?.Invoke(this, $"Debugging {name}");
        DebugSession? starting = null;
        try
        {
            ConsoleSink console = shown.Sink;
            DebugSession session = await open(built, line => shown.Append($"{line}\n"), output =>
            {
                ConsoleStream? stream = output.Category switch
                {
                    DapOutputCategories.Stdout => ConsoleStream.Output,
                    DapOutputCategories.Stderr => ConsoleStream.Error,
                    DapOutputCategories.Telemetry => null,
                    _ => ConsoleStream.System,
                };
                if (stream is { } into)
                {
                    console.Write(output.Output, into);
                }
                tab.OnProgramOutput(output);
            }, made =>
            {
                starting = made;
                tab.Session = made;
                if (disposed || ending || tab.OpeningStopped || !view.Sessions.Contains(tab))
                {
                    // Stopped as its adapter started, before there was a session to stop: its start fails as one stopped.
                    stoppedStarting.Add(made);
                    Send(made.CloseAsync());
                    return;
                }
                if (tab == chosen)
                {
                    view.Tree.Load = reference => MembersAsync(made, reference);
                }
                // The toolbar's Stop shows, and Stop runs, while the debugger waits for the program.
                StateChanged?.Invoke(this, EventArgs.Empty);
                tab.OnStateChanged();
            });
            if (disposed || ending || tab.OpeningStopped || !view.Sessions.Contains(tab))
            {
                await session.CloseAsync();
                tab.HasEnded = true;
                tab.OnStateChanged();
                return $"Debugging {name} ended before launch completed";
            }
            tab.HotReload = hotReloadOn ? new HotReload(session, Server, editors) : null;
            if (launched && !inTerminal)
            {
                shown.Input = new ConsoleInput(text => Send(session.InputAsync(text)), () => Send(session.CloseInputAsync()));
            }
            session.StateChanged += (_, _) => IdeErrors.Observe(OnStateChanged(tab));
            await OnStateChanged(tab);
            return null;
        }
        catch (Exception error) when (error is DapException or IOException or InvalidOperationException or TimeoutException)
        {
            bool stopped = starting is not null && stoppedStarting.Remove(starting);
            string failed = stopped
                ? launched ? $"Debugging {name} was stopped as it started" : $"Debugging {name} was stopped as the debugger attached, and it runs on"
                : $"Could not debug {name}: {(error is DapException dap ? dap.Reason : error.Message)}";
            // Its output followed from outside (a container's log) ends with the start, whichever tab is the window's.
            StopFollowing(tab);
            if (disposed || !view.Sessions.Contains(tab))
            {
                // Another session took its tab meanwhile (Debug again): this one's end is not shown over it.
                return failed;
            }
            // Its tab stays to say so, with no session: a caller asks Session whether it began.
            tab.Session = null;
            tab.Lines = null;
            tab.HasEnded = true;
            tab.OnStateChanged();
            if (stopped)
            {
                shown.Append($"{failed}\n");
            }
            shown.Flush();
            if (tab == chosen)
            {
                view.SetState(DebugState.Ended);
            }
            Message?.Invoke(this, failed);
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (tab == chosen && Next() is { } next)
            {
                await ChooseAsync(next, say: false);
            }
            return failed;
        }
    }

    /// <summary>A program's output followed from outside into its console (a container's log, which an attach gets none of
    /// through the protocol), from before the debugger lets it go until its session's end has let its last words land.</summary>
    private async Task FollowAsync(Func<System.Diagnostics.ProcessStartInfo> follow, OutputView console, DebugTab tab)
    {
        System.Diagnostics.Process process;
        try
        {
            System.Diagnostics.ProcessStartInfo start = follow();
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            // Its fork off the window's thread.
            process = await ChildProcesses.StartAsync(start);
        }
        catch (Exception failed) when (failed is FileNotFoundException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            console.Append($"Its output could not be followed: {failed.Message}\n");
            return;
        }
        if (disposed || !view.Sessions.Contains(tab))
        {
            // Its tab went as it started (closed, taken by another, its folder left), and its follower with it.
            ChildProcesses.Kill(process);
            return;
        }
        tab.Following = process;
        ConsoleSink sink = console.Sink;
        // For the program's life, as its output is: no screenshot waits on it. Its last words land as its follower ends.
        IdeErrors.Observe(Task.Run(() => sink.PumpAsync(process.StandardOutput, ConsoleStream.Output)));
        IdeErrors.Observe(Task.Run(() => sink.PumpAsync(process.StandardError, ConsoleStream.Error)));
    }

    /// <summary>Ends a tab's following of its program's output a moment after its session ended, its last words in.</summary>
    private static void StopFollowing(DebugTab tab)
    {
        if (tab.Following is not { } process)
        {
            return;
        }
        tab.Following = null;
        IdeErrors.Observe(Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => ChildProcesses.Kill(process), TaskScheduler.Default));
    }

    /// <summary>A program run with Hot Reload through the debugger rather than dotnet watch (a container run's service, whose
    /// image has no SDK): attached quietly, no breakpoint sent and no stop shown, so Apply Hot Reload Changes and a save
    /// apply the edits to it; ended with its run.</summary>
    public sealed record RunningReload(string Name, DebugSession Session, HotReload Reload, Action<string, bool> Says) : IHotReloadTarget
    {
        HotReload? IHotReloadTarget.HotReload => Reload;

        bool IHotReloadTarget.Quiet => true;

        void IHotReloadTarget.Say(string line, bool error) => Says(line, error);
    }

    private readonly List<RunningReload> running = [];

    /// <summary>The programs run with Hot Reload through the debugger, while they run.</summary>
    public IReadOnlyList<RunningReload> Running => running;

    /// <summary>Attaches quietly for Hot Reload alone to a program run, which a container run's Run with Hot Reload held at its
    /// start with DOTNET_MODIFIABLE_ASSEMBLIES=debug: the attach lets it go. What its applies come to is said by
    /// <paramref name="says"/>, where its output is. Null once attached, else why not.</summary>
    public async Task<string?> AttachForHotReloadAsync(int processId, string name, Action<string, bool> says, AdapterPlace? place = null,
        CancellationToken cancellation = default)
    {
        UiThread.Check();
        if (disposed)
        {
            return $"Could not attach to {name} for Hot Reload: its folder was closed";
        }
        BuiltLines built = new(path => CSharp.ServerPlaces.TextOf(editors, path));
        DebugSession? attaching = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using CancellationTokenRegistration stopped = cancellation.Register(() => UiThread.Post(() =>
            {
                if (attaching is { } pending)
                {
                    Send(pending.CloseAsync());
                }
            }));
            DebugSession session = await DebugSession.AttachAsync(processId, Breakpoints, built, _ => { }, _ => { },
                starting: session => attaching = session, hotReload: true, place: place, quiet: true);
            cancellation.ThrowIfCancellationRequested();
            if (disposed || ending)
            {
                throw new OperationCanceledException("The workspace ended during Hot Reload attach");
            }
            RunningReload reload = new(name, session, new HotReload(session, Server, editors), says);
            running.Add(reload);
            session.StateChanged += (_, _) =>
            {
                if (session.State == DebugState.Ended && running.Remove(reload))
                {
                    reload.Reload.End();
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
                else if (session.State == DebugState.Paused)
                {
                    // Never shown: a Debugger.Break in its code goes on as a run would.
                    Send(session.ContinueAsync());
                }
            };
            StateChanged?.Invoke(this, EventArgs.Empty);
            return null;
        }
        catch (Exception error) when (error is DapException or IOException or InvalidOperationException or TimeoutException)
        {
            if (attaching is not null)
            {
                await attaching.CloseAsync();
            }
            return $"Could not attach to {name} for Hot Reload: {(error is DapException dap ? dap.Reason : error.Message)}";
        }
        catch
        {
            if (attaching is not null)
            {
                await attaching.CloseAsync();
            }
            throw;
        }
    }

    /// <summary>A restart of the programs run with Hot Reload whose edits it could not apply, made as <paramref name="restart"/>
    /// says; declined, those edits are not asked about again until the next edit.</summary>
    private async Task<bool> RestartRunningAsked(HotReloadOutcome outcome, HotReloadRestart restart)
    {
        if (await restart(outcome, true))
        {
            return true;
        }
        foreach (RunningReload reload in running.Where(r => outcome.Restart.Contains(r.Name)))
        {
            reload.Reload.Changed = false;
        }
        return false;
    }

    /// <summary>Restarts the programs run with Hot Reload whose edits it could not apply (a container run's services, built
    /// again and restarted); true once they are. The window's.</summary>
    public Func<HotReloadOutcome, Task<bool>> RestartRunningFor { get; set; } = _ => Task.FromResult(false);

    /// <summary>What several programs' Apply Changes came to, as one: the gravest result, each one's words that say more than
    /// that nothing changed, and the programs that need a restart.</summary>
    internal static HotReloadOutcome Together(IReadOnlyList<(string Name, HotReloadOutcome Outcome)> each)
    {
        if (each is [(_, var only)] && only.Result != HotReloadResult.RestartNeeded)
        {
            return only;
        }
        HotReloadResult result = each.Select(e => e.Outcome.Result).OrderBy(r => r switch
        {
            HotReloadResult.Errors => 0,
            HotReloadResult.RestartNeeded => 1,
            HotReloadResult.Failed => 2,
            HotReloadResult.Applied => 3,
            _ => 4,
        }).First();
        // Nothing applied says more when Roslyn said why (a stale project's file).
        List<string> said = [.. each.Where(e => e.Outcome.Result != HotReloadResult.NothingToApply || e.Outcome.Problems.Count > 0).Select(e => $"{e.Name}: {e.Outcome.Said}")];
        return new HotReloadOutcome(result, said.Count == 0 ? HotReload.NothingApplied([]) : string.Join("; ", said))
        {
            Problems = [.. each.SelectMany(e => e.Outcome.Problems).Distinct()],
            Files = [.. each.SelectMany(e => e.Outcome.Files).Distinct(StringComparer.Ordinal)],
            Modules = [.. each.SelectMany(e => e.Outcome.Modules).Distinct(StringComparer.Ordinal)],
            Methods = each.Sum(e => e.Outcome.Methods),
            Took = each.Aggregate(TimeSpan.Zero, (sum, e) => sum + e.Outcome.Took),
            Restart = [.. each.Where(e => e.Outcome.Result == HotReloadResult.RestartNeeded).Select(e => e.Name)],
        };
    }

    /// <summary>The tab a new session takes: with no other program debugged, the place of every tab there is (each has
    /// ended), its console the window's first; else the ended tab of its own program, in its place; else a new one at the
    /// end. A tab taken lets its console go, but the console the new one keeps.</summary>
    private DebugTab Place(string name, bool replace = true)
    {
        List<DebugTab> tabs = [.. view.Sessions];
        bool alone = !tabs.Any(t => t.IsLive);
        List<DebugTab> taken = alone ? tabs : replace ? [.. tabs.Where(t => t.Name == name)] : [];
        OutputView console = alone ? view.FirstConsole : taken.FirstOrDefault()?.Console ?? view.NewConsole();
        int at = taken.Count > 0 ? tabs.IndexOf(taken[0]) : tabs.Count;
        foreach (DebugTab gone in taken)
        {
            StopFollowing(gone);
            view.Sessions.Remove(gone);
            if (gone.Console != console)
            {
                view.ForgetConsole(gone.Console);
            }
        }
        console.Input = null;
        console.Clear();
        DebugTab tab = new(name, console);
        view.Sessions.Insert(Math.Min(at, view.Sessions.Count), tab);
        TabsChanged?.Invoke(this, EventArgs.Empty);
        return tab;
    }

    /// <summary>Shows a session's tab: its console, its stop's frames and values, the toolbar for its state, and its
    /// execution point in the editor; nothing read of the last tab's stop is asked of again.</summary>
    public Task ChooseAsync(DebugTab tab) => ChooseAsync(tab, say: true);

    /// <summary>Chooses a tab, its state said in the status bar or not (as the chosen program ended, whose end it says).</summary>
    private Task ChooseAsync(DebugTab tab, bool say)
    {
        UiThread.Check();
        if (tab == chosen || disposed || !view.Sessions.Contains(tab))
        {
            return Task.CompletedTask;
        }
        chosen = tab;
        foreach (DebugTab each in view.Sessions)
        {
            each.IsChosen = each == tab;
        }
        TabsChanged?.Invoke(this, EventArgs.Empty);
        view.ShowConsole(tab.Console);
        NextShown();
        frame = null;
        Stop = null;
        showing = null;
        executionPoint = null;
        stopException = null;
        undescribed = null;
        propertyChange = null;
        bindingStop = null;
        bindingFrame = null;
        lastResult = null;
        clearing.Stop();
        editors.CodeEditor.CancelChoices();
        view.ShowFrames([], null, [], null);
        view.Tree.Show([]);
        ClearInlineValues();
        UpdateAllMarks();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        if (tab.Session is { } session)
        {
            view.Tree.Load = reference => MembersAsync(session, reference);
            return Show(tab, say);
        }
        view.SetState(tab.HasEnded ? DebugState.Ended : DebugState.Starting);
        view.ShowDetachable(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <summary>A tab's ×: its program, still debugged, is ended as a tab another takes the place of is (<see
    /// cref="EndAsync"/>), and the tab goes, another chosen in its place.</summary>
    private async Task CloseTabAsync(DebugTab tab)
    {
        UiThread.Check();
        if (tab.IsLive)
        {
            await EndAsync(tab);
        }
        // Its program's output followed from outside goes with it, ended or not.
        StopFollowing(tab);
        int at = view.Sessions.IndexOf(tab);
        if (at < 0 || disposed)
        {
            return;
        }
        view.Sessions.Remove(tab);
        view.ForgetConsole(tab.Console);
        if (tab != chosen)
        {
            TabsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        chosen = null;
        TabsChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        if ((Next() ?? (view.Sessions.Count > 0 ? view.Sessions[Math.Min(at, view.Sessions.Count - 1)] : null)) is { } next)
        {
            await ChooseAsync(next);
            return;
        }
        // None left: the window is as before any session, its first console shown empty.
        view.Clear();
        view.SetState(DebugState.Ended);
        executionPoint = null;
        UpdateAllMarks();
        ClearInlineValues();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The tab to show once the chosen one's program has ended: one paused, else the last that is debugged.</summary>
    private DebugTab? Next() =>
        view.Sessions.FirstOrDefault(t => t.IsLive && t.Session?.State == DebugState.Paused) ?? view.Sessions.LastOrDefault(t => t.IsLive && t.Session is not null);

    /// <summary>The sessions Stop ended while they started, whose failed start is said as stopped rather than as a failure.</summary>
    private readonly HashSet<DebugSession> stoppedStarting = [];

    /// <summary>Input for the program, which a session that has ended, or an adapter gone, may never take: its end says so.</summary>
    private static void Send(Task sending) =>
        _ = sending.ContinueWith(static sent => sent.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    /// <summary>Stop: the chosen program ends, or, with none chosen that is debugged, every one does (Stop All).</summary>
    public Task StopAsync()
    {
        UiThread.Check();
        return chosen is { IsLive: true } tab ? StopAsync(tab) : StopAllAsync();
    }

    /// <summary>Stop All: every program debugged ends, as Stop ends one.</summary>
    public Task StopAllAsync()
    {
        UiThread.Check();
        return Task.WhenAll([.. view.Sessions.Where(t => t.IsLive).Select(StopAsync)]);
    }

    /// <summary>Stops the program a name gives (a run configuration's) while it is debugged.</summary>
    public Task StopAsync(string name)
    {
        UiThread.Check();
        return view.Sessions.FirstOrDefault(t => t.Name == name && t.IsLive) is { } tab ? StopAsync(tab)
            : running.FirstOrDefault(r => r.Name == name) is { } run ? run.Session.StopAsync()
            : Task.CompletedTask;
    }

    /// <summary>Stop on one tab: the program the debugger started ends, at once while the debugger still waits for its
    /// runtime; one it is still attaching to is let go, running on, as nothing of the session has reached it.</summary>
    public async Task StopAsync(DebugTab tab)
    {
        UiThread.Check();
        tab.OpeningStopped = true;
        if (tab.Session is not { State: not DebugState.Ended } session)
        {
            return;
        }
        if (session.State == DebugState.Starting)
        {
            stoppedStarting.Add(session);
            if (session.Attached)
            {
                await session.DetachAsync();
                return;
            }
        }
        await session.StopAsync();
    }

    /// <summary>Ends a tab's program as another session takes its place, or its tab closes, or the folder is left: one the
    /// debugger attached to is let go, running on, as Detach does; one it started stops, as Stop stops it.</summary>
    private Task EndAsync(DebugTab tab) =>
        tab.Session is { Attached: true, State: DebugState.Running or DebugState.Paused } attached ? attached.DetachAsync() : StopAsync(tab);

    /// <summary>Detach: the debugger lets go of the program it attached to, which runs on. Null once it has, else why it
    /// could not (nothing attached to).</summary>
    public Task<string?> DetachAsync() => DetachAsync(Session);

    /// <summary>Detach of one session's program: the chosen tab's by default, else the one given.</summary>
    public async Task<string?> DetachAsync(DebugSession? session)
    {
        UiThread.Check();
        if ((session == Session ? WhyNotDetach() : WhyNotDetach(session)) is { } why)
        {
            return why;
        }
        await session!.DetachAsync();
        return null;
    }

    /// <summary>Lets go of the program a name gives (a container run's service) that the debugger attached to, in a tab or
    /// quietly for Hot Reload, which runs on; done once it has.</summary>
    public Task DetachAsync(string name)
    {
        UiThread.Check();
        return view.Sessions.FirstOrDefault(t => t.Name == name && t.IsLive)?.Session is { Attached: true } attached ? attached.DetachAsync()
            : running.FirstOrDefault(r => r.Name == name) is { } run ? run.Session.DetachAsync()
            : Task.CompletedTask;
    }

    /// <summary>Why Detach cannot run now: nothing is being debugged, or the debugger started the program, which ends with
    /// its session (its input and output are the adapter's).</summary>
    private string? WhyNotDetach() => NotLive() ?? WhyNotDetach(Session);

    /// <summary>Why one session's program cannot be let go now: it has ended, the debugger started it, or it still starts.</summary>
    private static string? WhyNotDetach(DebugSession? session) => session is not { State: not DebugState.Ended } ? NothingDebugged
        : !session.Attached ? "The debugger started this program: Stop ends it, and Detach lets go only of a program attached to"
        : session.State == DebugState.Starting ? StillStarting
        : null;

    /// <summary>Why what needs the debugger's hold of the program cannot run while it starts (Stop can).</summary>
    private const string StillStarting = "The debugger is still taking hold of the program";

    /// <summary>Ends every session's program and adapter as the IDE closes (<see cref="DebugSession.CloseAsync"/>).</summary>
    public Task CloseAsync()
    {
        UiThread.Check();
        ending = true;
        foreach (DebugTab tab in view.Sessions)
        {
            tab.OpeningStopped = true;
        }
        return Task.WhenAll([.. view.Sessions.Select(t => t.Session?.CloseAsync() ?? Task.CompletedTask), .. running.Select(r => r.Session.CloseAsync())]);
    }

    /// <summary>Its folder left with the window open: each program the debugger started is stopped, as Stop stops it, and
    /// each it attached to let go, running on, as the window's close lets it go.</summary>
    public Task LeaveAsync()
    {
        UiThread.Check();
        ending = true;
        return Task.WhenAll([.. view.Sessions.Where(t => t.IsLive).Select(EndAsync), .. running.Select(r => r.Session.StopAsync())]);
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
        }, (_, c) => Refusals.NeedsLines(c), new("Run", 2, 0));
        Add(AppActions.ToggleBreakpointEnabled, "Toggle Breakpoint Enabled", (d, c) => d.Breakpoints.ToggleEnabled(c.File!.Path, c.Caret.Line), (_, c) => Refusals.NeedsLines(c), new("Run", 2, 1));
        Add(AppActions.Resume, "Resume Program", (d, c) =>
        {
            if (!d.IsDebugging)
            {
                actions.Run(AppActions.Debug, c);
                return;
            }
            IdeErrors.Observe(d.ResumeAsync(session => session.ContinueAsync(), "resume"));
        }, (d, c) => d.IsDebugging ? d.NotPaused() : actions.WhyNot(AppActions.Debug, c), new("Run", 1, 0));
        Add(AppActions.Pause, "Pause Program", (d, c) => IdeErrors.Observe(d.Session!.PauseAsync()),
            (d, _) => d.NotLive() ?? d.Session!.State switch
            {
                DebugState.Paused => "The program is paused already",
                DebugState.Starting => StillStarting,
                _ => null,
            }, new("Run", 1, 1));
        Add(AppActions.StepOver, "Step Over", (d, c) => IdeErrors.Observe(d.ResumeAsync(session => session.StepOverAsync(), "step over")), (d, _) => d.NotPaused(), new("Run", 1, 2));
        Add(AppActions.StepInto, "Step Into", (d, c) => IdeErrors.Observe(d.ResumeAsync(session => session.StepIntoAsync(), "step into")), (d, _) => d.NotPaused(), new("Run", 1, 3));
        Add(AppActions.SmartStepInto, "Smart Step Into", (d, c) => IdeErrors.Observe(d.SmartStepIntoAsync()), (d, _) => d.NotPaused(), new("Run", 1, 4));
        Add(AppActions.StepOut, "Step Out", (d, c) => IdeErrors.Observe(d.ResumeAsync(session => session.StepOutAsync(), "step out")), (d, _) => d.NotPaused(), new("Run", 1, 5));
        Add(AppActions.RunToCursor, "Run to Cursor", (d, c) => IdeErrors.Observe(d.ResumeAsync(session => session.RunToCursorAsync(c.File!.Path, c.Caret.Line), "run to the cursor")),
            (d, c) => Refusals.NeedsLines(c) ?? d.NotPaused(), new("Run", 1, 6));
        Add(AppActions.SetNextStatement, "Set Next Statement", (d, c) => IdeErrors.Observe(d.SetNextStatementAsync(d.Session!, c.File!.Path, c.Caret.Line)),
            (d, c) => Refusals.NeedsLines(c) ?? d.NotPaused(), new("Run", 1, 7));
        Add(AppActions.ShowExecutionPoint, "Show Execution Point", (d, _) =>
        {
            (string path, int line) = d.executionPoint!.Value;
            d.Navigate?.Invoke(d, (path, new TextPosition(d.Current(path, line), 0)));
        }, (d, _) => d.NotPaused() ?? (d.executionPoint is null ? "Where the program is paused has no source" : null), new("Run", 1, 8));
        Add(AppActions.Evaluate, "Evaluate Expression…", (d, c) => d.view.FocusEvaluate(c.Symbol ?? string.Empty), (d, _) => d.NotPaused(), new("Run", 1, 9));
        Add(AppActions.AddToWatches, "Add to Watches", (d, c) => IdeErrors.Observe(d.Evaluate(c.Symbol!, watch: true)),
            (d, c) => d.NotPaused() ?? (c.Symbol is { Length: > 0 } ? null : "No expression is at the caret"), null);
        Add(AppActions.Detach, "Detach", (d, c) => IdeErrors.Observe(d.DetachAsync()), (d, _) => d.WhyNotDetach(), new("Run", 0, 8));
        // Every breakpoint stays as it is, and none stops the program until it is chosen again.
        actions.Add(new IdeAction(AppActions.MuteBreakpoints, "Mute Breakpoints", "Debug", _ => current().Breakpoints.SetMuted(!current().Breakpoints.Muted))
        {
            Menus = [new("Run", 2, 4)],
            IsChecked = _ => current().Breakpoints.Muted,
        });

        void Add(string id, string title, Action<DebugController, ActionContext> run, Func<DebugController, ActionContext, string?> whyNot, MenuPlace? menu) =>
            actions.Add(new IdeAction(id, title, "Debug", c => run(current(), c)) { WhyNot = c => whyNot(current(), c), Menus = menu is null ? [] : [menu] });
    }

    private const string NothingDebugged = "Nothing is being debugged";

    /// <summary>The Avalonia XAML attribute the caret is on, whose own breakpoint Toggle Breakpoint sets; null elsewhere.</summary>
    public static string? AttributeAtCaret(ActionContext context) =>
        context.File is { } file && XamlBreakpoints.Compiled(file.Path)
            ? XamlBreakpoints.AttributeAt(file.Text.Snapshot, file.Text.Snapshot.GetOffset(context.Caret))
            : null;

    /// <summary>The edits made while the program was paused, applied before it steps or resumes: false when the program
    /// restarted for them, or is no longer paused, and so is not to be set going.</summary>
    public async Task<bool> ApplyBeforeResumingAsync(DebugSession session)
    {
        if (HotReload is { Changed: true } && WhyNotApply() is null && session.State == DebugState.Paused)
        {
            if ((await ApplyAsync(Debugged(), SettingsRestart)).Restarted)
            {
                return false;
            }
            // Its own state alone: another program's stop may have taken the window meanwhile, and this one still goes on.
            if (session.State != DebugState.Paused)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Why a step cannot go now: nothing is being debugged, or the program runs.</summary>
    private string? NotPaused() => NotLive() ?? (Session!.State != DebugState.Paused ? "The program is not paused" : null);

    /// <summary>Why the chosen session takes no action now: nothing is being debugged, or the tab chosen is one that ended.</summary>
    private string? NotLive() => !IsDebugging ? NothingDebugged
        : Session is not { State: not DebugState.Ended } ? $"Debugging {name} has ended: choose a program that is debugged in the Debug window"
        : null;

    /// <summary>Sets the paused program going as an action asks, shown running at once; one the debugger refuses shows the
    /// stop again, and the reason in the status bar. Edits made while it was paused are applied first: one that needs a
    /// restart restarts it (or asks, as Settings says); one that does not compile is left, and the program goes on in the
    /// code it has, never stranded.</summary>
    private async Task ResumeAsync(Func<DebugSession, Task> resume, string action)
    {
        asked = (action, System.Diagnostics.Stopwatch.GetTimestamp());
        if (Session is not { } session || !await ApplyBeforeResumingAsync(session))
        {
            return;
        }
        try
        {
            await resume(session);
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

    /// <summary>Smart Step Into: the calls of the line the program is paused at, in the order they run, offered in
    /// its editor (each filled, the chosen one boxed, in the colour scheme's colours) to step straight into: the arrow keys
    /// or Tab move the choice, Enter or a click takes it, Escape leaves it. One call is stepped into at once; with none, it
    /// is Step Into.</summary>
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
        if (stop != shown || disposed)
        {
            return;
        }
        string path = PathNames.Canonical(source);
        Navigate?.Invoke(this, (path, Current(path, top)));
        CodeEditor editor = editors.CodeEditor;
        List<(StepInTarget Target, TextRange Range)> placed = editors.Active?.Path == path ? Placed(targets, path, editor.Document.Snapshot) : [];
        if (placed.Count <= 1)
        {
            // Nothing to choose between: the one call there is, else Step Into.
            await ResumeAsync(session => placed.Count == 1 ? session.StepIntoTargetAsync(placed[0].Target.Id) : session.StepIntoAsync(), "step into");
            return;
        }
        editor.Focus();
        editor.OfferChoices([.. placed.Select(p => p.Range)], chosen =>
        {
            if (chosen is int index && Session == session && session.State == DebugState.Paused && stop == shown)
            {
                IdeErrors.Observe(ResumeAsync(paused => paused.StepIntoTargetAsync(placed[index].Target.Id), "smart step into"));
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
            if (target is not { Line: int line, Column: int column, EndLine: int endLine, EndColumn: int endColumn })
            {
                continue;
            }
            TextPosition from = lines?.ToCurrent(path, line - 1, column - 1) ?? new TextPosition(line - 1, column - 1);
            TextPosition to = lines?.ToCurrent(path, endLine - 1, endColumn - 1) ?? new TextPosition(endLine - 1, endColumn - 1);
            if (from.Line != to.Line || from.Line < 0 || from.Line >= text.LineCount)
            {
                continue;
            }
            LineSpan row = text.GetLine(from.Line);
            if (from.Column > row.Length || to.Column > row.Length || to.Column <= from.Column)
            {
                continue;
            }
            placed.Add((target, TextRange.FromBounds(row.Start + from.Column, row.Start + to.Column)));
        }
        return placed;
    }

    /// <summary>Set Next Statement: the paused thread goes on from a line (from zero) of the method it is
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
            // The built lines first, each session's: a breakpoint's move asks them what it now asks of the debugger.
            bool code = change.Edits.Count > 0 && Path.GetExtension(document.Path).ToLowerInvariant() is ".cs" or ".razor" or ".cshtml";
            foreach (DebugTab tab in view.Sessions)
            {
                tab.Lines?.Follow(document.Path, change);
                if (code && tab is { HotReload: { } reload, IsLive: true })
                {
                    reload.Edited(document.Path, change.Before);
                }
            }
            foreach (RunningReload run in code ? running : [])
            {
                run.Reload.Edited(document.Path, change.Before);
            }
            Breakpoints.Follow(document.Path);
            FollowStop(document, change);
            if (Breakpoints.In(document.Path).Any(b => b.Attribute is not null))
            {
                // An attribute's tint goes where the attribute now is, which an edit inside its line moves.
                UpdateMarks(document);
            }
        };
        following[document] = follow;
        Breakpoints.Attach(document.Path, document.Text);
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
            Breakpoints.Detach(document.Path);
        }
    }

    /// <summary>Whether a save while the program is debugged applies the edits (Settings' Apply changes on file save).</summary>
    public Func<bool> HotReloadOnSave { get; init; } = () => false;

    /// <summary>An apply a save asked for, run once the saves of one Save All are done.</summary>
    private bool applyOnSave;

    private void OnSaved(object? sender, EventArgs e)
    {
        // A program run with Hot Reload takes a save's edits, whatever else is debugged.
        bool run = running.Any(r => r.Reload.Changed);
        bool debugged = HotReload is { Changed: true } && HotReloadOnSave() && WhyNotApply() is null;
        if (applyOnSave || !run && !debugged)
        {
            return;
        }
        applyOnSave = true;
        DebugSession? savedSession = Session;
        Dispatcher.UIThread.Post(() =>
        {
            applyOnSave = false;
            if (!disposed && Session == savedSession)
            {
                IdeErrors.Observe(SavedAsync());
            }
        });

        Task SavedAsync() => ApplyAsync([.. run ? running : [], .. debugged && HotReload is { Changed: true } ? Debugged() : []], SettingsRestart);
    }

    /// <summary>Apply Hot Reload Changes: the programs run with Hot Reload through the debugger take the edits whenever
    /// they have any (or nothing else would take them), whatever else is debugged, and the chosen session debugged with Hot
    /// Reload takes them too; each that needs it restarts as <paramref name="restart"/> says (the window's:
    /// <see cref="SettingsRestart"/>). Answers what each came to.</summary>
    public Task<HotReloadApplied> ApplyAllAsync(HotReloadRestart restart)
    {
        UiThread.Check();
        bool debugged = HotReload is not null && WhyNotApply() is null;
        return ApplyAsync([.. running.Count > 0 && (!debugged || running.Any(r => r.Reload.Changed)) ? running : [], .. Debugged()], restart);
    }

    /// <summary>The value of the expression under the pointer while paused (a.b.c up to the word hovered), evaluated in the
    /// chosen frame of the stop shown, for the hover card; one with members as its row, which opens as the Variables view's
    /// rows do (<see cref="HoverValues"/>). Null when there is none or it does not evaluate. The card goes with the stop.</summary>
    public async Task<CSharp.DebuggerHover?> HoverAt(int offset, CancellationToken cancellation)
    {
        UiThread.Check();
        if (Session is not { State: DebugState.Paused } session || frame is not { } at || ExpressionAt(editors.CodeEditor.Document.Snapshot, offset) is not { } expression)
        {
            return null;
        }
        int stop = shown;
        CancellationToken gone = stopShown.Token;
        try
        {
            EvaluateResponse value = await session.EvaluateAsync(expression, at.Id, "hover").WaitAsync(cancellation);
            if (stop != shown)
            {
                return null;
            }
            string code = $"{expression} = {(value.Type is { Length: > 0 } type && value.Result != type ? $"{{{type}}} " : "")}{VariableNode.Cut(value.Result)}";
            return new CSharp.DebuggerHover(code)
            {
                Values = value.VariablesReference > 0 ? HoverValues(session, at, stop, expression, value) : null,
                Gone = gone,
            };
        }
        catch (DapException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>A hovered value's rows: its own, opening into its members as the Variables view's rows do, each asked of the
    /// stop it was read at and of no other; its rows' actions are the Variables view's own (Add to Watches, Inspect, Set
    /// Value, a data breakpoint), and a value set there shows the hovered value read again with what was open still open.</summary>
    private VariablesList HoverValues(DebugSession session, DapStackFrame at, int stop, string expression, EvaluateResponse value)
    {
        VariablesTree tree = new()
        {
            Load = async reference => stop == shown && session.State == DebugState.Paused
                ? await session.VariablesAsync(reference)
                : throw new DapException(DapCommands.Variables, "The program has gone on from the stop this value was read at"),
        };
        tree.Show([Hovered(value)]);
        VariablesList list = new(tree) { DataWatch = DataWatchAsync };
        list.EvaluateRequested += (_, request) => IdeErrors.Observe(Evaluate(request.Expression, request.Watch));
        list.DataBreakpointRequested += (_, node) => IdeErrors.Observe(ToggleDataBreakpointAsync(node));
        list.SetValueRequested += (_, request) => IdeErrors.Observe(SetHoveredAsync(request.Node, request.Value));
        list.TextOpened += (_, node) => TextOpened?.Invoke(this, node.Value);
        return list;

        VariableNode Hovered(EvaluateResponse read) => new(expression, read.Result, read.Type, read.VariablesReference, 0, VariableRole.Value);

        async Task SetHoveredAsync(VariableNode node, string typed)
        {
            if (!await SetValue(node, typed, at) || stop != shown)
            {
                return;
            }
            try
            {
                EvaluateResponse again = await session.EvaluateAsync(expression, at.Id, "hover");
                if (stop == shown)
                {
                    await list.ShowAgain([Hovered(again)]);
                }
            }
            catch (DapException)
            {
                // The value set shows in Variables; the card keeps what it read.
            }
        }
    }

    /// <summary>The member access chain ending at the word around an offset: "this.name" for "name" in
    /// "this.name.Length".</summary>
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

    /// <summary>A session started, stopped, ran on or ended: its tab follows, and the window when the tab is chosen. A stop
    /// in a program not chosen chooses its tab; the chosen one's end chooses another still debugged (one paused first).</summary>
    private async Task OnStateChanged(DebugTab tab)
    {
        if (disposed || tab.Session is not { } session || !view.Sessions.Contains(tab))
        {
            return;
        }
        // What the program wrote before it stopped or ended is shown before the stop or the end is.
        if (session.State == DebugState.Ended)
        {
            tab.Console.Input = null;
            tab.Console.Sink.End(ConsoleStream.Output);
            tab.Console.Sink.End(ConsoleStream.Error);
            tab.Console.Sink.End(ConsoleStream.System);
            StopFollowing(tab);
            tab.HasEnded = true;
            tab.HotReload?.End();
            tab.Lines = null;
        }
        tab.Console.Flush();
        tab.OnStateChanged();
        if (disposed || !view.Sessions.Contains(tab))
        {
            return;
        }
        if (tab != chosen)
        {
            if (session.State == DebugState.Paused)
            {
                await ChooseAsync(tab);
                return;
            }
            Message?.Invoke(this, Told(tab));
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        await Show(tab, say: true);
        if (session.State == DebugState.Ended && tab == chosen && Next() is { } next)
        {
            // Said of the end, the status bar keeps it.
            await ChooseAsync(next, say: false);
        }
    }

    /// <summary>The chosen tab's session as it now is, in the window: the toolbar for its state, and its stop shown, or the
    /// last one's let go a moment after it runs on, or its end.</summary>
    private async Task Show(DebugTab tab, bool say)
    {
        DebugSession session = tab.Session!;
        view.SetState(session.State);
        view.ShowDetachable(session.Attached && session.State is DebugState.Running or DebugState.Paused);
        // Every stop is new and every run lets the last one's frames and values go: none is asked of again.
        NextShown();
        frame = null;
        Stop = null;
        clearing.Stop();
        showing = session.State == DebugState.Paused ? (System.Diagnostics.Stopwatch.GetTimestamp(), null) : null;
        switch (session.State)
        {
            case DebugState.Paused:
                int stop = shown;
                await ShowStop(session);
                if (stop != shown)
                {
                    break;
                }
                if (say)
                {
                    Message?.Invoke(this, Told(tab));
                }
                break;
            case DebugState.Running:
                editors.CodeEditor.CancelChoices();
                executionPoint = null;
                stopException = null;
                lastResult = null;
                bindingStop = null;
                bindingFrame = null;
                UpdateAllMarks();
                ClearInlineValues();
                clearing.Restart();
                if (say)
                {
                    Message?.Invoke(this, Told(tab));
                }
                break;
            case DebugState.Ended:
                executionPoint = null;
                UpdateAllMarks();
                ClearInlineValues();
                view.ShowFrames([], null, [], null);
                view.Tree.Show([]);
                view.SetState(DebugState.Ended);
                if (say)
                {
                    Message?.Invoke(this, Told(tab));
                }
                break;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A session's state in words, and with several programs debugged how many are and which are paused: the status
    /// bar's as it changes, and <see cref="Summary"/>'s.</summary>
    private string Told(DebugTab tab)
    {
        string said = Said(tab);
        List<DebugTab> live = [.. view.Sessions.Where(t => t.IsLive && t.Session is not null)];
        if (live.Count == 0 || live is [var only] && only == tab)
        {
            return said;
        }
        string[] paused = [.. live.Where(t => t.Session!.State == DebugState.Paused).Select(t => t.Name)];
        return $"{said}; {Plural.Of(live.Count, "program")} debugged"
            + (paused.Length > 0 ? $", {string.Join(" and ", paused)} paused" : string.Empty);
    }

    /// <summary>A session's state in words; what its stop was read to be only for the chosen tab, whose stop is read.</summary>
    private string Said(DebugTab tab) => Said(tab.Session!, tab.Name, tab == chosen);

    private string Said(DebugSession session, string name, bool read) => session.State switch
    {
        DebugState.Starting => $"Debugging {name}",
        DebugState.Paused when !read => $"{name}: paused ({session.StopReason})",
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
        _ when session.Gone is { } gone => $"Debugging {name} ended: {gone}",
        _ => session.Failure is { } failure
            ? $"Debugging {name} ended: {failure}"
            : $"Debugging {name} ended{(session.ExitCode is int code ? $" with exit code {code}" : "")}",
    };

    /// <summary>Shows a stop: its exception described, then its thread's frames and the top one's values. An exception the
    /// debugger cannot describe (a getter of its own that never returns) still shows where the program stopped. Held as
    /// background work until the stop is placed (its frames listed, the editor at its line), so a wait for the IDE to be
    /// quiet never ends with the editor about to go there; its values are read after, unheld, as Continue never waits.</summary>
    private async Task ShowStop(DebugSession session)
    {
        if (session.StoppedThread is not int thread)
        {
            return;
        }
        using IDisposable placing = BackgroundWork.Scope();
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
                // The inspector's call says the values and what set the new one, its place at the editor's line.
                if ((await session.StackTraceAsync(thread)).FirstOrDefault() is { } changed
                    && StringValue((await session.EvaluateAsync("change", changed.Id, "watch")).Result) is { } words)
                {
                    propertyChange = words + AppPlaces.At(SetAt(await session.EvaluateAsync("setAt", changed.Id, "watch")), CSharp.ServerPlaces.Texts(editors));
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
        if (session.StopReason == StopReasons.Exception)
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
            await ChooseThread(thread, placing);
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
        if (failure is null)
        {
            return null;
        }
        TextSnapshot? text = failure.Place is { } at ? BindingErrors.TextOf(at.File, editors.Documents) : null;
        SourcePlace? element = failure.Place is { } place ? AppPlaces.InEditor(place, _ => text) : null;
        return new BindingStop(BindingErrors.Problem(failure, text), error.Result, error.Type, error.VariablesReference)
        {
            Xaml = element is null ? null : $"{Path.GetFileName(element.File)}:{element.Line}:{element.Column}",
        };
    }

    /// <summary>The members behind a reference: those of <c>$bindingError</c> with its Xaml, which the app wrote at its XAML
    /// compiler's line, at the editor's (<see cref="BindingStop.Xaml"/>).</summary>
    public async Task<IReadOnlyList<DapVariable>> MembersAsync(DebugSession session, int reference)
    {
        IReadOnlyList<DapVariable> members = await session.VariablesAsync(reference);
        return bindingStop is { Xaml: { } xaml } failed && failed.Reference == reference && session == Session
            ? [.. members.Select(m => m.Name == "Xaml" && m.Value.StartsWith('"') ? m with { Value = SymbolDisplay.FormatLiteral(xaml, quote: true) } : m)]
            : members;
    }

    /// <summary>Where the style that set a watched property is written, as the inspector's call gives it (a place as the
    /// protocol writes one); null when it names none.</summary>
    private static SourcePlace? SetAt(EvaluateResponse evaluated)
    {
        try
        {
            return StringValue(evaluated.Result) is { } said ? JsonSerializer.Deserialize(said, WireJson.Default.SourcePlace) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A string as the debugger shows one, a C# literal, as it is; null for anything else (an error, null).</summary>
    public static string? StringValue(string shown) =>
        shown.Length > 1 && shown[0] == '"' && SyntaxFactory.ParseExpression(shown) is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal
            ? literal.Token.ValueText
            : null;

    /// <summary>Shows a thread's frames, the top one chosen and gone to; <paramref name="placed"/> is let go once it has been,
    /// before the frame's values are read.</summary>
    private async Task ChooseThread(int thread, IDisposable? placed = null)
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
        // Shown is the first frame of the user's code (Just My Code), past the inspector's call and a library's own sources.
        DapStackFrame? user = stack.FirstOrDefault(f => f.Source?.Path is not null && f.PresentationHint != "subtle");
        bool inspected = thread == session.StoppedThread && (session.PropertyStop is not null || session.BindingErrorStop);
        bool thrown = thread == session.StoppedThread && session.StopReason == StopReasons.Exception;
        DapStackFrame? top = inspected ? user ?? stack.FirstOrDefault()
            : (thrown ? user : null) ?? stack.FirstOrDefault(f => f.Source?.Path is not null) ?? stack.FirstOrDefault();
        bool shows = !inspected || top == user;
        executionPoint = thread == session.StoppedThread && shows && top?.Source?.Path is { } path ? (path, top.Line - 1) : executionPoint;
        if (thread == session.StoppedThread)
        {
            Stop = new DebugStop(thread, session.StopReason, stack, top, stopException?.Text) { Binding = bindingStop?.Problem };
            bindingFrame = bindingStop is not null && shows ? top?.Id : null;
        }
        view.ShowFrames(threads, threads.FirstOrDefault(t => t.Id == thread), stack, top);
        if (thread == session.StoppedThread)
        {
            FramesShown();
        }
        UpdateAllMarks();
        // The frame is gone to before its first wait, for its values.
        Task values = top is not null ? ChooseFrame(top, navigate: shows) : Task.CompletedTask;
        if (thread == session.StoppedThread && !shows && bindingStop?.Problem.Problem is { Path: { } xaml } failedAt && File.Exists(xaml))
        {
            // No code of the app's made the binding fail (a template's, a style's): its XAML is where to look.
            Navigate?.Invoke(this, (xaml, failedAt.Position));
        }
        placed?.Dispose();
        await values;
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
            Navigate?.Invoke(this, (path, Current(path, chosen)));
        }
        await ShowVariables();
    }

    /// <summary>The chosen frame's variables, under its watches and the last result evaluated; nothing more is asked once the
    /// program has gone on.</summary>
    private async Task ShowVariables()
    {
        UiThread.Check();
        if (Session is not { State: DebugState.Paused } session || frame is not { } current)
        {
            return;
        }
        int stop = shown;
        List<VariableNode> rows = [];
        if (bindingStop is { } failed && Stop is { } stopped && stopped.Frames.Any(f => f.Id == current.Id))
        {
            // The failed binding above the frame's own values, where $exception would be: the inspector call's parameter.
            rows.Add(new VariableNode(BindingStop.Name, failed.Value, failed.Type, failed.Reference, 0, VariableRole.Value));
        }
        foreach (string watch in watches)
        {
            rows.Add(await Evaluated(session, current, watch, VariableRole.Watch));
            if (stop != shown)
            {
                return;
            }
        }
        if (lastResult is { } result && result.Stop == stop)
        {
            VariableNode row = result.Row;
            rows.Add(new VariableNode(row.Name, row.Value, row.Type, row.Reference, 0, row.Role));
        }
        Dictionary<string, string> values = [];
        try
        {
            IReadOnlyList<DapScope> scopes = await session.ScopesAsync(current.Id);
            foreach (DapScope scope in scopes)
            {
                if (stop != shown)
                {
                    return;
                }
                IReadOnlyList<DapVariable> variables = await session.VariablesAsync(scope.VariablesReference);
                foreach (DapVariable variable in variables)
                {
                    values.TryAdd(variable.Name, variable.Value);
                }
                if (scopes.Count == 1)
                {
                    // One scope (the locals) needs no node of its own.
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
        if (frame == current && stop == shown)
        {
            view.Tree.Show(rows);
            ShowInlineValues(current, values);
            LocalsShown();
        }
    }

    /// <summary>The stop being shown: when it came, and when its frames were shown, until its locals are.</summary>
    private (long Came, long? Framed)? showing;

    /// <summary>The continue or step the person last asked for, and when, until the stop it comes to is shown.</summary>
    private (string Action, long At)? asked;

    /// <summary>How long the last stop took to show, as passenger.log has it.</summary>
    public StopTimes? LastStopTimes { get; private set; }

    private void FramesShown()
    {
        if (showing is { Framed: null } came)
        {
            showing = came with { Framed = System.Diagnostics.Stopwatch.GetTimestamp() };
        }
    }

    /// <summary>The stop's locals are shown: how long it took, from the step that came to it and from the stop, goes to
    /// passenger.log, since it is what a person waits on at every step.</summary>
    private void LocalsShown()
    {
        if (showing is not { } stop)
        {
            return;
        }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        showing = null;
        TimeSpan framed = System.Diagnostics.Stopwatch.GetElapsedTime(stop.Came, stop.Framed ?? now);
        TimeSpan listed = System.Diagnostics.Stopwatch.GetElapsedTime(stop.Came, now);
        (string Action, TimeSpan After)? step = asked is { } going ? (going.Action, System.Diagnostics.Stopwatch.GetElapsedTime(going.At, stop.Came)) : null;
        asked = null;
        LastStopTimes = new StopTimes(step?.Action, step?.After, framed, listed);
        IdeErrors.Write($"Debugging {name}: {(step is { } s ? $"{s.Action} stopped {s.After.TotalMilliseconds:0} ms after it was asked, and the stop" : "a stop")} "
            + $"showed its frames {framed.TotalMilliseconds:0} ms and its locals {listed.TotalMilliseconds:0} ms after it came");
    }

    /// <summary>The chosen frame's values after the lines of its method, in its file and no other.</summary>
    private void ShowInlineValues(DapStackFrame current, IReadOnlyDictionary<string, string> values)
    {
        if (disposed)
        {
            return;
        }
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

    /// <summary>An exception as a stop names it: its type without the namespace, and its message.</summary>
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

    /// <summary>What Set Data Breakpoint offers on a row of Variables, the debugger asked: to set one, to remove the one set
    /// on it (Remove Data Breakpoint), or neither and why (the system, the kind of member, the program running).</summary>
    public async Task<DataWatchOffer> DataWatchAsync(VariableNode node)
    {
        UiThread.Check();
        DebugSession? session = Session;
        (DataBreakpointInfoResponse? info, string? whyNot) = await DataInfoAsync(node);
        return info?.DataId is { } dataId && session is not null && Breakpoints.FindData(dataId, session) is not null
            ? new DataWatchOffer(DataWatchOffer.Remove, null)
            : new DataWatchOffer(DataWatchOffer.Set, whyNot);
    }

    /// <summary>Sets a data breakpoint on a row of Variables, or removes the one set on it (Set Data Breakpoint, Remove Data
    /// Breakpoint); why it cannot is said in the status bar.</summary>
    public async Task ToggleDataBreakpointAsync(VariableNode node)
    {
        UiThread.Check();
        // Held until the breakpoint is in the store, so a picture taken after the choice shows it.
        using IDisposable held = BackgroundWork.Scope();
        DebugSession? session = Session;
        (DataBreakpointInfoResponse? info, string? whyNot) = await DataInfoAsync(node);
        if (info?.DataId is not { } dataId || session is null)
        {
            Message?.Invoke(this, $"Set Data Breakpoint: {whyNot}");
            return;
        }
        if (Breakpoints.FindData(dataId, session) is { } set)
        {
            Breakpoints.Remove(set);
            return;
        }
        Breakpoints.AddData(dataId, DataLocation(node), session);
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

    /// <summary>A watched member as a data breakpoint's stop names it: the member on its object, and its type.</summary>
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

    /// <summary>Set Value: what a row's expression names takes the value typed (a C# expression), in the chosen frame or the
    /// one given (a hover's), and the frame's variables and inline values show what it now is; a refusal is said in the
    /// status bar. True when the value was set.</summary>
    public async Task<bool> SetValue(VariableNode node, string value, DapStackFrame? within = null)
    {
        UiThread.Check();
        node.IsEditing = false;
        if (Session is not { State: DebugState.Paused } session || (within ?? frame) is not { } current || node.Expression is not { } expression)
        {
            return false;
        }
        bool set = true;
        try
        {
            await session.SetExpressionAsync(expression, value, current.Id);
        }
        catch (DapException error)
        {
            Message?.Invoke(this, $"Could not set {expression}: {error.Reason}");
            set = false;
        }
        await ShowVariables();
        return set;
    }

    /// <summary>A new stop shown, or none: what the last one's requests bring back is dropped, and what still shows of it goes.</summary>
    private void NextShown()
    {
        shown++;
        CancellationTokenSource gone = stopShown;
        stopShown = new CancellationTokenSource();
        gone.Cancel();
    }

    /// <summary>Evaluate's Enter, or Add to Watches: a watch is evaluated at every stop, an expression only now, once, its
    /// side effects with it, its result kept for this stop alone.</summary>
    private async Task Evaluate(string expression, bool watch)
    {
        if (watch)
        {
            if (!watches.Contains(expression))
            {
                watches.Add(expression);
            }
        }
        else if (Session is { State: DebugState.Paused } session && frame is { } current)
        {
            int stop = shown;
            VariableNode row = await Evaluated(session, current, expression, VariableRole.Result);
            if (stop != shown)
            {
                return;
            }
            lastResult = (stop, row);
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
        if (disposed)
        {
            return;
        }
        bool debugging = IsDebugging;
        // Muted, a breakpoint shows grey whatever the debugger said of it.
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
            // An attribute's breakpoint tints the attribute; nothing once the attribute is edited away.
            Range: b.Attribute is { } attribute ? XamlBreakpoints.AttributeRange(text, b.Line, attribute) ?? new TextRange(0, 0) : null))];
        if (executionPoint is { } point && point.Path == document.Path)
        {
            marks.Add(new LineMark(Current(point.Path, point.Line), LineMarkKind.ExecutionPoint));
        }
        document.LineMarks = marks;
    }

    /// <summary>Carries the stop shown in a file through an edit to it: the arrow and the frames' rows from the built lines.
    /// The values go with their lines by themselves (<see cref="OpenDocument.LineAnnotations"/>).</summary>
    private void FollowStop(OpenDocument document, DocumentChangedEventArgs change)
    {
        if (executionPoint is { } point && point.Path == document.Path)
        {
            UpdateMarks(document);
        }
        if (change.After.LineCount != change.Before.LineCount && view.Frames.Count > 0)
        {
            view.ShowPlacesAgain();
        }
    }

    /// <summary>Where a built line (from zero) of a file is now, while a session runs: what the program's records of its build
    /// (an app's XAML places) are read against.</summary>
    public int CurrentLine(string path, int line) => Current(path, line);

    /// <summary>The built place (line and column from zero, as the compiler counted them) a place in a file now has: through
    /// the edits since the build while a session runs, else in the file's text as it is.</summary>
    public TextPosition BuiltPlace(string path, int line, int column)
    {
        if (lines is not null)
        {
            return lines.ToBuilt(path, line, column);
        }
        Lsp.LspPosition at = CSharp.ServerPlaces.CompiledPositionOf(path, CSharp.ServerPlaces.TextOf(editors, path), new TextPosition(line, column));
        return new TextPosition(at.Line, at.Character);
    }

    /// <summary>Whether a line (from zero) of a file is the build's, rather than written since the session began.</summary>
    public bool IsBuiltLine(string path, int line) => lines?.IsBuilt(path, line) ?? true;

    /// <summary>Where a stack frame is in its file now, line and column from zero as the editor counts them (the debugger's are
    /// the compiler's, from one, in the build); null for a frame without a file.</summary>
    public TextPosition? PlaceOf(DapStackFrame frame) => frame.Source?.Path is { } path ? Current(PathNames.Canonical(path), frame) : null;

    /// <summary>Where a frame of a session's program (the chosen one's or another's) is in its file now, through that
    /// session's built lines.</summary>
    public TextPosition? PlaceOf(DapStackFrame frame, DebugSession session)
    {
        if (frame.Source?.Path is not { } path)
        {
            return null;
        }
        int line = Math.Max(0, frame.Line - 1);
        int column = Math.Max(0, frame.Column - 1);
        return TabOf(session)?.Lines?.ToCurrent(PathNames.Canonical(path), line, column) ?? new TextPosition(line, column);
    }

    /// <summary>Where a built line of a file is now, while a session runs.</summary>
    private int Current(string path, int line) => lines?.ToCurrent(path, line) ?? line;

    /// <summary>Where a frame's built line and column in a file are now, while a session runs.</summary>
    private TextPosition Current(string path, DapStackFrame frame)
    {
        int line = Math.Max(0, frame.Line - 1);
        int column = Math.Max(0, frame.Column - 1);
        return lines?.ToCurrent(path, line, column) ?? new TextPosition(line, column);
    }

    /// <summary>The built line a line of a file has, while a session runs.</summary>
    private int Built(string path, int line) => lines?.ToBuilt(path, line) ?? line;
}

/// <summary>The exact native tab and process started or attached by an additional launch.</summary>
public sealed record DebugLaunch(DebugTab Tab, int ProcessId);

/// <summary>What Set Data Breakpoint offers on a row of Variables: its title (Set Data Breakpoint, or Remove Data
/// Breakpoint on a member watched), and why it cannot be chosen when it cannot.</summary>
public sealed record DataWatchOffer(string Title, string? WhyNot)
{
    public const string Set = "Set Data Breakpoint";
    public const string Remove = "Remove Data Breakpoint";
}

/// <summary>How long a stop took to show: from the continue or step that came to it (as the status bar names it), when the
/// person asked for one, and from the stop to its frames and to its locals shown.</summary>
public sealed record StopTimes(string? Asked, TimeSpan? ToStop, TimeSpan Frames, TimeSpan Locals);

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
    /// <summary>Its control's element as the parameter's Xaml names it ("MainWindow.axaml:8:20"), at the editor's line and
    /// column: the app wrote its XAML compiler's, which a lone '\r' above puts lines ahead.</summary>
    public string? Xaml { get; init; }

    /// <summary>Its row in Variables, named as the exception stopped at is <c>$exception</c>; the breakpoint's condition and
    /// log message read it by the same name.</summary>
    public const string Name = "$bindingError";
}
