using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Passenger.App.Debugging;
using Passenger.Core;

namespace Passenger.App.Run.Devices;

/// <summary>An iOS simulator's app as a start gives it Passenger's relay (<see cref="DeviceStart"/>): what the app loads of
/// Passenger's carried into its own data container (<see cref="Carried"/>), its variables given by the start, and at its end
/// the app ended and the carried files taken out.</summary>
public sealed partial record SimulatorApp : IDeviceApp
{
    /// <summary>The relay Passenger carries for the runtime identifier the app was built for; null where it carries none.</summary>
    public string? Relay()
    {
        string relay = Path.Combine(AppContext.BaseDirectory, "debugger", "relay", RuntimeIdentifier, RelayFile);
        return File.Exists(relay) ? relay : null;
    }

    /// <summary>What the app loads of Passenger's (the inspector's files; the relay and the update handlers' hook where
    /// <paramref name="relayed"/>) copied into <see cref="Carried"/>, the relay's variables and a free port of this Mac for it
    /// what the start gives it (<see cref="RelaySession.Given"/>), the app's assemblies read from its built bundle.</summary>
    public async Task<(DevicePut? Put, RelaySession? Relay, string? Problem)> PutInAsync(bool relayed, DeviceInspector? inspector, UpdateHandlerHook.DeviceReach? handlers,
        CancellationToken cancel)
    {
        string? relay = relayed ? Relay() : null;
        if (relayed && relay is null)
        {
            return (null, null, $"This Passenger carries no debug relay for {RuntimeIdentifier} ({Path.Combine(AppContext.BaseDirectory, "debugger", "relay", RuntimeIdentifier, RelayFile)})");
        }
        int? port = relayed ? FreePort() : null;
        if (relayed && port is null)
        {
            return (null, null, "No port of this Mac could be had for the debug relay");
        }
        string[] files = [.. inspector?.Files ?? [], .. relay is null ? [] : (string[])[relay], .. relayed && File.Exists(UpdateHandlerHook.HookPath) ? (string[])[UpdateHandlerHook.HookPath] : []];
        DevicePut put = new(string.Empty, null, relayed, files.Length > 0, []);
        cancel.ThrowIfCancellationRequested();
        if (files.Length > 0 && CarryIn(files) is { } uncarried)
        {
            await EndAsync(put, stop: false);
            return (null, null, $"What {Package} loads of Passenger's could not be put in its data container ({Carried}): {uncarried}");
        }
        // The inspector listens on its loopback port in the app, which Passenger reaches as it is.
        inspector?.Reach(int.Parse(inspector.Socket, System.Globalization.CultureInfo.InvariantCulture));
        if (port is not { } listens)
        {
            return (put, null, null);
        }
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        RelaySession session = new($"127.0.0.1:{listens}", listens, token, Bundle, RuntimeIdentifier, null)
        {
            Given = RelayVariables(Path.Combine(Carried, RelayFile), listens, token),
        };
        return (put, session, null);
    }

    /// <summary>The files copied into <see cref="Carried"/> by their names, made afresh; why not, or null.</summary>
    private string? CarryIn(IReadOnlyList<string> files)
    {
        try
        {
            Directory.CreateDirectory(Carried);
            foreach (string file in files)
            {
                File.Copy(file, Path.Combine(Carried, Path.GetFileName(file)), overwrite: true);
            }
            return null;
        }
        catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException)
        {
            return unwritten.Message;
        }
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
    /// on its port (its hook loaded from <see cref="Carried"/>, first among the startup hooks), the relay's and the update
    /// handlers' hook's (carried too, first again), each where the start has one.</summary>
    public Dictionary<string, string> Variables(IReadOnlyDictionary<string, string> environment, DeviceInspector? inspector, RelaySession? relay,
        UpdateHandlerHook? handlers)
    {
        Dictionary<string, string> given = new(environment);
        if (inspector is not null)
        {
            Over(given, inspector.Variables(Carried, [.. given]));
        }
        Over(given, relay?.Given ?? new Dictionary<string, string>());
        if (handlers is not null)
        {
            Over(given, handlers.Over(given, Path.Combine(Carried, Path.GetFileName(UpdateHandlerHook.HookPath))));
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
    /// (one already gone answers that it found nothing to end), then what a start carried in taken out. Never throws.</summary>
    public async Task EndAsync(DevicePut? put, bool stop)
    {
        if (stop)
        {
            await StopAsync();
        }
        if (put is { Carried: true })
        {
            try
            {
                Directory.Delete(Carried, recursive: true);
            }
            catch (Exception left) when (left is IOException or UnauthorizedAccessException)
            {
                IdeLog.Write(IdeLog.Ide, $"What a start carried into {Package} on the simulator {Udid} was not all taken out ({Carried}): {left.Message}");
            }
        }
    }

    /// <summary>The app ended on its simulator, simctl's answer in the log where it could not. Never throws.</summary>
    private async Task StopAsync()
    {
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
