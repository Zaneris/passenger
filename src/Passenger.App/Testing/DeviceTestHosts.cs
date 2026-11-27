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
/// app and a Mac Catalyst app are processes of this Mac, which reach Passenger's client on its loopback. A run begins with
/// <see cref="Begin"/>, which takes each project's target as chosen then and forgets what the last run made ready.</summary>
public sealed class DeviceTestHosts(Func<ProjectModel, KeptTarget?> targets, Func<ProjectModel, IReadOnlyDictionary<string, string>> globals, bool unseen,
    Action<string, string> booted, Action<ProjectModel, TargetPlatform, IReadOnlyList<DeviceTarget>> listed)
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

    private async Task<(DeviceTestApp? App, string? Problem)> ReadyCoreAsync(TestAssembly assembly, KeptTarget? target, IReadOnlyDictionary<string, string> properties,
        Action<string> stage, CancellationToken cancel)
    {
        ProjectModel project = assembly.Project;
        TargetPlatform platform = DeviceTargets.PlatformOf(assembly.Framework) ?? TargetPlatform.Desktop;
        LaunchPlan plan = new() { Name = project.Name, Program = string.Empty, WorkingDirectory = project.Folder, Project = project.Path, Unseen = unseen };
        if (platform == TargetPlatform.Android)
        {
            return (null, Refusals.AndroidTestsNotYet(project.Name));
        }
        if (platform == TargetPlatform.MacCatalyst)
        {
            if (!OperatingSystem.IsMacOS())
            {
                return (null, $"{project.Name} cannot run here: {Refusals.MacCatalystOnMacAlone}");
            }
            (LaunchPlan? opened, string? unopened) = await MacCatalystApps.ReadyAsync(plan with { MacCatalyst = new MacCatalystLaunch(assembly.Framework) }, properties, cancel);
            return opened?.MacCatalyst?.App is { } catalyst ? (new DeviceTestApp(catalyst, null, unseen), null) : (null, unopened);
        }
        if (target is not { Kind: not TargetKind.ThisMachine } chosen || chosen.Platform != platform)
        {
            return (null, Refusals.NoRunTarget(project.Name));
        }
        (LaunchPlan? installed, string? uninstalled) = await DeviceLaunches.ReadyAsync(plan with { Device = new DeviceLaunch(chosen, assembly.Framework) }, properties, unseen,
            stage, found => listed(project, platform, found), cancel, booted);
        return installed?.Device?.App is SimulatorApp simulator ? (new DeviceTestApp(null, simulator, unseen), null)
            : (null, uninstalled ?? Refusals.NotYetOn(chosen.Name));
    }
}

/// <summary>A device test project's app made ready where it runs, as a Mac Catalyst app here or an iOS simulator's: each
/// <see cref="Start"/> a start of it of its own, found by its own token.</summary>
public sealed class DeviceTestApp(MacCatalystApp? catalyst, SimulatorApp? simulator, bool unseen)
{
    /// <summary>The program that starts the app with the Testing Platform's <paramref name="server"/> options as its own
    /// arguments and <paramref name="environment"/> in its environment, lasting as long as the app does, and what follows
    /// the start: the app taken on once it runs, so it ends with Passenger, and ended once that program has
    /// <c>ended</c> (killed, its app runs on). A Mac Catalyst app's results are written beside its bundle, where the template's
    /// would go to the person's Documents, unless its sandbox keeps it in its container.</summary>
    public (ProcessStartInfo Program, Func<Task, Task> Follow) Start(IReadOnlyList<string> server, IReadOnlyDictionary<string, string> environment)
    {
        if (catalyst is not null)
        {
            MacCatalystApp app = MacCatalystApps.Afresh(catalyst);
            string[] own = app.InSandbox ? [.. server] : [.. server, "--results-directory", Path.Combine(Path.GetDirectoryName(app.Bundle)!, "TestResults")];
            return (ChildProcesses.Command(MacCatalystApps.Shell, MacCatalystApps.Opening(app, unseen, own, appKit: false), null, app.Variables(environment, null, null, null)),
                ended => MacCatalystApps.EndWithAsync(app, ended));
        }
        SimulatorApp launched = simulator! with { Process = new AppProcess() };
        return (ChildProcesses.Command(launched.Simctl, launched.Launch(server), null,
            SimulatorApp.Given(launched.Variables(environment, null, null, null))), ended => EndAsync(launched, ended));

        static async Task EndAsync(SimulatorApp app, Task ended)
        {
            await app.TakeOnAsync(ended);
            await ended.Ended();
            // By its process, never by its bundle identifier, which names the next test host's app too.
            app.Process.End();
        }
    }
}
