using Passenger.Core;
using Passenger.Inspector;

/// <summary>The runtime calls this before the app's Main, as DOTNET_STARTUP_HOOKS names this assembly: only in a WPF app's
/// run Passenger launched, never in the app as shipped. It takes itself and its socket out of the environment (so nothing the
/// app starts loads it; ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO, which WPF reads as it first loads XAML, stays), checks that the
/// app runs WPF, and starts the inspector, which needs no context of its own: WPF's assemblies are the app's shared framework.
/// Nothing it does can stop the app.</summary>
internal static class StartupHook
{
    public static void Initialize()
    {
        string? socket = Environment.GetEnvironmentVariable(Wire.SocketVariable);
        string? project = Environment.GetEnvironmentVariable(Wire.ProjectVariable);
        Unlist();
        if (socket is not { Length: > 0 })
        {
            return;
        }
        try
        {
            if (Problem() is { } problem)
            {
                Connection.Refuse(socket, null, problem);
                return;
            }
            Start(socket, project);
        }
        catch (Exception error)
        {
            InspectorTrace.Step($"could not start in the app: {error}");
            Connection.Refuse(socket, null, $"The inspector could not start in the app: {error.GetBaseException().Message}");
        }
    }

    /// <summary>Apart from <see cref="Initialize"/>, so an app without WPF fails here, inside its try, as WPF's types load.</summary>
    private static void Start(string socket, string? project)
    {
        InspectorTrace.Step($"the startup hook, on thread {Environment.CurrentManagedThreadId} ({Thread.CurrentThread.GetApartmentState()})");
        Passenger.Inspector.Wpf.Agent.Start(socket, Thread.CurrentThread, project);
    }

    /// <summary>This hook, the socket and the project, out of the environment the app's own programs inherit; other hooks stay.</summary>
    private static void Unlist()
    {
        Environment.SetEnvironmentVariable(Wire.SocketVariable, null);
        Environment.SetEnvironmentVariable(Wire.ProjectVariable, null);
        StartupHooks.TakeOut(typeof(StartupHook).Assembly.Location);
    }

    /// <summary>Why the inspector cannot serve the app: it has no WPF among the assemblies it will load.</summary>
    private static string? Problem() => StartupHooks.TrustedAssembly("PresentationFramework.dll") is null ? "The app does not use WPF" : null;
}
