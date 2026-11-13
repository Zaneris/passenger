using System.Diagnostics;
using Passenger.App.Debugging;
using Passenger.App.Editors;
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

    /// <summary>The app ended on its device once its run here ends, however it ended.</summary>
    private static async Task EndOnDeviceAsync(Task<int> ended, AndroidApp app)
    {
        await ended.Ended();
        await DeviceLaunches.EndAsync(app, null);
    }

    /// <summary>Debugs an app installed on a device: Passenger's relay put in it, the app started there by its shell (whose
    /// log is the session's console), and the session attached through the relay as the app's runtime waits for it. Once the
    /// session ends, however it ends, the app ends there and the relay comes out. Null once the session is open, else why not.</summary>
    private async Task<string?> DebugOnDeviceAsync(LaunchPlan plan, AndroidApp app, Working work, BuiltEdits? edits)
    {
        work.Say($"Putting Passenger's debugger in {app.Package}…");
        (RelaySession? relay, string? unrelayed) = await DeviceLaunches.PutRelayAsync(app, work.Stopping);
        if (relay is null)
        {
            await DeviceLaunches.EndAsync(app, null);
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
            await DeviceLaunches.EndAsync(app, relay);
            return $"{plan.Name} could not be started on its device: {unstarted.Message}";
        }
        DebugController debug = session.Debug;
        string? refused = await debug.AttachThroughRelayAsync(plan, relay, edits);
        if (refused is not null || debug.Tabs.FirstOrDefault(t => t.Name == plan.Name && t.IsLive) is not { } tab)
        {
            ChildProcesses.Kill(shell);
            await DeviceLaunches.EndAsync(app, relay);
            return refused ?? $"Debugging {plan.Name} ended as it started";
        }
        ConsoleSink console = tab.Console.Sink;
        Task log = Task.WhenAll(console.PumpAsync(ChildProcesses.Output(shell), ConsoleStream.Output), console.PumpAsync(ChildProcesses.Errors(shell), ConsoleStream.Error));
        IdeErrors.Observe(EndDebuggedOnDeviceAsync(DebugEnded(debug.SessionNamed(plan.Name)), shell, log, app, relay));
        return null;
    }

    /// <summary>A device's debugged app ended once its session ends: its shell here, the app there, the relay taken out.</summary>
    private static async Task EndDebuggedOnDeviceAsync(Task sessionEnded, Process shell, Task log, AndroidApp app, RelaySession relay)
    {
        await sessionEnded;
        await DeviceLaunches.EndAsync(app, relay);
        ChildProcesses.Kill(shell);
        await log.Ended();
    }
}
