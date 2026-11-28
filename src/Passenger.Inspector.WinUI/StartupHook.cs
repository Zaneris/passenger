using Passenger.Core;
using Passenger.Inspector;

/// <summary>The runtime calls this before the app's Main, as DOTNET_STARTUP_HOOKS names this assembly: only in a WinUI app's
/// run Passenger launched, never in the app as shipped. It takes itself and its socket out of the environment (so nothing
/// the app starts loads it; ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO, which WinUI reads as it loads XAML, stays), checks that the
/// app runs WinUI, and starts the inspector on a thread of its own. Nothing it does can stop the app.</summary>
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
            if (StartupHooks.TrustedAssembly("Microsoft.WinUI.dll") is null && !File.Exists(Path.Combine(AppContext.BaseDirectory, "Microsoft.WinUI.dll")))
            {
                Connection.Refuse(socket, null, "The app does not use WinUI");
                return;
            }
            InspectorTrace.Step($"the startup hook, on thread {Environment.CurrentManagedThreadId}");
            Passenger.Inspector.WinUI.Agent.Start(socket, project);
        }
        catch (Exception error)
        {
            InspectorTrace.Step($"could not start in the app: {error}");
            Connection.Refuse(socket, null, $"The inspector could not start in the app: {error.GetBaseException().Message}");
        }
    }

    /// <summary>This hook, the socket and the project, out of the environment the app's own programs inherit; other hooks stay.</summary>
    private static void Unlist()
    {
        OwnEnvironment.Set(Wire.SocketVariable, null);
        OwnEnvironment.Set(Wire.ProjectVariable, null);
        StartupHooks.TakeOut(typeof(StartupHook).Assembly.Location);
    }
}
