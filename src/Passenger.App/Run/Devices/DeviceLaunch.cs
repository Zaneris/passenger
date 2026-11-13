using System.Security.Cryptography;
using Passenger.Core;

namespace Passenger.App.Run.Devices;

/// <summary>Where a launch plan runs its app other than on this machine: the target chosen and the framework built for
/// it, and once its build is installed there, the app's start (<see cref="App"/>).</summary>
public sealed record DeviceLaunch(KeptTarget Target, string Framework)
{
    public AndroidApp? App { get; init; }
}

/// <summary>A Debug start's relay in an app on a device: the local address adb forwards to its socket, the token it takes
/// a client by, the build's copies of what the app loads, and what to put back once debugging ends.</summary>
public sealed record RelaySession(string Address, int Port, string Token, string Modules, string Abi, string? Linked);

/// <summary>An Android launch's steps around its build: the target found again (an emulator started where its AVD is
/// not running), the app installed and its start read (<see cref="ReadyAsync"/>); for Debug, Passenger's relay put in
/// the app with the runtime's profiler variables in the build's own environment file (<see cref="PutRelayAsync"/>), and
/// all of it taken out again as the session ends (<see cref="EndAsync"/>). Nothing goes into the project or its build.</summary>
public static class DeviceLaunches
{
    /// <summary>A plan's device found and its app installed, the plan's program then the device's shell running the app and
    /// writing its log (<see cref="AndroidApp.Run"/>); why not, in words for the person. <paramref name="listed"/> is told the
    /// targets found on the way, an emulator it started among them as running.</summary>
    public static async Task<(LaunchPlan? Plan, string? Problem)> ReadyAsync(LaunchPlan plan, IReadOnlyDictionary<string, string> globals, bool unseen, Action<string> say,
        Action<IReadOnlyList<DeviceTarget>> listed, CancellationToken cancel)
    {
        if (plan.Device is not { } device || plan.Project is not { } project)
        {
            return (plan, null);
        }
        if (device.Target.Platform != TargetPlatform.Android)
        {
            return (null, Refusals.NotYetOn(device.Target.Name));
        }
        string name = Path.GetFileNameWithoutExtension(project);
        say($"Looking for {device.Target.Name}…");
        (IReadOnlyList<DeviceTarget> targets, AndroidTools? tools, string? unlisted) = await AndroidDevices.ListAsync(project, device.Framework, globals, cancel);
        if (tools is null)
        {
            return (null, unlisted);
        }
        if (DeviceTargets.Find(device.Target, targets) is not { } target)
        {
            return (null, DeviceTargets.Gone(device.Target, targets, name));
        }
        string serial = target.Id;
        if (!target.Running)
        {
            (string? booted, string? unbooted) = await AndroidDevices.BootAsync(tools, target.Avd ?? target.Id, unseen, say, cancel);
            if (booted is null)
            {
                return (null, unbooted);
            }
            serial = booted;
            targets = [.. targets.Select(t => t == target ? t with { Id = booted, Running = true } : t)];
        }
        listed(targets);
        say($"Installing {name} on {target.Name}…");
        (AndroidApp? app, string? uninstalled) = await AndroidDevices.DeployAsync(project, device.Framework, serial, globals, cancel);
        if (app is null)
        {
            return (null, $"{name} could not be installed on {target.Name}: {uninstalled}");
        }
        return (plan with
        {
            Program = app.Adb,
            Arguments = app.Run(),
            Leading = app.Run().Count,
            WorkingDirectory = Path.GetDirectoryName(project)!,
            Device = device with { App = app },
        }, null);
    }

    /// <summary>Passenger's relay put in an installed Debug app, with the runtime's profiler variables beside the build's own
    /// and a token only Passenger knows, a local port forwarded to its socket and the app marked as debugged: the relay's
    /// session, or why not.</summary>
    public static async Task<(RelaySession? Relay, string? Problem)> PutRelayAsync(AndroidApp app, CancellationToken cancel)
    {
        ChildResult abiRead = await AndroidDevices.AdbAsync(app.Adb, app.On("shell", "getprop", "ro.product.cpu.abi"), cancel);
        string abi = abiRead.Output.Trim();
        if (AndroidApp.RelayRuntime(abi) is not { } runtime)
        {
            return (null, $"Passenger's debugger does not reach an app on an {(abi.Length > 0 ? abi : "unknown")} device: it debugs arm64-v8a and x86_64 ones");
        }
        string relay = Path.Combine(AppContext.BaseDirectory, "debugger", "relay", runtime, AndroidApp.RelayFile);
        if (!File.Exists(relay))
        {
            return (null, $"This Passenger carries no debug relay for {runtime} ({relay})");
        }
        string built = app.BuiltEnvironment(abi);
        if (!File.Exists(built))
        {
            return (null, $"{app.Package} is not a Debug build Passenger can debug: its build left no environment for {abi} ({built})");
        }
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        string socket = $"passenger-debug-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        byte[] environment = AndroidEnvironment.Write(AndroidEnvironment.With(AndroidEnvironment.Read(await File.ReadAllBytesAsync(built, cancel)),
            app.RelayVariables(abi, socket, token)));
        string staged = Path.Combine(Path.GetTempPath(), $"passenger-{Guid.NewGuid():N}-environment");
        try
        {
            // Only the person reads it: it holds the token.
            AtomicFile.Write(staged, environment, OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            foreach ((string local, string remote) in (IEnumerable<(string, string)>)[(relay, app.Staged("relay")), (staged, app.Staged("environment"))])
            {
                if (await AndroidDevices.AdbAsync(app.Adb, app.On("push", local, remote), cancel) is { Succeeded: false } unpushed)
                {
                    return (null, $"The debug relay could not be copied to the device: {ChildResult.FirstLineOf(unpushed.Error + unpushed.Output)}");
                }
            }
        }
        finally
        {
            File.Delete(staged);
        }
        ChildResult put = await AndroidDevices.AdbAsync(app.Adb, app.PutRelayIn(abi), cancel);
        string[] said = put.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!put.Succeeded || said.Any(l => l.Contains("run-as:", StringComparison.Ordinal) || l.StartsWith("cp:", StringComparison.Ordinal)))
        {
            return (null, $"The debug relay could not be put in {app.Package} (a Debug build run-as reaches): {ChildResult.FirstLineOf(put.Error + put.Output)}");
        }
        string? linked = said.FirstOrDefault(l => l.StartsWith('/'));
        ChildResult forward = await AndroidDevices.AdbAsync(app.Adb, app.On("forward", "tcp:0", $"localabstract:{socket}"), cancel);
        if (!int.TryParse(forward.Output.Trim(), System.Globalization.CultureInfo.InvariantCulture, out int port))
        {
            await AndroidDevices.AdbAsync(app.Adb, app.TakeRelayOut(abi, linked), CancellationToken.None);
            return (null, $"adb could not forward a port to the debug relay: {ChildResult.FirstLineOf(forward.Error + forward.Output)}");
        }
        await AndroidDevices.AdbAsync(app.Adb, app.SetDebugApp(), cancel);
        return (new RelaySession($"127.0.0.1:{port}", port, token, app.Deployed(abi), abi, linked), null);
    }

    /// <summary>The app ended on its device, and where it was debugged, its relay taken out, the build's environment put back,
    /// the port's forward removed and the app no longer marked as debugged. Never throws.</summary>
    public static async Task EndAsync(AndroidApp app, RelaySession? relay)
    {
        try
        {
            await AndroidDevices.AdbAsync(app.Adb, app.ForceStop(), CancellationToken.None);
            if (relay is not null)
            {
                await AndroidDevices.AdbAsync(app.Adb, app.TakeRelayOut(relay.Abi, relay.Linked), CancellationToken.None);
                await AndroidDevices.AdbAsync(app.Adb, app.On("forward", "--remove", $"tcp:{relay.Port}"), CancellationToken.None);
                await AndroidDevices.AdbAsync(app.Adb, app.ClearDebugApp(), CancellationToken.None);
            }
        }
        catch (Exception gone) when (gone is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            IdeLog.Write(IdeLog.Ide, $"{app.Package} on {app.Serial} was not ended cleanly: {gone.Message}");
        }
    }
}
