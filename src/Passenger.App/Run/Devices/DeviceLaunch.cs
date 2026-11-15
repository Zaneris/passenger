using System.Security.Cryptography;
using Passenger.App.Debugging;
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

/// <summary>What a start put in an app on its device beside its build, which its end takes out: the device's ABI, where
/// the build's environment file pointed, whether the relay and the files carried in (the inspector's, the update handlers'
/// hook) went in, the local ports adb forwards to their sockets, and the abstract sockets it reverses to Passenger.</summary>
public sealed record DevicePut(string Abi, string? Linked, bool Relayed, bool Carried, IReadOnlyList<int> Ports)
{
    /// <summary>The abstract sockets on the device adb's reverse carries to this machine.</summary>
    public IReadOnlyList<string> Reversed { get; init; } = [];
}

/// <summary>Passenger's inspector as a start puts it in an Avalonia app on a device: its files, the variables its startup
/// hook reads (<see cref="Variables"/>), the abstract socket it listens on, and the port Passenger reaches it through once
/// adb forwards one (<see cref="Reach"/>).</summary>
public sealed class DeviceInspector(string socket, string token, IReadOnlyList<string> files, IReadOnlyList<KeyValuePair<string, string>> more)
{
    private readonly TaskCompletionSource<int> reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The abstract socket the inspector listens on in the app.</summary>
    public string Socket { get; } = socket;

    /// <summary>The line Passenger first writes, by which the inspector takes it.</summary>
    public string Token { get; } = token;

    /// <summary>The inspector's files on this machine, put in the app's <see cref="AndroidApp.Carried"/> folder.</summary>
    public IReadOnlyList<string> Files { get; } = files;

    /// <summary>The local port adb forwards to the inspector's socket, once it does; faulted where it could not.</summary>
    public Task<int> Reached => reached.Task;

    public void Reach(int port) => reached.TrySetResult(port);

    public void Unreached(string why) => reached.TrySetException(new IOException(why));

    /// <summary>The variables the app's environment takes for the inspector in <paramref name="folder"/>: its startup hook
    /// first among those the build's environment names (<paramref name="built"/>), its socket and token, and the rest the
    /// launch gives it.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Variables(string folder, IReadOnlyList<KeyValuePair<string, string>> built)
    {
        string hook = $"{folder}/{Path.GetFileName(Files[0])}";
        string? others = built.LastOrDefault(v => v.Key == RuntimeVariables.StartupHooks).Value;
        return [new(RuntimeVariables.StartupHooks, others is { Length: > 0 } ? $"{hook}:{others}" : hook),
            new(Passenger.Inspector.Wire.SocketVariable, $"{Passenger.Inspector.Wire.Listens}{Socket}"), new(Passenger.Inspector.Wire.TokenVariable, Token), .. more];
    }
}

/// <summary>An Android launch's steps around its build: the target found again (an emulator started where its AVD is
/// not running), the app installed and its start read (<see cref="ReadyAsync"/>); for Debug, Passenger's relay put in
/// the app with the runtime's profiler variables in the build's own environment file (<see cref="PutInAsync"/>), and
/// all of it taken out again as the session ends (<see cref="EndAsync"/>), each start's held by its <see cref="DeviceStart"/>.
/// Nothing goes into the project or its build.</summary>
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
        (AndroidApp? app, string? uninstalled) = await AndroidDevices.DeployAsync(project, device.Framework, serial, tools.Adb, globals, cancel);
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

    /// <summary>Waited for once a start has put Passenger's relay in an app and forwarded a port to it, before the start goes
    /// on: a test's way to stop a start there. Null waits for nothing.</summary>
    public static Func<Task>? HeldPuts { get; set; }

    /// <summary>Waited for as an app's end on its device begins, before anything there is ended or taken out: a test's way to
    /// hold a clean-up. Null waits for nothing.</summary>
    public static Func<Task>? HeldEnds { get; set; }

    /// <summary>How long Passenger's end and a folder's wait for its apps' ends on devices, apart from what else they wait for:
    /// force-stop's own wait for the process to go, then each thing taken out, one adb call at a time, on a loaded machine.</summary>
    public static TimeSpan EndLimit => Waits.Of(TimeSpan.FromSeconds(30));

    /// <summary>What a start puts in an app beside its build (<see cref="DeviceStart"/>): the relay (<paramref name="relayed"/>)
    /// and its token, the inspector's files and the update handlers' hook (<paramref name="handlers"/>), copied in by the
    /// app's own shell; the build's environment with their variables; a port forwarded to the relay's and the inspector's
    /// socket, the hook's reversed to Passenger; and, for the relay, the app marked as debugged. Whatever fails partway, or
    /// is stopped once the files are in, is taken out again.</summary>
    internal static async Task<(DevicePut? Put, RelaySession? Relay, string? Problem)> PutInAsync(AndroidApp app, bool relayed, DeviceInspector? inspector,
        UpdateHandlerHook.DeviceReach? handlers, CancellationToken cancel)
    {
        ChildResult abiRead = await AndroidDevices.AdbAsync(app.Adb, app.On("shell", "getprop", "ro.product.cpu.abi"), cancel);
        string abi = abiRead.Output.Trim();
        string? relay = null;
        if (relayed)
        {
            if (AndroidApp.RelayRuntime(abi) is not { } runtime)
            {
                return (null, null, $"Passenger's debugger does not reach an app on an {(abi.Length > 0 ? abi : "unknown")} device: it debugs arm64-v8a and x86_64 ones");
            }
            relay = Path.Combine(AppContext.BaseDirectory, "debugger", "relay", runtime, AndroidApp.RelayFile);
            if (!File.Exists(relay))
            {
                return (null, null, $"This Passenger carries no debug relay for {runtime} ({relay})");
            }
        }
        string built = app.BuiltEnvironment(abi);
        if (!File.Exists(built))
        {
            return (null, null, $"{app.Package} is not a Debug build Passenger can {(relayed ? "debug" : "inspect")}: its build left no environment for {abi} ({built})");
        }
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        string socket = $"passenger-debug-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        IReadOnlyList<KeyValuePair<string, string>> variables = AndroidEnvironment.Read(await File.ReadAllBytesAsync(built, cancel));
        if (relayed)
        {
            variables = AndroidEnvironment.With(variables, app.RelayVariables(abi, socket, token));
        }
        if (inspector is not null)
        {
            variables = AndroidEnvironment.With(variables, inspector.Variables(app.Carried, variables));
        }
        if (handlers is not null)
        {
            variables = AndroidEnvironment.With(variables, handlers.Variables(app.Carried, variables));
        }
        byte[] environment = AndroidEnvironment.Write(variables);
        List<(string Local, string Remote)> pushed = [];
        if (relay is not null)
        {
            pushed.Add((relay, app.Staged("relay")));
        }
        IReadOnlyList<string> files = [.. inspector?.Files ?? [], .. handlers is null ? [] : (string[])[UpdateHandlerHook.HookPath]];
        foreach (string file in files)
        {
            pushed.Add((file, app.Staged(Path.GetFileName(file))));
        }
        string staged = Path.Combine(Path.GetTempPath(), $"passenger-{Guid.NewGuid():N}-environment");
        try
        {
            // Only the person reads it: it holds the tokens.
            AtomicFile.Write(staged, environment, OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            foreach ((string local, string remote) in pushed.Append((staged, app.Staged("environment"))))
            {
                if (await AndroidDevices.AdbAsync(app.Adb, app.On("push", local, remote), cancel) is { Succeeded: false } unpushed)
                {
                    return (null, null, $"Passenger's {(relayed ? "debug relay" : "inspector")} could not be copied to the device: {ChildResult.FirstLineOf(unpushed.Error + unpushed.Output)}");
                }
            }
        }
        finally
        {
            File.Delete(staged);
        }
        string[] carried = [.. files.Select(f => Path.GetFileName(f))];
        // Run to its end however the start is stopped, so what it put in is known to take out again.
        ChildResult put = await AndroidDevices.AdbAsync(app.Adb, app.PutIn(abi, relayed, carried), CancellationToken.None);
        string? linked = put.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => l.StartsWith('/'));
        DevicePut placed = new(abi, linked, relayed, carried.Length > 0, []);
        if (Unput(put) is { } why)
        {
            // What went in before the step that failed comes out again, the build's environment put back.
            await EndAsync(app, placed, stop: false);
            return (null, null, $"Passenger's {(relayed ? "debug relay" : "inspector")} could not be put in {app.Package}: {why}");
        }
        RelaySession? session = null;
        try
        {
            cancel.ThrowIfCancellationRequested();
            if (relayed)
            {
                if (await ForwardAsync(app, socket) is not { } port)
                {
                    await EndAsync(app, placed, stop: false);
                    return (null, null, "adb could not forward a port to the debug relay");
                }
                placed = placed with { Ports = [.. placed.Ports, port] };
                session = new RelaySession($"127.0.0.1:{port}", port, token, app.Deployed(abi), abi, linked);
                if (HeldPuts is { } held)
                {
                    await held();
                }
                await AndroidDevices.AdbAsync(app.Adb, app.SetDebugApp(), cancel);
            }
            if (inspector is not null)
            {
                cancel.ThrowIfCancellationRequested();
                if (await ForwardAsync(app, inspector.Socket) is { } port)
                {
                    placed = placed with { Ports = [.. placed.Ports, port] };
                    inspector.Reach(port);
                }
                else
                {
                    inspector.Unreached("adb could not forward a port to Passenger's inspector in the app");
                }
            }
            if (handlers is not null)
            {
                // Named before adb is asked, so a stop as it makes the reverse takes it out too.
                placed = placed with { Reversed = [handlers.Socket] };
                ChildResult reversed = await AndroidDevices.AdbAsync(app.Adb, app.On("reverse", $"localabstract:{handlers.Socket}", $"tcp:{handlers.Port}"), cancel);
                if (!reversed.Succeeded)
                {
                    // Where the reverse fails, the hook reaches nothing and the app runs on, its update handlers uncalled.
                    placed = placed with { Reversed = [] };
                    IdeLog.Write(IdeLog.Ide, $"adb could not reverse {handlers.Socket} on {app.Serial} to the update handlers' hook: {reversed.Said}");
                }
            }
            return (placed, session, null);
        }
        catch (OperationCanceledException)
        {
            // Stopped partway: what the device holds of the start comes out again, as where a step fails.
            inspector?.Unreached("The start was stopped before Passenger's inspector was reached in the app");
            await EndAsync(app, placed, stop: false);
            throw;
        }
    }

    /// <summary>Why the app's shell could not put in what a start staged (<see cref="AndroidApp.PutIn"/>), in the words of the
    /// step that failed; null where it put it all in. Its exit status and its errors say so, or, where the device's adb
    /// has no shell protocol and so mixes the errors into the output and always exits 0, the output names a step's failure.</summary>
    private static string? Unput(ChildResult put)
    {
        // adb's own notices of starting its server come on its errors too.
        string? said = Lines(put.Error).FirstOrDefault(l => !l.StartsWith("* daemon", StringComparison.Ordinal));
        string? failed = Lines(put.Output).FirstOrDefault(l => l.Contains("run-as:", StringComparison.Ordinal) || l.StartsWith("cp:", StringComparison.Ordinal)
            || l.StartsWith("mkdir:", StringComparison.Ordinal) || l.StartsWith("rm:", StringComparison.Ordinal) || l.Contains(": cd:", StringComparison.Ordinal));
        return said ?? failed ?? (put.Succeeded ? null : ExitCodes.Said(put.ExitCode));

        static string[] Lines(string words) => words.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>A local port adb forwards to an abstract socket in the app, which the log names beside the socket, so a
    /// forward left behind is known by its start; null where it could not, adb's words in the log. Run to its end however
    /// the start is stopped, so the port is known to take out again.</summary>
    private static async Task<int?> ForwardAsync(AndroidApp app, string socket)
    {
        ChildResult forward = await AndroidDevices.AdbAsync(app.Adb, app.On("forward", "tcp:0", $"localabstract:{socket}"), CancellationToken.None);
        int? port = int.TryParse(forward.Output.Trim(), System.Globalization.CultureInfo.InvariantCulture, out int made) ? made : null;
        IdeLog.Write(IdeLog.Ide, port is null ? $"adb could not forward a port to localabstract:{socket} on {app.Serial}: {forward.Said}"
            : $"adb forwarded tcp:{port} to localabstract:{socket} on {app.Serial}");
        return port;
    }

    /// <summary>The app ended on its device (unless <paramref name="stop"/> is false), and what a start put in taken out: the
    /// relay and the inspector's files, the build's environment put back, the ports' forwards removed and, where it was
    /// debugged, the app no longer marked so; at once where adb says the device has gone. Never throws.</summary>
    internal static async Task EndAsync(AndroidApp app, DevicePut? put, bool stop = true)
    {
        try
        {
            if (HeldEnds is { } held)
            {
                await held();
            }
            if (stop && Gone(app, await AndroidDevices.AdbAsync(app.Adb, app.ForceStop(), CancellationToken.None)))
            {
                return;
            }
            if (put is null || Gone(app, await AndroidDevices.AdbAsync(app.Adb, app.TakeOut(put.Abi, put.Linked, put.Relayed, put.Carried), CancellationToken.None)))
            {
                return;
            }
            foreach (int port in put.Ports)
            {
                if (Gone(app, await RemoveAsync(app, "forward", $"tcp:{port}")))
                {
                    return;
                }
            }
            foreach (string socket in put.Reversed)
            {
                if (Gone(app, await RemoveAsync(app, "reverse", $"localabstract:{socket}")))
                {
                    return;
                }
            }
            if (put.Relayed)
            {
                await AndroidDevices.AdbAsync(app.Adb, app.ClearDebugApp(), CancellationToken.None);
            }
        }
        catch (Exception gone) when (gone is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            IdeLog.Write(IdeLog.Ide, $"{app.Package} on {app.Serial} was not ended cleanly: {gone.Message}");
        }
    }

    /// <summary>Whether adb answered that the app's device is no longer there (unplugged, its emulator ended, offline), so
    /// nothing more of an end can reach it, which the log says.</summary>
    private static bool Gone(AndroidApp app, ChildResult answer)
    {
        string words = answer.Error + answer.Output;
        if (answer.Succeeded || !words.Contains($"device '{app.Serial}' not found", StringComparison.Ordinal) && !words.Contains("device offline", StringComparison.Ordinal))
        {
            return false;
        }
        IdeLog.Write(IdeLog.Ide, $"{app.Serial} was gone before {app.Package}'s end there was done, so nothing more of it was taken out: {answer.Said}");
        return true;
    }

    /// <summary>A forward or a reverse a start made taken out, adb's answer in the log whatever it was, so one left behind
    /// says why.</summary>
    private static async Task<ChildResult> RemoveAsync(AndroidApp app, string kind, string what)
    {
        ChildResult removed = await AndroidDevices.AdbAsync(app.Adb, app.On(kind, "--remove", what), CancellationToken.None);
        IdeLog.Write(IdeLog.Ide, removed.Succeeded ? $"adb took out the {kind} of {what} on {app.Serial}"
            : $"adb did not take out the {kind} of {what} on {app.Serial}: {removed.Said}");
        return removed;
    }
}
