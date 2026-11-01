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
    private bool disposed;

    private GodotGame(string name, string folder, string? version, TcpListener listener)
    {
        Name = name;
        Folder = folder;
        Version = version;
        this.listener = listener;
    }

    /// <summary>A plan as a launch starts it, with a game listened for when it runs a Godot game (<see cref="LaunchPlan.Godot"/>),
    /// <c>--remote-debug</c> among the arguments that start it; the plan as it is, and no game, for any other.</summary>
    public static (LaunchPlan Plan, GodotGame? Game) Listen(LaunchPlan plan)
    {
        if (plan.Godot is not { } folder || plan.Arguments.Take(plan.Leading).Contains("--editor"))
        {
            return (plan, null);
        }
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start(1);
        GodotGame game = new(plan.Name, folder, plan.GodotVersion, listener);
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
            case GodotWords.Error when GodotError.Read(message.Data) is { } error:
                Raised?.Invoke(this, error);
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
        return await Answer(asked.Task) ?? throw new LiveAppException($"{Name} sent a scene tree Passenger could not read");
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
        return await Answer(asked.Task);
    }

    /// <summary>Sets a property of an object to a value, which the game takes as it next reads its messages.</summary>
    public Task SetPropertyAsync(ulong id, string property, object? value) => SendAsync(GodotWords.SetObjectProperty, (long)id, property, value);

    private async Task<T> Answer<T>(Task<T> asked)
    {
        try
        {
            return await asked.WaitAsync(Waits.Of(AnswerLimit));
        }
        catch (TimeoutException)
        {
            throw new LiveAppException(Debugged is { State: Debugging.DebugState.Paused }
                ? $"{Name} is paused, and answers once it runs"
                : $"{Name} did not answer in {AnswerLimit.TotalSeconds:0} s");
        }
    }

    public async Task<AppTree> TreeAsync()
    {
        GodotNode root = await SceneTreeAsync();
        return new AppTree([ElementOf(root)]);
    }

    /// <summary>A node as the tree shows it, a script's path that names its type ("res://Main.cs") by its file's name.</summary>
    private static Element ElementOf(GodotNode node) =>
        new((long)node.Id, node.Type.StartsWith("res://", StringComparison.Ordinal) ? Path.GetFileName(node.Type) : node.Type, node.Name, [], false,
            node.SceneFile.Length > 0, node.Visible, [.. node.Children.Select(ElementOf)]);

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
