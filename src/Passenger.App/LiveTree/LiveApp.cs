using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
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
    Maui,
}

/// <summary>Passenger's end of its inspector inside an Avalonia app it runs or debugs (src/Passenger.Inspector), a WPF app
/// (src/Passenger.Inspector.Wpf, also given ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO) or a .NET MAUI app on a device or a
/// simulator (src/Passenger.Inspector.Maui), one protocol for all: a startup hook and a
/// socket of the launch's own; from before Main it tells of failed bindings, and once the UI is up says hello and answers on
/// the UI thread (paused under the debugger, once it runs again). One launch's alone, never in the app as built. A UI-thread
/// object: its events come on the UI thread.</summary>
public sealed class LiveApp : ILiveTreeSource, IDisposable
{
    /// <summary>The sockets this process has made, which number each one's name apart.</summary>
    private static int made;

    /// <summary>Where the app's inspector connects; null where Passenger could not listen, the app running uninspected.</summary>
    private readonly Socket? listener;
    private readonly CancellationTokenSource closing = new();
    private readonly PendingRequests<Request, Reply> asked = new();
    private readonly SemaphoreSlim writing = new(1, 1);

    /// <summary>What the app told unasked and the UI thread has yet to hear, which it hears all at once: an app saying
    /// thousands of things at once (its bindings failing as it opens) is one crossing, not one each.</summary>
    private readonly List<Reply> heard = [];
    private NetworkStream? stream;

    private LiveApp(string name, string socket, Socket? listener, LaunchPlan? plan, IReadOnlyList<string> overlays, LiveAppKind kind = LiveAppKind.Avalonia,
        string? uninspected = null, Run.Devices.DeviceInspector? device = null)
    {
        this.listener = listener;
        SocketPath = socket;
        Name = name;
        Overlays = overlays;
        Kind = kind;
        Device = device;
        if (device is not null)
        {
            // The app's environment is its device's, which the start writes the inspector's variables into.
            Plan = plan;
        }
        else if (plan is not null)
        {
            string inspector = kind == LiveAppKind.Wpf ? WpfInspectorPath : InspectorPath;
            // An Avalonia app nobody is to see may be started by its inspector, after every other hook, in place of its Main.
            bool unseen = plan.Unseen && kind == LiveAppKind.Avalonia;
            Dictionary<string, string> environment = new(plan.Environment)
            {
                [Wire.SocketVariable] = socket,
                [RuntimeVariables.StartupHooks] = unseen ? StartupHooks.Last(plan.Environment, inspector) : StartupHooks.First(plan.Environment, inspector),
            };
            if (unseen)
            {
                environment[Wire.UnseenVariable] = plan.OnOwnDesktop ? Wire.UnseenRuns : Wire.UnseenEnds;
            }
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
        if (listener is null && device is null)
        {
            // The app still carries the inspector, which finds no Passenger to serve and lets the app run on, saying why.
            State = LiveAppState.Refused;
            Problem = uninspected;
            return;
        }
        // Not held: it waits as long as the app runs, which nothing drawn waits for.
        IdeErrors.Observe(BackgroundWork.Unheld(ServeAsync));
    }

    /// <summary>Where the inspectors' sockets are made: the state's socket folder (<see cref="SocketFolders.Own"/>) while null,
    /// else this folder, a test's own.</summary>
    public static string? SocketsIn { get; set; }

    /// <summary>The inspector as a start puts it in an app on a device, which listens there for Passenger; null for an app of
    /// this machine, which connects to Passenger's socket.</summary>
    public Run.Devices.DeviceInspector? Device { get; }

    /// <summary>The inspector beside the app, as its build carries it.</summary>
    public static string InspectorPath => Path.Combine(AppContext.BaseDirectory, "inspector", "Passenger.Inspector.dll");

    /// <summary>The WPF app's inspector, which only a Windows build carries.</summary>
    public static string WpfInspectorPath => Path.Combine(AppContext.BaseDirectory, "inspector", "wpf", "Passenger.Inspector.Wpf.dll");

    /// <summary>A .NET MAUI app's inspector.</summary>
    public static string MauiInspectorPath => Path.Combine(AppContext.BaseDirectory, "inspector", "maui", "Passenger.Inspector.Maui.dll");

    /// <summary>The framework the app is built on, which its inspector serves.</summary>
    public LiveAppKind Kind { get; }

    /// <summary>Whether the app draws on Avalonia's headless platform, where nobody sees it, as it said in its hello.</summary>
    public bool Headless { get; private set; }

    /// <summary>Whether its inspector started the app through its BuildAvaloniaApp() in place of its Main, as it said in its
    /// hello: an app nobody is to see whose Main would draw where people do.</summary>
    public bool MainLeftOut { get; private set; }

    /// <summary>The framework and its version as the window says it, once the app has said hello: "Avalonia 12.1.6",
    /// "WPF 10.0.1"; the framework alone before.</summary>
    public string Framework => Kind == LiveAppKind.Wpf ? FrameworkSaid ?? "WPF"
        : Kind == LiveAppKind.Maui ? FrameworkSaid ?? "MAUI"
        : Avalonia is { } version ? $"Avalonia {(System.Version.TryParse(version, out Version? read) ? read.ToString(3) : version)}" : "Avalonia";

    /// <summary>What a WPF or MAUI app's inspector said it runs, from its hello.</summary>
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
        (string socket, Socket? listener, string? problem) = Listen("Passenger");
        if (listener is null)
        {
            throw new LiveAppException(problem ?? "Passenger could not listen for its own inspector");
        }
        // Kept for the process, it starts in no call's context, whose window would ride along in its reads and threads for good.
        using (ExecutionContext.SuppressFlow())
        {
            LiveApp app = new("Passenger", socket, listener, plan: null, []);
            try
            {
                Assembly inspector = AssemblyLoadContext.Default.LoadFromAssemblyPath(InspectorPath);
                Type agent = inspector.GetType("Passenger.Inspector.Agent", throwOnError: true)!;
                // Only the person answers Passenger's questions for agents, changes its Settings and trusts a folder: no agent's input does.
                agent.GetMethod("Guard", BindingFlags.Public | BindingFlags.Static)!
                    .Invoke(null, [(Func<object, string?>)MainWindow.KeptFromAgents, (Func<Action, string?>)AgentAsks.Input]);
                agent.GetMethod("Start", BindingFlags.Public | BindingFlags.Static)!
                    .Invoke(null, [socket, Thread.CurrentThread, typeof(Avalonia.AvaloniaObject).Assembly.GetName().Version!.ToString(), null, false, false]);
            }
            catch (Exception error) when (error is IOException or BadImageFormatException or TypeLoadException or MissingMethodException or TargetInvocationException)
            {
                app.Dispose();
                throw new LiveAppException($"Passenger's inspector could not start in Passenger: {error.GetBaseException().Message}");
            }
            own = app;
            return app;
        }
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

    /// <summary>The live app for a plan whose program on this machine takes an inspector (<see cref="Inspected"/>), or that runs
    /// an Avalonia or MAUI app on a device (<see cref="OnDevice"/>), showing these renderer debug overlays from its first window; null
    /// for any other plan. One whose socket could not be made is refused, saying why, its plan carrying the inspector all the
    /// same, which lets the app run on without it.</summary>
    public static LiveApp? For(LaunchPlan plan, IReadOnlyList<string>? overlays = null, bool wpf = true)
    {
        if (plan.Device is not null)
        {
            return plan.Device.App is Run.Devices.SimulatorApp simulator ? OnSimulator(plan, simulator, overlays ?? []) : OnDevice(plan, overlays ?? []);
        }
        if (plan.MacCatalyst?.App is { } catalyst)
        {
            return OnMacCatalyst(plan, catalyst, overlays ?? []);
        }
        if (Inspected(plan, wpf) is not { Kind: var kind })
        {
            return null;
        }
        (string socket, Socket? listener, string? problem) = Listen(plan.Name);
        return new LiveApp(plan.Name, socket, listener, plan, overlays ?? [], kind, problem);
    }

    /// <summary>The program a plan's start hands a startup hook to, as it runs (<see cref="LaunchPlan.DesktopApp"/>), where it is
    /// one this build of Passenger carries an inspector for: an Avalonia app (<see cref="DesktopPrograms.AvaloniaAssembly"/>
    /// beside it), or, with <paramref name="wpf"/>, on Windows a WPF app (a WindowsDesktop app's assembly that references
    /// WPF's); null for any other program, one taking Avalonia.Base alone too, or one its start goes past hooks for.</summary>
    public static (string File, LiveAppKind Kind)? Inspected(LaunchPlan plan, bool wpf = true) =>
        plan.DesktopApp() is not { PastHooks: null, File: var started } ? null
        : DesktopPrograms.BesideAvalonia(started) && File.Exists(InspectorPath) ? (started, LiveAppKind.Avalonia)
        : wpf && OperatingSystem.IsWindows() && File.Exists(WpfInspectorPath) && IsWpf(started) ? (started, LiveAppKind.Wpf)
        : null;

    /// <summary>The live app for a plan that runs an app on a device whose build deployed Avalonia or .NET MAUI (its controls'
    /// assembly among the assemblies its Debug build deployed, Avalonia's first): the framework's inspector, with the files it
    /// carries and, for Avalonia, its overlays, for the start to put in (<see cref="Run.Devices.DeviceLaunches"/>), listening
    /// on an abstract socket of its own for the token only this launch knows; null for any other app, one not installed yet,
    /// or a build of Passenger without that inspector.</summary>
    private static LiveApp? OnDevice(LaunchPlan plan, IReadOnlyList<string> overlays)
    {
        if (plan.Device?.App is not Run.Devices.AndroidApp app)
        {
            return null;
        }
        string assets = Path.Combine(app.Intermediate, "android", "assets");
        if (!Directory.Exists(assets) || DeployedKind(Directory.EnumerateFiles(assets, "*.dll", SearchOption.AllDirectories)) is not { } kind)
        {
            return null;
        }
        string inspector = kind == LiveAppKind.Maui ? MauiInspectorPath : InspectorPath;
        if (!File.Exists(inspector))
        {
            return null;
        }
        string socket = $"passenger-inspector-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        return new LiveApp(plan.Name, $"{Wire.Listens}{socket}", null, plan, kind == LiveAppKind.Avalonia ? overlays : [], kind,
            device: new Run.Devices.DeviceInspector(socket, token, Carried(inspector), More(kind, plan, overlays)));
    }

    /// <summary>The files a start carries into an app on a device for an inspector: the hook first, then its folder's other
    /// assemblies, which it carries (Avalonia's XAML Hot Reload compiler, its headless platform).</summary>
    private static string[] Carried(string inspector) =>
        [inspector, .. Directory.EnumerateFiles(Path.GetDirectoryName(inspector)!, "*.dll").Where(f => !PathNames.Same(f, inspector)).Order(StringComparer.Ordinal)];

    /// <summary>The live app for a plan that runs an app on an iOS simulator whose built bundle carries Avalonia or .NET MAUI: the
    /// framework's inspector, with the files the start carries into the app's data container, listening on a loopback port of
    /// its own (the simulator's .NET has no Unix sockets) for the token only this launch knows, its variables for the start to
    /// give it through simctl (<see cref="Run.Devices.SimulatorApp.Variables"/>); null for any other app, or a build of
    /// Passenger without that inspector.</summary>
    private static LiveApp? OnSimulator(LaunchPlan plan, Run.Devices.SimulatorApp app, IReadOnlyList<string> overlays)
    {
        if (!Directory.Exists(app.Bundle) || DeployedKind(Directory.EnumerateFiles(app.Bundle, "*.dll")) is not { } kind)
        {
            return null;
        }
        string inspector = kind == LiveAppKind.Maui ? MauiInspectorPath : InspectorPath;
        if (!File.Exists(inspector) || Run.Devices.SimulatorApp.FreePort() is not { } port)
        {
            return null;
        }
        string listens = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        return new LiveApp(plan.Name, $"{Wire.Listens}{listens}", null, plan, kind == LiveAppKind.Avalonia ? overlays : [], kind,
            device: new Run.Devices.DeviceInspector(listens, token, Carried(inspector), More(kind, plan, overlays)));
    }

    /// <summary>The live app for a plan that runs a Mac Catalyst app whose bundle carries Avalonia or .NET MAUI: the framework's
    /// inspector, carried into the folder the start carries into (<see cref="Run.Devices.MacCatalystApp.Carried"/>), listening
    /// on a socket there (its sandbox takes a listener only in its container) for the token only this launch knows, its
    /// variables for the start to give it through the opener; null for any other app, one whose build trims .NET's own
    /// assemblies, one whose socket's path is past what a socket's address holds, or a build of Passenger without that inspector.</summary>
    private static LiveApp? OnMacCatalyst(LaunchPlan plan, Run.Devices.MacCatalystApp app, IReadOnlyList<string> overlays)
    {
        if (!app.Whole || !Directory.Exists(app.Assemblies) || DeployedKind(Directory.EnumerateFiles(app.Assemblies, "*.dll")) is not { } kind)
        {
            return null;
        }
        string inspector = kind == LiveAppKind.Maui ? MauiInspectorPath : InspectorPath;
        if (!File.Exists(inspector))
        {
            return null;
        }
        if (!SocketFolders.Fits(app.InspectorSocket))
        {
            IdeErrors.Write($"{plan.Name} runs uninspected: the socket for its inspector in its container is too long, as {SocketFolders.TooLong(app.InspectorSocket)}");
            return null;
        }
        string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        return new LiveApp(plan.Name, $"{Wire.Listens}{app.InspectorSocket}", null, plan, kind == LiveAppKind.Avalonia ? overlays : [], kind,
            device: new Run.Devices.DeviceInspector(app.InspectorSocket, token, Carried(inspector), More(kind, plan, overlays)));
    }

    /// <summary>The variables an app on a device takes for its inspector beyond its hook, socket and token: MAUI's record of
    /// where its XAML made each element and the project the inspector is told, or Avalonia's overlays.</summary>
    private static KeyValuePair<string, string>[] More(LiveAppKind kind, LaunchPlan plan, IReadOnlyList<string> overlays) => kind == LiveAppKind.Maui
        ? [new(RuntimeVariables.XamlDiagnosticsSourceInfo, "1"), .. plan.Project is { } project ? [new KeyValuePair<string, string>(Wire.ProjectVariable, Path.GetDirectoryName(project)!)] : (KeyValuePair<string, string>[])[]]
        : overlays.Count > 0 ? [new(Wire.OverlaysVariable, string.Join(',', overlays))] : [];

    /// <summary>The framework whose inspector an app on a device takes, by the assemblies its build deployed: Avalonia where
    /// its controls are among them, else .NET MAUI where its controls are; null for neither.</summary>
    public static LiveAppKind? DeployedKind(IEnumerable<string> assemblies)
    {
        bool maui = false;
        foreach (string assembly in assemblies)
        {
            string name = Path.GetFileName(assembly);
            if (string.Equals(name, DesktopPrograms.AvaloniaAssembly, StringComparison.OrdinalIgnoreCase))
            {
                return LiveAppKind.Avalonia;
            }
            maui |= string.Equals(name, MauiControls, StringComparison.OrdinalIgnoreCase);
        }
        return maui ? LiveAppKind.Maui : null;
    }

    /// <summary>The assembly of .NET MAUI's controls, which every MAUI app deploys.</summary>
    private const string MauiControls = "Microsoft.Maui.Controls.dll";

    /// <summary>The inspector a XAML file's Avalonia previewer loads to say where it draws each element, and what the
    /// previewer's environment adds for it (the inspector first among its startup hooks, the socket, the file previewed);
    /// null when this build carries no inspector, or no socket could be made.</summary>
    public static (LiveApp Live, Dictionary<string, string> Environment)? ForPreview(string file, IReadOnlyDictionary<string, string> environment)
    {
        string name = $"Preview of {Path.GetFileName(file)}";
        if (!File.Exists(InspectorPath) || Listen(name) is not (string socket, Socket listener, _))
        {
            return null;
        }
        Dictionary<string, string> added = new(StringComparer.Ordinal)
        {
            [Wire.SocketVariable] = socket,
            [RuntimeVariables.StartupHooks] = StartupHooks.First(environment, InspectorPath),
            [Wire.PreviewVariable] = file,
        };
        return (new LiveApp(name, socket, listener, null, []), added);
    }

    /// <summary>Whether an assembly is a WPF app's: it runs on the WindowsDesktop framework (its runtimeconfig.json names it)
    /// and references WPF's assemblies, as a Windows Forms app does not.</summary>
    private static bool IsWpf(string built)
    {
        try
        {
            if (!File.Exists(built) || RuntimeConfig.Read(built)?.OnWindowsDesktop != true)
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

    /// <summary>A socket of its own in Passenger's socket folder for one app's inspector to connect to; where it cannot be
    /// made (its path too long for a socket's address, the folder not the user's), no listener and why the app runs
    /// uninspected.</summary>
    private static (string Socket, Socket? Listener, string? Problem) Listen(string name)
    {
        string socket = $"{Environment.ProcessId}-{Interlocked.Increment(ref made)}.inspector";
        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket = Path.Combine(SocketsIn ?? SocketFolders.Own(StateStore.DefaultRoot), socket);
            if (!SocketFolders.Fits(socket))
            {
                listener.Dispose();
                return (socket, null, $"{name} runs uninspected: the Live Visual Tree's socket for its inspector is too long, as {SocketFolders.TooLong(socket)}");
            }
            // Only a dead Passenger that had this process's number leaves one by this name.
            File.Delete(socket);
            listener.Bind(new UnixDomainSocketEndPoint(socket));
            listener.Listen(1);
        }
        catch (Exception error) when (error is SocketException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            listener.Dispose();
            IdeErrors.Write($"The Live Visual Tree could not listen for {name} at {socket}, so it runs uninspected: {error.Message}");
            return (socket, null, $"{name} runs uninspected: the Live Visual Tree could not listen for its inspector at {socket}: {error.Message}");
        }
        return (socket, listener, null);
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

    /// <summary>The elements the app made at places of a XAML file, by the ids the tree knows them by, no bindings read; of
    /// several made at one, the one nearest the element chosen.</summary>
    public async Task<XamlValues> MadeAsync(XamlRead read) =>
        (await AskAsync(new Request(0, Wire.Made) { Xaml = read })).Values ?? throw new LiveAppException($"{Name} did not say what it made there");

    /// <summary>Where the app shows each element it made from a XAML file of this name, in the newest window showing any.</summary>
    public async Task<IReadOnlyList<PlacedBox>> PlacedAsync(string file) =>
        (await AskAsync(new Request(0, Wire.Placed) { Xaml = new XamlRead(file, [], null) })).Placed ?? [];

    /// <summary>A XAML file's text applied to what the app made from it (XAML Hot Reload), answered with what it came to.</summary>
    public async Task<XamlReloaded> ReloadAsync(XamlChange change) =>
        (await AskAsync(new Request(0, Wire.Reload) { Change = change }, limit: ReloadLimit)).Reloaded ?? throw new LiveAppException($"{Name} did not say what the XAML came to");

    /// <summary>How long the app may take to apply a XAML file: its first compiles Avalonia's XAML compiler itself, slower
    /// again under the debugger.</summary>
    public static TimeSpan ReloadLimit => Waits.Of(TimeSpan.FromSeconds(30));

    /// <summary>Its XAML Hot Reload, given by the session that launched it; null for Passenger's own windows.</summary>
    public XamlHotReload? Xaml { get; set; }

    /// <summary>Input to the app, as a person gives it, answered once it has been handled and laid out.</summary>
    public async Task<InputDone> InputAsync(InputAction input, CancellationToken giveUp = default) =>
        (await AskAsync(new Request(0, Wire.Input) { Input = input }, giveUp)).Done ?? throw new LiveAppException($"{Name} did not say what the input did");

    /// <summary>A request's answer within <see cref="AnswerLimit"/> or <paramref name="limit"/>; <paramref name="giveUp"/> stops
    /// the wait for it (the app paused under the debugger before it could answer), its answer then forgotten.</summary>
    private async Task<Reply> AskAsync(Request request, CancellationToken giveUp = default, TimeSpan? limit = null)
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
            reply = await answer.WaitAsync(limit ?? AnswerLimit, giveUp);
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
            throw new LiveAppException(limit is { } longer ? $"{Name} did not answer within {Durations.Said(longer)}" : NotAnswered(Name));
        }
        return reply.Error is { } why ? throw new LiveAppException(why) : reply;
    }

    /// <summary>How long the app may take to answer: its UI thread busy, or the app paused under the debugger.</summary>
    public static readonly TimeSpan AnswerLimit = TimeSpan.FromSeconds(10);

    /// <summary>What a question the app left unanswered past <see cref="AnswerLimit"/> fails with.</summary>
    public static string NotAnswered(string app) => $"{app} did not answer within {Durations.Said(AnswerLimit)}";

    /// <summary>Takes the app's one connection, then reads what it says until it goes.</summary>
    private async Task ServeAsync()
    {
        Socket client;
        if (Device is { } device)
        {
            if (await ReachAsync(device, closing.Token) is not { } reached)
            {
                return;
            }
            client = reached;
        }
        else
        {
            try
            {
                client = await listener!.AcceptAsync(closing.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            finally
            {
                // The one app it was made for has come (or will not): nothing else may connect.
                listener!.Dispose();
                Forget(SocketPath);
            }
        }
        NetworkStream network = new(client, ownsSocket: true);
        stream = network;
        try
        {
            await foreach (Reply reply in JsonLines.ReadAsync(network, WireJson.Default.Reply,
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

    /// <summary>How long a connection through adb's forward is watched for adb's end of it, which comes at once where the app's
    /// socket is not listening yet.</summary>
    private static readonly TimeSpan Unanswered = TimeSpan.FromMilliseconds(500);

    /// <summary>The inspector in an app on a device reached where the start says once it has put it in (adb's forward, a
    /// simulator's port, a socket in a Mac Catalyst app's container): connected and given the token, again and again until
    /// the app's socket takes it, as adb takes each connection here and ends it at once while the app's inspector is not
    /// listening yet. Null where the session ended first, or it could not be reached, which the app is refused for.</summary>
    private async Task<Socket?> ReachAsync(Run.Devices.DeviceInspector device, CancellationToken stop)
    {
        EndPoint at;
        try
        {
            at = await device.Reached.WaitAsync(stop);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (IOException unforwarded)
        {
            UiThread.Post(() =>
            {
                Problem = $"{Name} runs uninspected: {unforwarded.Message}";
                Become(LiveAppState.Refused);
            });
            return null;
        }
        byte[] token = System.Text.Encoding.UTF8.GetBytes(device.Token + "\n");
        while (!stop.IsCancellationRequested)
        {
            Socket socket = at is UnixDomainSocketEndPoint ? new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
                : new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(at, stop);
                await socket.SendAsync(token, SocketFlags.None, stop);
                if (!socket.Poll(Unanswered, SelectMode.SelectRead) || socket.Available > 0)
                {
                    return socket;
                }
            }
            catch (SocketException)
            {
                // adb's end, no adb, or no socket listening yet: asked again in a moment.
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                return null;
            }
            socket.Dispose();
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), stop);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        return null;
    }

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
            // Why an app was refused, heard as it ended, says why it ended: one nobody was to see, ended before its Main.
            if (reply is { Event: Wire.Hello, Hello.Problem: { } why })
            {
                Problem ??= why;
            }
            return;
        }
        switch (reply.Event)
        {
            case Wire.Hello when reply.Hello is { } hello:
                Avalonia = hello.Avalonia;
                FrameworkSaid = hello.Framework;
                ProcessId = hello.ProcessId;
                Headless = hello.Headless;
                MainLeftOut = hello.MainLeftOut;
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
                    IdeErrors.Write($"{Name}'s inspector left out {Numbers.Said(untold.Messages - Untold.Messages)} of its own messages, too many waiting to be sent at once");
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
        listener?.Dispose();
        stream?.Dispose();
        if (Device is null)
        {
            Forget(SocketPath);
        }
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
