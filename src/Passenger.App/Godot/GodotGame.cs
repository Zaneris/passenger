using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Inspector;

namespace Passenger.App.Godot;

/// <summary>A Godot game Run, Debug or Profile started, as its remote debugger: Passenger listens on loopback for the one
/// connection the game makes back (<c>--remote-debug tcp://127.0.0.1:port</c>, which <see cref="Listen"/> adds to its plan),
/// and speaks Godot's remote debug protocol over it as Godot's editor does. The game tells its errors and warnings
/// (<see cref="Raised"/>, each with the stack it was raised on) and its performance monitors once a second; asked, its scene
/// tree and a node's properties, and it takes a property's new value. So it is a source of the Live Visual Tree. Its
/// script breakpoints are skipped and its errors never stop it, since Passenger's own debugger is C#'s. Events are raised
/// on the UI thread; it ends with its connection, or once disposed.</summary>
public sealed class GodotGame : ILiveTreeSource, ILiveTreeValues, IDisposable
{
    /// <summary>The flag that gives a game its debugger.</summary>
    public const string RemoteDebug = "--remote-debug";

    private readonly TcpListener listener;
    private readonly CancellationTokenSource ending = new();
    private readonly SemaphoreSlim writing = new(1, 1);
    private NetworkStream? stream;
    private TaskCompletionSource<GodotNode?>? treeAsked;
    private readonly Dictionary<ulong, TaskCompletionSource<GodotObjectState>> objectsAsked = [];
    private readonly Dictionary<ulong, GodotObjectState> inspected = [];
    private readonly Dictionary<long, TaskCompletionSource<(int Width, int Height, string Path)>> picturesAsked = [];
    private readonly Dictionary<long, TaskCompletionSource<List<object?>>> agentAsked = [];
    private long asks;
    private bool disposed;

    /// <summary>Passenger's helper sent for (<see cref="AskAsync"/>), done once the game is told to make it: another ask waits
    /// for it rather than making a second.</summary>
    private Task? bringing;

    /// <summary>The id under which the game's live editing knows the path of Passenger's helper (<see cref="GodotAgent"/>).</summary>
    private const long AgentPath = 7046;

    private GodotGame(string name, string folder, string? version, bool headless, TcpListener listener)
    {
        Name = name;
        Folder = folder;
        Version = version;
        Headless = headless;
        this.listener = listener;
    }

    /// <summary>A plan as a launch starts it, with a game listened for when it runs a Godot game (<see cref="LaunchPlan.Godot"/>),
    /// <c>--remote-debug</c> among the arguments that start it; the plan as it is, and no game, for any other.</summary>
    public static (LaunchPlan Plan, GodotGame? Game) Listen(LaunchPlan plan)
    {
        if (plan.Godot is not { } folder || plan.GodotEditor)
        {
            return (plan, null);
        }
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start(1);
        bool headless = plan.Arguments.Contains("--headless")
            || plan.Arguments.Zip(plan.Arguments.Skip(1)).Any(p => p is ("--display-driver", "headless"));
        GodotGame game = new(plan.Name, folder, plan.GodotVersion, headless, listener);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        List<string> arguments = [.. plan.Arguments];
        arguments.InsertRange(plan.Leading, [RemoteDebug, $"tcp://127.0.0.1:{port}"]);
        // For the game's life, as its output is: no screenshot or test waits on a connection that lasts until the game ends.
        IdeErrors.Observe(BackgroundWork.Unheld(game.ServeAsync));
        return (plan with { Arguments = arguments, Leading = plan.Leading + 2 }, game);
    }

    public string Name { get; }

    /// <summary>The Godot project's folder, which its <c>res://</c> paths are under.</summary>
    public string Folder { get; }

    /// <summary>The Godot version it runs on, where known.</summary>
    public string? Version { get; }

    /// <summary>Whether Godot runs it headless (<c>--headless</c>), drawing nothing.</summary>
    public bool Headless { get; }

    public LiveAppState State { get; private set; } = LiveAppState.Waiting;

    public string? Problem => null;

    public int? ProcessId { get; private set; }

    /// <summary>"Godot 4.7.2".</summary>
    public string Framework => Version is { } version ? $"Godot {version}" : "Godot";

    /// <summary>Its performance monitors as it last sent them, or null before.</summary>
    public GodotMonitors? Monitors { get; private set; }

    /// <summary>The Debug session the game runs under, while one does: its stops pause the game's answers.</summary>
    public Debugging.DebugSession? Debugged { get; set; }

    public event EventHandler? StateChanged;

    /// <summary>Its count of nodes changed since its monitors last said it, so its tree did.</summary>
    public event EventHandler? TreeChanged;

    /// <summary>Its monitors came, once a second.</summary>
    public event EventHandler? MonitorsChanged;

    /// <summary>An error or warning it raised.</summary>
    public event EventHandler<GodotError>? Raised;

    /// <summary>A <c>res://</c> path as a path on this machine, under the project's folder; another path as it is.</summary>
    public string PathOf(string path) => path.StartsWith("res://", StringComparison.Ordinal)
        ? Path.GetFullPath(Path.Combine(Folder, path["res://".Length..]))
        : path;

    private async Task ServeAsync()
    {
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(ending.Token);
            listener.Stop();
            client.NoDelay = true;
            stream = client.GetStream();
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
                GodotMessage message;
                try
                {
                    message = GodotMessage.Read(body);
                }
                catch (GodotVariantException unread)
                {
                    IdeErrors.Write($"Godot: a message of {Name} could not be read: {unread.Message}");
                    continue;
                }
                UiThread.Post(() => Heard(message));
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

    private void Connected()
    {
        if (disposed)
        {
            return;
        }
        State = LiveAppState.Connected;
        // A GDScript breakpoint or error would stop the game waiting for a debugger that shows no GDScript stop.
        Send(GodotWords.SkipBreakpoints, true);
        Send(GodotWords.IgnoreErrorBreaks, true);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Ended()
    {
        if (State == LiveAppState.Ended)
        {
            return;
        }
        State = LiveAppState.Ended;
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
                break;
            case GodotWords.SceneTree:
                treeAsked?.TrySetResult(GodotNode.Tree(message.Data));
                break;
            case GodotWords.InspectObjects:
                foreach (GodotObjectState state in GodotObjectState.Read(message.Data))
                {
                    inspected[state.Id] = state;
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
            case GodotWords.Error when GodotError.Read(message.Data) is { } error:
                Raised?.Invoke(this, error);
                break;
            case GodotWords.Screenshotted when message.Data is [long id, long width, long height, string path]:
                picturesAsked.GetValueOrDefault(id)?.TrySetResult(((int)width, (int)height, path));
                break;
            case GodotAgent.Said when message.Data is List<object?> { Count: >= 2 } said && said[0] is long ask:
                agentAsked.GetValueOrDefault(ask)?.TrySetResult(said);
                break;
            case GodotWords.Monitors when GodotMonitors.Read(message.Data) is { } monitors:
                bool moved = Monitors is { } was && was.Nodes != monitors.Nodes;
                Monitors = monitors;
                MonitorsChanged?.Invoke(this, EventArgs.Empty);
                if (moved)
                {
                    TreeChanged?.Invoke(this, EventArgs.Empty);
                }
                break;
            case GodotWords.DebugEnter:
                // A script's stop Passenger cannot show: the game goes on.
                Send(GodotWords.Continue);
                break;
        }
    }

    private void Send(string name, params object?[] data) => IdeErrors.Observe(SendAsync(name, data));

    private async Task SendAsync(string name, params object?[] data)
    {
        if (stream is not { } open || disposed)
        {
            throw new LiveAppException($"{Name} is not connected");
        }
        byte[] frame = GodotMessage.Frame(name, data);
        await writing.WaitAsync();
        try
        {
            await open.WriteAsync(frame, ending.Token);
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
    public async Task<GodotNode> SceneTreeAsync()
    {
        UiThread.Check();
        TaskCompletionSource<GodotNode?> asked = treeAsked is { Task.IsCompleted: false } pending ? pending
            : treeAsked = new TaskCompletionSource<GodotNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendAsync(GodotWords.RequestSceneTree);
        return await Answer(asked.Task, "with its scene tree") ?? throw new LiveAppException($"{Name} sent a scene tree Passenger could not read");
    }

    /// <summary>A node's (or any object's) properties now.</summary>
    public async Task<GodotObjectState> InspectAsync(ulong id)
    {
        UiThread.Check();
        if (!objectsAsked.TryGetValue(id, out TaskCompletionSource<GodotObjectState>? asked))
        {
            asked = objectsAsked[id] = new TaskCompletionSource<GodotObjectState>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        await SendAsync(GodotWords.InspectObjects, new List<object?> { (long)id }, false);
        return await Answer(asked.Task, "with the node's properties");
    }

    /// <summary>Sets a property of an object to a value, which the game takes as it next reads its messages.</summary>
    public Task SetPropertyAsync(ulong id, string property, object? value) => SendAsync(GodotWords.SetObjectProperty, (long)id, property, value);

    /// <summary>The answer to a question of the game's, <paramref name="what"/> naming the question its refusal says went unanswered.</summary>
    private async Task<T> Answer<T>(Task<T> asked, string? what = null)
    {
        try
        {
            return await asked.WaitAsync(Waits.Of(AnswerLimit));
        }
        catch (TimeoutException)
        {
            throw new LiveAppException(Debugged is { State: Debugging.DebugState.Paused }
                ? $"{Name} is paused, and answers once it runs"
                : $"{Name} did not answer{(what is null ? string.Empty : $" {what}")} in {AnswerLimit.TotalSeconds:0} s");
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

    /// <summary>Why a game Godot runs headless has no picture.</summary>
    public string HeadlessPicture => $"{Name} runs headless (--headless among its arguments), and Godot draws nothing for a headless game, "
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
            await SendAsync(GodotWords.Screenshot, ask);
            (int width, int height, string path) = await Answer(asked.Task, "with its picture");
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
    /// once, where the game's tree lacks it: through the live editing of a scene open once under the root, a node made beside
    /// it and its script set from <see cref="GodotAgent.Written"/>. What it answered after its ask and done; a
    /// <see cref="LiveAppException"/> saying why it did not do it.</summary>
    public async Task<List<object?>> AskAsync(string call, params object?[] arguments)
    {
        UiThread.Check();
        GodotNode tree = await SceneTreeAsync();
        List<GodotNode> all = [.. tree.All()];
        string scene = tree.Children.Select(c => c.SceneFile).FirstOrDefault(f => f.Length > 0 && all.Count(n => n.SceneFile == f) == 1)
            ?? throw new LiveAppException($"{Name} has no scene open once under its root, beside which Passenger's helper comes into the game");
        await SendAsync(GodotWords.LiveSetRoot, GodotAgent.PathOf("/root"), scene);
        await SendAsync(GodotWords.LiveNodePath, GodotAgent.PathOf($"../{GodotAgent.NodeName}"), AgentPath);
        if (bringing is null && !tree.Children.Any(GodotAgent.Is))
        {
            bringing = BringAsync();
        }
        if (bringing is { } brought)
        {
            try
            {
                await brought;
            }
            catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException)
            {
                bringing = null;
                throw new LiveAppException($"Passenger's helper could not be written for {Name}: {unwritten.Message}");
            }
        }
        long ask = ++asks;
        TaskCompletionSource<List<object?>> asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        agentAsked[ask] = asked;
        try
        {
            await SendAsync(GodotWords.LiveNodeCall, [AgentPath, call, ask, .. arguments]);
            List<object?> said = await Answer(asked.Task, $"Passenger's helper's {call}");
            return said[1] is true ? said[2..]
                : throw new LiveAppException(said is [_, _, string why, ..] ? why : $"Passenger's helper in {Name} did not say why it could not");
        }
        finally
        {
            agentAsked.Remove(ask);
        }
    }

    /// <summary>Has the game make Passenger's helper beside the scene its live editing names, its script the file written.</summary>
    private async Task BringAsync()
    {
        string script = await BackgroundWork.Run(GodotAgent.Written);
        await SendAsync(GodotWords.LiveCreateNode, GodotAgent.PathOf(".."), "Node", GodotAgent.NodeName);
        // Godot's own paths part their folders with '/' on every system.
        await SendAsync(GodotWords.LiveNodePropertyResource, AgentPath, "script", script.Replace('\\', '/'));
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

    public string? WhyNotSet(long element, PropertyValue value) =>
        State != LiveAppState.Connected ? $"{Name} is not running"
        : !inspected.TryGetValue((ulong)element, out GodotObjectState? state) || state.Properties.FirstOrDefault(p => p.Name == value.Name) is not { } property
            ? $"{value.Name} is not one of the node's properties as last read"
        : property.Settable ? null
        : $"{value.Name} is a {property.Type}{((property.Usage & (1 << 28)) != 0 ? ", read-only" : string.Empty)}, which is not set from here";

    public async Task SetAsync(long element, PropertyValue value, string text)
    {
        if (WhyNotSet(element, value) is { } why)
        {
            throw new LiveAppException(why);
        }
        GodotProperty property = inspected[(ulong)element].Properties.First(p => p.Name == value.Name);
        (object? read, string? wanted) = GodotVariant.Parse(property.Type, text);
        if (wanted is not null)
        {
            throw new LiveAppException($"{value.Name} takes {wanted}");
        }
        await SetPropertyAsync((ulong)element, value.Name, read);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        ending.Cancel();
        listener.Stop();
        stream?.Dispose();
        Ended();
    }
}
