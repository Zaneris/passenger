using System.Reflection;
using Passenger.Core;
using Passenger.Inspector;

/// <summary>The runtime calls this before the app's code, as DOTNET_STARTUP_HOOKS names this assembly: only in a .NET MAUI
/// app's run Passenger launched, never in the app as shipped. It takes itself, its socket and its project out of the
/// environment (so nothing the app starts loads it; ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO, which MAUI reads as it first
/// records where its XAML made an element, stays), checks that the app has MAUI's controls, and starts the inspector,
/// which needs no context of its own: MAUI's assemblies are the app's. Nothing it does can stop the app.</summary>
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
            if (Controls() is not { } maui)
            {
                Connection.Refuse(socket, null, "The app does not use .NET MAUI");
                return;
            }
            Start(socket, maui, project);
        }
        catch (Exception error)
        {
            InspectorTrace.Step($"could not start in the app: {error}");
            Connection.Refuse(socket, null, $"The inspector could not start in the app: {error.GetBaseException().Message}");
        }
    }

    /// <summary>Apart from <see cref="Initialize"/>, so an app whose MAUI cannot be read fails here, inside its try.</summary>
    private static void Start(string socket, Assembly maui, string? project)
    {
        InspectorTrace.Step($"the startup hook, on thread {Environment.CurrentManagedThreadId}, MAUI {maui.GetName().Version}");
        Passenger.Inspector.Maui.Agent.Start(socket, maui, project);
    }

    /// <summary>This hook, its socket and its project, out of the environment the app's own programs inherit; other hooks stay.</summary>
    private static void Unlist()
    {
        OwnEnvironment.Set(Wire.SocketVariable, null);
        OwnEnvironment.Set(Wire.ProjectVariable, null);
        StartupHooks.TakeOut(typeof(StartupHook).Assembly.Location);
    }

    /// <summary>MAUI's controls as the app loads them, from the assemblies the runtime was given, else (an app on a device,
    /// whose host finds its assemblies itself) as the runtime finds them by name; null when the app has none.</summary>
    private static Assembly? Controls()
    {
        try
        {
            return StartupHooks.TrustedAssembly("Microsoft.Maui.Controls.dll") is { } path
                ? System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path)
                : System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("Microsoft.Maui.Controls"));
        }
        catch (Exception error) when (error is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }
}
