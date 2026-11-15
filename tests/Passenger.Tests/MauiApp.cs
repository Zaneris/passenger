using Passenger.App.Run.Devices;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The app <c>dotnet new maui</c> makes, as its Android head alone: the template's other heads taken out of its
/// frameworks, so its restore and build need Android's workload and no other, restored from nuget.org, as no restore of
/// the solution's brings MAUI's Android packages (so a test of it is never one of the gate's). The template's main page
/// has a label "Hello, World!" and a button named CounterBtn, "Click me", which counts its clicks.</summary>
internal static class MauiApp
{
    /// <summary>The app's project, run configuration and Android application id.</summary>
    public const string Name = "MauiApp1";

    public const string Package = "com.companyname.mauiapp1";

    /// <summary>The framework the head is built for: the first .NET whose Android apps run on CoreCLR.</summary>
    public static readonly string Android = $"net{Passenger.App.Mobile.MobilePlatforms.FirstOnCoreClr}.0-android";

    /// <summary>The longest dotnet new and the restore may each take.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(6);

    /// <summary>Makes the app in a folder of its own under <paramref name="folder"/> and restores it: its project, and why it
    /// could not be made or restored here (no MAUI workload, no reach of nuget.org), or null.</summary>
    public static async Task<(string Project, string? Unmade)> CreateAndroidHeadAsync(string folder)
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
        File.WriteAllLines(project, lines.Where(l => !l.TrimStart().StartsWith("<TargetFrameworks Condition=", StringComparison.Ordinal)));
        ChildResult restored = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["restore", project], app), limit: Limit);
        return (project, restored.Succeeded ? null
            : $"{Name}'s Android head does not restore here from nuget.org: {restored.Output.Split('\n').FirstOrDefault(l => l.Contains(": error ", StringComparison.Ordinal))?.Trim() ?? restored.FirstLine}");
    }

    /// <summary>The device or emulator to run on, of those listed: the one adb's ANDROID_SERIAL names, as adb itself takes
    /// it where several are connected, else the first running, else the first emulator to start; null for none.</summary>
    public static DeviceTarget? Target(IReadOnlyList<DeviceTarget> targets) =>
        (Environment.GetEnvironmentVariable("ANDROID_SERIAL") is { Length: > 0 } serial ? targets.FirstOrDefault(t => t.Running && t.Id == serial) : null)
        ?? targets.FirstOrDefault(t => t.Running) ?? targets.FirstOrDefault(t => t.Kind == TargetKind.Emulator);
}
