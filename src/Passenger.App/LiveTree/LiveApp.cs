using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Passenger.App.Run;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Inspector;

namespace Passenger.App.LiveTree;

/// <summary>Where the inspector in an app stands: launched and not yet heard from, answering, refused (another Avalonia,
/// none), or gone with the app.</summary>
public enum LiveAppState
{
    Waiting,
    Connected,
    Refused,
    Ended,
}

/// <summary>A binding failed in an app: the failure as the app last said it, and how many times since it last did.</summary>
public sealed class BindingFailedEventArgs(BindingFailure failure, int times) : EventArgs
{
    public BindingFailure Failure { get; } = failure;

    public int Times { get; } = times;
}

/// <summary>Why the app could not answer: not connected, gone, or what the inspector in it said.</summary>
public sealed class LiveAppException(string message) : Exception(message);

/// <summary>The framework an inspected app is built on, which picks the inspector it is given.</summary>
public enum LiveAppKind
{
    Avalonia,
    Wpf,
}

/// <summary>Passenger's end of its inspector inside an Avalonia app it runs or debugs (src/Passenger.Inspector) or a WPF app
/// (src/Passenger.Inspector.Wpf, also given ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO), one protocol for both: a startup hook and a
/// socket of the launch's own; from before Main it tells of failed bindings, and once the UI is up says hello and answers on
/// the UI thread (paused under the debugger, once it runs again). One launch's alone, never in the app as built. A UI-thread
/// object: its events come on the UI thread.</summary>
public sealed class LiveApp : IDisposable
{
    /// <summary>The sockets this process has made, which number each one's name apart.</summary>
    private static int made;

    private readonly Socket listener;
    private readonly CancellationTokenSource closing = new();
    private readonly PendingRequests<Request, Reply> asked = new();
    private readonly SemaphoreSlim writing = new(1, 1);

    /// <summary>What the app told unasked and the UI thread has yet to hear, which it hears all at once: an app saying
    /// thousands of things at once (its bindings failing as it opens) is one crossing, not one each.</summary>
    private readonly List<Reply> heard = [];
    private NetworkStream? stream;

    private LiveApp(string name, string socket, Socket listener, LaunchPlan? plan, IReadOnlyList<string> overlays, LiveAppKind kind = LiveAppKind.Avalonia)
    {
        this.listener = listener;
        SocketPath = socket;
        Name = name;
        Overlays = overlays;
        Kind = kind;
        if (plan is not null)
        {
            Dictionary<string, string> environment = new(plan.Environment)
            {
                [Wire.SocketVariable] = socket,
                [RuntimeVariables.StartupHooks] = StartupHooks.First(plan.Environment, kind == LiveAppKind.Wpf ? WpfInspectorPath : InspectorPath),
            };
            if (kind == LiveAppKind.Wpf)
            {
                environment[RuntimeVariables.XamlDiagnosticsSourceInfo] = "1";
                if (plan.Project is { } project)
                {
                    environment[Wire.ProjectVariable] = Path.GetDirectoryName(project)!;
                }
            }
            else if (overlays.Count > 0)
            {
                environment[Wire.OverlaysVariable] = string.Join(',', overlays);
            }
            Plan = plan with { Environment = environment };
        }
        // Not held: it waits as long as the app runs, which nothing drawn waits for.
        IdeErrors.Observe(Task.Run(ServeAsync));
    }

    /// <summary>The inspector beside the app, as its build carries it.</summary>
    public static string InspectorPath => Path.Combine(AppContext.BaseDirectory, "inspector", "Passenger.Inspector.dll");

    /// <summary>The WPF app's inspector, which only a Windows build carries.</summary>
    public static string WpfInspectorPath => Path.Combine(AppContext.BaseDirectory, "inspector", "wpf", "Passenger.Inspector.Wpf.dll");

    /// <summary>The framework the app is built on, which its inspector serves.</summary>
    public LiveAppKind Kind { get; }

    /// <summary>The framework and its version as the window says it, once the app has said hello: "Avalonia 12.1.4",
    /// "WPF 10.0.1"; the framework alone before.</summary>
    public string Framework => Kind == LiveAppKind.Wpf
        ? FrameworkSaid ?? "WPF"
        : Avalonia is { } version ? $"Avalonia {(System.Version.TryParse(version, out Version? read) ? read.ToString(3) : version)}" : "Avalonia";

    /// <summary>What a WPF app's inspector said it runs, from its hello.</summary>
    private string? FrameworkSaid { get; set; }

    /// <summary>The plan as it is launched: the inspector among its startup hooks, and the socket to connect to; none for
    /// Passenger's own windows (<see cref="Own"/>).</summary>
    public LaunchPlan? Plan { get; }

    /// <summary>Passenger's own windows, once an agent has asked for them.</summary>
    private static LiveApp? own;

    /// <summary>Passenger's own windows, every one of this process's, inspected as an app is: the same inspector, loaded into
    /// Passenger the first time an agent asks and kept from then on, the process's, as it takes over Avalonia's one logger
    /// and keeps it.</summary>
    public static LiveApp Own()
    {
        UiThread.Check();
        if (own is not null)
        {
            return own;
        }
        if (!File.Exists(InspectorPath))
        {
            throw new LiveAppException("This build of Passenger carries no inspector");
        }
        // A Passenger another runs or debugs has that one's inspector, whose binding log a second would fight over the logger.
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Passenger.Inspector"))
        {
            throw new LiveAppException("This Passenger was started by another Passenger, whose inspector it carries: that one's app tools (app run or debug) look at its windows");
        }
        (string socket, Socket listener) = Listen("Passenger") ?? throw new LiveAppException("Passenger could not listen for its own inspector");
        LiveApp app = new("Passenger", socket, listener, plan: null, []);
        try
        {
            Assembly inspector = AssemblyLoadContext.Default.LoadFromAssemblyPath(InspectorPath);
            Type agent = inspector.GetType("Passenger.Inspector.Agent", throwOnError: true)!;
            // Only the person answers Passenger's questions for agents and changes its Settings: no agent's input reaches them.
            agent.GetMethod("Guard", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [(Func<object, string?>)MainWindow.KeptFromAgents]);
            agent.GetMethod("Start", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [socket, Thread.CurrentThread, typeof(Avalonia.AvaloniaObject).Assembly.GetName().Version!.ToString(), null]);
        }
        catch (Exception error) when (error is IOException or BadImageFormatException or TypeLoadException or MissingMethodException or TargetInvocationException)
        {
            app.Dispose();
            throw new LiveAppException($"Passenger's inspector could not start in Passenger: {error.GetBaseException().Message}");
        }
        own = app;
        return app;
    }

    /// <summary>The run configuration's name, as the window says it.</summary>
    public string Name { get; }

    /// <summary>The socket's file, while it waits for the app.</summary>
    public string SocketPath { get; }

    public LiveAppState State { get; private set; }

    /// <summary>Why the inspector cannot serve the app, once refused.</summary>
    public string? Problem { get; private set; }

    /// <summary>The app's Avalonia, from its hello.</summary>
    public string? Avalonia { get; private set; }

    /// <summary>The app's process, from its hello.</summary>
    public int? ProcessId { get; private set; }

    /// <summary>The app connected, was refused, or went.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Controls came or went in the app since its tree was last asked for.</summary>
    public event EventHandler? TreeChanged;

    /// <summary>Select Element ended: the element the person pointed at, or null when they pressed Escape.</summary>
    public event EventHandler<long?>? Picked;

    /// <summary>A property whose binding the last values read showed has changed in the app since.</summary>
    public event EventHandler? ValuesChanged;

    /// <summary>A binding failed in the app, for the first time or again (<see cref="BindingFailedEventArgs.Times"/> since
    /// it was last told), from before its UI is up until it ends.</summary>
    public event EventHandler<BindingFailedEventArgs>? BindingFailed;

    /// <summary>Each binding that failed in the app, once, with how many times it has, in the order they first failed.</summary>
    public IReadOnlyList<BindingFailure> Bindings => [.. bindings.Values];

    private readonly Dictionary<int, BindingFailure> bindings = [];

    /// <summary>What the app could not tell, as it last said: the binding failures it counted past those it lists, and the
    /// messages its inspector left out while too many waited.</summary>
    public Untold Untold { get; private set; } = new(0, 0);

    /// <summary>The app said it could not tell more than it had said (<see cref="Untold"/>).</summary>
    public event EventHandler? UntoldChanged;

    /// <summary>The live app for a plan whose program is built with Avalonia (Avalonia.Base beside its assembly), showing
    /// these renderer debug overlays from its first window, or, with <paramref name="wpf"/>, on Windows with WPF (its
    /// assembly a WindowsDesktop app's that references WPF's); null for any other program, or when this build of Passenger
    /// carries no inspector for it.</summary>
    public static LiveApp? For(LaunchPlan plan, IReadOnlyList<string>? overlays = null, bool wpf = true)
    {
        if (plan.Built is not { } built)
        {
            return null;
        }
        LiveAppKind kind;
        if (File.Exists(Path.Combine(Path.GetDirectoryName(built)!, "Avalonia.Base.dll")) && File.Exists(InspectorPath))
        {
            kind = LiveAppKind.Avalonia;
        }
        else if (wpf && OperatingSystem.IsWindows() && File.Exists(WpfInspectorPath) && IsWpf(built))
        {
            kind = LiveAppKind.Wpf;
        }
        else
        {
            return null;
        }
        return Listen(plan.Name) is (string socket, Socket listener) ? new LiveApp(plan.Name, socket, listener, plan, overlays ?? [], kind) : null;
    }

    /// <summary>Whether an assembly is a WPF app's: it runs on the WindowsDesktop framework (its runtimeconfig.json names it)
    /// and references WPF's assemblies, as a Windows Forms app does not.</summary>
    private static bool IsWpf(string built)
    {
        string config = Path.ChangeExtension(built, ".runtimeconfig.json");
        try
        {
            if (!File.Exists(built) || !File.Exists(config) || !File.ReadAllText(config).Contains("Microsoft.WindowsDesktop.App", StringComparison.Ordinal))
            {
                return false;
            }
            return AssemblyFacts.Read(built)?.References.Any(r => r.Name is "PresentationFramework" or "PresentationCore") == true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The renderer debug overlays the app's windows show, as the launch asked or the app last said.</summary>
    public IReadOnlyList<string> Overlays { get; private set; }

    /// <summary>The app's windows show these renderer debug overlays (none to take them away) from now, windows opened
    /// later too; answered with the windows, each with the overlays it shows.</summary>
    public async Task<IReadOnlyList<AppWindow>> SetOverlaysAsync(IReadOnlyList<string> overlays)
    {
        IReadOnlyList<AppWindow> windows = (await AskAsync(new Request(0, Wire.Overlays) { Overlays = overlays })).Windows ?? [];
        Overlays = overlays;
        return windows;
    }

    /// <summary>A socket of its own in Passenger's socket folder for one app's inspector to connect to, or null when it cannot
    /// be made, which the IDE's errors say, the app then launched as it is.</summary>
    private static (string Socket, Socket Listener)? Listen(string name)
    {
        string socket = $"{Environment.ProcessId}-{Interlocked.Increment(ref made)}.inspector";
        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket = Path.Combine(SocketFolders.Own(StateStore.DefaultRoot), socket);
            // Only a dead Passenger that had this process's number leaves one by this name.
            File.Delete(socket);
            listener.Bind(new UnixDomainSocketEndPoint(socket));
            listener.Listen(1);
        }
        // ArgumentException: a path past what a Unix socket's address holds (104 bytes on macOS).
        catch (Exception error) when (error is SocketException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            listener.Dispose();
            IdeErrors.Write($"The Live Visual Tree could not listen for {name} at {socket}, so it runs uninspected: {error.Message}");
            return null;
        }
        return (socket, listener);
    }

    /// <summary>The app's windows and their visual trees.</summary>
    public async Task<AppTree> TreeAsync() => (await AskAsync(new Request(0, Wire.Tree))).Tree ?? new AppTree([]);

    /// <summary>An element looked at closely: its properties and their sources, its layout, its styles.</summary>
    public async Task<ElementDetails> DetailsAsync(long element) =>
        (await AskAsync(new Request(0, Wire.Details, element))).Details ?? throw new LiveAppException("The app said nothing of that element");

    /// <summary>Select Element on (the next click in the app chooses what it is on) or off.</summary>
    public Task PickAsync(bool on) => AskAsync(new Request(0, Wire.Pick, On: on));

    /// <summary>Outlines an element in the app, or takes the outline away.</summary>
    public Task HighlightAsync(long? element) => AskAsync(new Request(0, Wire.Highlight, element));

    /// <summary>The app's windows and their popups, for an agent.</summary>
    public async Task<IReadOnlyList<AppWindow>> WindowsAsync() => (await AskAsync(new Request(0, Wire.Windows))).Windows ?? [];

    /// <summary>The windows' trees as an agent reads them: each element's place, text and state, and the properties named.</summary>
    public async Task<AppTree> AgentTreeAsync(IReadOnlyList<string> properties) =>
        (await AskAsync(new Request(0, Wire.Tree) { Layout = true, Properties = properties })).Tree ?? new AppTree([]);

    /// <summary>A picture the app draws of a window, or of an element's part of it, or of an area of the window.</summary>
    public async Task<Picture> CaptureAsync(long element, double? scale, Box? area) =>
        (await AskAsync(new Request(0, Wire.Capture, element) { Scale = scale, Area = area })).Picture ?? throw new LiveAppException($"{Name} drew nothing");

    /// <summary>The property breakpoints its inspector was told to watch, each with the hit count it was given: the Live
    /// Visual Tree's word to it, kept with the app so each app debugged at once keeps its own.</summary>
    internal Dictionary<Debugging.PropertyBreakpoint, string> Watched { get; } = [];

    /// <summary>A property breakpoint's watch on an element's property, its hit count counted in the app, in place of what
    /// that breakpoint watched before.</summary>
    public Task WatchAsync(int breakpoint, long element, string property, string? hitCondition) =>
        AskAsync(new Request(0, Wire.Watch, element, On: true) { Breakpoint = breakpoint, Property = property, HitCondition = hitCondition });

    /// <summary>A property breakpoint's watch ended.</summary>
    public Task UnwatchAsync(int breakpoint) => AskAsync(new Request(0, Wire.Watch, On: false) { Breakpoint = breakpoint });

    /// <summary>A XAML file's bindings read with their values while the app runs, whose properties the app watches from now
    /// (<see cref="ValuesChanged"/>).</summary>
    public async Task<XamlValues> ValuesAsync(XamlRead read) =>
        (await AskAsync(new Request(0, Wire.Values) { Xaml = read })).Values ?? throw new LiveAppException($"{Name} read no bindings");

    /// <summary>Input to the app, as a person gives it, answered once it has been handled and laid out.</summary>
    public async Task<InputDone> InputAsync(InputAction input, CancellationToken giveUp = default) =>
        (await AskAsync(new Request(0, Wire.Input) { Input = input }, giveUp)).Done ?? throw new LiveAppException($"{Name} did not say what the input did");

    /// <summary>A request's answer; <paramref name="giveUp"/> stops the wait for it (the app paused under the debugger
    /// before it could answer), its answer then forgotten.</summary>
    private async Task<Reply> AskAsync(Request request, CancellationToken giveUp = default)
    {
        if (State != LiveAppState.Connected || stream is not { } open)
        {
            throw new LiveAppException(State switch
            {
                LiveAppState.Waiting => $"{Name} has not shown its UI yet",
                LiveAppState.Refused => Problem ?? $"{Name} cannot be inspected",
                _ => $"{Name} has ended",
            });
        }
        (int id, Task<Reply> answer) = asked.Add(request);
        byte[] line = [.. JsonSerializer.SerializeToUtf8Bytes(request with { Id = id }, WireJson.Default.Request), (byte)'\n'];
        await writing.WaitAsync();
        try
        {
            await open.WriteAsync(line);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or SocketException)
        {
            asked.Forget(id, answer);
            throw new LiveAppException($"{Name} has ended");
        }
        finally
        {
            writing.Release();
        }
        Reply reply;
        try
        {
            reply = await answer.WaitAsync(AnswerLimit, giveUp);
        }
        catch (OperationCanceledException) when (giveUp.IsCancellationRequested)
        {
            asked.Forget(id, answer);
            throw;
        }
        catch (TimeoutException)
        {
            // Forgotten, so its answer, if it ever comes, finds no one waiting.
            asked.Forget(id, answer);
            throw new LiveAppException(NotAnswered(Name));
        }
        return reply.Error is { } why ? throw new LiveAppException(why) : reply;
    }

    /// <summary>How long the app may take to answer: its UI thread busy, or the app paused under the debugger.</summary>
    public static readonly TimeSpan AnswerLimit = TimeSpan.FromSeconds(10);

    /// <summary>What a question the app left unanswered past <see cref="AnswerLimit"/> fails with.</summary>
    public static string NotAnswered(string app) => $"{app} did not answer within {AnswerLimit.TotalSeconds:0} s";

    /// <summary>Takes the app's one connection, then reads what it says until it goes.</summary>
    private async Task ServeAsync()
    {
        Socket client;
        try
        {
            client = await listener.AcceptAsync(closing.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            return;
        }
        finally
        {
            // The one app it was made for has come (or will not): nothing else may connect.
            listener.Dispose();
            Forget(SocketPath);
        }
        NetworkStream network = new(client, ownsSocket: true);
        stream = network;
        try
        {
            using StreamReader reader = new(network, new UTF8Encoding(false));
            await foreach (Reply reply in JsonLines.ReadAsync(reader, WireJson.Default.Reply,
                (_, error) => IdeErrors.Write($"The Live Visual Tree could not read what {Name} said: {error.Message}"), closing.Token))
            {
                if (reply.Id is int id)
                {
                    asked.Answer(id, reply);
                    continue;
                }
                bool first;
                lock (heard)
                {
                    first = heard.Count == 0;
                    heard.Add(reply);
                }
                if (first)
                {
                    UiThread.Post(Hear);
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
        {
        }
        finally
        {
            await network.DisposeAsync();
            FailAll();
            UiThread.Post(() => Become(LiveAppState.Ended));
        }
    }

    private void FailAll() => asked.End(_ => new LiveAppException($"{Name} has ended"));

    /// <summary>Everything the app has told unasked since the UI thread last heard it, in the order told.</summary>
    private void Hear()
    {
        List<Reply> told;
        lock (heard)
        {
            told = [.. heard];
            heard.Clear();
        }
        foreach (Reply reply in told)
        {
            Told(reply);
        }
    }

    /// <summary>What the app tells unasked, on the UI thread.</summary>
    private void Told(Reply reply)
    {
        if (State == LiveAppState.Ended)
        {
            return;
        }
        switch (reply.Event)
        {
            case Wire.Hello when reply.Hello is { } hello:
                Avalonia = hello.Avalonia;
                FrameworkSaid = hello.Framework;
                ProcessId = hello.ProcessId;
                if (hello.Protocol != Wire.Version)
                {
                    Problem = $"{Name}'s inspector speaks version {hello.Protocol} of Passenger's protocol, not {Wire.Version}";
                }
                else
                {
                    Problem = hello.Problem;
                }
                Become(Problem is null ? LiveAppState.Connected : LiveAppState.Refused);
                break;
            case Wire.TreeChanged:
                TreeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case Wire.ValuesChanged:
                ValuesChanged?.Invoke(this, EventArgs.Empty);
                break;
            case Wire.Picked:
                Picked?.Invoke(this, reply.Element);
                break;
            case Wire.PickEnded:
                Picked?.Invoke(this, null);
                break;
            case Wire.BindingFailed when reply.Binding is { } failure:
                int before = bindings.TryGetValue(failure.Id, out BindingFailure? known) ? known.Count : 0;
                if (failure.Count > before)
                {
                    bindings[failure.Id] = failure;
                    BindingFailed?.Invoke(this, new BindingFailedEventArgs(failure, failure.Count - before));
                }
                break;
            case Wire.BindingFailed when reply.Error is { } why:
                IdeErrors.Write($"{Name}: {why}");
                break;
            case Wire.Untold when reply.Untold is { } untold && untold != Untold:
                if (untold.Messages > Untold.Messages)
                {
                    IdeErrors.Write($"{Name}'s inspector left out {untold.Messages - Untold.Messages} of its own messages, too many waiting to be sent at once");
                }
                Untold = untold;
                UntoldChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void Become(LiveAppState state)
    {
        if (State == state || State == LiveAppState.Ended)
        {
            return;
        }
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The app is done with: its socket closed, whatever it was asked failing, and its state ended.</summary>
    public void Dispose()
    {
        UiThread.Check();
        if (closing.IsCancellationRequested)
        {
            return;
        }
        closing.Cancel();
        listener.Dispose();
        stream?.Dispose();
        Forget(SocketPath);
        FailAll();
        Become(LiveAppState.Ended);
    }

    private static void Forget(string socket)
    {
        try
        {
            File.Delete(socket);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
