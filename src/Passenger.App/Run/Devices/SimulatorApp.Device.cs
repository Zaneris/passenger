using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Passenger.App.Debugging;
using Passenger.Core;

namespace Passenger.App.Run.Devices;

/// <summary>An iOS simulator's app as a start gives it Passenger's relay (<see cref="DeviceStart"/>): nothing is put in it, and
/// its end only ends it.</summary>
public sealed partial record SimulatorApp : IDeviceApp
{
    /// <summary>The relay Passenger carries for the runtime identifier the app was built for; null where it carries none.</summary>
    public string? Relay()
    {
        string relay = Path.Combine(AppContext.BaseDirectory, "debugger", "relay", RuntimeIdentifier, RelayFile);
        return File.Exists(relay) ? relay : null;
    }

    /// <summary>Nothing goes in the app: the relay's variables and a free port of this Mac for it are what the start gives it
    /// (<see cref="RelaySession.Given"/>), the app's assemblies read from its built bundle. The inspector and the update
    /// handlers' hook reach Passenger as a program of this Mac's do, through their variables in the plan.</summary>
    public Task<(DevicePut? Put, RelaySession? Relay, string? Problem)> PutInAsync(bool relayed, DeviceInspector? inspector, UpdateHandlerHook.DeviceReach? handlers,
        CancellationToken cancel)
    {
        DevicePut put = new(string.Empty, null, relayed, false, []);
        // The inspector listens on its loopback port in the app, which Passenger reaches as it is.
        inspector?.Reach(int.Parse(inspector.Socket, System.Globalization.CultureInfo.InvariantCulture));
        if (!relayed)
        {
            return Task.FromResult<(DevicePut?, RelaySession?, string?)>((put, null, null));
        }
        if (Relay() is not { } relay)
        {
            return Task.FromResult<(DevicePut?, RelaySession?, string?)>((null, null,
                $"This Passenger carries no debug relay for {RuntimeIdentifier} ({Path.Combine(AppContext.BaseDirectory, "debugger", "relay", RuntimeIdentifier, RelayFile)})"));
        }
        if (FreePort() is not { } port)
        {
            return Task.FromResult<(DevicePut?, RelaySession?, string?)>((null, null, "No port of this Mac could be had for the debug relay"));
        }
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        RelaySession session = new($"127.0.0.1:{port}", port, token, Bundle, RuntimeIdentifier, null) { Given = RelayVariables(relay, port, token) };
        return Task.FromResult<(DevicePut?, RelaySession?, string?)>((put, session, null));
    }

    /// <summary>A port of the loopback no program listens on now, for the relay to listen on; null where none could be had.</summary>
    internal static int? FreePort()
    {
        try
        {
            using Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.LocalEndPoint!).Port;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>The variables a start gives the app over the plan's <paramref name="environment"/>: the inspector's, listening
    /// on its port (its hook loaded from where Passenger carries it, first among the startup hooks), the relay's and the update
    /// handlers' hook's (first again), each where the start has one.</summary>
    public static Dictionary<string, string> Variables(IReadOnlyDictionary<string, string> environment, DeviceInspector? inspector, RelaySession? relay,
        UpdateHandlerHook? handlers)
    {
        Dictionary<string, string> given = new(environment);
        if (inspector is not null)
        {
            Over(given, inspector.Variables(Path.GetDirectoryName(inspector.Files[0])!, [.. given]));
        }
        Over(given, relay?.Given ?? new Dictionary<string, string>());
        if (handlers is not null)
        {
            Over(given, handlers.Over(given));
        }
        return given;

        static void Over(Dictionary<string, string> under, IEnumerable<KeyValuePair<string, string>> over)
        {
            foreach ((string name, string value) in over.ToList())
            {
                under[name] = value;
            }
        }
    }

    /// <summary>The app ended on its simulator where <paramref name="stop"/>, simctl's answer in the log where it could not
    /// (one already gone answers that it found nothing to end). Never throws.</summary>
    public async Task EndAsync(DevicePut? put, bool stop)
    {
        if (!stop)
        {
            return;
        }
        try
        {
            if (DeviceLaunches.HeldEnds is { } held)
            {
                await held();
            }
            else if (DeviceLaunches.EndHold > TimeSpan.Zero)
            {
                await Task.Delay(DeviceLaunches.EndHold);
            }
            ChildResult ended = await AppleSimulators.SimctlAsync(Simctl, Terminate(), CancellationToken.None);
            if (!ended.Succeeded && !ended.Said.Contains("found nothing to terminate", StringComparison.Ordinal))
            {
                IdeLog.Write(IdeLog.Ide, $"{Package} on the simulator {Udid} was not ended cleanly: {ended.Said}");
            }
        }
        catch (Exception unreached) when (unreached is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            IdeLog.Write(IdeLog.Ide, $"{Package} on the simulator {Udid} was not ended cleanly: {unreached.Message}");
        }
    }

    /// <summary>Nothing to put back: the app's environment is its start's alone.</summary>
    public Task PutBackEnvironmentAsync(DevicePut put) => Task.CompletedTask;
}
