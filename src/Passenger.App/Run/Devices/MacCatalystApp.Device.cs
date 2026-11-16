using System.Security.Cryptography;
using Passenger.App.Debugging;
using Passenger.Core;

namespace Passenger.App.Run.Devices;

/// <summary>A Mac Catalyst app as a start gives it Passenger's relay or inspector (<see cref="DeviceStart"/>): what it loads of
/// Passenger's carried into <see cref="Carried"/>, its variables given through the opener, and at its end the app ended and
/// the carried folder taken out, the sockets in it too.</summary>
public sealed partial record MacCatalystApp : IDeviceApp
{
    /// <summary>The relay Passenger carries for the runtime identifier the app was built for; null where it carries none.</summary>
    public string? Relay()
    {
        string relay = RelayCarried();
        return File.Exists(relay) ? relay : null;
    }

    private string RelayCarried() => Path.Combine(AppContext.BaseDirectory, "debugger", "relay", RuntimeIdentifier, RelayFile);

    /// <summary>The opener says nothing of the app's process, which is found by its program (<see cref="MacCatalystApps.StartedAsync"/>).</summary>
    public string? StartedIn(Passenger.Text.TextSnapshot shown) => null;

    /// <summary>What the app loads of Passenger's (the inspector's files; the relay and the update handlers' hook where
    /// <paramref name="relayed"/>) copied into <see cref="Carried"/>, the inspector then reached at its socket there, and the
    /// relay's variables, listening at its own socket there, what the start gives it (<see cref="RelaySession.Given"/>), the
    /// app's assemblies read from its bundle.</summary>
    public async Task<(DevicePut? Put, RelaySession? Relay, string? Problem)> PutInAsync(bool relayed, DeviceInspector? inspector, UpdateHandlerHook.DeviceReach? handlers,
        CancellationToken cancel)
    {
        string? relay = relayed ? Relay() : null;
        if (relayed && relay is null)
        {
            return (null, null, $"This Passenger carries no debug relay for {RuntimeIdentifier} ({RelayCarried()})");
        }
        if (relayed && !SocketFolders.Fits(RelaySocket))
        {
            return (null, null, $"The debug relay's socket in {Package}'s container is too long, as {SocketFolders.TooLong(RelaySocket)}");
        }
        string[] files = [.. inspector?.Files ?? [], .. relay is null ? [] : (string[])[relay],
            .. relayed && Whole && File.Exists(UpdateHandlerHook.HookPath) ? (string[])[UpdateHandlerHook.HookPath] : []];
        DevicePut put = new(string.Empty, null, relayed, Carried: true, []);
        cancel.ThrowIfCancellationRequested();
        if (CarriedIn.Copy(Carried, files) is { } uncarried)
        {
            await EndAsync(put, stop: false);
            return (null, null, $"What {Package} loads of Passenger's could not be put in its container ({Carried}): {uncarried}");
        }
        inspector?.Reach(InspectorSocket);
        if (relay is null)
        {
            return (put, null, null);
        }
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        RelaySession session = new(RelaySocket, 0, token, Assemblies, RuntimeIdentifier, null)
        {
            Given = RelayVariables(Path.Combine(Carried, RelayFile), RelaySocket, token),
        };
        return (put, session, null);
    }

    /// <summary>The variables a start gives the app over the plan's <paramref name="environment"/>, through the opener, whose
    /// environment the system hands the app: the inspector's, the relay's and the update handlers' hook's, each loaded from
    /// <see cref="Carried"/> where the start has one.</summary>
    public Dictionary<string, string> Variables(IReadOnlyDictionary<string, string> environment, DeviceInspector? inspector, RelaySession? relay,
        UpdateHandlerHook? handlers) => CarriedIn.Variables(Carried, environment, inspector, relay, handlers);

    /// <summary>The app's process started since <see cref="Since"/> ended where <paramref name="stop"/>, then <see cref="Carried"/>
    /// taken out, whatever of it is there (the hook's socket is made before what is copied in). Never throws.</summary>
    public async Task EndAsync(DevicePut? put, bool stop)
    {
        if (stop)
        {
            if (DeviceLaunches.HeldEnds is { } held)
            {
                await held();
            }
            if (MacCatalystApps.Find(Executable, Since) is int id)
            {
                ChildProcesses.KillLeft(id);
            }
        }
        CarriedIn.TakeOut(Carried, Package);
    }

    /// <summary>Nothing to put back: the app's environment is its start's alone.</summary>
    public Task PutBackEnvironmentAsync(DevicePut put) => Task.CompletedTask;
}
