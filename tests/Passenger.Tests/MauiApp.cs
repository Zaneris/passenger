using Passenger.App.Run.Devices;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The app <c>dotnet new maui</c> makes, as one head alone (its Android or its iOS head): the template's other heads
/// taken out of its frameworks, so its restore and build need that platform's workload and no other, restored from
/// nuget.org, as no restore of the solution's brings MAUI's mobile packages (so a test of it is never one of the gate's). The
/// template's main page has a label "Hello, World!" and a button named CounterBtn, "Click me", which counts its clicks.</summary>
internal static class MauiApp
{
    /// <summary>The app's project, run configuration and Android application id (its iOS bundle identifier too).</summary>
    public const string Name = "MauiApp1";

    public const string Package = "com.companyname.mauiapp1";

    /// <summary>The framework the Android head is built for: the first .NET whose Android apps run on CoreCLR.</summary>
    public static readonly string Android = $"net{Passenger.App.Mobile.MobilePlatforms.FirstOnCoreClr}.0-android";

    /// <summary>The framework the iOS head is built for: the first .NET whose iOS apps run on CoreCLR.</summary>
    public static readonly string Ios = $"net{Passenger.App.Mobile.MobilePlatforms.FirstOnCoreClr}.0-ios";

    /// <summary>The longest dotnet new and the restore may each take.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(6);

    /// <summary>Makes the app in a folder of its own under <paramref name="folder"/>, its Android head alone, and restores it:
    /// its project, and why it could not be made or restored here (no MAUI workload, no reach of nuget.org), or null.</summary>
    public static Task<(string Project, string? Unmade)> CreateAndroidHeadAsync(string folder) => CreateHeadAsync(folder, Android);

    /// <summary>The app's iOS head alone, made and restored as <see cref="CreateAndroidHeadAsync"/> makes its Android head.</summary>
    public static Task<(string Project, string? Unmade)> CreateIosHeadAsync(string folder) => CreateHeadAsync(folder, Ios);

    /// <summary>The app with <paramref name="framework"/> alone in place of the template's first framework and those its
    /// conditions add, restored.</summary>
    private static async Task<(string Project, string? Unmade)> CreateHeadAsync(string folder, string framework)
    {
        string app = Path.Combine(folder, Name);
        string project = Path.Combine(app, $"{Name}.csproj");
        ChildResult made = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["new", "maui", "--output", app, "--name", Name, "--no-restore"], folder), limit: Limit);
        if (!made.Succeeded)
        {
            return (project, $"This .NET makes no .NET MAUI app, so it lacks MAUI's workload (dotnet workload install maui): {made.FirstLine}");
        }
        TestFolders.Write(folder, "nuget.config", TestRun.FromNuGetOrg);
        string[] lines = File.ReadAllLines(project);
        Assert.True(lines.Count(l => l.Trim() == $"<TargetFrameworks>{Android}</TargetFrameworks>") == 1,
            $"The template's project no longer names {Android} alone first among its frameworks:\n{string.Join('\n', lines)}");
        File.WriteAllLines(project, lines.Where(l => !l.TrimStart().StartsWith("<TargetFrameworks Condition=", StringComparison.Ordinal))
            .Select(l => l.Trim() == $"<TargetFrameworks>{Android}</TargetFrameworks>" ? l.Replace(Android, framework, StringComparison.Ordinal) : l));
        ChildResult restored = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["restore", project], app), limit: Limit);
        return (project, restored.Succeeded ? null
            : $"{Name}'s {framework} head does not restore here from nuget.org: {restored.Output.Split('\n').FirstOrDefault(l => l.Contains(": error ", StringComparison.Ordinal))?.Trim() ?? restored.FirstLine}");
    }

    /// <summary>The device or emulator to run on, of those listed: the one adb's ANDROID_SERIAL names, as adb itself takes
    /// it where several are connected, else the first running, else the first emulator to start; null for none.</summary>
    public static DeviceTarget? Target(IReadOnlyList<DeviceTarget> targets) =>
        (Environment.GetEnvironmentVariable("ANDROID_SERIAL") is { Length: > 0 } serial ? targets.FirstOrDefault(t => t.Running && t.Id == serial) : null)
        ?? targets.FirstOrDefault(t => t.Running) ?? targets.FirstOrDefault(t => t.Kind == TargetKind.Emulator);
}
