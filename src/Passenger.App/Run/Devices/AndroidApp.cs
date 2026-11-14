using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Run.Devices;

/// <summary>An app installed on an Android device, as its build says it starts: the device's serial, adb, the package and
/// the activity it launches, and the build's intermediate folder (where a Debug build keeps the environment and the
/// assemblies it deployed). Its commands are adb's arguments, each made here once.</summary>
public sealed record AndroidApp(string Serial, string Adb, string Package, string Activity, string Intermediate)
{
    /// <summary>The properties the workload's <c>ComputeRunArguments</c> leaves that the app's start reads.</summary>
    public static readonly IReadOnlyList<string> Properties = ["AndroidLaunchActivity", "_AndroidPackage", "_AdbToolPath", MSBuildNames.IntermediateOutputPath, "AndroidInstrumentation"];

    /// <summary>The profiler the runtime loads Passenger's relay as (its class id, which the relay's factory answers to).</summary>
    public const string RelayProfiler = "{7A3E5B2C-8D41-4F6A-9C0E-5B1D2E3F4A50}";

    /// <summary>The relay's file name in the app's fast-deployment folder.</summary>
    public const string RelayFile = "libpassenger-debug.so";

    /// <summary>The abstract socket the relay listens on in the app, named afresh for each session.</summary>
    [IdentifierHome]
    public const string RelaySocketVariable = "PASSENGER_DEBUG_SOCKET";

    /// <summary>The token the relay takes a client by, which only the session that put it there knows.</summary>
    [IdentifierHome]
    public const string RelayTokenVariable = "PASSENGER_DEBUG_TOKEN";

    /// <summary>How long the relay holds the app's start for Passenger to attach, in milliseconds.</summary>
    [IdentifierHome]
    public const string RelayWaitVariable = "PASSENGER_DEBUG_WAIT_MS";

    /// <summary>The app's start as <c>dotnet msbuild -getProperty</c> wrote it after <c>ComputeRunArguments</c>; why not where
    /// it launches no activity.</summary>
    public static (AndroidApp? App, string? Problem) Read(string json, string serial, string projectFolder)
    {
        using JsonDocument read = JsonDocument.Parse(json);
        JsonElement? properties = JsonMembers.Object(read.RootElement, "Properties");
        string package = JsonMembers.Text(properties, "_AndroidPackage") ?? string.Empty;
        string activity = JsonMembers.Text(properties, "AndroidLaunchActivity") ?? string.Empty;
        string adb = JsonMembers.Text(properties, "_AdbToolPath") ?? string.Empty;
        string intermediate = JsonMembers.Text(properties, MSBuildNames.IntermediateOutputPath) ?? string.Empty;
        if (package.Length == 0 || adb.Length == 0)
        {
            return (null, "The Android workload did not say which package it installed");
        }
        if (activity.Length == 0)
        {
            return (null, JsonMembers.Text(properties, "AndroidInstrumentation") is { Length: > 0 }
                ? $"{package} starts through its instrumentation, which Passenger does not run: give it a launcher activity"
                : $"{package} has no launcher activity to start");
        }
        string folder = Path.DirectorySeparatorChar == '\\' ? intermediate : intermediate.Replace('\\', '/');
        return (new AndroidApp(serial, adb, package, activity, PathNames.Canonical(Path.Combine(projectFolder, folder))), null);
    }

    /// <summary>adb's arguments for one command on the app's device.</summary>
    public IReadOnlyList<string> On(params string[] command) => ["-s", Serial, .. command];

    /// <summary>The device's shell starting the app afresh, saying once it has started (<see cref="Started"/>), then writing
    /// its log from its start (<c>logcat</c> of its process alone) until its process ends, when the shell ends too: what the
    /// Run window shows as the program. Its last process is ended first, so the one found is the new one; the first of
    /// several is its main one.</summary>
    public IReadOnlyList<string> Run() => On("shell",
        $"am force-stop {Package}; am start -n {Package}/{Activity} >/dev/null || exit 1; i=0; p=; "
        + $"while [ -z \"$p\" ] && [ $i -lt 300 ]; do p=$(pidof {Package}) || {{ sleep 0.1; i=$((i+1)); }}; done; "
        + $"[ -n \"$p\" ] || {{ echo '{Package} did not start'; exit 1; }}; p=${{p%% *}}; echo \"{Started}$p\"; "
        + "logcat -v tag --pid=$p & l=$!; while [ -d /proc/$p ]; do sleep 1; done; kill $l");

    /// <summary>What the app's shell says once it found the app's process, the process's number after it: the line a run
    /// on the device has started at.</summary>
    public string Started => $"{Package} started as process ";

    /// <summary>The app's process as its shell said it started (<see cref="Started"/>), from what the Run window shows of the
    /// shell's first lines; null until it has.</summary>
    public string? StartedIn(Passenger.Text.TextSnapshot shown)
    {
        for (int line = 0; line < Math.Min(shown.LineCount, FirstLines); line++)
        {
            if (shown.GetLineText(line) is { } said && said.StartsWith(Started, StringComparison.Ordinal))
            {
                return said[Started.Length..].Trim();
            }
        }
        return null;
    }

    /// <summary>The lines of the Run window looked through for the start: the plan's notes, then the shell's first.</summary>
    private const int FirstLines = 64;

    /// <summary>Ends the app however it runs.</summary>
    public IReadOnlyList<string> ForceStop() => On("shell", "am", "force-stop", Package);

    /// <summary>Marks the app as debugged, so the system lets a stop of its main thread pass without ending it.</summary>
    public IReadOnlyList<string> SetDebugApp() => On("shell", "am", "set-debug-app", Package);

    /// <summary>The device's debugged app cleared, only while it is still this one: another start may have marked its own since.</summary>
    public IReadOnlyList<string> ClearDebugApp() =>
        On("shell", $"[ \"$(dumpsys activity processes | grep -c 'mDebugApp={Package}/')\" != 0 ] && am clear-debug-app; true");

    /// <summary>The app's fast-deployment folder on the device for an ABI, whose environment its runtime's host reads.</summary>
    public string OverrideFolder(string abi) => $"/data/user/0/{Package}/files/.__override__/{abi}";

    /// <summary>The build's own environment file for an ABI, which a Debug start adds the relay's variables to.</summary>
    public string BuiltEnvironment(string abi) => Path.Combine(Intermediate, "fastdeploy2-environment", abi, "environment");

    /// <summary>The build's copies of the assemblies it deployed for an ABI, each beside its PDB, read on this machine.</summary>
    public string Deployed(string abi) => Path.Combine(Intermediate, "android", "assets", abi);

    /// <summary>Where a Debug start leaves the relay and its environment for the app to copy in.</summary>
    public string Staged(string what) => $"/data/local/tmp/passenger-{Package}-{what}";

    /// <summary>The variables a Debug start puts in the app's environment: the runtime's profiler, Passenger's relay in the
    /// app's folder listening on <paramref name="socket"/> for a client that says <paramref name="token"/>.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> RelayVariables(string abi, string socket, string token) =>
    [
        new(RuntimeVariables.EnableProfiling, "1"),
        new(RuntimeVariables.Profiler, RelayProfiler),
        new(RuntimeVariables.ProfilerPath, $"{OverrideFolder(abi)}/{RelayFile}"),
        new(RelaySocketVariable, socket),
        new(RelayTokenVariable, token),
        new(RelayWaitVariable, "60000"),
    ];

    /// <summary>The app's own shell putting the staged relay and environment in its folder, saying first where its own
    /// environment file pointed (a link fast deployment made), which <see cref="TakeRelayOut"/> puts back.</summary>
    public IReadOnlyList<string> PutRelayIn(string abi) => On("shell",
        $"run-as {Package} sh -c 'cd {OverrideFolder(abi)} || exit 1; readlink environment; cp {Staged("relay")} {RelayFile} && rm -f environment && cp {Staged("environment")} environment'"
        + $"; rm -f {Staged("relay")} {Staged("environment")}");

    /// <summary>The app's own shell taking the relay and its environment out again, the build's link put back where there
    /// was one (<paramref name="linked"/>).</summary>
    public IReadOnlyList<string> TakeRelayOut(string abi, string? linked) => On("shell",
        $"run-as {Package} sh -c 'cd {OverrideFolder(abi)} || exit 1; rm -f {RelayFile} environment"
        + (linked is { Length: > 0 } ? $" && ln -s {linked} environment'" : "'"));

    /// <summary>The ABI's folder name for the relay's runtime identifier (<c>arm64-v8a</c>'s android-arm64); null for one no relay is built for.</summary>
    public static string? RelayRuntime(string abi) => abi switch
    {
        "arm64-v8a" => "android-arm64",
        "x86_64" => "android-x64",
        _ => null,
    };
}
