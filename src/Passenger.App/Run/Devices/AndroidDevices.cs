using System.Diagnostics;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Run.Devices;

/// <summary>The Android SDK's tools as the workload resolves them for a project: adb's and the emulator's, and the SDK
/// package of the platform the workload builds against where the SDK lacks it (<c>platforms;android-37.0</c>).</summary>
public sealed record AndroidTools(string Adb, string Emulator, string? Lacks = null);

/// <summary>Android's targets through its workload and the SDK's own tools: the devices and emulators the workload lists
/// (<c>ComputeAvailableDevices</c>, what <c>dotnet run --list-devices</c> asks), an emulator started where none runs,
/// and an app's install there (<c>DeployToDevice</c>, what <c>dotnet run</c> runs after its build). Every program through
/// ChildProcesses, the .NET CLI's as questions.</summary>
public static class AndroidDevices
{
    /// <summary>How long an emulator started here is waited for, to its system's boot.</summary>
    private static readonly TimeSpan BootLimit = TimeSpan.FromMinutes(5);

    /// <summary>The project's Android targets and the SDK's tools, or why none could be listed (no workload, no SDK, no
    /// platform to build against) in words for the person. The project is restored first with no framework given: one
    /// given to a restore reaches the projects the app references, restoring them for the app's platform alone (a shared
    /// library of net11.0 as net11.0-android), which its install's build then finds without its own framework.</summary>
    public static async Task<(IReadOnlyList<DeviceTarget> Targets, AndroidTools? Tools, string? Problem)> ListAsync(string project, string framework,
        IReadOnlyDictionary<string, string> globals, CancellationToken cancel)
    {
        string folder = Path.GetDirectoryName(project)!;
        string[] given = [.. globals.Where(g => g.Key != MSBuildNames.TargetFramework).Select(g => $"-property:{g.Key}={g.Value}")];
        ChildResult listed = await ChildProcesses.RunAsync(ChildProcesses.DotnetQuestion(["msbuild", project, "-nologo", "-target:Restore", .. given], folder), cancel: cancel);
        if (listed.Succeeded)
        {
            listed = await ChildProcesses.RunAsync(ChildProcesses.DotnetQuestion(["msbuild", project, "-nologo", "-target:ComputeAvailableDevices", "-getItem:Devices",
                "-getProperty:AdbToolPath", "-getProperty:EmulatorToolPath", "-getProperty:AndroidSdkDirectory", "-getProperty:TargetPlatformVersion",
                $"-property:{MSBuildNames.TargetFramework}={framework}", .. given], folder), cancel: cancel);
        }
        if (!listed.Succeeded)
        {
            return ([], null, DeviceTargets.AndroidPlatformLacked(listed.Output + "\n" + listed.Error) is { } lacks
                ? Refusals.NoAndroidPlatform(lacks) : Refusals.NoAndroid(ProblemOf(listed)));
        }
        try
        {
            IReadOnlyList<DeviceTarget> targets = DeviceTargets.Parse(TargetPlatform.Android, listed.Output);
            using JsonDocument read = JsonDocument.Parse(listed.Output);
            JsonElement? properties = JsonMembers.Object(read.RootElement, "Properties");
            string adb = JsonMembers.Text(properties, "AdbToolPath") ?? string.Empty;
            string emulator = JsonMembers.Text(properties, "EmulatorToolPath") ?? string.Empty;
            string? lacks = PlatformLacked(JsonMembers.Text(properties, "AndroidSdkDirectory"), JsonMembers.Text(properties, "TargetPlatformVersion"));
            return (targets, new AndroidTools(Tool(adb, "adb"), Tool(emulator, "emulator"), lacks), null);
        }
        catch (JsonException)
        {
            return ([], null, $"The Android workload's list of devices could not be read: {listed.FirstLine}");
        }
    }

    /// <summary>The SDK package of the platform the workload builds against (<paramref name="version"/>, 37.0) where the SDK
    /// has no android.jar of it, under either of the folder names the SDK gives a platform; null where it has, or the
    /// workload named no SDK or version.</summary>
    internal static string? PlatformLacked(string? sdk, string? version)
    {
        if (sdk is not { Length: > 0 } || version is not { Length: > 0 })
        {
            return null;
        }
        string major = version.Split('.')[0];
        return File.Exists(Path.Combine(sdk, "platforms", $"android-{version}", "android.jar")) || File.Exists(Path.Combine(sdk, "platforms", $"android-{major}", "android.jar"))
            ? null : $"platforms;android-{version}";
    }

    /// <summary>A tool in the folder the workload names, as the system names its program.</summary>
    private static string Tool(string folder, string name) => Path.Combine(folder, OperatingSystem.IsWindows() ? name + ".exe" : name);

    /// <summary>What a failed MSBuild says first: its first error, else its first line.</summary>
    internal static string ProblemOf(ChildResult failed)
    {
        string words = failed.Output + "\n" + failed.Error;
        string? error = words.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains(": error ", StringComparison.Ordinal));
        if (error is not null)
        {
            int said = error.IndexOf(": error ", StringComparison.Ordinal);
            string message = error[(said + ": error ".Length)..];
            int place = message.LastIndexOf(" [", StringComparison.Ordinal);
            return place > 0 ? message[..place] : message;
        }
        return ChildResult.FirstLineOf(words) is { Length: > 0 } first ? first : $"MSBuild ended with {ExitCodes.Said(failed.ExitCode)}";
    }

    /// <summary>Starts an emulator of an AVD and waits until its system has booted: with no window where nobody sees the
    /// Passenger starting it. Its serial once booted, else why not.</summary>
    public static async Task<(string? Serial, string? Problem)> BootAsync(AndroidTools tools, string avd, bool unseen, Action<string> say, CancellationToken cancel)
    {
        List<string> arguments = ["-avd", avd];
        if (unseen)
        {
            arguments.AddRange(["-no-window", "-no-audio", "-no-boot-anim"]);
        }
        ProcessStartInfo start = ChildProcesses.Command(tools.Emulator, arguments);
        // Its last words say why it ended, where it ends as it starts.
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        Process emulator = await ChildProcesses.StartAsync(start);
        List<string> heard = [];
        Task hearing = ChildProcesses.Hear(emulator, line => Heard(heard, line), line => Heard(heard, line));
        IdeErrors.Observe(hearing);
        say($"Starting the emulator {avd}…");
        Stopwatch waited = Stopwatch.StartNew();
        string? serial = null;
        while (waited.Elapsed < BootLimit)
        {
            cancel.ThrowIfCancellationRequested();
            if (!DotnetProcesses.Runs(emulator))
            {
                lock (heard)
                {
                    return (null, $"The emulator {avd} ended as it started{(heard.Count > 0 ? ": " + heard[^1] : string.Empty)}");
                }
            }
            serial ??= await SerialOfAsync(tools, avd, cancel);
            if (serial is not null && (await AdbAsync(tools.Adb, ["-s", serial, "shell", "getprop", "sys.boot_completed"], cancel)).Output.Trim() == "1")
            {
                return (serial, null);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancel);
        }
        return (null, $"The emulator {avd} did not finish starting in {BootLimit.TotalMinutes:0} minutes");

        static void Heard(List<string> heard, string line)
        {
            lock (heard)
            {
                heard.Add(line);
                if (heard.Count > 20)
                {
                    heard.RemoveAt(0);
                }
            }
        }
    }

    /// <summary>The serial of the running emulator of an AVD, as adb lists it; null where none runs yet.</summary>
    private static async Task<string?> SerialOfAsync(AndroidTools tools, string avd, CancellationToken cancel)
    {
        ChildResult devices = await AdbAsync(tools.Adb, ["devices"], cancel);
        foreach (string line in devices.Output.Split('\n').Skip(1))
        {
            string[] parts = line.Split('\t', StringSplitOptions.TrimEntries);
            if (parts is [{ } serial, "device"] && serial.StartsWith("emulator-", StringComparison.Ordinal)
                && (await AdbAsync(tools.Adb, ["-s", serial, "emu", "avd", "name"], cancel)).Output.Split('\n')[0].Trim() == avd)
            {
                return serial;
            }
        }
        return null;
    }

    /// <summary>One adb command through ChildProcesses, its words read whole.</summary>
    internal static Task<ChildResult> AdbAsync(string adb, IReadOnlyList<string> arguments, CancellationToken cancel) =>
        ChildProcesses.RunAsync(ChildProcesses.Command(adb, arguments), cancel: cancel);

    /// <summary>Installs the built app on a device (<c>DeployToDevice</c>, no build of its own) and reads how it starts
    /// there; what MSBuild writes goes to <paramref name="output"/>. The app's start, or why not.</summary>
    public static async Task<(AndroidApp? App, string? Problem)> DeployAsync(string project, string framework, string serial, IReadOnlyDictionary<string, string> globals,
        CancellationToken cancel)
    {
        List<string> arguments = ["msbuild", project, "-nologo", "-target:DeployToDevice;ComputeRunArguments", $"-property:{MSBuildNames.TargetFramework}={framework}", $"-property:Device={serial}",
            .. globals.Where(g => g.Key is not MSBuildNames.TargetFramework and not "Device").Select(g => $"-property:{g.Key}={g.Value}"),
            .. AndroidApp.Properties.Select(p => $"-getProperty:{p}")];
        ChildResult deployed = await ChildProcesses.RunAsync(ChildProcesses.DotnetQuestion(arguments, Path.GetDirectoryName(project)!), cancel: cancel);
        if (!deployed.Succeeded)
        {
            return (null, ProblemOf(deployed));
        }
        try
        {
            return AndroidApp.Read(deployed.Output, serial, Path.GetDirectoryName(project)!);
        }
        catch (JsonException)
        {
            return (null, $"MSBuild did not say how the app starts: {deployed.FirstLine}");
        }
    }
}
