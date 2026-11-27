using Passenger.App.Debugging;

namespace Passenger.App.Run.Devices;

/// <summary>An Android app as a start puts Passenger's relay and inspector in it, through adb (<see cref="DeviceLaunches"/>).</summary>
public sealed partial record AndroidApp : IDeviceApp
{
    /// <summary>Put in through adb, the app's own shell copying in what it staged (<see cref="DeviceLaunches.PutInAsync"/>).</summary>
    public Task<(DevicePut? Put, RelaySession? Relay, string? Problem)> PutInAsync(bool relayed, DeviceInspector? inspector, UpdateHandlerHook.DeviceReach? handlers,
        CancellationToken cancel) => DeviceLaunches.PutInAsync(this, relayed, inspector, handlers, cancel);

    /// <summary>Ended by force-stop and taken out by the app's own shell (<see cref="DeviceLaunches.EndAsync"/>).</summary>
    public Task EndAsync(DevicePut? put, bool stop) => DeviceLaunches.EndAsync(this, put, stop);

    /// <summary>The build's environment linked back (<see cref="DeviceLaunches.PutBackEnvironmentAsync"/>).</summary>
    public Task PutBackEnvironmentAsync(DevicePut put) => DeviceLaunches.PutBackEnvironmentAsync(this, put);

    /// <summary>adb ending the app it started and clearing the mark as the debugged app a relayed put set, while it is still
    /// this app's (<see cref="ClearDebugApp"/>), in one call of the device's shell.</summary>
    public string? LeftEnd(DevicePut? put, bool started)
    {
        string[] steps = [.. started ? [$"am force-stop {Package}"] : (string[])[], .. put?.Relayed == true ? [ClearingDebugApp] : (string[])[]];
        return steps.Length == 0 ? null
            : string.Join(' ', On("shell", string.Join("; ", steps)).Prepend(Adb).Select(word => global::Passenger.Terminal.ShellWords.Quoted(word, global::Passenger.Core.ShellKind.Posix)));
    }
}
