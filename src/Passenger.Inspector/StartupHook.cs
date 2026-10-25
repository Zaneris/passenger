using System.Reflection;
using Passenger.Core;
using Passenger.Inspector;

/// <summary>The runtime calls this before the app's Main, as DOTNET_STARTUP_HOOKS names this assembly: only in a run
/// Passenger launched for its Live Visual Tree, never in the app as shipped. Nothing here touches Avalonia, which the app has
/// not set up yet: the hook takes itself out of the environment (so nothing the app starts loads it), checks the app's
/// Avalonia, turns on Avalonia's own diagnostics for the profiler's frames, and starts the inspector in a context of its own.
/// Nothing it does can stop the app.</summary>
internal static class StartupHook
{
    /// <summary>The switch Avalonia's Diagnostic class reads once, as Avalonia first measures or renders: on, Avalonia makes its
    /// layout activities and its frame meters, which cost nothing until something listens (FrameRecorder, while a profiler's
    /// session has its events on).</summary>
    private const string AvaloniaDiagnostics = "Avalonia.Diagnostics.Diagnostic.IsEnabled";

    public static void Initialize()
    {
        string? socket = Environment.GetEnvironmentVariable(Wire.SocketVariable);
        string? overlays = Environment.GetEnvironmentVariable(Wire.OverlaysVariable);
        Unlist();
        if (socket is not { Length: > 0 })
        {
            return;
        }
        try
        {
            Thread main = Thread.CurrentThread;
            (Version? avalonia, bool served) = AppAvaloniaContext.ProgramAvalonia();
            if (!served)
            {
                Connection.Refuse(socket, avalonia, avalonia is null
                    ? "The app does not use Avalonia"
                    : $"The app runs Avalonia {avalonia.ToString(3)}; the Live Visual Tree needs Avalonia {StartupHooks.AvaloniaMajor}");
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
            inside.GetType("Passenger.Inspector.Agent", throwOnError: true)!
                .GetMethod("Start", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [socket, main, avalonia!.ToString(), overlays]);
        }
        catch (Exception error)
        {
            InspectorTrace.Step($"could not start in the app: {error}");
            Connection.Refuse(socket, null, $"The inspector could not start in the app: {error.GetBaseException().Message}");
        }
    }

    /// <summary>This hook, the socket and the overlays, out of the environment the app's own programs inherit; other hooks stay.</summary>
    private static void Unlist()
    {
        Environment.SetEnvironmentVariable(Wire.SocketVariable, null);
        Environment.SetEnvironmentVariable(Wire.OverlaysVariable, null);
        StartupHooks.TakeOut(typeof(StartupHook).Assembly.Location);
    }
}
