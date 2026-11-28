using Passenger.Core;
using Passenger.Inspector;

/// <summary>The runtime calls this before the app's Main, as DOTNET_STARTUP_HOOKS names this assembly: only in a Windows Forms
/// app's run Passenger launched, never in the app as shipped. It takes itself and its socket out of the environment (so
/// nothing the app starts loads it), checks that the app runs Windows Forms, and starts the inspector, which needs no
/// context of its own: Windows Forms' assemblies are the app's shared framework. Nothing it does can stop the app.</summary>
internal static class StartupHook
{
    public static void Initialize()
    {
        string? socket = Environment.GetEnvironmentVariable(Wire.SocketVariable);
        Unlist();
        if (socket is not { Length: > 0 })
        {
            return;
        }
        try
        {
            if (StartupHooks.TrustedAssembly("System.Windows.Forms.dll") is null)
            {
                Connection.Refuse(socket, null, "The app does not use Windows Forms");
                return;
            }
            Start(socket);
        }
        catch (Exception error)
        {
            InspectorTrace.Step($"could not start in the app: {error}");
            Connection.Refuse(socket, null, $"The inspector could not start in the app: {error.GetBaseException().Message}");
        }
    }

    /// <summary>Apart from <see cref="Initialize"/>, so an app without Windows Forms fails here, inside its try, as its types load.</summary>
    private static void Start(string socket)
    {
        InspectorTrace.Step($"the startup hook, on thread {Environment.CurrentManagedThreadId} ({Thread.CurrentThread.GetApartmentState()})");
        Passenger.Inspector.WinForms.Agent.Start(socket);
    }

    /// <summary>This hook and the socket, out of the environment the app's own programs inherit; other hooks stay.</summary>
    private static void Unlist()
    {
        OwnEnvironment.Set(Wire.SocketVariable, null);
        StartupHooks.TakeOut(typeof(StartupHook).Assembly.Location);
    }
}
