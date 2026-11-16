using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.App.Run.Devices;

/// <summary>The Xcode an Apple workload builds with, as its <c>_DetectSdkLocations</c> found it: its developer folder, whose
/// simctl runs the simulators.</summary>
public sealed record AppleTools(string Developer)
{
    public string Simctl => Path.Combine(Developer, "usr", "bin", "simctl");
}

/// <summary>iOS's simulators through its workload and that workload's own Xcode: the simulators and devices the workload
/// lists (<c>ComputeAvailableDevices</c>, what <c>dotnet run --list-devices</c> asks), a simulator booted where it is shut
/// down, always with no window (<c>simctl boot</c> opens none), the built app installed there (<c>simctl install</c>) and
/// shut down again once the start that booted it is over. Every program through ChildProcesses, the .NET CLI's as questions.</summary>
public static class AppleSimulators
{
    /// <summary>How long a simulator's boot is waited for, to its system's start.</summary>
    private static readonly TimeSpan BootLimit = TimeSpan.FromMinutes(5);

    /// <summary>How long one of simctl's other commands is waited for (an install copies the whole app).</summary>
    private static readonly TimeSpan SimctlLimit = TimeSpan.FromMinutes(3);

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
    /// anyone's screen. Null once booted, else why not.</summary>
    public static async Task<string?> BootAsync(AppleTools tools, DeviceTarget simulator, Action<string> say, CancellationToken cancel)
    {
        say($"Starting the simulator {simulator.Name}…");
        try
        {
            // bootstatus -b boots it where it is shut down and waits for its boot to finish.
            ChildResult booted = await ChildProcesses.RunAsync(ChildProcesses.Command(tools.Simctl, ["bootstatus", simulator.Id, "-b"]), limit: BootLimit, cancel: cancel);
            return booted.Succeeded ? null : $"The simulator {simulator.Name} did not start: {booted.Said}";
        }
        catch (TimeoutException)
        {
            return $"The simulator {simulator.Name} did not finish starting in {Durations.Said(BootLimit)}";
        }
    }

    /// <summary>Installs the built app on a booted simulator, its bundle and identifier as its workload reads them
    /// (<c>_ReadAppManifest</c>, no build of its own): the app, or why not.</summary>
    public static async Task<(SimulatorApp? App, string? Problem)> InstallAsync(string project, string framework, string udid, AppleTools tools,
        IReadOnlyDictionary<string, string> globals, CancellationToken cancel)
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
        bundle = PathNames.Canonical(bundle);
        try
        {
            ChildResult installed = await SimctlAsync(tools.Simctl, ["install", udid, bundle], cancel);
            return installed.Succeeded
                ? (new SimulatorApp(udid, tools.Simctl, identifier, bundle, read.Property(MSBuildNames.RuntimeIdentifier) ?? string.Empty), null)
                : (null, installed.Said);
        }
        catch (TimeoutException)
        {
            return (null, $"simctl did not finish installing it in {Durations.Said(SimctlLimit)}");
        }
    }

    /// <summary>A simulator a start booted shut down again, simctl's answer in the log where it could not. Never throws.</summary>
    public static async Task ShutDownAsync(string simctl, string udid)
    {
        try
        {
            ChildResult down = await SimctlAsync(simctl, ["shutdown", udid], CancellationToken.None);
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
