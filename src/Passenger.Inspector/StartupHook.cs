using System.Reflection;
using System.Runtime.InteropServices;
using Passenger.Core;
using Passenger.Inspector;

/// <summary>The runtime calls this before the app's Main, as DOTNET_STARTUP_HOOKS names this assembly: only in a run
/// Passenger launched for its Live Visual Tree, never in the app as shipped. Nothing here touches Avalonia, which the app has
/// not set up yet: the hook takes itself out of the environment (so nothing the app starts loads it), checks the app's
/// Avalonia, turns on Avalonia's own diagnostics for the profiler's frames, and starts the inspector in a context of its own.
/// Nothing it does stops the app, but where nobody is to see it (<see cref="Wire.UnseenVariable"/>): there an app whose Main
/// would draw where people see it is started by the hook on Avalonia's headless platform in the Main's place
/// (<see cref="Unseen"/>), or where it cannot be, ends before its Main unless a desktop of its own hides it. A Main whose own
/// code uses that platform chooses for itself, and ends if it starts one of a screen (<see cref="KeepOffScreens"/>).</summary>
internal static class StartupHook
{
    /// <summary>The switch Avalonia's Diagnostic class reads once, as Avalonia first measures or renders: on, Avalonia makes its
    /// layout activities and its frame meters, which cost nothing until something listens (FrameRecorder, while a profiler's
    /// session has its events on).</summary>
    private const string AvaloniaDiagnostics = "Avalonia.Diagnostics.Diagnostic.IsEnabled";

    /// <summary>The exit code of an app nobody is to see that ended before it drew where people see it.</summary>
    internal const int EndedUnseen = 1;

    /// <summary>Avalonia's platforms that draw where people see them, by their assemblies.</summary>
    private static readonly string[] ScreenPlatforms = ["Avalonia.Native", "Avalonia.X11", "Avalonia.Win32", "Avalonia.LinuxFramebuffer"];

    /// <summary>Says to Passenger why the app ends over the inspector's own connection, once it has one.</summary>
    private static Action<string>? told;

    public static void Initialize()
    {
        string? socket = Environment.GetEnvironmentVariable(Wire.SocketVariable);
        string? overlays = Environment.GetEnvironmentVariable(Wire.OverlaysVariable);
        string? preview = Environment.GetEnvironmentVariable(Wire.PreviewVariable);
        string? unseen = Environment.GetEnvironmentVariable(Wire.UnseenVariable);
        Unlist();
        if (socket is not { Length: > 0 })
        {
            return;
        }
        // Only a Main whose own code uses the headless platform can choose it, so only such a Main runs: what it chooses is seen as it starts.
        bool itself = unseen is not null && AppAvaloniaContext.ProgramUsesHeadless();
        bool ends = unseen == Wire.UnseenEnds && !itself;
        if (itself && unseen == Wire.UnseenEnds)
        {
            KeepOffScreens(socket);
        }
        // The app as its BuildAvaloniaApp() builds it on the headless platform, which the hook starts in place of its Main.
        Func<string[], int>? start = null;
        try
        {
            Thread main = Thread.CurrentThread;
            (Version? avalonia, bool served) = AppAvaloniaContext.ProgramAvalonia();
            if (!served)
            {
                string why = avalonia is null
                    ? "The app does not use Avalonia"
                    : $"The app runs Avalonia {avalonia.ToString(3)}; the Live Visual Tree needs Avalonia {StartupHooks.AvaloniaMajor}";
                if (ends && avalonia is not null)
                {
                    End(socket, avalonia, $"{why}, and only an Avalonia {StartupHooks.AvaloniaMajor} app draws on Avalonia's headless platform where nobody sees it");
                }
                Connection.Refuse(socket, avalonia, why);
                return;
            }
            AppContext.SetSwitch(AvaloniaDiagnostics, true);
            string self = typeof(StartupHook).Assembly.Location;
            Assembly inside = new AppAvaloniaContext("Passenger inspector", self).LoadFromAssemblyPath(self);
            // An evaluation of BindingValues.Read finds this copy first: it hands the read to the one that inspects.
            BindingValues.Live = inside.GetType("Passenger.Inspector.BindingValues", throwOnError: true)!
                .GetMethod(nameof(BindingValues.Read), BindingFlags.Public | BindingFlags.Static)!
                .CreateDelegate<Func<string, object?[]>>();
            inside.GetType("Passenger.Inspector.FrameRecorder", throwOnError: true)!
                .GetMethod("Start", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, null);
            if (unseen is not null && !itself)
            {
                Type hidden = inside.GetType("Passenger.Inspector.Unseen", throwOnError: true)!;
                if (hidden.GetMethod(nameof(Unseen.Prepare), BindingFlags.Public | BindingFlags.Static)!.CreateDelegate<Func<string?>>()() is { } why)
                {
                    if (ends)
                    {
                        End(socket, avalonia, $"The app cannot draw on Avalonia's headless platform, where nobody sees it: {why}");
                    }
                    InspectorTrace.Step($"runs its Main on a desktop of its own: {why}");
                }
                else
                {
                    start = hidden.GetMethod(nameof(Unseen.Run), BindingFlags.Public | BindingFlags.Static)!.CreateDelegate<Func<string[], int>>();
                }
            }
            Type agent = inside.GetType("Passenger.Inspector.Agent", throwOnError: true)!;
            if (preview is { Length: > 0 })
            {
                agent.GetMethod("Preview", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [preview]);
            }
            agent.GetMethod("Start", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [socket, main, avalonia!.ToString(), overlays, unseen == Wire.UnseenEnds, start is not null]);
            told = agent.GetMethod(nameof(Agent.Tell), BindingFlags.Public | BindingFlags.Static)!.CreateDelegate<Action<string>>();
        }
        catch (Exception error)
        {
            InspectorTrace.Step($"could not start in the app: {error}");
            string problem = $"The inspector could not start in the app: {error.GetBaseException().Message}";
            if (ends && start is null)
            {
                End(socket, null, problem);
            }
            Connection.Refuse(socket, null, problem);
        }
        if (start is not null)
        {
            // The app's own exceptions end it from here as from its Main; its end is the process's, the Main never run.
            Environment.Exit(start(Environment.GetCommandLineArgs()[1..]));
        }
    }

    /// <summary>Ends an app nobody is to see the moment its Main starts a platform of a screen: at the platform's first call
    /// into the system, before it has a window or a place in the Dock. Only loaded, as a builder choosing the headless
    /// platform after <c>UsePlatformDetect</c> leaves it, a platform has called nothing and nothing ends.</summary>
    private static void KeepOffScreens(string socket) =>
        AppDomain.CurrentDomain.AssemblyLoad += (_, loaded) =>
        {
            if (loaded.LoadedAssembly.GetName().Name is { } platform && ScreenPlatforms.Contains(platform))
            {
                NativeLibrary.SetDllImportResolver(loaded.LoadedAssembly, (_, _, _) =>
                {
                    End(socket, null, $"The app's Main started {platform}, which draws where people see it", "as it started");
                    return IntPtr.Zero;
                });
            }
        };

    /// <summary>Ends an app nobody is to see before it draws where people see it, saying why to Passenger and in the app's
    /// own console, which outlasts a socket the app's end may close unread.</summary>
    private static void End(string socket, Version? avalonia, string why, string when = "before its Main")
    {
        string said = $"{why}. It was ended {when}, as a Passenger nobody sees opens no window on the person's screen";
        Console.Error.WriteLine(said);
        if (told is { } say)
        {
            say(said);
        }
        else
        {
            Connection.Refuse(socket, avalonia, said);
        }
        Environment.Exit(EndedUnseen);
    }

    /// <summary>This hook, the socket, the overlays, the file previewed and whether nobody is to see the app, out of the environment the app's own programs inherit;
    /// other hooks stay.</summary>
    private static void Unlist()
    {
        OwnEnvironment.Set(Wire.SocketVariable, null);
        OwnEnvironment.Set(Wire.OverlaysVariable, null);
        OwnEnvironment.Set(Wire.PreviewVariable, null);
        OwnEnvironment.Set(Wire.UnseenVariable, null);
        StartupHooks.TakeOut(typeof(StartupHook).Assembly.Location);
    }
}
