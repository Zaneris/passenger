using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Run.Devices;

/// <summary>The platforms an app of one project runs on, each by the platform its target frameworks name
/// (<c>net11.0-android</c>): those whose app runs on a device, an emulator or a simulator, or on this machine as an app of
/// its platform (Mac Catalyst, Windows), or as a desktop app of a framework of no platform (<see cref="Desktop"/>).</summary>
public enum TargetPlatform
{
    Android,
    Ios,
    MacCatalyst,
    Windows,

    /// <summary>A head of no platform (<c>net11.0</c>) that runs as a desktop app wherever .NET does, Linux too: a MAUI app's
    /// Avalonia head (<see cref="DeviceTargets.DesktopHead"/>).</summary>
    Desktop,
}

/// <summary>What a target is: a device plugged in or on the network, an emulator or a simulator, or this machine.</summary>
public enum TargetKind
{
    Device,
    Emulator,
    Simulator,
    ThisMachine,
}

/// <summary>Where an app runs: a platform's device, emulator or simulator as its workload lists it
/// (<c>ComputeAvailableDevices</c>), or this machine. <see cref="Id"/> is what the workload names it by (an adb serial, an
/// AVD's name while it is not running), <see cref="Key"/> what a choice of it is kept by: an emulator's AVD, whose serial
/// changes with each start.</summary>
public sealed record DeviceTarget(TargetPlatform Platform, string Id, string Name, TargetKind Kind, bool Running)
{
    /// <summary>The Android emulator's AVD, where the target is one.</summary>
    public string? Avd { get; init; }

    public string Key => Avd ?? Id;

    /// <summary>The target as the chooser names it: its name, and what it is where its name does not say.</summary>
    public string Shown => Kind switch
    {
        TargetKind.Emulator or TargetKind.Simulator when !Running => $"{Name} (not running)",
        _ => Name,
    };
}

/// <summary>A target as a folder keeps its choice: its platform, the key it is found again by (<see cref="DeviceTarget.Key"/>),
/// its name and what it is.</summary>
public sealed record KeptTarget
{
    public TargetPlatform Platform { get; init; }

    public string Key { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public TargetKind Kind { get; init; }

    public static KeptTarget Of(DeviceTarget target) => new() { Platform = target.Platform, Key = target.Key, Name = target.Name, Kind = target.Kind };
}

/// <summary>Reads what a platform's workload lists, and tells a project's platforms from its target frameworks.</summary>
public static partial class DeviceTargets
{
    /// <summary>The target framework's platform (<c>net11.0-android36.0</c>'s Android); null for a framework of no platform
    /// an app runs on elsewhere, or a Windows desktop app's (<c>net11.0-windows</c>).</summary>
    public static TargetPlatform? PlatformOf(string framework) => FrameworkNames.PlatformOf(framework) switch
    {
        FrameworkNames.Android => TargetPlatform.Android,
        FrameworkNames.Ios => TargetPlatform.Ios,
        FrameworkNames.MacCatalyst => TargetPlatform.MacCatalyst,
        // A Windows app's head of several (MAUI's, WinUI's) names its platform's version; a desktop app's runs here as it is.
        FrameworkNames.Windows when FrameworkNames.NamesPlatformVersion(framework) => TargetPlatform.Windows,
        _ => null,
    };

    /// <summary>The project's framework for a platform its frameworks name, the first of them in its order; null where it
    /// targets none.</summary>
    public static string? FrameworkFor(IEnumerable<string> frameworks, TargetPlatform platform) =>
        frameworks.FirstOrDefault(f => PlatformOf(f) == platform);

    /// <summary>The framework a project builds for a target's platform: its desktop head's for
    /// <see cref="TargetPlatform.Desktop"/> (<see cref="DesktopHead"/>), else its first naming the platform; null where it
    /// has none.</summary>
    public static string? FrameworkFor(ProjectModel project, TargetPlatform platform) =>
        platform == TargetPlatform.Desktop ? DesktopHead(project) : FrameworkFor(project.TargetFrameworks, platform);

    /// <summary>The framework of a project's desktop head beside its device heads: its first .NET framework of no platform
    /// whose own packages bring Avalonia's desktop platforms (<see cref="Run.DesktopPrograms.TakesAvaloniaDesktop"/>), as a
    /// MAUI app's Avalonia head's do; null for none, as for one kept for tests or a library, or a project of no device
    /// head.</summary>
    public static string? DesktopHead(ProjectModel project) =>
        PlatformsOf(project.TargetFrameworks).Count == 0 ? null
            : project.TargetFrameworks.FirstOrDefault(framework => FrameworkNames.PlatformOf(framework) is null && FrameworkNames.VersionOf(framework) is not null
                && project.Evaluated?.Dependencies.FirstOrDefault(d => string.Equals(d.Framework, framework, StringComparison.OrdinalIgnoreCase)) is { } dependencies
                && Run.DesktopPrograms.TakesAvaloniaDesktop(dependencies));

    /// <summary>The platforms a project's frameworks run on elsewhere, in its order, each once.</summary>
    public static IReadOnlyList<TargetPlatform> PlatformsOf(IEnumerable<string> frameworks) =>
        [.. frameworks.Select(PlatformOf).OfType<TargetPlatform>().Distinct()];

    /// <summary>Whether a project runs only through a target of its platforms: every framework it has names one of
    /// Android, iOS or Mac Catalyst (a Windows framework runs here as it is).</summary>
    public static bool RunsOnlyElsewhere(IReadOnlyCollection<string> frameworks) =>
        frameworks.Count > 0 && frameworks.All(f => PlatformOf(f) is TargetPlatform.Android or TargetPlatform.Ios or TargetPlatform.MacCatalyst);

    /// <summary>The targets a workload's <c>ComputeAvailableDevices</c> answered (<c>-getItem:Devices</c>: each one's
    /// Identity, Description, Type, Status, and an emulator's AvdName). One with no Identity is passed over.</summary>
    public static IReadOnlyList<DeviceTarget> Parse(TargetPlatform platform, MSBuildAnswer listed)
    {
        List<DeviceTarget> found = [];
        foreach (JsonElement device in listed.Items(MSBuildNames.Devices))
        {
            if (JsonMembers.Text(device, "Identity") is not { Length: > 0 } id)
            {
                continue;
            }
            string type = JsonMembers.Text(device, "Type") ?? string.Empty;
            string status = JsonMembers.Text(device, "Status") ?? string.Empty;
            TargetKind kind = type.Equals("Emulator", StringComparison.OrdinalIgnoreCase) ? TargetKind.Emulator
                : type.Equals("Simulator", StringComparison.OrdinalIgnoreCase) ? TargetKind.Simulator
                : TargetKind.Device;
            bool running = !status.Equals("NotRunning", StringComparison.OrdinalIgnoreCase) && !status.Equals("Shutdown", StringComparison.OrdinalIgnoreCase)
                && !status.Equals("Offline", StringComparison.OrdinalIgnoreCase);
            string? avd = JsonMembers.Text(device, "AvdName") is { Length: > 0 } named ? named : null;
            string name = JsonMembers.Text(device, "Description") is { Length: > 0 } description ? Unsaid(description) : avd ?? id;
            found.Add(new DeviceTarget(platform, id, name, kind, running) { Avd = kind == TargetKind.Emulator ? avd : null });
        }
        return found;
    }

    /// <summary>A description without the workload's own word on whether it runs (Android's "Not Running", a simulator's
    /// state), which the chooser says itself.</summary>
    private static string Unsaid(string description) =>
        ((string[])[" (Not Running)", " (Shutdown)", " (Booted)"]).FirstOrDefault(s => description.EndsWith(s, StringComparison.OrdinalIgnoreCase)) is { } state
            ? description[..^state.Length] : description;

    /// <summary>This machine as the target of a platform whose apps run here (a Windows app's head on Windows, a Mac
    /// Catalyst one on a Mac), which the chooser lists beside the devices.</summary>
    public static DeviceTarget Here(TargetPlatform platform) =>
        new(platform, platform.ToString().ToLowerInvariant(), $"{Named(platform)} (this machine)", TargetKind.ThisMachine, Running: true);

    /// <summary>Where a project runs with no target chosen and none of this machine's platform: its desktop head
    /// (<see cref="DesktopHead"/>) on this machine, as a MAUI app's Avalonia head on Linux; null where it has none.</summary>
    public static KeptTarget? UnchosenDesktop(ProjectModel project) => DesktopHead(project) is not null ? KeptTarget.Of(Here(TargetPlatform.Desktop)) : null;

    /// <summary>A platform as people name it.</summary>
    public static string Named(TargetPlatform platform) => platform switch
    {
        TargetPlatform.Ios => "iOS",
        TargetPlatform.MacCatalyst => "Mac Catalyst",
        _ => platform.ToString(),
    };

    /// <summary>The platform whose apps run on this machine, where the system has one: Windows' on Windows, Mac Catalyst's
    /// on a Mac.</summary>
    public static TargetPlatform? OfThisMachine => OperatingSystem.IsWindows() ? TargetPlatform.Windows
        : OperatingSystem.IsMacOS() ? TargetPlatform.MacCatalyst
        : null;

    /// <summary>Where a project of these frameworks runs with no target chosen: this machine, where its apps run here
    /// (<paramref name="here"/>, <see cref="OfThisMachine"/>) and the project has a head of that platform, as a MAUI app's
    /// Windows head on Windows and its Mac Catalyst head on a Mac; null where a target must be chosen.</summary>
    public static KeptTarget? Unchosen(IReadOnlyList<string> frameworks, TargetPlatform? here) =>
        here is { } platform && FrameworkFor(frameworks, platform) is not null ? KeptTarget.Of(Here(platform)) : null;

    /// <summary>The one platform a launch profile running the project names, as a multi-platform app's template names its
    /// Windows profile ("Windows Machine"): that platform's own, taken where it is the target (<see cref="ProfileFor"/>);
    /// null for a profile naming none or several, or running another program.</summary>
    public static TargetPlatform? TiedTo(Run.LaunchProfile profile)
    {
        if (!profile.RunsProject)
        {
            return null;
        }
        string[] words = [.. NameWords().Split(profile.Name).Where(w => w.Length > 0).Select(w => w.ToLowerInvariant())];
        HashSet<TargetPlatform> named = [];
        for (int i = 0; i < words.Length; i++)
        {
            TargetPlatform? platform = words[i] switch
            {
                FrameworkNames.Windows => TargetPlatform.Windows,
                FrameworkNames.Android => TargetPlatform.Android,
                FrameworkNames.Ios => TargetPlatform.Ios,
                FrameworkNames.MacCatalyst => TargetPlatform.MacCatalyst,
                "mac" when i + 1 < words.Length && words[i + 1] == "catalyst" => TargetPlatform.MacCatalyst,
                _ => null,
            };
            if (platform is { } one)
            {
                named.Add(one);
            }
        }
        return named.Count == 1 ? named.Single() : null;
    }

    /// <summary>The launch profiles that make run configurations of their own for a project of these frameworks: every one,
    /// but for a project whose apps run on devices, those tied to one platform, which its own configuration takes where
    /// that platform is the target, so it is named for the project and the target alone says where it runs.</summary>
    public static IReadOnlyList<Run.LaunchProfile> OwnProfiles(IReadOnlyList<string> frameworks, IReadOnlyList<Run.LaunchProfile> profiles) =>
        PlatformsOf(frameworks).Count > 0 ? [.. profiles.Where(p => TiedTo(p) is null)] : profiles;

    /// <summary>The project's first launch profile tied to a platform (<see cref="TiedTo"/>); null where none is.</summary>
    public static Run.LaunchProfile? ProfileFor(IEnumerable<Run.LaunchProfile> profiles, TargetPlatform platform) =>
        profiles.FirstOrDefault(p => TiedTo(p) == platform);

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NameWords();

    /// <summary>A kept choice in a listing, by its platform and key.</summary>
    public static DeviceTarget? Find(KeptTarget? choice, IReadOnlyList<DeviceTarget> targets) =>
        choice is null ? null : targets.FirstOrDefault(t => t.Platform == choice.Platform && t.Key == choice.Key);

    /// <summary>A listing with one platform's targets listed again (<paramref name="found"/>, as a run on one of them found
    /// them): the platform's targets in place of those it had, where its first stood, and every other platform's kept.</summary>
    public static IReadOnlyList<DeviceTarget> Relisted(IReadOnlyList<DeviceTarget> listed, TargetPlatform platform, IReadOnlyList<DeviceTarget> found)
    {
        List<DeviceTarget> relisted = [];
        bool placed = false;
        foreach (DeviceTarget target in listed)
        {
            if (target.Platform != platform)
            {
                relisted.Add(target);
            }
            else if (!placed)
            {
                relisted.AddRange(found);
                placed = true;
            }
        }
        if (!placed)
        {
            relisted.AddRange(found);
        }
        return relisted;
    }

    /// <summary>What a listing says of a project's chosen target that it no longer has; null where it has it, or none was chosen.</summary>
    public static string? Gone(KeptTarget? choice, IReadOnlyList<DeviceTarget> targets, string project) =>
        choice is not null && Find(choice, targets) is null
            ? $"{choice.Name}, where {project} was chosen to run, is not connected now: choose another where it runs" : null;

    /// <summary>The SDK package of the platform a failed Android listing or build says the SDK lacks (XA5207, naming the
    /// android.jar it looked for in the platform's folder), as sdkmanager names it (<c>platforms;android-37.0</c>); null
    /// where it says nothing of one.</summary>
    public static string? AndroidPlatformLacked(string said)
    {
        foreach (string line in said.Split('\n'))
        {
            int jar = line.Contains("XA5207", StringComparison.Ordinal) ? line.LastIndexOf("android.jar", StringComparison.Ordinal) : -1;
            if (jar < 1 || line[jar - 1] is not ('/' or '\\'))
            {
                continue;
            }
            string folder = line[..(jar - 1)];
            folder = folder[(folder.LastIndexOfAny(['/', '\\']) + 1)..];
            if (folder.StartsWith("android-", StringComparison.Ordinal))
            {
                return $"platforms;{folder}";
            }
        }
        return null;
    }
}
