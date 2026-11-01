using System.Collections.ObjectModel;
using Passenger.App.Debugging;
using Passenger.Dap;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.ToolWindows;

/// <summary>A program's tab in the Debug window: its name (its run configuration's, or the program attached to), its own
/// console, and whether its session has ended, the tab then kept greyed until another starts in its place or it is closed.
/// The debugger keeps the session, and what it knows of it while the tab is not shown, here.</summary>
public sealed class DebugTab(string name, OutputView console) : Observable, IHotReloadTarget
{
    private bool isChosen;
    private bool hasEnded;

    public string Name { get; } = name;

    /// <summary>What its program wrote, and the debugger's words on it.</summary>
    public OutputView Console { get; } = console;

    /// <summary>Its session, from its first moment; null until the debugger hands it over.</summary>
    public DebugSession? Session { get; internal set; }

    /// <summary>Unformatted output from this program, on the debugger transport's callback thread.</summary>
    public event EventHandler<OutputEvent>? ProgramOutput;

    /// <summary>This session started, paused, ran on or ended.</summary>
    public event EventHandler? StateChanged;

    /// <summary>The owner's restart for edits Hot Reload cannot apply, instead of a configuration restart.</summary>
    public Func<HotReloadOutcome, Task<bool>>? RestartFor { get; set; }

    internal bool OpeningStopped { get; set; }
    internal void OnProgramOutput(OutputEvent output) => ProgramOutput?.Invoke(this, output);
    internal void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>The session's built lines, from its start to its end.</summary>
    internal BuiltLines? Lines { get; set; }

    /// <summary>The session's Hot Reload, when it has one.</summary>
    internal Debugging.HotReload? HotReload { get; set; }

    Debugging.HotReload? IHotReloadTarget.HotReload => HotReload;

    bool IHotReloadTarget.Quiet => false;

    void IHotReloadTarget.Say(string line, bool error) => Console.Append($"{line}\n", error);

    /// <summary>Its program as an Avalonia or WPF app with Passenger's inspector in it, from its launch until the tab goes
    /// (ended with its session, its failed bindings kept while the tab is); null for any other program.</summary>
    public LiveTree.LiveApp? App { get; internal set; }

    /// <summary>Whether its program was launched with its output in this console, rather than attached to or in the terminal.</summary>
    internal bool OwnsConsole { get; init; }

    /// <summary>The plan Debug launched its program by, where it serves; null for a program attached to.</summary>
    public Run.LaunchPlan? Plan { get; internal set; }

    /// <summary>Passenger's hook in its program, told of each update Hot Reload applied so the program's update handlers run;
    /// null for a program launched without it.</summary>
    internal Debugging.UpdateHandlerHook? Handlers { get; set; }

    /// <summary>Whether its console has said once that its program's update handlers are not called.</summary>
    internal bool ToldUnhandled { get; set; }

    /// <summary>The programs it is debugged with (a container run's services, by its configuration's name): Hot Reload's
    /// changes go to each of them together.</summary>
    public string? Group { get; internal set; }

    /// <summary>The tab of the program this tab's session debugs another part of (a Godot game's C#, whose GDScript this tab
    /// debugs): what names no session takes that one while neither is paused, and the two count as one program.</summary>
    public DebugTab? Companion { get; internal set; }

    /// <summary>What follows its program's output from outside into its console (a container's log), while it does.</summary>
    internal System.Diagnostics.Process? Following { get; set; }

    /// <summary>Whether it is the tab shown: its frames, values, console and toolbar are the window's.</summary>
    public bool IsChosen
    {
        get => isChosen;
        set => Set(ref isChosen, value);
    }

    public bool HasEnded
    {
        get => hasEnded;
        set => Set(ref hasEnded, value);
    }

    /// <summary>Whether its session runs, is paused, or is still starting.</summary>
    public bool IsLive => !HasEnded && Session is not { State: DebugState.Ended };
}

/// <summary>What the Debug window shows, kept whether or not its view is built: each program's tab and console, the
/// chosen one's threads, frames and values, which of its tabs (Debugger or Console) is shown, and what its toolbar can press.
/// The debugger writes and reads it; <see cref="DebugView"/> shows it once the window is first shown, and what is done there
/// (a frame chosen, an expression evaluated, a toolbar button) comes back through its events.</summary>
public sealed class DebugModel
{
    private readonly OutputView first;
    private readonly List<OutputView> consoles = [];
    private OutputView shown;
    private DebugState state = DebugState.Ended;
    private bool detachable;

    public DebugModel()
    {
        first = NewConsole();
        shown = first;
    }

    /// <summary>A toolbar button pressed: the app action it runs.</summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>Avalonia's debug overlays in the app being debugged, shown while one with the inspector in it runs: the
    /// view's toolbar holds it once built.</summary>
    public OverlaysButton Overlays { get; } = new();

    public event EventHandler<DapThread>? ThreadChosen;

    public event EventHandler<DapStackFrame>? FrameChosen;

    /// <summary>An expression to evaluate once (false), or to keep as a watch (true).</summary>
    public event EventHandler<(string Expression, bool Watch)>? EvaluateRequested;

    /// <summary>A watch to stop watching.</summary>
    public event EventHandler<string>? WatchRemoved;

    /// <summary>Set Value: a row's new value typed in its place (a C# expression), Enter pressed.</summary>
    public event EventHandler<(VariableNode Node, string Value)>? SetValueRequested;

    /// <summary>Set Data Breakpoint, or Remove Data Breakpoint, chosen on a row.</summary>
    public event EventHandler<VariableNode>? DataBreakpointRequested;

    /// <summary>A row to run again with every thread let go (<see cref="VariablesList.RunWithThreadsRequested"/>).</summary>
    public event EventHandler<VariableNode>? RunWithThreadsRequested;

    /// <summary>Read Again, over Variables once every thread ran in an evaluation: the frame's values to be read anew.</summary>
    public event EventHandler? ReadAgainRequested;

    /// <summary>A query's SQL, read, to open whole in a read-only editor tab.</summary>
    public event EventHandler<VariableNode>? TextOpened;

    /// <summary>A link in a session's console was clicked.</summary>
    public event EventHandler<LinkTarget>? ConsoleLinkClicked;

    /// <summary>A session's tab was pressed.</summary>
    public event EventHandler<DebugTab>? SessionChosen;

    /// <summary>A session's tab was closed with its ×.</summary>
    public event EventHandler<DebugTab>? SessionClosed;

    /// <summary>What the toolbar can press changed, or Mute Breakpoints' or Hot Reload's look.</summary>
    internal event EventHandler? ToolsChanged;

    /// <summary>The threads and frames shown again, with the ones chosen.</summary>
    internal event EventHandler? FramesShown;

    /// <summary>The Debugger or Console tab shown, or another session's console in it.</summary>
    internal event EventHandler? TabShown;

    /// <summary>The Evaluate field given an expression and the keys.</summary>
    internal event EventHandler? EvaluateFocused;

    /// <summary>Each frame's place to be said again, an edit made while paused perhaps having moved its code.</summary>
    internal event EventHandler? PlacesMoved;

    /// <summary>What Variables says over its rows changed (<see cref="Moved"/>).</summary>
    internal event EventHandler? MovedChanged;

    /// <summary>What Set Data Breakpoint offers on a row, the debugger asked: the menu's item says so once it answers.</summary>
    public Func<VariableNode, Task<DataWatchOffer>>? DataWatch { get; set; }

    /// <summary>Where a frame is in its file now, from zero, as the editor counts lines (the debugger's are its build's
    /// compiler's), the debugger asked: the line a frame's row and Copy Stack name.</summary>
    public Func<DapStackFrame, TextPosition?>? PlaceOf { get; set; }

    public VariablesTree Tree { get; } = new();

    /// <summary>The chosen program's output, as the Console tab shows it.</summary>
    public OutputView Console => shown;

    /// <summary>Every session's console, the first made with the window.</summary>
    public IReadOnlyList<OutputView> Consoles => consoles;

    /// <summary>The programs debugged, a tab each, in the order they started.</summary>
    public ObservableCollection<DebugTab> Sessions { get; } = [];

    public ObservableCollection<DapThread> Threads { get; } = [];

    public ObservableCollection<DapStackFrame> Frames { get; } = [];

    /// <summary>The thread whose frames are shown.</summary>
    internal DapThread? SelectedThread { get; private set; }

    /// <summary>The frame chosen among them.</summary>
    public DapStackFrame? SelectedFrame { get; private set; }

    /// <summary>Whether the Console tab is shown, rather than the Debugger's.</summary>
    public bool IsConsoleShown { get; private set; }

    /// <summary>What Variables says over its rows once every thread ran in an evaluation: that the values read before it
    /// may have moved; null while it says nothing.</summary>
    public string? Moved { get; private set; }

    /// <summary>What the Evaluate field was last given to evaluate.</summary>
    internal string? EvaluateText { get; private set; }

    /// <summary>The debugged program's state, as the toolbar last showed it.</summary>
    internal DebugState State => state;

    /// <summary>Whether Detach can be pressed: the debugger has hold of a program it attached to.</summary>
    internal bool Detachable => detachable;

    /// <summary>Whether Apply Hot Reload Changes shows, and whether it can be pressed.</summary>
    internal (bool On, bool CanApply) HotReloadShown { get; private set; }

    /// <summary>Whether Mute Breakpoints shows pressed.</summary>
    public bool ShowsMuted { get; private set; }

    /// <summary>A console for another session's program, set up as the first is: its links, the folders they are looked
    /// for in, and its buffer.</summary>
    public OutputView NewConsole()
    {
        OutputView console = new();
        if (consoles.Count > 0)
        {
            console.Limit = first.Limit;
            console.Links.Folders = first.Links.Folders;
        }
        console.LinkClicked += (_, target) => ConsoleLinkClicked?.Invoke(this, target);
        consoles.Add(console);
        return console;
    }

    /// <summary>Shows a session's console in the Console tab.</summary>
    public void ShowConsole(OutputView console)
    {
        if (shown != console)
        {
            shown = console;
            TabShown?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Lets a session's console go, its tab closed or replaced; the first stays, for the next.</summary>
    public void ForgetConsole(OutputView console)
    {
        if (console != first)
        {
            consoles.Remove(console);
        }
    }

    /// <summary>The console a new session writes to when no other is live and so its tab replaces the rest: the first.</summary>
    public OutputView FirstConsole => first;

    /// <summary>Shows a thread's frames, one of them chosen, without raising the choice back.</summary>
    public void ShowFrames(IReadOnlyList<DapThread> all, DapThread? thread, IReadOnlyList<DapStackFrame> stack, DapStackFrame? chosen)
    {
        Threads.Clear();
        foreach (DapThread each in all)
        {
            Threads.Add(each);
        }
        SelectedThread = thread is null ? null : Threads.FirstOrDefault(t => t.Id == thread.Id);
        Frames.Clear();
        foreach (DapStackFrame frame in stack)
        {
            Frames.Add(frame);
        }
        SelectedFrame = chosen;
        FramesShown?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What can be pressed in each state. The panes show what the controller gives them: a stop's frames and
    /// values, cleared a moment after the program goes on.</summary>
    public void SetState(DebugState state)
    {
        this.state = state;
        detachable &= state is DebugState.Running or DebugState.Paused or DebugState.Starting;
        ToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Detach is pressed only while the debugger has hold of a program it attached to.</summary>
    public void ShowDetachable(bool attached)
    {
        detachable = attached;
        ToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows Mute Breakpoints pressed while the breakpoints are muted.</summary>
    public void ShowMuted(bool muted)
    {
        ShowsMuted = muted;
        ToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows Apply Hot Reload Changes while the session has Hot Reload, pressable while its program runs.</summary>
    public void ShowHotReload(bool on, bool canApply)
    {
        HotReloadShown = (on, canApply);
        ToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears what the last folder's sessions left, as another opens: their tabs, and every console but the first,
    /// which is shown empty.</summary>
    public void Clear()
    {
        ShowFrames([], null, [], null);
        Tree.Show([]);
        ForgetSessions();
        first.Input = null;
        first.Clear();
    }

    /// <summary>Lets the last debugger's tabs go, as another folder's debugger takes the window: every console but the first,
    /// which is shown.</summary>
    public void ForgetSessions()
    {
        Sessions.Clear();
        consoles.RemoveAll(c => c != first);
        ShowConsole(first);
    }

    /// <summary>The Evaluate field given an expression and the keys, the Debugger tab shown, once the view is built.</summary>
    public void FocusEvaluate(string text)
    {
        ShowTab(console: false);
        EvaluateText = text;
        EvaluateFocused?.Invoke(this, EventArgs.Empty);
    }

    public void ShowTab(bool console)
    {
        IsConsoleShown = console;
        TabShown?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Says again the place each frame's row names, whose code an edit made while paused may have moved.</summary>
    public void ShowPlacesAgain() => PlacesMoved?.Invoke(this, EventArgs.Empty);

    /// <summary>Has Variables say <paramref name="said"/> over its rows, or nothing for null.</summary>
    public void SayMoved(string? said)
    {
        if (Moved != said)
        {
            Moved = said;
            MovedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void ReadAgain() => ReadAgainRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The line (from one) a frame's row names: where its code is in the editor's lines.</summary>
    internal int LineOf(DapStackFrame frame) => PlaceOf?.Invoke(frame) is { } place ? place.Line + 1 : frame.Line;

    internal void Request(string action) => ActionRequested?.Invoke(this, action);

    internal void ChooseThread(DapThread thread)
    {
        SelectedThread = thread;
        ThreadChosen?.Invoke(this, thread);
    }

    internal void ChooseFrame(DapStackFrame frame)
    {
        SelectedFrame = frame;
        FrameChosen?.Invoke(this, frame);
    }

    /// <summary>A frame gone to from its menu, the one chosen.</summary>
    internal void GoToFrame(DapStackFrame frame) => FrameChosen?.Invoke(this, frame);

    internal void Evaluate(string expression, bool watch) => EvaluateRequested?.Invoke(this, (expression, watch));

    internal void Unwatch(string expression) => WatchRemoved?.Invoke(this, expression);

    internal void SetValue(VariableNode node, string value) => SetValueRequested?.Invoke(this, (node, value));

    internal void RunWithThreads(VariableNode node) => RunWithThreadsRequested?.Invoke(this, node);

    internal void ToggleDataBreakpoint(VariableNode node) => DataBreakpointRequested?.Invoke(this, node);

    internal void OpenText(VariableNode node) => TextOpened?.Invoke(this, node);

    internal void Choose(DebugTab tab) => SessionChosen?.Invoke(this, tab);

    internal void Close(DebugTab tab) => SessionClosed?.Invoke(this, tab);
}
