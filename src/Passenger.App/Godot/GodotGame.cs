using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>A Godot game Run, Debug or Profile started, as its remote debugger: Passenger listens on loopback for the one
/// connection the game makes back (<c>--remote-debug tcp://127.0.0.1:port</c>, which <see cref="Listen"/> adds to its plan),
/// and speaks Godot's remote debug protocol over it as Godot's editor does. The game tells its errors and warnings
/// (<see cref="Raised"/>, each with the stack it was raised on) and its performance monitors once a second; asked, its scene
/// tree and a node's properties, and it takes a property's new value; its loop is held and stepped a frame at a time, and a
/// click in its window picks a node, through Godot's own messages. So it is a source of the Live Visual Tree. Under Debug
/// its GDScript stops go to its script debugger (<see cref="Scripts"/>); under Run and Profile its breakpoints are skipped and
/// its errors never stop it. A game Godot's editor plays is heard the same way through Passenger's addon in that editor
/// (<see cref="Relay"/>), the editor keeping its debugger. Events are raised on the UI thread; it ends with its connection,
/// or once disposed.</summary>
public sealed class GodotGame : ILiveTreeSource, ILiveTreeValues, IGodotValues, IDisposable
{
    /// <summary>The flag that gives a game its debugger.</summary>
    public const string RemoteDebug = "--remote-debug";

    private readonly TcpListener? listener;

    /// <summary>Where a relayed game's messages go (<see cref="Relay"/>): each message's bytes, as a frame holds them after its
    /// length, sent on through the editor that holds its debugger; null for a game Passenger is the debugger of.</summary>
    private readonly Func<byte[], CancellationToken, Task>? relay;

    private readonly CancellationTokenSource ending = new();
    private readonly SemaphoreSlim writing = new(1, 1);
    private NetworkStream? stream;
    private volatile bool taken;

    /// <summary>When the game last sent a message (a <see cref="System.Diagnostics.Stopwatch"/> timestamp), written as its
    /// messages are read: whether it went on talking is what a question it left unanswered says.</summary>
    private long lastHeard;

    /// <summary>The last error the game raised that was none of Passenger's helper's, and when: what a question it left
    /// unanswered meanwhile says.</summary>
    private (long At, string Said)? lastRaised;

    /// <summary>The scene tree asked of the game, answered with the changes of its count of nodes heard as it came
    /// (<see cref="moves"/>): the tree is of then, or later.</summary>
    private TaskCompletionSource<(GodotNode? Tree, int Moves)>? treeAsked;

    /// <summary>The scene tree as the game last sent it, where a node set from the properties pane is placed.</summary>
    private GodotNode? lastTree;
    private readonly Dictionary<ulong, TaskCompletionSource<GodotObjectState>> objectsAsked = [];
    private readonly Dictionary<ulong, GodotObjectState> inspected = [];
    private readonly Dictionary<long, TaskCompletionSource<(int Width, int Height, string Path)>> picturesAsked = [];
    private readonly Dictionary<long, TaskCompletionSource<List<object?>>> agentAsked = [];
    private long asks;
    private bool disposed;

    /// <summary>The profile Godot's own profilers are recording into, while the game is profiled; read off the UI thread as
    /// its frames come.</summary>
    private volatile Passenger.Profiler.GameRecording? recording;

    /// <summary>The most script functions a frame names, the heaviest by their time in all: Godot leaves out the rest.</summary>
    private const int FrameFunctions = 128;

    /// <summary>Passenger's helper sent for (<see cref="AskAsync"/>), done once the game is told to make it: another ask waits
    /// for it rather than making a second.</summary>
    private Task? bringing;

    /// <summary>How many times the game has been told to make the helper: a scene tree asked before the last of them may
    /// predate it, so only one asked since says the helper is gone.</summary>
    private int brought;


    private GodotGame(string name, string folder, string? version, bool headless, bool unseen, bool scriptsAlone, TcpListener? listener, GodotScriptStart? scripts,
        Func<byte[], CancellationToken, Task>? relay = null)
    {
        Name = name;
        ScriptsAlone = scriptsAlone;
        Folder = folder;
        Version = version;
        Headless = headless;
        Unseen = unseen;
        Tuning = new GodotTuning(folder);
        this.listener = listener;
        this.relay = relay;
        ScriptsDebugged = scripts is not null;
        StartBreakpoints = scripts?.Breakpoints ?? [];
    }

    /// <summary>A plan as a launch starts it, with a game listened for when it runs a Godot game (<see cref="LaunchPlan.Godot"/>),
    /// <c>--remote-debug</c> among the arguments that start it; the plan as it is, and no game, for any other. With
    /// <paramref name="scripts"/> (Debug's) its GDScript is debugged from its first line: its breakpoints given as it starts,
    /// and its errors stopping it unless they are not to.</summary>
    public static (LaunchPlan Plan, GodotGame? Game) Listen(LaunchPlan plan, GodotScriptStart? scripts = null)
    {
        if (plan.Godot is not { } folder || plan.GodotEditor)
        {
            return (plan, null);
        }
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start(1);
        GodotGame game = new(plan.Name, folder, plan.GodotVersion, GodotExecutables.RunsHeadless(plan.Arguments), plan.GodotUnseen, plan.ScriptsAlone, listener, scripts);
        if (VirtualDisplays.Serves(plan.Environment.GetValueOrDefault(VirtualDisplays.Variable)))
        {
            game.drawsOnServer = true;
            VirtualDisplays.Hold();
        }
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        List<string> arguments = [.. plan.Arguments];
        List<string> debugging = [RemoteDebug, $"tcp://127.0.0.1:{port}"];
        if (scripts is not null && GodotScriptBreakpoints.Argument(scripts.Breakpoints) is { } breakpoints)
        {
            debugging.AddRange([GodotScriptBreakpoints.Flag, breakpoints]);
        }
        if (scripts is { StopAtErrors: false })
        {
            debugging.Add(GodotScriptBreakpoints.IgnoreErrors);
        }
        arguments.InsertRange(plan.Leading, debugging);
        // For the game's life, as its output is: no screenshot or test waits on a connection that lasts until the game ends.
        IdeErrors.Observe(BackgroundWork.Unheld(game.ServeAsync));
        return (plan with { Arguments = arguments, Leading = plan.Leading + debugging.Count }, game);
    }

    /// <summary>A game Godot's editor plays, heard through Passenger's addon there: what the game tells the editor comes in by
    /// <see cref="Take"/>, what Passenger tells it goes out through <paramref name="send"/>, and the editor keeps its
    /// debugger, its script stops, its loop and its picking (<see cref="EditorHolds"/>). It connects with <see cref="RelayConnected"/>
    /// and ends with <see cref="RelayEnded"/>.</summary>
    public static GodotGame Relay(string name, string folder, string? version, Func<byte[], CancellationToken, Task> send) =>
        new(name, folder, version, headless: false, unseen: false, scriptsAlone: false, listener: null, scripts: null, relay: send);

    /// <summary>Whether Godot's editor holds the game's debugger, Passenger hearing it through its addon (<see cref="Relay"/>).</summary>
    public bool Relayed => relay is not null;

    /// <summary>Why Passenger leaves the game's loop and picking alone: Godot's editor holds them, for a game it plays; null for
    /// a game Passenger is the debugger of.</summary>
    public string? EditorHolds => Relayed ? $"Godot's editor holds {Name}'s loop and its picking: pause, step and pick nodes in its Game view" : null;

    /// <summary>Whether a relayed game is stopped in a script, its editor's to let go on.</summary>
    private bool stoppedInEditor;

    /// <summary>A relayed game's editor began its session with it.</summary>
    public void RelayConnected()
    {
        UiThread.Check();
        if (taken || disposed)
        {
            return;
        }
        taken = true;
        Connected();
    }

    /// <summary>A message a relayed game told its editor, its bytes as a frame holds them after its length; any thread.</summary>
    public void Take(byte[] body) => Heard(body, GodotRelay.Objects);

    /// <summary>A relayed game's session with its editor ended.</summary>
    public void RelayEnded() => UiThread.Post(Ended);

    /// <summary>Whether a GDScript file of its project saved is reloaded in it (Hot Reload is on for its run).</summary>
    public bool ReloadsScripts { get; set; }

    /// <summary>The fold of the console its program writes to, once its launch has one: Godot's errors from before its
    /// debugger hears any (a renderer that could not start, which Godot then tells only in a dialog of its own).</summary>
    public GodotConsoleFold? Console { get; set; }

    /// <summary>Whether its console has said what Godot's reload of a script keeps.</summary>
    public bool ToldReload { get; set; }

    /// <summary>Whether its GDScript is debugged (it was started for Debug): a stop waits for its script debugger.</summary>
    public bool ScriptsDebugged { get; private set; }

    /// <summary>The breakpoints it was given as it started, by file and line from one.</summary>
    public IReadOnlyList<(string Resource, int Line)> StartBreakpoints { get; }

    /// <summary>Its script debugger, once one has attached: its stops, and the answers to what it asks, go to it.</summary>
    public GodotScriptAdapter? Scripts { get; set; }

    /// <summary>A stop the game made before its script debugger attached, waiting for it.</summary>
    private GodotBreak? held;

    /// <summary>The stop that waits for the script debugger, which takes it; null for none.</summary>
    public GodotBreak? TakeHeldBreak()
    {
        GodotBreak? taken = held;
        held = null;
        return taken;
    }

    /// <summary>No script debugger will come (its session could not start): the game goes on, as one not debugged does.</summary>
    public void LetScriptsGo()
    {
        UiThread.Check();
        if (!ScriptsDebugged || State != LiveAppState.Connected)
        {
            ScriptsDebugged = false;
            return;
        }
        ScriptsDebugged = false;
        Send(GodotWords.SkipBreakpoints, true);
        Send(GodotWords.IgnoreErrorBreaks, true);
        if (TakeHeldBreak() is { } stop)
        {
            IdeErrors.Observe(SendToAsync(GodotWords.Continue, stop.Thread));
        }
    }

    public string Name { get; }

    /// <summary>The Godot project's folder, which its <c>res://</c> paths are under.</summary>
    public string Folder { get; }

    /// <summary>The Godot version it runs on, where known.</summary>
    public string? Version { get; }

    /// <summary>Whether Godot runs it headless (<c>--headless</c>), drawing nothing.</summary>
    public bool Headless { get; }

    /// <summary>Whether nobody sees the Passenger that started it, which runs every game headless (<see cref="LaunchPlan.GodotUnseen"/>).</summary>
    public bool Unseen { get; }

    /// <summary>Whether it is a game of GDScript alone (<see cref="LaunchPlan.ScriptsAlone"/>), which runs no .NET code.</summary>
    public bool ScriptsAlone { get; }

    public LiveAppState State { get; private set; } = LiveAppState.Waiting;

    /// <summary>Whether Passenger has taken the game's connection, which the window's thread then hears of (<see cref="State"/>
    /// becoming <see cref="LiveAppState.Connected"/>); read on any thread.</summary>
    public bool ConnectionTaken => taken;

    public string? Problem => null;

    public int? ProcessId { get; private set; }

    /// <summary>The game, holding the keys in Passenger's Game window, heard the keys that hand them back to Passenger.</summary>
    public event EventHandler? KeysAsked;

    /// <summary>"Godot 4.7.2".</summary>
    public string Framework => Version is { } version ? $"Godot {version}" : "Godot";

    /// <summary>Its performance monitors as it last sent them, or null before.</summary>
    public GodotMonitors? Monitors { get; private set; }

    /// <summary>The ids of the custom monitors the game last said it has, whose values end each monitors message.</summary>
    private IReadOnlyList<string> customMonitors = [];

    /// <summary>The Debug session the game runs under, while one does: its stops pause the game's answers.</summary>
    public Debugging.DebugSession? Debugged
    {
        get;
        set
        {
            if (field != value)
            {
                if (field is not null)
                {
                    field.StateChanged -= CountStop;
                }
                field = value;
                if (value is not null)
                {
                    value.StateChanged += CountStop;
                }
                DebuggedChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>How many times the game's Debug session has stopped it: a question sent before a stop is said stopped at
    /// once (<see cref="Answer"/>), never waited out.</summary>
    private int stops;

    /// <summary>Ends at the game's next stop under the debugger.</summary>
    private TaskCompletionSource nextStop = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void CountStop(object? sender, EventArgs e)
    {
        if (sender is Debugging.DebugSession { State: Debugging.DebugState.Paused })
        {
            stops++;
            TaskCompletionSource came = nextStop;
            nextStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            came.TrySetResult();
        }
    }

    /// <summary>Why a game held at its start for the debugger was let go to run on without it, once it was.</summary>
    public string? Undebugged
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                DebuggedChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The game's Debug session was given, or another, or it was let go undebugged (<see cref="Undebugged"/>).</summary>
    public event EventHandler? DebuggedChanged;

    public event EventHandler? StateChanged;

    /// <summary>Its count of nodes changed since its monitors last said it, so its tree did.</summary>
    public event EventHandler? TreeChanged;

    /// <summary>Its monitors came, once a second.</summary>
    public event EventHandler? MonitorsChanged;

    /// <summary>An error or warning it raised.</summary>
    public event EventHandler<GodotError>? Raised;

    /// <summary>The game said its process, as it does first of all, before its .NET runtime starts.</summary>
    public event EventHandler<int>? ProcessFound;

    /// <summary>The id of the context a game on macOS's embedded display shares the layer it draws to through, once told; null before.</summary>
    public uint? EmbeddedContext { get; private set; }

    /// <summary>A game on the embedded display told its context's id (<see cref="EmbeddedContext"/>).</summary>
    public event EventHandler? EmbeddedContextTold;

    /// <summary>A game on the embedded display asked for a mouse mode: Godot's DisplayServer.MouseMode, 0 visible, 1 hidden,
    /// 2 captured, 3 confined, 4 confined and hidden.</summary>
    public event EventHandler<int>? MouseModeAsked;

    /// <summary>Tells a game on the embedded display what the program it is shown in gives it (<see cref="GodotWords.EmbedEvent"/>
    /// and the rest), answered by nothing.</summary>
    public void Embed(string what, object? value) => Send(what, value);

    /// <summary>The scene Godot's editor edits, for a relayed game: what its live editing is set back to after Passenger's.</summary>
    public string EditorScene { get; set; } = string.Empty;

    /// <summary>A <c>res://</c> path as a path on this machine, under the project's folder; another path as it is.</summary>
    public string PathOf(string path) => path.StartsWith("res://", StringComparison.Ordinal)
        ? Path.GetFullPath(Path.Combine(Folder, path["res://".Length..]))
        : path;

    private async Task ServeAsync()
    {
        try
        {
            using TcpClient client = await listener!.AcceptTcpClientAsync(ending.Token);
            listener.Stop();
            client.NoDelay = true;
            stream = client.GetStream();
            taken = true;
            UiThread.Post(Connected);
            byte[] head = new byte[4];
            while (!ending.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(head, ending.Token);
                int length = BinaryPrimitives.ReadInt32LittleEndian(head);
                if (length is < 0 or > GodotMessage.Largest)
                {
                    break;
                }
                byte[] body = new byte[length];
                await stream.ReadExactlyAsync(body, ending.Token);
                Heard(body, null);
            }
        }
        catch (Exception gone) when (gone is IOException or SocketException or OperationCanceledException or ObjectDisposedException or EndOfStreamException)
        {
            // The game ended, or Passenger let go of it: either way it is over.
        }
        finally
        {
            UiThread.Post(Ended);
        }
    }

    /// <summary>A message's bytes read, the objects a relay sent as ids put back as they were (<paramref name="relayed"/>), and
    /// handed to the window's thread; a profile's frames kept as they come.</summary>
    private void Heard(byte[] body, Func<GodotMessage, GodotMessage>? relayed)
    {
        Interlocked.Exchange(ref lastHeard, System.Diagnostics.Stopwatch.GetTimestamp());
        GodotMessage message;
        try
        {
            message = GodotMessage.Read(body);
        }
        catch (GodotVariantException unread)
        {
            IdeErrors.Write($"Godot: a message of {Name} could not be read: {unread.Message}");
            return;
        }
        message = relayed?.Invoke(message) ?? message;
        // Many a second while profiled, each kept as it comes, when it came: the window's thread never hears them.
        if (recording is { } profile && Recorded(profile, message, DateTime.UtcNow))
        {
            return;
        }
        UiThread.Post(() => Heard(message));
    }

    private void Connected()
    {
        if (disposed)
        {
            return;
        }
        State = LiveAppState.Connected;
        // A relayed game's breakpoints and error stops are its editor's own.
        if (!ScriptsDebugged && !Relayed)
        {
            // A GDScript breakpoint or error would stop the game waiting for a debugger that shows no GDScript stop.
            Send(GodotWords.SkipBreakpoints, true);
            Send(GodotWords.IgnoreErrorBreaks, true);
        }
        if (recording is { IsStopped: false })
        {
            Profiling(true);
        }
        if (AudioMuted)
        {
            Send(GodotWords.MuteAudio, true);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records Godot's own frame profile into <paramref name="profile"/> beside the samples until it stops: its
    /// servers' and script functions' times (calls into Godot's own API among them) and, where the game draws, its
    /// renderer's stages. A game yet to connect begins as it does.</summary>
    public void Profile(Passenger.Profiler.GameRecording profile)
    {
        UiThread.Check();
        recording = profile;
        profile.Stopped += (_, _) => UiThread.Post(() =>
        {
            if (recording == profile)
            {
                recording = null;
                if (State == LiveAppState.Connected && !disposed)
                {
                    Profiling(false);
                }
            }
        });
        if (State == LiveAppState.Connected)
        {
            Profiling(true);
        }
    }

    /// <summary>Turns Godot's own profilers on or off; its renderer's only where the game draws.</summary>
    private void Profiling(bool on)
    {
        Send(GodotWords.ProfileServers, on ? [true, new List<object?> { (long)FrameFunctions, true }] : [false]);
        if (!Headless)
        {
            Send(GodotWords.ProfileVisual, on);
        }
    }

    /// <summary>Takes a message of Godot's profilers into the profile; false for any other message.</summary>
    private static bool Recorded(Passenger.Profiler.GameRecording profile, GodotMessage message, DateTime came)
    {
        switch (message.Name)
        {
            case GodotWords.FunctionSignature:
                if (GodotProfile.Signature(message.Data) is ({ } number, { } signature))
                {
                    profile.Signature(number, signature);
                }
                return true;
            case GodotWords.ServersFrame:
                if (GodotProfile.Frame(message.Data) is { } frame)
                {
                    profile.Took(frame, came);
                }
                return true;
            case GodotWords.VisualFrame:
                profile.Rendered(GodotProfile.Rendered(message.Data));
                return true;
            case GodotWords.Hardware:
                profile.Hardware(GodotProfile.Gpu(message.Data));
                return true;
            case GodotWords.ServersTotal:
                // The whole run's sums, which its frames hold already.
                return true;
            default:
                return false;
        }
    }

    private void Ended()
    {
        if (State == LiveAppState.Ended)
        {
            return;
        }
        State = LiveAppState.Ended;
        Picking = GodotPicking.Off;
        Scripts?.GameEnded();
        treeAsked?.TrySetException(new LiveAppException($"{Name} has ended"));
        foreach (TaskCompletionSource<GodotObjectState> asked in objectsAsked.Values)
        {
            asked.TrySetException(new LiveAppException($"{Name} has ended"));
        }
        objectsAsked.Clear();
        foreach (TaskCompletionSource<(int, int, string)> asked in picturesAsked.Values)
        {
            asked.TrySetException(new LiveAppException($"{Name} has ended"));
        }
        foreach (TaskCompletionSource<List<object?>> asked in agentAsked.Values)
        {
            asked.TrySetException(new LiveAppException($"{Name} has ended"));
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Heard(GodotMessage message)
    {
        if (disposed)
        {
            return;
        }
        switch (message.Name)
        {
            case GodotWords.ProcessId when message.Data is [long id]:
                ProcessId = (int)id;
                ProcessFound?.Invoke(this, ProcessId.Value);
                break;
            case GodotWords.SceneTree:
                GodotNode? told = GodotNode.Tree(message.Data);
                lastTree = told ?? lastTree;
                treeAsked?.TrySetResult((told, moves));
                break;
            case GodotWords.InspectObjects:
                foreach (GodotObjectState state in GodotObjectState.Read(message.Data))
                {
                    inspected[state.Id] = state;
                    Tuning.Read(state);
                    if (objectsAsked.Remove(state.Id, out TaskCompletionSource<GodotObjectState>? asked))
                    {
                        asked.TrySetResult(state);
                    }
                }
                break;
            case GodotWords.Error when GodotError.Read(message.Data) is { } error && error.Function == GodotWords.ScreenshotFunction && picturesAsked.Count > 0:
                foreach (TaskCompletionSource<(int, int, string)> asked in picturesAsked.Values)
                {
                    asked.TrySetException(new LiveAppException($"Godot took no picture of {Name}: {error.Said}"));
                }
                break;
            case GodotWords.Error when GodotError.Read(message.Data) is { } error && GodotAgent.Raised(error):
                // Passenger's own helper failed: the asks waiting on it say so, and the game's problems stay its own.
                foreach (TaskCompletionSource<List<object?>> asked in agentAsked.Values.Where(_ => !error.Warning))
                {
                    asked.TrySetException(new LiveAppException($"Passenger's helper in {Name} failed: {error.Said}"));
                }
                break;
            case GodotWords.Error when GodotError.Read(message.Data) is { } error && Scripts?.Claims(error) == true:
                break;
            case GodotWords.Error when GodotError.Read(message.Data) is { } error:
                lastRaised = (System.Diagnostics.Stopwatch.GetTimestamp(), error.Said);
                Raised?.Invoke(this, error);
                break;
            case GodotWords.ObjectsSelected when GodotPicks.Picked(message.Data) is { } picked:
                inspected[picked.Id] = picked;
                if (outlinesUnanswered > 0)
                {
                    outlinesUnanswered--;
                }
                else if (Picking != GodotPicking.Off)
                {
                    // One node a pick, as Select Element picks: the game takes its own input again.
                    Picking = GodotPicking.Off;
                    foreach (string typed in GodotPicks.TypeMessages(Version))
                    {
                        Send(typed, (long)GodotPicking.Off);
                    }
                    Pick(picked);
                }
                break;
            case GodotWords.NothingSelected when outlinesUnanswered > 0:
                outlinesUnanswered--;
                break;
            case GodotWords.NothingSelected when Picking != GodotPicking.Off:
                Pick(null);
                break;
            case GodotWords.EmbeddedContext when message.Data is [long shared]:
                EmbeddedContext = (uint)shared;
                EmbeddedContextTold?.Invoke(this, EventArgs.Empty);
                break;
            case GodotWords.EmbeddedMouseMode when message.Data is [long mode]:
                MouseModeAsked?.Invoke(this, (int)mode);
                break;
            case GodotWords.Screenshotted when message.Data is [long id, long width, long height, string path]:
                picturesAsked.GetValueOrDefault(id)?.TrySetResult(((int)width, (int)height, path));
                break;
            case GodotAgent.Said when message.Data is List<object?> { Count: >= 2 } said && said[0] is long ask:
                agentAsked.GetValueOrDefault(ask)?.TrySetResult(said);
                break;
            case GodotAgent.Gone:
                // The game's count of nodes may never show it: come and gone within one beat of its monitors, or a node made in its place.
                beside = null;
                Freed();
                break;
            case GodotAgent.KeysAsked:
                KeysAsked?.Invoke(this, EventArgs.Empty);
                break;
            case GodotWords.MonitorNames:
                customMonitors = GodotMonitors.Names(message.Data);
                break;
            case GodotWords.Monitors when GodotMonitors.Read(message.Data, customMonitors) is { } monitors:
                bool moved = Monitors is { } was && was.Nodes != monitors.Nodes;
                Monitors = monitors;
                if (moved)
                {
                    // Counted before the beat's own asks, so they ask for the tree again rather than go beside a scene or helper gone.
                    moves++;
                }
                MonitorsChanged?.Invoke(this, EventArgs.Empty);
                if (moved)
                {
                    TreeChanged?.Invoke(this, EventArgs.Empty);
                }
                break;
            case GodotWords.DebugEnter or GodotWords.DebugExit when Relayed:
                // Its editor's script debugger shows its stops, and lets it go on.
                stoppedInEditor = message.Name == GodotWords.DebugEnter;
                break;
            case GodotWords.StackDump or GodotWords.StackFrameVars or GodotWords.StackFrameVar or GodotWords.EvaluationReturn when Relayed:
                break;
            case GodotWords.DebugEnter when Scripts is { } scripts:
                scripts.Heard(message);
                break;
            case GodotWords.DebugEnter when ScriptsDebugged && GodotBreak.Read(message.Data) is { } stop:
                held = stop;
                break;
            case GodotWords.DebugEnter:
                // A script's stop Passenger cannot show: the game goes on.
                IdeErrors.Observe(SendToAsync(GodotWords.Continue, GodotBreak.Read(message.Data)?.Thread ?? GodotMessage.MainThread));
                break;
            case GodotWords.DebugExit or GodotWords.StackDump or GodotWords.StackFrameVars or GodotWords.StackFrameVar or GodotWords.EvaluationReturn:
                Scripts?.Heard(message);
                break;
        }
    }

    private void Send(string name, params object?[] data) => IdeErrors.Observe(SendAsync(name, data));

    private Task SendAsync(string name, params object?[] data) => SendToAsync(name, GodotMessage.MainThread, data);

    /// <summary>The agent's call this flow serves, while one asks the game (<see cref="Within"/>): its every message and
    /// question given up at the call's limit, saying what it waited on.</summary>
    private static readonly AsyncLocal<AgentAsking?> Asking = new();

    /// <summary>An agent's call asking a game, given up whole at <see cref="Limit"/>: however the game, its connection or
    /// Passenger's own writing to it stall, the call fails within it saying what it waited on.</summary>
    private sealed class AgentAsking(TimeSpan limit) : IDisposable
    {
        private readonly CancellationTokenSource giveUp = new(limit);

        public TimeSpan Limit { get; } = limit;

        public CancellationToken GiveUp => giveUp.Token;

        public void Dispose()
        {
            Asking.Value = null;
            giveUp.Dispose();
        }
    }

    /// <summary>A scope inside an agent's call that asks the game already, which keeps the call's own limit.</summary>
    private sealed class Inside : IDisposable
    {
        public static readonly Inside Scope = new();

        public void Dispose()
        {
        }
    }

    /// <summary>Gives every message and question this flow sends a game from here a limit in all (an agent's call's), unless
    /// the flow is inside one already; ended as the scope is disposed.</summary>
    public static IDisposable Within(TimeSpan limit)
    {
        if (Asking.Value is not null)
        {
            return Inside.Scope;
        }
        AgentAsking asking = new(limit);
        Asking.Value = asking;
        return asking;
    }

    /// <summary>What a message or question of an agent's call says once the call's limit is up, waiting on
    /// <paramref name="what"/>.</summary>
    private string GivenUp(AgentAsking asking, string what) => $"{Name} did not answer within the {asking.Limit.TotalSeconds:0} s an agent's call on it has, waiting {what}";

    /// <summary>Sends a message to one of the game's threads: a thread stopped in a script reads only its own. Inside an
    /// agent's call (<see cref="Within"/>) it waits for its turn and its write no longer than the call may; a write given up
    /// part sent ends the connection, which no later message could be read on.</summary>
    public async Task SendToAsync(string name, long thread, params object?[] data)
    {
        if ((stream is null && relay is null) || !taken || disposed)
        {
            throw new LiveAppException($"{Name} is not connected");
        }
        byte[] frame = GodotMessage.Frame(name, thread, data);
        AgentAsking? asking = Asking.Value;
        CancellationToken giveUp = asking?.GiveUp ?? CancellationToken.None;
        if (!writing.Wait(0))
        {
            Mcp.AgentCall.Say($"Waiting to tell {Name} {name}, as Passenger tells it something else");
            try
            {
                await writing.WaitAsync(giveUp);
            }
            catch (OperationCanceledException) when (giveUp.IsCancellationRequested)
            {
                throw new LiveAppException(GivenUp(asking!, $"to tell it {name} behind another message that its connection had not taken"));
            }
        }
        using CancellationTokenSource writes = CancellationTokenSource.CreateLinkedTokenSource(ending.Token, giveUp);
        try
        {
            Task written = relay is { } through ? through(frame[4..], writes.Token) : stream!.WriteAsync(frame, writes.Token).AsTask();
            if (!written.IsCompleted)
            {
                Mcp.AgentCall.Say($"Telling {Name} {name}, which its connection has not yet taken");
            }
            await written;
        }
        catch (OperationCanceledException) when (giveUp.IsCancellationRequested)
        {
            stream?.Dispose();
            throw new LiveAppException(GivenUp(asking!, $"for its connection to take {name}, which it would not: Passenger let go of the connection"));
        }
        catch (Exception lost) when (lost is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            throw new LiveAppException($"{Name} could not be told: {lost.Message}");
        }
        finally
        {
            writing.Release();
        }
    }

    /// <summary>How long a question of the game's waits for its answer.</summary>
    private static TimeSpan AnswerLimit => LiveApp.AnswerLimit;

    /// <summary>The game's scene tree now, its root Window first.</summary>
    public async Task<GodotNode> SceneTreeAsync() => (await TreeOfBringingAsync()).Tree;

    /// <summary>Whether the game has been seen with its first scene open, after which it is not looked for again.</summary>
    private bool opened;

    /// <summary>Waits for the game to open its first scene, as it does a moment after it connects: its tree read again until a
    /// scene no autoload adds is open under its root, within <paramref name="limit"/>. A <see cref="LiveAppException"/> says
    /// it opened none in that time, or that it was paused meanwhile.</summary>
    public async Task FirstSceneAsync(TimeSpan limit, CancellationToken cancel)
    {
        UiThread.Check();
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        HashSet<string>? autoloads = null;
        while (!opened)
        {
            if (Debugged is { State: Debugging.DebugState.Paused })
            {
                throw new LiveAppException($"{Name} is paused in the debugger before it opened its first scene, and its scene tree answers nothing until it runs: debug_continue");
            }
            GodotNode tree = await SceneTreeAsync();
            autoloads ??= await BackgroundWork.Run(() => Autoloads(Folder));
            opened = tree.Children.Any(c => c.SceneFile.Length > 0 && !autoloads.Contains(c.Name));
            if (opened)
            {
                return;
            }
            if (System.Diagnostics.Stopwatch.GetElapsedTime(began) >= limit)
            {
                throw new LiveAppException($"{Name} has opened no scene in the {limit.TotalSeconds:0} s since it connected, and Passenger's helper comes into the game beside one");
            }
            Mcp.AgentCall.Say($"Waiting for {Name} to open its first scene");
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancel);
        }
    }

    /// <summary>The names of the autoloads a project's project.godot gives, the nodes Godot adds under /root before its
    /// first scene; none where it cannot be read.</summary>
    private static HashSet<string> Autoloads(string folder)
    {
        try
        {
            GodotFile file = GodotFile.Parse(File.ReadAllText(Path.Combine(folder, GodotProject.FileName)));
            return [.. file.Sections.Where(s => s.Tag == "autoload").SelectMany(s => s.Properties).Select(p => p.Key)];
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The game's scene tree now, how many times the helper had been made when it was asked (<see cref="brought"/>),
    /// which a question shared with an earlier one dates from that one, and the changes of the game's count of nodes heard
    /// when it came (<see cref="moves"/>).</summary>
    private async Task<(GodotNode Tree, int Brought, int Moves)> TreeOfBringingAsync()
    {
        UiThread.Check();
        TaskCompletionSource<(GodotNode? Tree, int Moves)> asked = treeAsked is { Task.IsCompleted: false } pending ? pending
            : treeAsked = new TaskCompletionSource<(GodotNode? Tree, int Moves)>(brought, TaskCreationOptions.RunContinuationsAsynchronously);
        int sent = stops;
        await AskingAsync(asked.Task, GodotWords.RequestSceneTree);
        (GodotNode? told, int moved) = await Answer(asked.Task, sent, "with its scene tree");
        return (told ?? throw new LiveAppException($"{Name} sent a scene tree Passenger could not read"), (int)asked.Task.AsyncState!, moved);
    }

    /// <summary>Sends a question whose answer is waited for after: where it cannot be sent, no one waits for that answer, and
    /// the game's end that fails it is heard by no one.</summary>
    private async Task AskingAsync(Task answer, string name, params object?[] data)
    {
        try
        {
            await SendAsync(name, data);
        }
        catch (LiveAppException)
        {
            UnawaitedWork.Dismiss(answer);
            throw;
        }
    }

    /// <summary>A node's (or any object's) properties now.</summary>
    public async Task<GodotObjectState> InspectAsync(ulong id)
    {
        UiThread.Check();
        if (!objectsAsked.TryGetValue(id, out TaskCompletionSource<GodotObjectState>? asked))
        {
            asked = objectsAsked[id] = new TaskCompletionSource<GodotObjectState>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        int sent = stops;
        await AskingAsync(asked.Task, GodotWords.InspectObjects, new List<object?> { (long)id }, false);
        return await Answer(asked.Task, sent, "with the node's properties");
    }

    /// <summary>Sets a property of an object to a value, which the game takes as it next reads its messages.</summary>
    public Task SetPropertyAsync(ulong id, string property, object? value) => SendAsync(GodotWords.SetObjectProperty, (long)id, property, value);

    /// <summary>The answer to a question of the game's, <paramref name="what"/> naming the question its refusal says went
    /// unanswered; the refusal says too whether the game went on sending its other messages meanwhile (a question lost, else a
    /// game or a connection held still), and the last error it raised meanwhile. A game the debugger stopped since the
    /// question was sent (<paramref name="stopsWhenSent"/>: a breakpoint in the handler a click ran) is said so at once.</summary>
    private async Task<T> Answer<T>(Task<T> asked, int stopsWhenSent, string? what = null)
    {
        long since = System.Diagnostics.Stopwatch.GetTimestamp();
        TimeSpan limit = Waits.Of(AnswerLimit);
        AgentAsking? asking = Asking.Value;
        Mcp.AgentCall.Say($"Waiting for {Name} to answer{(what is null ? string.Empty : $" {what}")}");
        Task stopped = stops != stopsWhenSent ? Task.CompletedTask : nextStop.Task;
        try
        {
            Task<T> answered = asked.WaitAsync(limit, asking?.GiveUp ?? CancellationToken.None);
            if (await Task.WhenAny(answered, stopped) != answered)
            {
                UnawaitedWork.Dismiss(answered);
                UnawaitedWork.Dismiss(asked);
                throw new LiveAppException($"{Name} stopped under the debugger before it answered{(what is null ? string.Empty : $" {what}")}, as it ran what it was asked: "
                    + "debug_state says where, and it answers once it runs on");
            }
            return await answered;
        }
        catch (OperationCanceledException) when (asking is { GiveUp.IsCancellationRequested: true })
        {
            // Given up on, the question fails later only as the game ends, which no one is to hear.
            UnawaitedWork.Dismiss(asked);
            throw new LiveAppException(GivenUp(asking, $"for it to answer{(what is null ? string.Empty : $" {what}")}"));
        }
        catch (TimeoutException)
        {
            UnawaitedWork.Dismiss(asked);
            Unanswered++;
            if (Debugged is { State: Debugging.DebugState.Paused })
            {
                throw new LiveAppException($"{Name} is paused, and answers once it runs");
            }
            if (stoppedInEditor)
            {
                throw new LiveAppException($"{Name} is stopped at a GDScript breakpoint in Godot's editor, and answers once it goes on there");
            }
            long heard = Interlocked.Read(ref lastHeard);
            string meanwhile = heard > since
                ? $", though it went on sending its other messages, the last {System.Diagnostics.Stopwatch.GetElapsedTime(heard).TotalSeconds:0.#} s ago"
                : ", and sent nothing else meanwhile";
            string raised = lastRaised is { } error && error.At > since ? $"; the last error it raised meanwhile: {error.Said}" : string.Empty;
            throw new LiveAppException($"{Name} did not answer{(what is null ? string.Empty : $" {what}")} in {limit.TotalSeconds:0} s{meanwhile}{raised}");
        }
    }

    public async Task<AppTree> TreeAsync()
    {
        GodotNode root = await SceneTreeAsync();
        return new AppTree([ElementOf(root)]);
    }

    /// <summary>A node as the tree shows it, a script's path that names its type ("res://Main.cs") by its file's name;
    /// Passenger's own helper left out.</summary>
    private static Element ElementOf(GodotNode node) =>
        new((long)node.Id, node.Type.StartsWith("res://", StringComparison.Ordinal) ? Path.GetFileName(node.Type) : node.Type, node.Name, [], false,
            node.SceneFile.Length > 0, node.Visible, [.. node.Children.Where(c => !GodotAgent.Is(c)).Select(ElementOf)]);

    /// <summary>Whether the game draws on Passenger's own X server (<see cref="VirtualDisplays"/>), held while it runs.</summary>
    private bool drawsOnServer;

    /// <summary>Why a game Godot runs headless has no picture.</summary>
    public string HeadlessPicture => Unseen && OperatingSystem.IsLinux() && PassengerApp.ForAgent
        ? $"{Name} runs headless, and Godot draws nothing for a headless game, so there is no picture of it: an agent's own Passenger draws its games on Linux "
            + (Programs.OnPath(GodotExecutables.VirtualDisplay) is null
                ? "on an X server of its own that shows nowhere, Xvfb, which is not installed here: install it (the xvfb package), then run the game again"
                : "on an X server of its own that shows nowhere, Xvfb, which did not start here (Passenger's log says why)")
        : Unseen
        ? $"{Name} runs headless, as every Godot game a Passenger nobody sees starts does (an agent's own, a test's, a screenshot's), and Godot draws "
            + "nothing for a headless game, so there is no picture of it: a person's Passenger runs it with a window"
        : $"{Name} runs headless (--headless among its arguments), and Godot draws nothing for a headless game, "
            + "so there is no picture of it: run it without --headless to take one";

    /// <summary>A PNG of the game's window as Godot draws it (its root viewport), and its size in pixels, the file Godot
    /// wrote it to deleted once read; refused for a headless game, which draws nothing.</summary>
    public async Task<(byte[] Png, int Width, int Height)> PictureAsync()
    {
        UiThread.Check();
        if (Headless)
        {
            throw new LiveAppException(HeadlessPicture);
        }
        long ask = ++asks;
        TaskCompletionSource<(int Width, int Height, string Path)> asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        picturesAsked[ask] = asked;
        try
        {
            int sent = stops;
            await SendAsync(GodotWords.Screenshot, ask);
            (int width, int height, string path) = await Answer(asked.Task, sent, "with its picture");
            try
            {
                return (await File.ReadAllBytesAsync(path), width, height);
            }
            catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
            {
                throw new LiveAppException($"{Name}'s picture could not be read from {path}: {unread.Message}");
            }
            finally
            {
                Gone(path);
            }
        }
        finally
        {
            picturesAsked.Remove(ask);
        }

        static void Gone(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception kept) when (kept is IOException or UnauthorizedAccessException)
            {
                // Godot's own temporary folder keeps it.
            }
        }
    }

    /// <summary>Asks Passenger's helper in the game (<see cref="GodotAgent"/>) to run one of its calls, bringing it in first
    /// where the game's tree lacks it (again, once the game has freed it): through the live editing of a scene open once under
    /// the root, a node made beside it and its script set from <see cref="GodotAgent.Written"/>. What it answered after its ask
    /// and done; a <see cref="LiveAppException"/> saying why it did not do it.</summary>
    public Task<List<object?>> AskAsync(string call, params object?[] arguments) => AskAsync(call, arguments, null);

    /// <summary>Asks the helper as <see cref="AskAsync(string, object?[])"/> does, sending <paramref name="then"/>'s message
    /// once the call is sent, before its answer, which comes only once the game has taken that.</summary>
    private async Task<List<object?>> AskAsync(string call, object?[] arguments, Func<Task>? then)
    {
        UiThread.Check();
        (string Scene, int Moves)? named = beside is { } known && known.Moves == moves ? known : null;
        (GodotNode Tree, int Brought, int Moves)? asked = null;
        if (named is null || bringing is null)
        {
            asked = await TreeOfBringingAsync();
            GodotNode tree = asked.Value.Tree;
            List<GodotNode> all = [.. tree.All()];
            named = (tree.Children.Select(c => c.SceneFile).FirstOrDefault(f => f.Length > 0 && all.Count(n => n.SceneFile == f) == 1)
                ?? throw new LiveAppException($"{Name} has no scene open once under its root, beside which Passenger's helper comes into the game"), asked.Value.Moves);
        }
        (string scene, int namedAt) = named.Value;
        await SendAsync(GodotWords.LiveSetRoot, GodotAgent.PathOf("/root"), scene);
        await SendAsync(GodotWords.LiveNodePath, GodotAgent.PathOf($"../{GodotAgent.NodeName}"), GodotLiveEditIds.Agent);
        // Brought in only once the game knows the path its script is set through, or the script goes nowhere.
        if (asked is ({ } lacking, int since, _) && !lacking.Children.Any(GodotAgent.Is) && (bringing is null || (bringing.IsCompleted && since == brought)))
        {
            Freed();
            bringing = BringAsync();
        }
        if (bringing is { } coming)
        {
            try
            {
                await coming;
            }
            catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException)
            {
                bringing = null;
                throw new LiveAppException($"Passenger's helper could not be written for {Name}: {unwritten.Message}");
            }
        }
        long ask = ++asks;
        TaskCompletionSource<List<object?>> answer = new(call, TaskCreationOptions.RunContinuationsAsynchronously);
        agentAsked[ask] = answer;
        try
        {
            if (namedAt != moves)
            {
                CallsBesideMoved++;
            }
            int sent = stops;
            // Through the one call the helper guards every other with.
            await SendAsync(GodotWords.LiveNodeCall, GodotLiveEditIds.Agent, GodotAgent.Asked, call, ask, new List<object?>(arguments));
            if (then is not null)
            {
                await then();
            }
            await GiveBackLiveRootAsync();
            List<object?> said;
            try
            {
                said = await Answer(answer.Task, sent, $"Passenger's helper's {call}");
            }
            catch (LiveAppException)
            {
                beside = null;
                throw;
            }
            beside = (scene, namedAt);
            return said[1] is true ? said[2..]
                : throw new LiveAppException(said is [_, _, string why, ..] ? why : $"Passenger's helper in {Name} did not say why it could not");
        }
        finally
        {
            agentAsked.Remove(ask);
        }
    }

    /// <summary>The helper's calls still waiting for their answers failed at once, saying why: the helper said it left the
    /// game's tree, or the tree shows it gone with no other brought in since that tree was asked for, so the helper they were
    /// sent to answers none of them.</summary>
    private void Freed()
    {
        foreach (TaskCompletionSource<List<object?>> waiting in agentAsked.Values)
        {
            waiting.TrySetException(new LiveAppException($"{Name} freed Passenger's helper before it answered its {waiting.Task.AsyncState}: the next call brings it in again"));
        }
    }

    /// <summary>The questions of the game's that waited out the whole of an answer's limit, which a game that runs and a
    /// helper that is there never leave unanswered.</summary>
    public int Unanswered { get; private set; }

    /// <summary>The scene the helper last answered beside, and the changes of the game's count of nodes heard when the tree
    /// naming it came: while none has come since, the next ask goes there without asking for the tree, which a game of many
    /// nodes sends at a cost.</summary>
    private (string Scene, int Moves)? beside;

    /// <summary>The changes of the game's count of nodes heard so far, each a sign the helper or the scene open may be gone.</summary>
    private int moves;

    /// <summary>The helper's calls sent beside a scene the game's tree named before its count of nodes last changed: to a
    /// helper the game may have freed, or a scene no longer open, where the call goes unanswered.</summary>
    public int CallsBesideMoved { get; private set; }

    /// <summary>The values of scripts' members in the nodes that run them (or a script they run is based on), through
    /// Passenger's helper: for each script asked of, how many nodes run it and the members of the one chosen, else of the
    /// only one, as one message of the game's; a <see cref="LiveAppException"/> where the helper could not read them.</summary>
    public async Task<IReadOnlyList<GodotScriptRead>> ScriptValuesAsync(IReadOnlyList<GodotScriptAsk> asks)
    {
        List<object?> asked = [.. asks.Select(a => (object?)new List<object?> { a.Script, (long)a.Chosen, new List<object?>(a.Names) })];
        List<object?> said = await AskAsync("values", asked);
        return GodotScriptRead.Read(said, asks) ?? throw new LiveAppException($"Passenger's helper in {Name} answered with values Passenger could not read");
    }

    /// <summary>The object a node's property holds (a resource), by its id, class and file, read through Passenger's helper:
    /// the game sends a resource loaded from a file by its path alone. One sent by its id is that id.</summary>
    public async Task<(ulong Id, string Class, string Path)> HeldAsync(long element, string property)
    {
        UiThread.Check();
        if (InspectedOf(element)?.Properties.FirstOrDefault(p => p.Name == property)?.Value is GodotObjectId { } sent)
        {
            return Held(sent);
        }
        List<object?> said = await AskAsync("held", element, property);
        if (said is not [long id, string named, string path, ..])
        {
            throw new LiveAppException($"Passenger's helper in {Name} said no object for {property}");
        }
        heldObjects.Add((ulong)id);
        heldPaths[(ulong)id] = path;
        return ((ulong)id, named, path);
    }

    /// <summary>An object the game sent by its id (a node's property's, an array's item), as <see cref="HeldAsync"/> reads one:
    /// its id, its class where it was read, and no path, its own properties naming its file.</summary>
    public (ulong Id, string Class, string Path) Held(GodotObjectId sent)
    {
        UiThread.Check();
        heldObjects.Add(sent.Id);
        return (sent.Id, inspected.GetValueOrDefault(sent.Id)?.Class ?? string.Empty, string.Empty);
    }

    /// <summary>The objects nodes' properties hold that were read by their ids (<see cref="HeldAsync"/>), whose values set are
    /// recorded in <see cref="Tuning"/> as the resource's own, by its path.</summary>
    private readonly HashSet<ulong> heldObjects = [];

    /// <summary>The paths Passenger's helper gave the resources it read by their ids, for one whose own properties name none.</summary>
    private readonly Dictionary<ulong, string> heldPaths = [];

    /// <summary>Has the game make Passenger's helper beside the scene its live editing names, its script the file written.</summary>
    private async Task BringAsync()
    {
        string script = await BackgroundWork.Run(GodotAgent.Written);
        try
        {
            await SendAsync(GodotWords.LiveCreateNode, GodotAgent.PathOf(".."), "Node", GodotAgent.NodeName);
            // Godot's own paths part their folders with '/' on every system.
            await SendAsync(GodotWords.LiveNodePropertyResource, GodotLiveEditIds.Agent, "script", script.Replace('\\', '/'));
        }
        finally
        {
            brought++;
        }
    }

    /// <summary>Whether Godot's runtime node selection is set up in the game, which takes its setup once.</summary>
    private bool selectionSetUp;

    /// <summary>The outlines asked for that the game has yet to answer, each answered as a pick would be.</summary>
    private int outlinesUnanswered;

    /// <summary>What a click in the game's window picks now, through Godot's runtime node selection: off while the game takes
    /// its own input, as it does again once a node is picked.</summary>
    public GodotPicking Picking { get; private set; }

    /// <summary>The picks heard so far: an agent's click compares it before and after.</summary>
    public int Picks { get; private set; }

    /// <summary>The node the last pick found, as Godot sent it; null for none, or before a pick.</summary>
    public GodotObjectState? LastPick { get; private set; }

    /// <summary>The ids of the game's custom monitors asked of Passenger's helper, where the game said them before Passenger
    /// heard it (a game Godot's Play started, relayed from its next message): its monitors' values name them from then.</summary>
    public async Task NameMonitorsAsync()
    {
        if (customMonitors.Count > 0)
        {
            return;
        }
        try
        {
            if (await AskAsync("monitors") is [List<object?> names] && names.Count > 0 && customMonitors.Count == 0)
            {
                customMonitors = [.. names.Select(name => GodotVariant.Shown(name))];
            }
        }
        catch (LiveAppException)
        {
            // A game the helper cannot answer for charts its engine's monitors alone.
        }
    }

    /// <summary>What the game's 3D physics space holds under the last press Passenger's helper gave, where Godot's node
    /// selection casts its ray: a body's or area's words ("StaticBody3D Crate"), "" for none, null where no press of the
    /// helper's or no camera is known.</summary>
    public async Task<string?> ShapeAtPressAsync() => await AskAsync("shapes") is [bool known, string held] && known ? held : null;

    /// <summary>What a 3D pick that found nothing says of the point, from the helper's own ray (<see cref="ShapeAtPressAsync"/>),
    /// in a game drawing nothing, whose pick takes only nodes with a collision shape: null where it cannot say.</summary>
    public async Task<string?> MissedAsync()
    {
        if (!Headless)
        {
            return null;
        }
        try
        {
            return await ShapeAtPressAsync() switch
            {
                "" => "no collision shape at this point: a headless game picks only nodes with one",
                { } held => $"{held}'s shape is at this point, which Godot's pick did not take",
                null => null,
            };
        }
        catch (LiveAppException)
        {
            return null;
        }
    }

    /// <summary>A click in the game picked a node (as Godot sent it), or none Godot could pick there (null), while it picked.</summary>
    public event EventHandler<GodotObjectState?>? Picked;

    private void Pick(GodotObjectState? picked)
    {
        Picks++;
        LastPick = picked;
        Picked?.Invoke(this, picked);
    }

    /// <summary>Has a click in the game's window pick a node, among its 2D nodes and controls or its 3D nodes, or takes
    /// picking off; Godot outlines the node picked in <paramref name="outline"/>'s colour (red, green, blue from 0 to 1). A
    /// node locked in Godot's editor is passed over, and one inside a group made there picks the group.</summary>
    public async Task PickAsync(GodotPicking picking, (double Red, double Green, double Blue) outline)
    {
        UiThread.Check();
        if (EditorHolds is { } held)
        {
            throw new LiveAppException(held);
        }
        await SetUpSelectionAsync(outline);
        foreach (string typed in GodotPicks.TypeMessages(Version))
        {
            await SendAsync(typed, (long)picking);
        }
        Picking = picking;
    }

    /// <summary>Outlines a node in the game's window as Godot outlines one picked, or takes the outline away (null).</summary>
    public async Task OutlineAsync(ulong? node, (double Red, double Green, double Blue) outline)
    {
        UiThread.Check();
        // A relayed game's selection outline is its editor's.
        if (Relayed)
        {
            return;
        }
        await SetUpSelectionAsync(outline);
        if (node is ulong id)
        {
            outlinesUnanswered++;
            await SendAsync(GodotWords.InspectObjects, new List<object?> { (long)id }, true);
        }
        else
        {
            await SendAsync(GodotWords.ClearSelection);
        }
    }

    private async Task SetUpSelectionAsync((double Red, double Green, double Blue) outline)
    {
        if (selectionSetUp)
        {
            return;
        }
        selectionSetUp = true;
        await SendAsync(GodotWords.SelectSetup, GodotPicks.Settings(outline.Red, outline.Green, outline.Blue));
        // Godot's selection begins picking in 2D (before 4.8), which would take the game's clicks before Select Element asked it to.
        foreach (string typed in GodotPicks.TypeMessages(Version))
        {
            await SendAsync(typed, (long)GodotPicking.Off);
        }
        await SendAsync(GodotWords.SelectAvoidLocked, true);
        await SendAsync(GodotWords.SelectPreferGroup, true);
    }

    /// <summary>Whether the game's audio is muted by Godot's debug mute: it plays on, silent. One muted before it connects is
    /// muted as it does.</summary>
    public bool AudioMuted { get; private set; }

    /// <summary>The game's audio was muted or let play.</summary>
    public event EventHandler? AudioChanged;

    /// <summary>Has a game yet to connect start with its audio muted, which it is told as it connects.</summary>
    public void StartMuted()
    {
        UiThread.Check();
        AudioMuted = true;
        AudioChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Mutes the game's audio, or lets it play, done once the game is told; one yet to connect is told as it does.
    /// Godot answers nothing: a refusal would come as an error the game raises.</summary>
    public async Task MuteAudioAsync(bool muted)
    {
        UiThread.Check();
        if (AudioMuted != muted)
        {
            AudioMuted = muted;
            AudioChanged?.Invoke(this, EventArgs.Empty);
        }
        if (State == LiveAppState.Connected)
        {
            await SendAsync(GodotWords.MuteAudio, muted);
        }
    }

    /// <summary>Whether Passenger holds the game's loop (<see cref="HoldLoopAsync"/>): its nodes process nothing, its time
    /// stands, and it takes no input, while it answers its debugger.</summary>
    public bool LoopHeld { get; private set; }

    /// <summary>The frames the game has run, as Passenger's helper last counted them: null until it counts, from the first
    /// hold on the game's loop.</summary>
    public long? Frame { get; private set; }

    /// <summary>The game's loop was held or let go, or its frames counted.</summary>
    public event EventHandler? LoopChanged;

    /// <summary>Holds the game's loop at the frame it is at, or lets it go on, the debugger not breaking in, and says the
    /// frames it has run then (null where Passenger's helper could not come into the game to count them, which a hold does
    /// not wait on).</summary>
    public async Task<long?> HoldLoopAsync(bool held)
    {
        UiThread.Check();
        if (EditorHolds is { } holds)
        {
            throw new LiveAppException(holds);
        }
        if (held && Frame is null)
        {
            // Counted from before the hold, so the count is of every frame the game has run.
            await CountedAsync();
        }
        await SendAsync(GodotWords.SuspendChanged, held);
        long? frame = await CountedAsync();
        LoopHeld = held;
        Frame = frame ?? Frame;
        LoopChanged?.Invoke(this, EventArgs.Empty);
        return frame;
    }

    /// <summary>The frames the game has run, counted again and shown; null where the helper could not count them.</summary>
    public async Task<long?> TryCountAsync()
    {
        long? frame = await CountedAsync();
        if (frame is not null)
        {
            Frame = frame;
            LoopChanged?.Invoke(this, EventArgs.Empty);
        }
        return frame;
    }

    /// <summary>The frames the game has run, Passenger's helper brought in to count them from now where it is not there; null
    /// where it could not come, or said no count.</summary>
    private async Task<long?> CountedAsync()
    {
        try
        {
            return (await AskAsync("count")) is [long frame, ..] ? frame : null;
        }
        catch (LiveAppException)
        {
            return null;
        }
    }

    /// <summary>Runs one frame of the game's loop held, the loop held again after it, and says the frames run then; a game
    /// that draws nothing (headless) has the helper end the frame, which Godot ends at its drawing.</summary>
    public async Task<long> StepAsync()
    {
        UiThread.Check();
        if (EditorHolds is { } held)
        {
            throw new LiveAppException(held);
        }
        if (!LoopHeld)
        {
            throw new LiveAppException($"{Name}'s loop runs: hold it to step it a frame");
        }
        List<object?> said = await AskAsync("step", [Headless], () => SendAsync(GodotWords.NextFrame));
        long stepped = said is [long frame, ..] ? frame : throw new LiveAppException($"Passenger's helper in {Name} said no frame count");
        Frame = stepped;
        LoopChanged?.Invoke(this, EventArgs.Empty);
        return stepped;
    }

    public async Task<ElementDetails> DetailsAsync(long element)
    {
        GodotObjectState state = await InspectAsync((ulong)element);
        string? name = state.Properties.FirstOrDefault(p => p.Name == "name")?.Value?.ToString();
        string? script = state.Properties.FirstOrDefault(p => p.Name == "script")?.Value as string;
        SourcePlace? source = script is { Length: > 0 } && File.Exists(PathOf(script)) ? new SourcePlace(PathOf(script), 0, 0) : null;
        List<PropertyValue> properties = [.. state.Properties.Where(p => p.Name is not ("name" or "script"))
            .Select(p => new PropertyValue(p.Name, p.Category, false, p.Type.ToString(), GodotVariant.Shown(p.Value), p.Category, null, null))];
        Box none = new(0, 0, 0, 0);
        Edges flat = new(0, 0, 0, 0);
        return new ElementDetails(element, state.Class, state.Class, name, [], [], source, source is null ? "A node with no script of the project's" : null, null,
            new LayoutInfo(none, null, 0, 0, flat, null, null, string.Empty, string.Empty), properties, []);
    }

    /// <summary>The values set from the properties pane, kept or not.</summary>
    public GodotTuning Tuning { get; }

    /// <summary>A node's properties as last read, or null before.</summary>
    public GodotObjectState? InspectedOf(long element) => inspected.GetValueOrDefault((ulong)element);

    GodotProperty? IGodotValues.PropertyOf(long element, string name) => InspectedOf(element)?.Properties.FirstOrDefault(p => p.Name == name);

    /// <summary>A running game takes each value as it moves, settled or not.</summary>
    Task IGodotValues.SetValueAsync(long element, string name, object? value, bool settles) => SetValueAsync(element, name, value);

    GodotTuned? IGodotValues.TunedOf(long element, string name) => Tuning.Of((ulong)element, name);

    /// <summary>Where a node is in the scene tree as last sent (its chain from the root, each scene's root with its scene),
    /// with its script as last read; null for a node the tree does not hold.</summary>
    public GodotNodePlace? PlaceOf(ulong id)
    {
        if (lastTree is not { } root || Chain(root, id) is not { } chain)
        {
            return null;
        }
        string? script = inspected.GetValueOrDefault(id)?.Properties.FirstOrDefault(p => p.Name == "script")?.Value as string;
        return new GodotNodePlace([.. chain.Skip(1).Select(n => (n.Name, n.SceneFile))], script is { Length: > 0 } ? script : null);

        static List<GodotNode>? Chain(GodotNode node, ulong id)
        {
            if (node.Id == id)
            {
                return [node];
            }
            foreach (GodotNode child in node.Children)
            {
                if (Chain(child, id) is { } below)
                {
                    below.Insert(0, node);
                    return below;
                }
            }
            return null;
        }
    }

    public string? WhyNotSet(long element, PropertyValue value) => WhyNotSet(element, value.Name);

    /// <summary>Why a property of a node cannot be set now, or null when it can: a resource's only where the node is in a
    /// scene, which live editing reaches it through.</summary>
    public string? WhyNotSet(long element, string name) =>
        State != LiveAppState.Connected ? $"{Name} is not running"
        : !inspected.TryGetValue((ulong)element, out GodotObjectState? state) || state.Properties.FirstOrDefault(p => p.Name == name) is not { } property
            ? $"{name} is not one of the node's properties as last read"
        : property.Settable ? null
        : property is { Type: GodotType.Object, Hint: GodotHints.ResourceType } && (property.Usage & (1 << 28)) == 0
            ? PlaceOf((ulong)element) is { } place && place.Chain.Any(c => c.Scene.Length > 0) ? null : $"{name} is set through its scene, which {state.Class} is in none of"
        : $"{name} is a {property.Type}{((property.Usage & (1 << 28)) != 0 ? ", read-only" : string.Empty)}, which is not set from here";

    public async Task SetAsync(long element, PropertyValue value, string text)
    {
        if (WhyNotSet(element, value) is { } why)
        {
            throw new LiveAppException(why);
        }
        GodotProperty property = inspected[(ulong)element].Properties.First(p => p.Name == value.Name);
        (object? read, string? wanted) = await GodotVariant.ReadAsync(property.Type, text, script: null);
        if (wanted is not null)
        {
            throw new LiveAppException($"{value.Name} takes {wanted}");
        }
        await SetValueAsync(element, value.Name, read);
    }

    /// <summary>Sets an object's property to a value of its type, recorded among the values set (<see cref="Tuning"/>): a
    /// node's, or a resource's own a node holds (<see cref="HeldAsync"/>), by the resource's path, which Keep writes.</summary>
    public async Task SetValueAsync(long element, string name, object? value)
    {
        UiThread.Check();
        if (WhyNotSet(element, name) is { } why)
        {
            throw new LiveAppException(why);
        }
        GodotObjectState state = inspected[(ulong)element];
        GodotProperty property = state.Properties.First(p => p.Name == name);
        if (!heldObjects.Contains((ulong)element))
        {
            Tuning.Set((ulong)element, PlaceOf((ulong)element) ?? new GodotNodePlace([], null), property, value);
        }
        else
        {
            // A resource's own, kept in the file or the scene its path names.
            string path = state.Properties.FirstOrDefault(p => p.Name == "resource_path")?.Value as string is { Length: > 0 } own ? own : heldPaths.GetValueOrDefault((ulong)element, string.Empty);
            string shown = path.Contains("::", StringComparison.Ordinal) ? path[(path.IndexOf("::", StringComparison.Ordinal) + 2)..]
                : path.Length > 0 ? System.IO.Path.GetFileName(path) : state.Class;
            Tuning.Set((ulong)element, new GodotNodePlace([(shown, string.Empty)], null), property, value, resource: path);
        }
        await SetPropertyAsync((ulong)element, name, value);
    }

    /// <summary>Sets a node's resource to a file of the project's (<c>res://</c>), which the game loads: through live editing
    /// of the scene the node is nearest the root of, which reaches it in every copy of that scene.</summary>
    public async Task SetResourceAsync(long element, string name, string resource)
    {
        UiThread.Check();
        if (WhyNotSet(element, name) is { } why)
        {
            throw new LiveAppException(why);
        }
        GodotProperty property = inspected[(ulong)element].Properties.First(p => p.Name == name);
        if (property.Type != GodotType.Object)
        {
            await SetValueAsync(element, name, resource);
            return;
        }
        GodotNodePlace place = PlaceOf((ulong)element) ?? throw new LiveAppException($"{name}'s node has left {Name}");
        int root = place.Chain.ToList().FindLastIndex(c => c.Scene.Length > 0);
        string below = string.Join('/', place.Chain.Skip(root + 1).Select(c => c.Name));
        Tuning.Set((ulong)element, place, property, resource);
        await SendAsync(GodotWords.LiveSetRoot, GodotAgent.PathOf("/root"), place.Chain[root].Scene);
        await SendAsync(GodotWords.LiveNodePath, GodotAgent.PathOf(below.Length == 0 ? "." : below), GodotLiveEditIds.Tuned);
        await SendAsync(GodotWords.LiveNodePropertyResource, GodotLiveEditIds.Tuned, name, resource);
        await GiveBackLiveRootAsync();
    }

    /// <summary>A relayed game's live editing set back to the scene its editor edits, as the editor sets it, once Passenger's
    /// own live editing has been sent: the editor's next live edit then reaches the scene it means.</summary>
    private async Task GiveBackLiveRootAsync()
    {
        if (Relayed)
        {
            await SendAsync(GodotWords.LiveSetRoot, GodotAgent.PathOf("/root"), EditorScene);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        ending.Cancel();
        listener?.Stop();
        stream?.Dispose();
        if (drawsOnServer)
        {
            VirtualDisplays.Release();
        }
        Ended();
    }
}
