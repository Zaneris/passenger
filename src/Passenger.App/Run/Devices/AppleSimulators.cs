using System.Text.Json;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.App.Run.Devices;

/// <summary>The Xcode an Apple workload builds with, as its <c>_DetectSdkLocations</c> found it: its developer folder, whose
/// simctl runs the simulators.</summary>
public sealed record AppleTools(string Developer)
{
    public string Simctl => Path.Combine(Developer, "usr", "bin", "simctl");
}

/// <summary>An app a build made for a simulator, as its workload reads it: its bundle identifier, the built bundle and the
/// runtime identifier it was built for.</summary>
public sealed record SimulatorBuild(string Package, string Bundle, string RuntimeIdentifier);

/// <summary>iOS's simulators through its workload and that workload's own Xcode: the simulators and devices the workload
/// lists (<c>ComputeAvailableDevices</c>, what <c>dotnet run --list-devices</c> asks), a simulator booted where it is shut
/// down, always with no window (<c>simctl boot</c> opens none), the built app installed there (<c>simctl install</c>) and
/// shut down again once the start that booted it is over. Every program through ChildProcesses, the .NET CLI's as questions.</summary>
public static class AppleSimulators
{
    /// <summary>How long a simulator's boot is waited for, to its system's start.</summary>
    private static readonly TimeSpan BootLimit = TimeSpan.FromMinutes(5);

    /// <summary>How long one of simctl's other commands is waited for, an install's but none.</summary>
    private static readonly TimeSpan SimctlLimit = TimeSpan.FromMinutes(3);

    /// <summary>The simulators this process booted and has not shut down, by their simctl and UDID, which the watchdog shuts
    /// down however Passenger ends (<see cref="ChildProcesses.AtIdeEnd"/>).</summary>
    private static readonly HashSet<(string Simctl, string Udid)> Up = [];

    /// <summary>A simulator a start of this process boots, shut down by the watchdog where Passenger ends before
    /// <see cref="ShutDownAsync"/> shuts it down. Any thread.</summary>
    public static void Booting(string simctl, string udid)
    {
        lock (Up)
        {
            if (Up.Add((simctl, udid)))
            {
                TellWatchdog();
            }
        }
    }

    /// <summary>The watchdog's command shutting down every simulator still up. Under <see cref="Up"/>'s lock.</summary>
    private static void TellWatchdog() => ChildProcesses.AtIdeEnd(nameof(AppleSimulators), Up.Count == 0 ? null
        : string.Join("; ", Up.Select(b => $"{Quoted(b.Simctl)} shutdown {Quoted(b.Udid)}")));

    private static string Quoted(string word) => global::Passenger.Terminal.ShellWords.Quoted(word, ShellKind.Posix);

    /// <summary>The project's iOS targets and the Xcode its workload builds with, or why none could be listed (no Mac, no
    /// workload, no Xcode) in words for the person. The project is restored first with no framework given, as Android's
    /// listing is.</summary>
    public static async Task<(IReadOnlyList<DeviceTarget> Targets, AppleTools? Tools, string? Problem)> ListAsync(string project, string framework,
        IReadOnlyDictionary<string, string> globals, CancellationToken cancel)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return ([], null, Refusals.IosOnMacAlone);
        }
        MSBuildAnswer listed = await MSBuildQuestions.AskAsync(project, null, globals, new MSBuildQuestion([MSBuildNames.Restore]), cancel);
        if (listed.Succeeded)
        {
            listed = await MSBuildQuestions.AskAsync(project, framework, globals, new MSBuildQuestion([MSBuildNames.ComputeAvailableDevices])
            {
                Items = [MSBuildNames.Devices],
                Properties = [MSBuildNames.SdkDevPath],
            }, cancel);
        }
        if (!listed.Succeeded)
        {
            return ([], null, Refusals.NoIos(AndroidDevices.ProblemOf(listed.Said)));
        }
        if (!listed.Answered)
        {
            return ([], null, $"The iOS workload's list of simulators could not be read: {listed.Said.FirstLine}");
        }
        if (listed.Property(MSBuildNames.SdkDevPath) is not { Length: > 0 } developer || !File.Exists(new AppleTools(developer).Simctl))
        {
            return ([], null, Refusals.NoIos("the iOS workload found no Xcode with simctl"));
        }
        return (DeviceTargets.Parse(TargetPlatform.Ios, listed), new AppleTools(PathNames.Canonical(developer)), null);
    }

    /// <summary>Boots a shut-down simulator and waits until its system has started, with no window: nothing of it shows on
    /// anyone's screen. Null once booted, else why not. Another boot of it landing between the look that found it shut
    /// down and this one's request, which simctl then refuses, is waited for as this one would have been.</summary>
    public static async Task<string?> BootAsync(AppleTools tools, DeviceTarget simulator, Action<string> say, CancellationToken cancel)
    {
        say($"Starting the simulator {simulator.Name}…");
        Deadline limit = Deadline.In(BootLimit);
        try
        {
            // bootstatus -b boots it where it is shut down and waits for its boot to finish.
            ChildResult booted = await ChildProcesses.RunAsync(ChildProcesses.Command(tools.Simctl, ["bootstatus", simulator.Id, "-b"]), limit: BootLimit, cancel: cancel);
            if (booted.Succeeded)
            {
                return null;
            }
            if (await StateAsync(tools.Simctl, simulator.Id, cancel) is not (BootingState or BootedState))
            {
                return $"The simulator {simulator.Name} did not start: {booted.Said}";
            }
            IdeLog.Write(IdeLog.Ide, $"The simulator {simulator.Id} was booted by another as a start booted it: its boot is waited for ({booted.Said})");
            // Without -b, bootstatus waits for the boot under way, or says at once that it has ended.
            ChildResult waited = await ChildProcesses.RunAsync(ChildProcesses.Command(tools.Simctl, ["bootstatus", simulator.Id]), limit: limit.Left, cancel: cancel);
            return waited.Succeeded ? null : $"The simulator {simulator.Name} did not start: {waited.Said}";
        }
        catch (TimeoutException)
        {
            return $"The simulator {simulator.Name} did not finish starting in {Durations.Said(BootLimit)}";
        }
    }

    /// <summary>A simulator's state as simctl names it while it boots.</summary>
    private const string BootingState = "Booting";

    /// <summary>A simulator's state as simctl names it once booted.</summary>
    private const string BootedState = "Booted";

    /// <summary>A simulator's state as simctl lists it now, null where it lists no such simulator or could not be read.</summary>
    private static async Task<string?> StateAsync(string simctl, string udid, CancellationToken cancel)
    {
        ChildResult listed = await SimctlAsync(simctl, ["list", "devices", "-j", udid], cancel);
        return listed.Succeeded ? JsonMembers.Properties(JsonMembers.ObjectIn(listed.Output), "devices")
            .SelectMany(runtime => runtime.Value.ValueKind == JsonValueKind.Array ? runtime.Value.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Where(device => JsonMembers.Text(device, "udid") == udid).Select(device => JsonMembers.Text(device, "state")).FirstOrDefault() : null;
    }

    /// <summary>The app the project's build made for a simulator, its bundle and identifier as its workload reads them
    /// (<c>_ReadAppManifest</c>, no build of its own): the app, or why not.</summary>
    public static async Task<(SimulatorBuild? Built, string? Problem)> ReadBuildAsync(string project, string framework, IReadOnlyDictionary<string, string> globals,
        CancellationToken cancel)
    {
        MSBuildAnswer read = await MSBuildQuestions.AskAsync(project, framework, globals, new MSBuildQuestion([MSBuildNames.ReadAppManifest])
        {
            Properties = [MSBuildNames.BundleIdentifier, MSBuildNames.AppBundlePath, MSBuildNames.RuntimeIdentifier],
        }, cancel);
        if (!read.Succeeded)
        {
            return (null, AndroidDevices.ProblemOf(read.Said));
        }
        string identifier = read.Property(MSBuildNames.BundleIdentifier) ?? string.Empty;
        string relative = read.Property(MSBuildNames.AppBundlePath) ?? string.Empty;
        if (identifier.Length == 0 || relative.Length == 0)
        {
            return (null, $"The iOS workload did not say which app it built: {read.Said.FirstLine}");
        }
        string bundle = Path.TrimEndingDirectorySeparator(Path.GetFullPath(relative.Replace('\\', '/'), Path.GetDirectoryName(project)!));
        if (!Directory.Exists(bundle))
        {
            return (null, $"{Path.GetFileName(bundle)} is not built ({bundle})");
        }
        return (new SimulatorBuild(identifier, PathNames.Canonical(bundle), read.Property(MSBuildNames.RuntimeIdentifier) ?? string.Empty), null);
    }

    /// <summary>Installs the built app on a booted simulator: the app, or why not. The install is waited for as long as it
    /// goes, as an Android device's is, the start's Stop ending it: the first of a boot starts the simulator's install
    /// services, which takes seconds on an idle Mac and minutes on a loaded one.</summary>
    public static async Task<(SimulatorApp? App, string? Problem)> InstallAsync(SimulatorBuild built, string udid, AppleTools tools, CancellationToken cancel)
    {
        ChildResult installed = await ChildProcesses.RunAsync(ChildProcesses.Command(tools.Simctl, ["install", udid, built.Bundle]), cancel: cancel);
        if (!installed.Succeeded)
        {
            return (null, installed.Said);
        }
        try
        {
            ChildResult data = await SimctlAsync(tools.Simctl, ["get_app_container", udid, built.Package, "data"], cancel);
            return data.Succeeded && data.Output.Trim() is { Length: > 0 } container
                ? (new SimulatorApp(udid, tools.Simctl, built.Package, built.Bundle, built.RuntimeIdentifier)
                {
                    Carried = Path.Combine(container, "tmp", "passenger"),
                }, null)
                : (null, $"simctl did not say where its data container is: {data.Said}");
        }
        catch (TimeoutException)
        {
            return (null, $"simctl did not say where its data container is in {Durations.Said(SimctlLimit)}");
        }
    }

    /// <summary>A simulator a start booted shut down again, simctl's answer in the log where it could not. Never throws.</summary>
    public static async Task ShutDownAsync(string simctl, string udid)
    {
        try
        {
            ChildResult down = await SimctlAsync(simctl, ["shutdown", udid], CancellationToken.None);
            if (down.Succeeded)
            {
                lock (Up)
                {
                    if (Up.Remove((simctl, udid)))
                    {
                        TellWatchdog();
                    }
                }
            }
            IdeLog.Write(IdeLog.Ide, down.Succeeded ? $"The simulator {udid}, which a start booted, is shut down" : $"The simulator {udid} was not shut down: {down.Said}");
        }
        catch (Exception unreached) when (unreached is TimeoutException or System.ComponentModel.Win32Exception)
        {
            IdeLog.Write(IdeLog.Ide, $"The simulator {udid} was not shut down: {unreached.Message}");
        }
    }

    /// <summary>One simctl command through ChildProcesses, its words read whole, given <see cref="SimctlLimit"/>.</summary>
    internal static Task<ChildResult> SimctlAsync(string simctl, IReadOnlyList<string> arguments, CancellationToken cancel) =>
        ChildProcesses.RunAsync(ChildProcesses.Command(simctl, arguments), limit: SimctlLimit, cancel: cancel);
}
