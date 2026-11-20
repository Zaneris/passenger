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

    /// <summary>Whether a configuration runs its project's Mac Catalyst app on this machine, as the target in force says.</summary>
    private bool RunsOnMacCatalyst(RunConfiguration chosen) =>
        chosen.Program.Length == 0 && session.Projects.FirstOrDefault(p => PathNames.Same(p.Path, chosen.Project)) is { } project
        && session.Targets.InForce(project) is { Platform: TargetPlatform.MacCatalyst, Kind: TargetKind.ThisMachine }
        && DeviceTargets.FrameworkFor(project.TargetFrameworks, TargetPlatform.MacCatalyst) is not null;

    /// <summary>The app ended on its device once its run here ends, however it ended, and what its start put in taken out.</summary>
    private static async Task EndOnDeviceAsync(Task<int> ended, DeviceStart start)
    {
        await ended.Ended();
        await start.TakeOutAsync();
    }

    /// <summary>Passenger's inspector put in an Avalonia or MAUI app Run starts on a device, before its start; where it cannot be,
    /// the app runs uninspected, the Run window saying why.</summary>
    private async Task InspectOnDeviceAsync(DeviceStart start, LiveApp? inspected, Working work)
    {
        if (inspected?.Device is not { } device)
        {
            return;
        }
        work.Say($"Putting Passenger's inspector in {start.App.Package}…");
        if (await start.PutInspectorAsync(device, work.Stopping) is { } problem)
        {
            device.Unreached(problem);
            parts.Views.Run.Append($"{problem}; it runs uninspected\n");
        }
    }

    /// <summary>Passenger's relay put in an app Run starts on a device with Hot Reload, its inspector beside it where it is an
    /// Avalonia app's and the hook that calls its update handlers after each update, so the debugger attaches quietly as its
    /// start waits there (<see cref="AttachOnDeviceForHotReloadAsync"/>): the relay and the hook, else why not, the app then
    /// running without Hot Reload.</summary>
    private async Task<(RelaySession? Relay, UpdateHandlerHook? Handlers, string? Problem)> RelayOnDeviceAsync(LaunchPlan plan, DeviceStart start, LiveApp? inspected,
        Working work)
    {
        work.Say($"Putting Passenger's debugger in {start.App.Package} for Hot Reload…");
        UpdateHandlerHook? handlers = HandlersOnDevice(plan);
        (RelaySession? relay, string? unrelayed) = await start.PutRelayAsync(inspected?.Device, handlers, work.Stopping);
        if (relay is null)
        {
            handlers?.Dispose();
            return (null, null, unrelayed ?? "Passenger's debugger could not be put in the app");
        }
        return (relay, handlers, null);
    }

    /// <summary>The hook that calls the update handlers of a plan's app on a device after each Hot Reload update; null where
    /// it cannot be loaded there. A simulator's app reaches it as a program of this Mac does, its start giving it its variables;
    /// a Mac Catalyst app at a socket in the folder its start carries into, which its sandbox lets it reach.</summary>
    private static UpdateHandlerHook? HandlersOnDevice(LaunchPlan plan) => plan switch
    {
        { Device.App: SimulatorApp } => UpdateHandlerHook.OnSimulator(plan),
        { Device: { } device } => UpdateHandlerHook.OnDevice(device.Framework, plan.Name),
        { MacCatalyst.App: { Whole: true } catalyst } => CarriedIn.Make(catalyst.Carried) is null ? UpdateHandlerHook.At(plan, catalyst.HandlersSocket) : null,
        _ => null,
    };

    /// <summary>An app Run started on a device with Passenger's relay in it, attached to quietly for Hot Reload alone, which
    /// lets its start there go, each update told to <paramref name="handlers"/>: what its applies come to is said in
    /// <paramref name="console"/> (null for the terminal's tab) and the status bar, which says <paramref name="running"/>
    /// once attached, the build's environment then back in the app (<see cref="DeviceStart.Started"/>). After an attach that
    /// fails, saying why, the app goes on without Hot Reload: the relay lets its start go as the connection ends, or once its wait is over.</summary>
    private async Task AttachOnDeviceForHotReloadAsync(string name, DeviceStart start, RelaySession relay, ConsoleSink? console, Working work, BuiltEdits? edits,
        string running, LiveApp? app, ProgramBuild? builds, UpdateHandlerHook? handlers)
    {
        work.Say($"Attaching to {name} on its device for Hot Reload…");
        string? refused = await session.Debug.AttachForHotReloadThroughRelayAsync(relay, name, (line, error) =>
        {
            console?.Write($"{line}\n", error ? ConsoleStream.Error : ConsoleStream.Output);
            parts.Say(line);
        }, work.Stopping, edits, app, builds, handlers);
        if (refused is null)
        {
            start.Started();
        }
        Running(work, refused is null ? running : $"Running {name} without Hot Reload: {refused}");
    }

    /// <summary>Debugs an app installed on a device: Passenger's relay put in it, the app started there by its shell (whose
    /// log is the session's console), and the session attached through the relay as the app's runtime waits for it. Once the
    /// session ends, however it ends, the app ends there and the relay comes out; Stop and the folder's end wait for it from
    /// the start's first step there (<see cref="DeviceStart"/>). Null once the session is open, else why not.</summary>
    private async Task<string?> DebugOnDeviceAsync(LaunchPlan plan, IDeviceApp app, Working work, BuiltEdits? edits)
    {
        work.Say($"Putting Passenger's debugger in {app.Package}…");
        // An Avalonia or MAUI app's inspector goes in beside the relay, which the Live Visual Tree reaches.
        LiveApp? inspected = session.Inspected(plan).App;
        // With Hot Reload, the hook that calls its update handlers after each update goes in too.
        UpdateHandlerHook? handlers = session.Debug.HotReloadOn() ? HandlersOnDevice(plan) : null;
        using DeviceStart onDevice = session.StartsOnDevice(plan.Name, app);
        (RelaySession? relay, string? unrelayed) = await onDevice.PutRelayAsync(inspected?.Device, handlers, work.Stopping);
        if (relay is null || work.Stopping.IsCancellationRequested)
        {
            // Refused, or stopped once the relay is in: the app never starts, and what went in comes out first.
            handlers?.Dispose();
            inspected?.Dispose();
            await onDevice.TakeOutAsync();
            return relay is null ? unrelayed : Canceled(work, plan.Name);
        }
        work.Say($"Starting {plan.Name} {(app is MacCatalystApp ? "on this Mac" : "on its device")} under the debugger…");
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
            // The inspector's, the relay's and the hook's: a simulator's app takes them from simctl's launch, a Mac Catalyst app from its opener.
            IReadOnlyDictionary<string, string> given = app switch
            {
                SimulatorApp simulator => SimulatorApp.Given(simulator.Variables(plan.Environment, inspected?.Device, relay, handlers)),
                MacCatalystApp catalyst => catalyst.Variables(plan.Environment, inspected?.Device, relay, handlers),
                _ => new Dictionary<string, string>(),
            };
            foreach ((string name, string value) in given)
            {
                start.Environment[name] = value;
            }
            shell = await ChildProcesses.StartAsync(start);
        }
        catch (System.ComponentModel.Win32Exception unstarted)
        {
            handlers?.Dispose();
            inspected?.Dispose();
            await onDevice.TakeOutAsync();
            return $"{plan.Name} could not be started on its device: {unstarted.Message}";
        }
        DebugController debug = session.Debug;
        string? refused = await debug.AttachThroughRelayAsync(plan, relay, edits, inspected, handlers);
        if (refused is not null || debug.Tabs.FirstOrDefault(t => t.Name == plan.Name && t.IsLive) is not { } tab)
        {
            ChildProcesses.Kill(shell);
            inspected?.Dispose();
            await onDevice.TakeOutAsync();
            return refused ?? $"Debugging {plan.Name} ended as it started";
        }
        // The app has read its environment as its runtime started, which the relay's hello says.
        onDevice.Started();
        ConsoleSink console = tab.Console.Sink;
        Task log = Task.WhenAll(console.PumpAsync(ChildProcesses.Output(shell), ConsoleStream.Output), console.PumpAsync(ChildProcesses.Errors(shell), ConsoleStream.Error));
        Task ended = DebugEnded(debug.SessionNamed(plan.Name));
        if (app is MacCatalystApp opened)
        {
            // The system starts it apart from its opener: it ends with its session here, and with Passenger.
            IdeErrors.Observe(MacCatalystApps.EndWithAsync(opened, ended));
        }
        else if (app is SimulatorApp simulator)
        {
            IdeErrors.Observe(simulator.TakeOnAsync(ended));
        }
        onDevice.EndsWith(EndDebuggedOnDeviceAsync(ended, shell, log, onDevice));
        return null;
    }

    /// <summary>A device's debugged app ended once its session ends: its shell here, the app there, what its start put in taken out.</summary>
    private static async Task EndDebuggedOnDeviceAsync(Task sessionEnded, Process shell, Task log, DeviceStart start)
    {
        await sessionEnded;
        await start.TakeOutAsync();
        ChildProcesses.Kill(shell);
        await log.Ended();
    }
}
