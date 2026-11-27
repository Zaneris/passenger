using System.Diagnostics;
using Passenger.App.Run;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Testing;

/// <summary>Where a device test project's tests run (.NET's androidtest, iostest and maccatalysttest make one): its app on
/// the target its project runs on (<see cref="DeviceChoices.InForce"/>), made ready there once a run as Run makes an app
/// ready (<see cref="DeviceLaunches"/>, <see cref="MacCatalystApps"/>), a simulator it boots shut down with the folder, then
/// started there for each test host with the Testing Platform's server options as its own arguments: an iOS simulator's
/// app and a Mac Catalyst app are processes of this Mac, which reach Passenger's client on its loopback. An Android app's
/// instrumentation runs its tests (<see cref="InstrumentationRunner"/>), each start held as Run holds one on a device
/// (<paramref name="starts"/>, null once the folder has ended). A run begins with <see cref="Begin"/>, which takes each
/// project's target as chosen then and forgets what the last run made ready.</summary>
public sealed class DeviceTestHosts(Func<ProjectModel, KeptTarget?> targets, Func<ProjectModel, IReadOnlyDictionary<string, string>> globals, bool unseen,
    Action<string, string> booted, Action<ProjectModel, TargetPlatform, IReadOnlyList<DeviceTarget>> listed, Func<string, IDeviceApp, Task<DeviceStart?>>? starts = null)
{
    private readonly Lock gate = new();

    /// <summary>Each test project's target and global properties as the run began, by its path.</summary>
    private Dictionary<string, (KeptTarget? Target, IReadOnlyDictionary<string, string> Globals)> given = new(PathNames.Comparer);

    /// <summary>Each assembly's app made ready this run, by its path.</summary>
    private Dictionary<string, Task<(DeviceTestApp? App, string? Problem)>> ready = new(StringComparer.Ordinal);

    private Action<string> say = _ => { };

    /// <summary>A run of these test projects begins, each stage of an app's readying told to <paramref name="stage"/>.</summary>
    public void Begin(IEnumerable<ProjectModel> projects, Action<string> stage)
    {
        UiThread.Check();
        Dictionary<string, (KeptTarget?, IReadOnlyDictionary<string, string>)> now = new(PathNames.Comparer);
        foreach (ProjectModel project in projects.Where(DeviceTargets.TestsInApp))
        {
            now[project.Path] = (targets(project), globals(project));
        }
        lock (gate)
        {
            given = now;
            ready = new(StringComparer.Ordinal);
            say = stage;
        }
    }

    /// <summary>The assembly's app made ready where its project runs, once this run however many hosts it starts; else why
    /// not, in words for the person.</summary>
    public Task<(DeviceTestApp? App, string? Problem)> ReadyAsync(TestAssembly assembly, CancellationToken cancel)
    {
        lock (gate)
        {
            if (!ready.TryGetValue(assembly.Path, out Task<(DeviceTestApp?, string?)>? made))
            {
                (KeptTarget? Target, IReadOnlyDictionary<string, string> Globals) its = given.GetValueOrDefault(assembly.Project.Path, (null, new Dictionary<string, string>()));
                Action<string> stage = say;
                ready[assembly.Path] = made = BackgroundWork.Run(() => ReadyCoreAsync(assembly, its.Target, its.Globals, stage, cancel), cancel);
            }
            return made;
        }
    }

    /// <summary>A start of an app on its device held as Run holds one (<see cref="DeviceStart"/>), so Stop and the folder's
    /// and Passenger's ends wait until it has ended there; null once the folder has ended, which starts nothing more.</summary>
    public Task<DeviceStart?> StartsOnDeviceAsync(string name, IDeviceApp app) => starts?.Invoke(name, app) ?? Task.FromResult<DeviceStart?>(null);

    private async Task<(DeviceTestApp? App, string? Problem)> ReadyCoreAsync(TestAssembly assembly, KeptTarget? target, IReadOnlyDictionary<string, string> properties,
        Action<string> stage, CancellationToken cancel)
    {
        ProjectModel project = assembly.Project;
        TargetPlatform platform = DeviceTargets.PlatformOf(assembly.Framework) ?? TargetPlatform.Desktop;
        LaunchPlan plan = new() { Name = project.Name, Program = string.Empty, WorkingDirectory = project.Folder, Project = project.Path, Unseen = unseen };
        if (platform == TargetPlatform.MacCatalyst)
        {
            if (!OperatingSystem.IsMacOS())
            {
                return (null, $"{project.Name} cannot run here: {Refusals.MacCatalystOnMacAlone}");
            }
            (LaunchPlan? opened, string? unopened) = await MacCatalystApps.ReadyAsync(plan with { MacCatalyst = new MacCatalystLaunch(assembly.Framework) }, properties, cancel);
            return opened?.MacCatalyst?.App is not null ? (new DeviceTestApp(opened, unseen), null) : (null, unopened);
        }
        if (target is not { Kind: not TargetKind.ThisMachine } chosen || chosen.Platform != platform)
        {
            return (null, Refusals.NoRunTarget(project.Name));
        }
        (LaunchPlan? installed, string? uninstalled) = await DeviceLaunches.ReadyAsync(plan with { Device = new DeviceLaunch(chosen, assembly.Framework) }, properties, unseen,
            stage, found => listed(project, platform, found), cancel, booted);
        return installed?.Device?.App switch
        {
            SimulatorApp or AndroidApp { Instrumentation.Length: > 0 } => (new DeviceTestApp(installed, unseen), null),
            AndroidApp => (null, Refusals.NoInstrumentation(project.Name)),
            _ => (null, uninstalled ?? Refusals.NotYetOn(chosen.Name)),
        };
    }
}

/// <summary>A device test project's app made ready where it runs, as Run makes it ready (<paramref name="ready"/>): a Mac
/// Catalyst app here or an iOS simulator's, each <see cref="Start"/> a start of it of its own, found by its own token; or
/// installed on an Android device or emulator, whose instrumentation runs its tests (<see cref="Android"/>). Debugged, each
/// start is a plan the debugger starts through Passenger's relay in it (<see cref="Plan"/>).</summary>
public sealed class DeviceTestApp(LaunchPlan ready, bool unseen)
{
    /// <summary>A start's name, as the debugger's tab and the device's hold of it name it.</summary>
    private const string Name = "Tests";

    /// <summary>The app installed on an Android device or emulator, started through its instrumentation; null for an Apple one.</summary>
    public AndroidApp? Android => ready.Device?.App as AndroidApp;

    /// <summary>The program that starts the app with the Testing Platform's <paramref name="server"/> options as its own
    /// arguments and <paramref name="environment"/> in its environment, lasting as long as the app does, and what follows
    /// the start: the app taken on once it runs, so it ends with Passenger, and ended once that program has
    /// <c>ended</c> (killed, its app runs on).</summary>
    public (ProcessStartInfo Program, Func<Task, Task> Follow) Start(IReadOnlyList<string> server, IReadOnlyDictionary<string, string> environment)
    {
        LaunchPlan plan = Plan(server, environment);
        if (plan.MacCatalyst?.App is { } app)
        {
            return (ChildProcesses.Command(plan.Program, plan.Arguments, null, app.Variables(environment, null, null, null)), ended => MacCatalystApps.EndWithAsync(app, ended));
        }
        SimulatorApp launched = (SimulatorApp)plan.Device!.App!;
        return (ChildProcesses.Command(plan.Program, plan.Arguments, null, SimulatorApp.Given(launched.Variables(environment, null, null, null))), ended => EndAsync(launched, ended));

        static async Task EndAsync(SimulatorApp app, Task ended)
        {
            await app.TakeOnAsync(ended);
            await ended.Ended();
            // By its process, never by its bundle identifier, which names the next test host's app too.
            app.Process.End();
        }
    }

    /// <summary>A start of the app of its own as a plan: an Android app's instrumentation (no <paramref name="server"/>
    /// options), an iOS simulator's app launched afresh with the Testing Platform's options as its own arguments, a Mac
    /// Catalyst app opened afresh with them, its results written beside its bundle where the template's would go to the
    /// person's Documents, unless its sandbox keeps it in its container; <paramref name="environment"/> its own.</summary>
    public LaunchPlan Plan(IReadOnlyList<string>? server, IReadOnlyDictionary<string, string> environment)
    {
        LaunchPlan plan = ready with { Name = Name, Environment = new Dictionary<string, string>(environment) };
        if (ready.MacCatalyst is { App: { } catalyst } opening)
        {
            MacCatalystApp app = MacCatalystApps.Afresh(catalyst);
            string[] own = app.InSandbox ? [.. server ?? []] : [.. server ?? [], "--results-directory", Path.Combine(Path.GetDirectoryName(app.Bundle)!, "TestResults")];
            IReadOnlyList<string> opened = MacCatalystApps.Opening(app, unseen, own, appKit: false);
            return plan with { Program = MacCatalystApps.Shell, Arguments = opened, Leading = opened.Count - own.Length, MacCatalyst = opening with { App = app } };
        }
        if (ready.Device is { App: SimulatorApp simulator } device)
        {
            SimulatorApp launched = simulator with { Process = new AppProcess() };
            IReadOnlyList<string> launch = launched.Launch(server ?? []);
            return plan with { Program = launched.Simctl, Arguments = launch, Leading = launch.Count - (server?.Count ?? 0), Device = device with { App = launched } };
        }
        AndroidApp android = Android ?? throw new InvalidOperationException($"{ready.Name}'s app is on no device");
        return plan with { Program = android.Adb, Arguments = android.Instrument(), Leading = android.Instrument().Count };
    }
}
