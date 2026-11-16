using System.Text.Json;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Mobile;

/// <summary>The edits an iOS app's project and its Info.plist need before it builds here (<see cref="IosHeads.Edits"/>): its
/// framework moved from one on Mono to the first on CoreCLR, its least iOS raised to the least that framework's workload
/// builds for, in the project and as Info.plist's MinimumOSVersion, and a scene manifest in Info.plist where the SDK of the
/// Xcode it builds with asks every app for one. Each is said over the window and made by Make the iOS Edits.</summary>
public sealed record IosHeadEdits(string Project, string Name)
{
    /// <summary>The framework the project targets, whose iOS apps run on Mono, and the one on CoreCLR it moves to; nulls
    /// where it is on CoreCLR.</summary>
    public string? Framework { get; init; }

    public string? Moved { get; init; }

    /// <summary>The least iOS the workload builds for, where the project's own (<see cref="Version"/>) is below it.</summary>
    public string? Least { get; init; }

    /// <summary>The least iOS the project builds for (its SupportedOSPlatformVersion), as it is.</summary>
    public string? Version { get; init; }

    /// <summary>The app's Info.plist, where it is to be edited.</summary>
    public string? Manifest { get; init; }

    /// <summary>The MinimumOSVersion Info.plist holds, where it is not the least iOS the project builds for once edited.</summary>
    public string? ManifestMinimum { get; init; }

    /// <summary>The SDK version of the Xcode the app builds with, where that SDK asks every app for a scene manifest and
    /// Info.plist has none.</summary>
    public string? SceneSdk { get; init; }

    /// <summary>Whether the app's delegate hands iOS its scene delegate, so a scene manifest is all its scenes need.</summary>
    public bool ScenesDelegated { get; init; }

    /// <summary>Why an edit of the project's is not Passenger's to make (what it edits is set outside its own
    /// unconditioned properties); null where each is.</summary>
    public string? ByHand { get; init; }

    /// <summary>Whether Passenger writes an edit of the project's.</summary>
    public bool WritesProject { get; init; }

    /// <summary>The least iOS the project builds for once edited.</summary>
    public string? Building => Least ?? Version;

    /// <summary>Whether anything is to be edited.</summary>
    public bool Needed => Moved is not null || Least is not null || ManifestMinimum is not null || SceneSdk is not null;

    /// <summary>Whether Make the iOS Edits has an edit of these to make.</summary>
    public bool Makes => Files.Count > 0;

    /// <summary>The files the edits write.</summary>
    public IReadOnlyList<string> Files => [.. (WritesProject ? [Project] : Array.Empty<string>()),
        .. (Manifest is not null && (ManifestMinimum is not null || (SceneSdk is not null && ScenesDelegated)) ? [Manifest] : Array.Empty<string>())];

    /// <summary>The banner's sentence: each edit, and what is left to the person.</summary>
    public string Said
    {
        get
        {
            List<string> edits = [];
            if (Moved is not null)
            {
                edits.Add($"{Moved} in place of {Framework}, whose iOS apps run on Mono (Passenger builds, runs and debugs mobile apps from .NET {MobilePlatforms.FirstOnCoreClr}, on CoreCLR)");
            }
            if (Least is not null)
            {
                edits.Add($"iOS {Least} at the least, the least .NET {FrameworkNames.Major(Moved ?? Framework) ?? MobilePlatforms.FirstOnCoreClr}'s iOS workload builds for, "
                    + $"in place of {Version} in its project{(ManifestMinimum is null ? string.Empty : " and Info.plist")}");
            }
            else if (ManifestMinimum is not null)
            {
                edits.Add($"iOS {Building} as Info.plist's {AppManifests.MinimumOSVersion}, as its project says, in place of {ManifestMinimum}");
            }
            if (SceneSdk is not null)
            {
                edits.Add($"a scene manifest ({AppManifests.SceneManifest}) in Info.plist, which the iOS {SceneSdk} SDK asks of every app"
                    + (ScenesDelegated ? string.Empty : $", and a scene delegate of the app's own ({SceneGuide})"));
            }
            string listed = edits.Count == 1 ? edits[0] : $"{string.Join("; ", edits[..^1])}; and {edits[^1]}";
            return $"{Name} needs {(edits.Count == 1 ? "an edit" : "edits")} to build here: {listed}{(ByHand is null ? string.Empty : $". {ByHand}")}";
        }
    }

    /// <summary>Apple's guide to an app's scenes, which the iOS workload names too.</summary>
    public const string SceneGuide = "https://aka.ms/uiscene";
}

/// <summary>What the iOS workload answers of an iOS app's project at the framework it builds on CoreCLR: the SDK version of
/// the Xcode it builds with, the Info.plist it takes and those laid over it, the least iOS the project builds for and the
/// least the workload builds for, whether it checks the app's scene manifest and makes an app's bundle, and the version of
/// Avalonia's iOS package it references, whose app delegate hands iOS its scene delegate from 12.</summary>
public sealed record IosHeadAnswer(string? SdkVersion, string? Manifest, IReadOnlyList<string> Laid, string? Version, string? Least, bool ChecksScenes, bool MakesApp,
    string? Avalonia);

/// <summary>The iOS app heads of a folder: which project is one, what its workload is asked and what its edits are.</summary>
public static class IosHeads
{
    /// <summary>Avalonia's iOS package, whose app delegate hands iOS its scene delegate from <see cref="AvaloniaScenes"/>.</summary>
    private const string AvaloniaIos = "Avalonia.iOS";

    private const int AvaloniaScenes = 12;

    /// <summary>The first Xcode SDK whose apps must take UIKit's scenes.</summary>
    private const int ScenesAsked = 27;

    /// <summary>A project's one framework a workload builds, where it is iOS's: the iOS head its edits are asked of.</summary>
    public static string? HeadOf(IReadOnlyList<string> frameworks) =>
        MobilePlatforms.FrameworksOf(frameworks) is [{ } only] && MobilePlatforms.Of(only) == FrameworkNames.Ios ? only : null;

    /// <summary>The framework an app builds on CoreCLR: its own where it is, else the first .NET's on CoreCLR for its platform
    /// (net11.0-ios of net10.0-ios and of net9.0-ios18.0).</summary>
    public static string OnCoreClr(string framework) =>
        FrameworkNames.Major(framework) is >= MobilePlatforms.FirstOnCoreClr ? framework : $"net{MobilePlatforms.FirstOnCoreClr}.0-{FrameworkNames.PlatformOf(framework)}";

    /// <summary>The question of the head's workload, asked at <see cref="OnCoreClr"/>.</summary>
    public static MSBuildQuestion Question { get; } = new([MSBuildNames.DetectSdkLocations, MSBuildNames.DetectAppManifest])
    {
        Properties = [MSBuildNames.SdkVersion, MSBuildNames.AppBundleManifest, MSBuildNames.SupportedOSPlatformVersion, MSBuildNames.MinSupportedOSPlatformVersion,
            MSBuildNames.ValidateSceneManifest, MSBuildNames.CanOutputAppBundle, MSBuildNames.IsAppExtension],
        Items = [MSBuildNames.PartialAppManifest, "PackageReference", "PackageVersion"],
    };

    /// <summary>What the workload answered the question, its files' paths full from the project's folder; null for no answer.</summary>
    public static IosHeadAnswer? Answer(MSBuildAnswer said, string folder)
    {
        if (!said.Succeeded || !said.Answered)
        {
            return null;
        }
        string? Full(string? path) => path is { Length: > 0 } ? PathNames.Canonical(Path.Combine(folder, path)) : null;
        string? Text(string name) => said.Property(name) is { Length: > 0 } value ? value : null;
        string? avalonia = Version(said.Items("PackageReference")) ?? Version(said.Items("PackageVersion"));
        return new IosHeadAnswer(Text(MSBuildNames.SdkVersion), Full(Text(MSBuildNames.AppBundleManifest)),
            [.. said.Items(MSBuildNames.PartialAppManifest).Select(i => Full(JsonMembers.Text(i, "FullPath") ?? JsonMembers.Text(i, "Identity"))).OfType<string>()],
            Text(MSBuildNames.SupportedOSPlatformVersion), Text(MSBuildNames.MinSupportedOSPlatformVersion),
            !string.Equals(Text(MSBuildNames.ValidateSceneManifest), "false", StringComparison.OrdinalIgnoreCase),
            string.Equals(Text(MSBuildNames.CanOutputAppBundle), "true", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Text(MSBuildNames.IsAppExtension), "true", StringComparison.OrdinalIgnoreCase),
            avalonia);

        // Avalonia's iOS package's version as an item of it gives it, its own or its central version's.
        static string? Version(IReadOnlyList<JsonElement> items) => items
            .Where(i => string.Equals(JsonMembers.Text(i, "Identity"), AvaloniaIos, StringComparison.OrdinalIgnoreCase))
            .Select(i => JsonMembers.Text(i, "VersionOverride") is { Length: > 0 } over ? over : JsonMembers.Text(i, "Version"))
            .FirstOrDefault(v => v is { Length: > 0 });
    }

    /// <summary>The edits an iOS head needs, from its project's text and its workload's answer at <paramref name="built"/>,
    /// the framework it builds on CoreCLR, the files it reads read through <paramref name="read"/> (null for one that
    /// cannot be); null where it needs none.</summary>
    public static IosHeadEdits? Edits(string project, string name, string framework, string built, IosHeadAnswer answer, Func<string, string?> read)
    {
        if (!answer.MakesApp)
        {
            return null;
        }
        bool moved = !string.Equals(framework, built, StringComparison.OrdinalIgnoreCase);
        bool raised = Below(answer.Version, answer.Least);
        string? building = raised ? answer.Least : answer.Version;
        IReadOnlyDictionary<string, string?>? keys = answer.Manifest is { } manifest && read(manifest) is { } plist ? AppManifests.Keys(plist) : null;
        string? minimum = keys is not null && keys.TryGetValue(AppManifests.MinimumOSVersion, out string? held) && held is not null && building is not null
            && !Same(held, building) ? held : null;
        bool scenes = answer.ChecksScenes && keys is not null && Major(answer.SdkVersion) is >= ScenesAsked && !keys.ContainsKey(AppManifests.SceneManifest)
            && !answer.Laid.Any(l => read(l) is { } laid && AppManifests.Keys(laid)?.ContainsKey(AppManifests.SceneManifest) == true);
        IosHeadEdits edits = new(project, name)
        {
            Framework = moved ? framework : null,
            Moved = moved ? built : null,
            Least = raised ? answer.Least : null,
            Version = answer.Version,
            Manifest = minimum is not null || scenes ? answer.Manifest : null,
            ManifestMinimum = minimum,
            SceneSdk = scenes ? answer.SdkVersion : null,
            ScenesDelegated = Leading(answer.Avalonia) is >= AvaloniaScenes,
        };
        if (!edits.Needed)
        {
            return null;
        }
        if (!(moved || raised) || read(project) is not { } text)
        {
            return edits;
        }
        (string made, string? why) = ProjectText(text, edits);
        return edits with { ByHand = why, WritesProject = made != text };
    }

    /// <summary>The project's text with its framework moved and its least iOS raised where <paramref name="edits"/> say,
    /// each where its own unconditioned property sets it, and why not for each set elsewhere (null for none).</summary>
    public static (string Text, string? Why) ProjectText(string text, IosHeadEdits edits)
    {
        string now = text;
        List<string> unmade = [];
        if (edits is { Framework: { } framework, Moved: { } moved })
        {
            if (Retargeted(now, edits.Project, framework, moved) is { } retargeted)
            {
                now = retargeted;
            }
            else
            {
                unmade.Add($"its framework {framework} is set outside the project's own properties: set {moved} there");
            }
        }
        if (edits is { Least: { } least, Version: { } version })
        {
            if (Same(ProjectEdits.PropertyIn(now, MSBuildNames.SupportedOSPlatformVersion) ?? string.Empty, version)
                && ProjectEdits.WithProperty(now, edits.Project, MSBuildNames.SupportedOSPlatformVersion, least).Text is { } raised)
            {
                now = raised;
            }
            else
            {
                unmade.Add($"its {MSBuildNames.SupportedOSPlatformVersion} {version} is set outside the project's own properties: set {least} there");
            }
        }
        string left = string.Join("; ", unmade);
        return (now, left.Length == 0 ? null : char.ToUpperInvariant(left[0]) + left[1..]);
    }

    /// <summary>Info.plist's text with its MinimumOSVersion and scene manifest as <paramref name="edits"/> say; unchanged
    /// where they say nothing of it.</summary>
    public static string ManifestText(string text, IosHeadEdits edits)
    {
        string now = text;
        if (edits is { ManifestMinimum: not null, Building: { } building } && AppManifests.WithMinimumOSVersion(now, building) is { } lowered)
        {
            now = lowered;
        }
        if (edits is { SceneSdk: not null, ScenesDelegated: true } && AppManifests.WithSceneManifest(now) is { } scened)
        {
            now = scened;
        }
        return now;
    }

    /// <summary>The project's text with its framework named <paramref name="moved"/> in place of <paramref name="framework"/>
    /// in its own unconditioned TargetFramework, else TargetFrameworks; null where neither names it.</summary>
    private static string? Retargeted(string text, string project, string framework, string moved)
    {
        if (ProjectEdits.PropertyIn(text, MSBuildNames.TargetFramework) is { } single && string.Equals(single, framework, StringComparison.OrdinalIgnoreCase))
        {
            return ProjectEdits.WithProperty(text, project, MSBuildNames.TargetFramework, moved).Text;
        }
        if (ProjectEdits.PropertyIn(text, MSBuildNames.TargetFrameworks) is { } several
            && several.Split(';').Select(f => f.Trim()).ToList() is { } listed && listed.FindIndex(f => string.Equals(f, framework, StringComparison.OrdinalIgnoreCase)) is >= 0 and int at)
        {
            listed[at] = moved;
            return ProjectEdits.WithProperty(text, project, MSBuildNames.TargetFrameworks, string.Join(';', listed.Where(f => f.Length > 0))).Text;
        }
        return null;
    }

    /// <summary>Whether a version is below another, each as Apple writes them (15, 15.0, 15.0.1); false where either is none.</summary>
    private static bool Below(string? version, string? least) => Parsed(version) is { } own && Parsed(least) is { } floor && own < floor;

    private static bool Same(string version, string other) => Parsed(version) is { } one && Parsed(other) is { } two ? one == two : version.Trim() == other.Trim();

    private static int? Major(string? version) => Parsed(version)?.Major;

    /// <summary>The number a package's version or range begins with (12 of 12.1.6, [12.1,13) and 12.*); null for none.</summary>
    private static int? Leading(string? version) =>
        int.TryParse(new string([.. (version ?? string.Empty).TrimStart('[', '(', ' ').TakeWhile(char.IsAsciiDigit)]), out int major) ? major : null;

    private static Version? Parsed(string? version)
    {
        string text = (version ?? string.Empty).Trim();
        if (text.Length > 0 && !text.Contains('.', StringComparison.Ordinal))
        {
            text += ".0";
        }
        if (!System.Version.TryParse(text, out Version? parsed))
        {
            return null;
        }
        // 15 and 15.0.0 are 15.0: a part left unwritten is nought.
        return new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
    }
}
