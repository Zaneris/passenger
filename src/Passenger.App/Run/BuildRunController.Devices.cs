using System.Diagnostics;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.LiveTree;
using Passenger.App.Run.Devices;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>Run and Debug of an app on a device or an emulator (<see cref="LaunchPlan.Device"/>): its build installed
/// there as the plan's start prepares it, Run's program the device's shell writing the app's log, Debug's session
/// attached through Passenger's relay in the app, and the app ended there once either ends.</summary>
public sealed partial class BuildRunController
{
    /// <summary>Whether a configuration runs its project's app on a device or an emulator: one is chosen for it, or its
    /// project runs nowhere else; this machine, chosen or in force with nothing chosen, runs it here.</summary>
    private bool RunsOnDevice(RunConfiguration chosen) =>
        chosen.Program.Length == 0 && session.Projects.FirstOrDefault(p => PathNames.Same(p.Path, chosen.Project)) is { } project
        && DeviceTargets.PlatformsOf(project.TargetFrameworks).Count > 0
        && (session.Targets.InForce(project) is { } target ? target.Kind != TargetKind.ThisMachine : DeviceTargets.RunsOnlyElsewhere(project.TargetFrameworks));

    /// <summary>The app ended on its device once its run here ends, however it ended, and what its start put in taken out.</summary>
    private static async Task EndOnDeviceAsync(Task<int> ended, AndroidApp app, DevicePut? put)
    {
        await ended.Ended();
        await DeviceLaunches.EndAsync(app, put);
    }

    /// <summary>Passenger's inspector put in an Avalonia or MAUI app Run starts on a device, before its start; where it cannot be,
    /// the app runs uninspected, the Run window saying why.</summary>
    private async Task<DevicePut?> InspectOnDeviceAsync(AndroidApp app, LiveApp? inspected, Working work)
    {
        if (inspected?.Device is not { } device)
        {
            return null;
        }
        work.Say($"Putting Passenger's inspector in {app.Package}…");
        (DevicePut? put, string? problem) = await DeviceLaunches.PutInspectorAsync(app, device, work.Stopping);
        if (problem is not null)
        {
            device.Unreached(problem);
            parts.Views.Run.Append($"{problem}; it runs uninspected\n");
        }
        return put;
    }

    /// <summary>Passenger's relay put in an app Run starts on a device with Hot Reload, its inspector beside it where it is an
    /// Avalonia app's and the hook that calls its update handlers after each update, so the debugger attaches quietly as its
    /// start waits there (<see cref="AttachOnDeviceForHotReloadAsync"/>): the relay, what was put in and the hook, else why
    /// not, the app then running without Hot Reload.</summary>
    private async Task<(RelaySession? Relay, DevicePut? Put, UpdateHandlerHook? Handlers, string? Problem)> RelayOnDeviceAsync(LaunchPlan plan, AndroidApp app,
        LiveApp? inspected, Working work)
    {
        work.Say($"Putting Passenger's debugger in {app.Package} for Hot Reload…");
        UpdateHandlerHook? handlers = HandlersOnDevice(plan);
        (RelaySession? relay, DevicePut? put, string? unrelayed) = await DeviceLaunches.PutRelayAsync(app, inspected?.Device, handlers, work.Stopping);
        if (relay is null)
        {
            handlers?.Dispose();
            return (null, null, null, unrelayed ?? "Passenger's debugger could not be put in the app");
        }
        return (relay, put, handlers, null);
    }

    /// <summary>The hook that calls the update handlers of a plan's app on a device after each Hot Reload update; null where
    /// it cannot be loaded there.</summary>
    private static UpdateHandlerHook? HandlersOnDevice(LaunchPlan plan) =>
        plan.Device is { } device ? UpdateHandlerHook.OnDevice(device.Framework, plan.Name) : null;

    /// <summary>An app Run started on a device with Passenger's relay in it, attached to quietly for Hot Reload alone, which
    /// lets its start there go, each update told to <paramref name="handlers"/>: what its applies come to is said in
    /// <paramref name="console"/> (null for the terminal's tab) and the status bar, which says <paramref name="running"/>
    /// once attached. After an attach that fails, saying why, the app goes on without Hot Reload: the relay lets its start go
    /// as the connection ends, or once its wait is over.</summary>
    private async Task AttachOnDeviceForHotReloadAsync(string name, RelaySession relay, ConsoleSink? console, Working work, BuiltEdits? edits, string running,
        LiveApp? app, ProgramBuild? builds, UpdateHandlerHook? handlers)
    {
        work.Say($"Attaching to {name} on its device for Hot Reload…");
        string? refused = await session.Debug.AttachForHotReloadThroughRelayAsync(relay, name, (line, error) =>
        {
            console?.Write($"{line}\n", error ? ConsoleStream.Error : ConsoleStream.Output);
            parts.Say(line);
        }, work.Stopping, edits, app, builds, handlers);
        parts.Say(refused is null ? running : $"Running {name} without Hot Reload: {refused}");
    }

    /// <summary>Debugs an app installed on a device: Passenger's relay put in it, the app started there by its shell (whose
    /// log is the session's console), and the session attached through the relay as the app's runtime waits for it. Once the
    /// session ends, however it ends, the app ends there and the relay comes out. Null once the session is open, else why not.</summary>
    private async Task<string?> DebugOnDeviceAsync(LaunchPlan plan, AndroidApp app, Working work, BuiltEdits? edits)
    {
        work.Say($"Putting Passenger's debugger in {app.Package}…");
        // An Avalonia or MAUI app's inspector goes in beside the relay, which the Live Visual Tree reaches.
        LiveApp? inspected = session.Inspected(plan).App;
        // With Hot Reload, the hook that calls its update handlers after each update goes in too.
        UpdateHandlerHook? handlers = session.Debug.HotReloadOn() ? HandlersOnDevice(plan) : null;
        (RelaySession? relay, DevicePut? put, string? unrelayed) = await DeviceLaunches.PutRelayAsync(app, inspected?.Device, handlers, work.Stopping);
        if (relay is null)
        {
            handlers?.Dispose();
            inspected?.Dispose();
            await DeviceLaunches.EndAsync(app, put);
            return unrelayed;
        }
        work.Say($"Starting {plan.Name} on its device under the debugger…");
        parts.Dock.Show(ToolWindowIds.Debug);
        parts.Views.Debug.ShowTab(console: true);
        DebugProject = Path.GetDirectoryName(plan.Project);
        Process shell;
        try
        {
            // Read here, its log going to the session's console once the session has one.
            System.Diagnostics.ProcessStartInfo start = plan.Start();
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            shell = await ChildProcesses.StartAsync(start);
        }
        catch (System.ComponentModel.Win32Exception unstarted)
        {
            handlers?.Dispose();
            inspected?.Dispose();
            await DeviceLaunches.EndAsync(app, put);
            return $"{plan.Name} could not be started on its device: {unstarted.Message}";
        }
        DebugController debug = session.Debug;
        string? refused = await debug.AttachThroughRelayAsync(plan, relay, edits, inspected, handlers);
        if (refused is not null || debug.Tabs.FirstOrDefault(t => t.Name == plan.Name && t.IsLive) is not { } tab)
        {
            ChildProcesses.Kill(shell);
            inspected?.Dispose();
            await DeviceLaunches.EndAsync(app, put);
            return refused ?? $"Debugging {plan.Name} ended as it started";
        }
        ConsoleSink console = tab.Console.Sink;
        Task log = Task.WhenAll(console.PumpAsync(ChildProcesses.Output(shell), ConsoleStream.Output), console.PumpAsync(ChildProcesses.Errors(shell), ConsoleStream.Error));
        session.EndsOnDevices(plan.Name, EndDebuggedOnDeviceAsync(DebugEnded(debug.SessionNamed(plan.Name)), shell, log, app, put), () => DeviceLaunches.EndAsync(app, put));
        return null;
    }

    /// <summary>A device's debugged app ended once its session ends: its shell here, the app there, what its start put in taken out.</summary>
    private static async Task EndDebuggedOnDeviceAsync(Task sessionEnded, Process shell, Task log, AndroidApp app, DevicePut? put)
    {
        await sessionEnded;
        await DeviceLaunches.EndAsync(app, put);
        ChildProcesses.Kill(shell);
        await log.Ended();
    }
}
