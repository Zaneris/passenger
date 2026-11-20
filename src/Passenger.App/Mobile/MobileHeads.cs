using System.Text.Json;
using Passenger.App.Run.Devices;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Mobile;

/// <summary>A head's least version of its platform (its SupportedOSPlatformVersion) raised to the least the workload of the
/// .NET it builds on builds for.</summary>
public sealed record HeadMinimum(string Platform, string Version, string Least);

/// <summary>How an app's scenes reach a scene delegate once its Info.plist has a scene manifest: its app delegate hands iOS
/// one (Avalonia's, from 12), MAUI's configuration names a SceneDelegate of its MauiUISceneDelegate, the configuration names
/// the SceneDelegate the app has beside its Info.plist, or the app is to write its own.</summary>
public enum SceneDelegation
{
    AppDelegate,
    Maui,
    OwnClass,
    OwnToWrite,
}

/// <summary>An Apple head's Info.plist edits: its least version set where it no longer matches the project's
/// (<see cref="Key"/>: iOS's MinimumOSVersion, or Mac Catalyst's LSMinimumSystemVersion, the macOS version its workload maps
/// the Mac Catalyst one to), and a scene manifest where the Xcode SDK asks every app for one and it has none, with the
/// SceneDelegate a MAUI app's configuration names written beside it where none is.</summary>
public sealed record ManifestEdit(string Platform, string Path)
{
    public string? Key { get; init; }

    public string? Was { get; init; }

    public string? Now { get; init; }

    /// <summary>The least version of its platform the head builds for once edited.</summary>
    public string? Building { get; init; }

    /// <summary>The SDK version that asks for a scene manifest Info.plist lacks; null where it has one or none is asked.</summary>
    public string? SceneSdk { get; init; }

    public SceneDelegation Scenes { get; init; }

    /// <summary>The SceneDelegate class a MAUI app's scene manifest names, to be written; null where none is.</summary>
    public string? DelegateFile { get; init; }

    /// <summary>The namespace the project's own code is written in, which a SceneDelegate written takes.</summary>
    public string? Namespace { get; init; }

    /// <summary>Whether Passenger writes Info.plist.</summary>
    public bool Writes => Now is not null || (SceneSdk is not null && Scenes != SceneDelegation.OwnToWrite);
}

/// <summary>The edits a mobile project's heads (its Android, iOS and Mac Catalyst apps) need before they build here
/// (<see cref="MobileHeads.Edits"/>): its frameworks moved from .NET's on Mono to the first on CoreCLR, each head's least
/// version raised to the least its workload builds for, and its Info.plist's least version and scene manifest. Each is said
/// over the window and made by Make the Mobile Edits, or by the fix on a line it changes.</summary>
public sealed record HeadEdits(string Project, string Name)
{
    /// <summary>The frameworks the project writes, on Mono, each with the one on CoreCLR it moves to.</summary>
    public IReadOnlyList<(string Framework, string Moved)> Moves { get; init; } = [];

    public IReadOnlyList<HeadMinimum> Raised { get; init; } = [];

    public IReadOnlyList<ManifestEdit> Manifests { get; init; } = [];

    /// <summary>Why an edit is not Passenger's to make (what it edits set outside the project's own properties, a version
    /// it cannot map); null where each is.</summary>
    public string? ByHand { get; init; }

    /// <summary>Whether Passenger writes an edit of the project's.</summary>
    public bool WritesProject { get; init; }

    /// <summary>Whether its Mac Catalyst head's Debug build trims .NET's own assemblies, which Passenger's inspector and
    /// update handlers' hook need whole (no need of its build, so the banner leaves it to the Run window and the project's
    /// fix: <see cref="MobileHeads.WholeText"/>); with the platform MtouchLink is set for where it has other heads.</summary>
    public bool TrimsDebug { get; init; }

    public string? TrimmedPlatform { get; init; }

    /// <summary>Whether anything is to be edited.</summary>
    public bool Needed => Moves.Count > 0 || Raised.Count > 0 || Manifests.Any(m => m.Now is not null || m.SceneSdk is not null);

    /// <summary>Whether Make the Mobile Edits has an edit of these to make.</summary>
    public bool Makes => Files.Count > 0;

    /// <summary>The files the edits write, a SceneDelegate written among them.</summary>
    public IReadOnlyList<string> Files => [.. (WritesProject ? [Project] : Array.Empty<string>()), .. Manifests.Where(m => m.Writes).Select(m => m.Path),
        .. Manifests.Where(m => m is { SceneSdk: not null, Scenes: SceneDelegation.Maui, DelegateFile: not null }).Select(m => m.DelegateFile!)];

    /// <summary>The banner's sentence: each edit, and what is left to the person.</summary>
    public string Said
    {
        get
        {
            List<string> edits = [];
            string mono = $"(Passenger builds, runs and debugs mobile apps from .NET {MobilePlatforms.FirstOnCoreClr}, on CoreCLR)";
            if (Moves is [var (framework, moved)])
            {
                edits.Add($"{moved} in place of {framework}, whose {MobilePlatforms.Named(FrameworkNames.PlatformOf(framework)!)} apps run on Mono {mono}");
            }
            else if (Moves.Count > 1)
            {
                edits.Add($"{Plural.Listed([.. Moves.Select(m => m.Moved)])} in place of {Plural.Listed([.. Moves.Select(m => m.Framework)])}, whose mobile apps run on Mono {mono}");
            }
            foreach (HeadMinimum raised in Raised)
            {
                string platform = MobilePlatforms.Named(raised.Platform);
                ManifestEdit? same = Manifests.FirstOrDefault(m => m.Platform == raised.Platform && m.Key == AppManifests.MinimumOSVersion && m.Now == raised.Least);
                edits.Add($"{(raised.Platform == FrameworkNames.Android ? "Android API" : platform)} {raised.Least} at the least, the least .NET "
                    + $"{MobilePlatforms.FirstOnCoreClr}'s {platform} workload builds for, in place of {raised.Version} in its project{(same is null ? string.Empty : $" and {Named(same)}")}");
            }
            foreach (ManifestEdit manifest in Manifests.Where(m => m.Now is not null && !Raised.Any(r => r.Platform == m.Platform && m.Key == AppManifests.MinimumOSVersion && m.Now == r.Least)))
            {
                edits.Add(manifest.Key == AppManifests.MinimumSystemVersion
                    ? $"macOS {manifest.Now} as {Named(manifest)}'s {manifest.Key}, Mac Catalyst {manifest.Building}'s, in place of {manifest.Was}"
                    : $"{MobilePlatforms.Named(manifest.Platform)} {manifest.Now} as {Named(manifest)}'s {manifest.Key}, as its project says, in place of {manifest.Was}");
            }
            foreach (ManifestEdit manifest in Manifests.Where(m => m.SceneSdk is not null))
            {
                edits.Add($"a scene manifest ({AppManifests.SceneManifest}) in {Named(manifest)}, which the {MobilePlatforms.Named(manifest.Platform)} {manifest.SceneSdk} SDK asks of every app"
                    + manifest.Scenes switch
                    {
                        SceneDelegation.Maui when manifest.DelegateFile is not null => ", naming a SceneDelegate of MAUI's MauiUISceneDelegate written beside it",
                        SceneDelegation.Maui or SceneDelegation.OwnClass => ", naming its SceneDelegate",
                        SceneDelegation.OwnToWrite => $", and a scene delegate of the app's own ({SceneGuide})",
                        _ => string.Empty,
                    });
            }
            if (edits.Count == 0)
            {
                // Its Mac Catalyst head's trimmed Debug build alone, which the banner leaves to its run and its fix.
                return string.Empty;
            }
            string listed = edits.Count == 1 ? edits[0] : $"{string.Join("; ", edits[..^1])}; and {edits[^1]}";
            return $"{Name} needs {(edits.Count == 1 ? "an edit" : "edits")} to build and run here: {listed}{(ByHand is null ? string.Empty : $". {ByHand}")}";
        }
    }

    /// <summary>An Info.plist as the banner names it: Info.plist where the project has one to edit, else its path in the project.</summary>
    private string Named(ManifestEdit manifest) => Manifests.Select(m => m.Path).Distinct(PathNames.Comparer).Count() == 1 ? "Info.plist"
        : PathNames.Relative(manifest.Path, System.IO.Path.GetDirectoryName(Project)!) ?? manifest.Path;

    /// <summary>Apple's guide to an app's scenes, which the iOS workload names too.</summary>
    public const string SceneGuide = "https://aka.ms/uiscene";
}

/// <summary>What a head's workload answers of its project at the framework it builds on CoreCLR: its platform and the
/// framework it was asked at, the least version the project builds for and the least the workload builds for; for an Apple
/// head too, the SDK version of the Xcode it builds with, the Info.plist it takes and those laid over it, whether the
/// workload checks the app's scene manifest and makes an app's bundle, and the workload's folder. Whether the project is
/// MAUI's, its namespace and the version of Avalonia's iOS package it references come with each.</summary>
public sealed record HeadAnswer(string Platform, string Asked, string? Version, string? Least)
{
    /// <summary>How a Mac Catalyst head's Debug build trims its assemblies (None, SdkOnly, Full).</summary>
    public string? LinkMode { get; init; }

    public string? SdkVersion { get; init; }

    public string? Manifest { get; init; }

    public IReadOnlyList<string> Laid { get; init; } = [];

    public bool ChecksScenes { get; init; }

    public bool MakesApp { get; init; } = true;

    public string? SdkRoot { get; init; }

    public bool Maui { get; init; }

    public string? Namespace { get; init; }

    public string? Avalonia { get; init; }
}

/// <summary>A folder's mobile heads: which projects have heads Passenger edits, what their workloads are asked and what
/// their edits are, the project's and Info.plist's texts edited, and where in them each edit stands.</summary>
public static partial class MobileHeads
{
    /// <summary>Avalonia's iOS package, whose app delegate hands iOS its scene delegate from <see cref="AvaloniaScenes"/>.</summary>
    private const string AvaloniaIos = "Avalonia.iOS";

    private const int AvaloniaScenes = 12;

    /// <summary>The first Xcode SDK whose apps must take UIKit's scenes.</summary>
    private const int ScenesAsked = 27;

    /// <summary>The class a scene configuration names, and its file.</summary>
    private const string SceneDelegate = "SceneDelegate";

    /// <summary>A class registered with the Objective-C runtime as the SceneDelegate a scene configuration names.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\[\s*Register\s*\(\s*""SceneDelegate""\s*\)\s*\]")]
    private static partial System.Text.RegularExpressions.Regex RegistersDelegate();

    /// <summary>The name .NET's templates give an app's one scene configuration.</summary>
    private const string DefaultConfiguration = "Default Configuration";

    /// <summary>The Apple workload's map of Mac Catalyst's versions to macOS's, in its Versions.plist.</summary>
    private const string CatalystVersions = "MacCatalystVersionMap";

    /// <summary>The platforms whose heads Passenger edits.</summary>
    private static readonly string[] Edited = [FrameworkNames.Android, FrameworkNames.Ios, FrameworkNames.MacCatalyst];

    /// <summary>A project's heads to ask of: its mobile frameworks, where each is a platform Passenger edits (none for a
    /// project with another, a tvOS app's), its Apple ones only on a Mac.</summary>
    public static IReadOnlyList<string> HeadsOf(IReadOnlyList<string> frameworks, bool onMac)
    {
        IReadOnlyList<string> mobile = MobilePlatforms.FrameworksOf(frameworks);
        return mobile.Any(f => MobilePlatforms.Of(f) is not { } platform || !Edited.Contains(platform)) ? []
            : [.. mobile.Where(f => onMac || !MobilePlatforms.IsApple(MobilePlatforms.Of(f)!)).DistinctBy(f => MobilePlatforms.Of(f))];
    }

    /// <summary>The framework an app builds on CoreCLR: its own where it is, else the first .NET's on CoreCLR for its platform
    /// (net11.0-ios of net10.0-ios and of net9.0-ios18.0).</summary>
    public static string OnCoreClr(string framework) =>
        FrameworkNames.Major(framework) is >= MobilePlatforms.FirstOnCoreClr ? framework : $"net{MobilePlatforms.FirstOnCoreClr}.0-{FrameworkNames.PlatformOf(framework)}";

    /// <summary>The question of a head's workload, asked at <see cref="OnCoreClr"/>; a Mac Catalyst head's of its Debug build,
    /// whose trimming it asks too.</summary>
    public static MSBuildQuestion Question(string platform) => platform == FrameworkNames.Android
        ? new([]) { Properties = [MSBuildNames.SupportedOSPlatformVersion, MSBuildNames.AndroidMinimumSupportedApiLevel, MSBuildNames.UseMaui, MSBuildNames.RootNamespace] }
        : new([MSBuildNames.DetectSdkLocations, MSBuildNames.DetectAppManifest])
        {
            Properties = [MSBuildNames.SdkVersion, MSBuildNames.AppBundleManifest, MSBuildNames.SupportedOSPlatformVersion, MSBuildNames.MinSupportedOSPlatformVersion,
                MSBuildNames.ValidateSceneManifest, MSBuildNames.CanOutputAppBundle, MSBuildNames.IsAppExtension, MSBuildNames.XamarinSdkRootDirectory,
                MSBuildNames.UseMaui, MSBuildNames.RootNamespace, MSBuildNames.PlatformsProjectFolder, platform == FrameworkNames.MacCatalyst ? MSBuildNames.MacCatalystProjectFolder
                    : MSBuildNames.IosProjectFolder, .. platform == FrameworkNames.MacCatalyst ? [MSBuildNames.LinkMode] : Array.Empty<string>()],
            Items = [MSBuildNames.PartialAppManifest, MSBuildNames.PackageReference, MSBuildNames.PackageVersion],
            Given = platform == FrameworkNames.MacCatalyst ? new Dictionary<string, string> { [MSBuildNames.Configuration] = "Debug" } : new Dictionary<string, string>(),
        };

    /// <summary>What a head's workload answered its question, its files' paths full from the project's folder; null for no answer.</summary>
    public static HeadAnswer? Answer(string platform, string asked, MSBuildAnswer said, string folder)
    {
        if (!said.Succeeded || !said.Answered)
        {
            return null;
        }
        // MSBuild's values name folders with either slash, a MAUI package's with backslashes.
        string? Full(string? path) => path is { Length: > 0 } ? PathNames.Canonical(Path.GetFullPath(Path.Combine(folder, path.Replace('\\', '/')))) : null;
        string? Text(string name) => said.Property(name) is { Length: > 0 } value ? value : null;
        bool True(string name) => string.Equals(Text(name), "true", StringComparison.OrdinalIgnoreCase);
        HeadAnswer answer = new(platform, asked, Text(MSBuildNames.SupportedOSPlatformVersion),
            Text(platform == FrameworkNames.Android ? MSBuildNames.AndroidMinimumSupportedApiLevel : MSBuildNames.MinSupportedOSPlatformVersion))
        {
            Maui = True(MSBuildNames.UseMaui),
            Namespace = Text(MSBuildNames.RootNamespace),
        };
        if (platform == FrameworkNames.Android)
        {
            return answer;
        }
        return answer with
        {
            SdkVersion = Text(MSBuildNames.SdkVersion),
            Manifest = Full(Text(MSBuildNames.AppBundleManifest)) ?? (answer.Maui ? Full(Path.Combine(MauiFolder(), "Info.plist")) : null),
            Laid = [.. said.Items(MSBuildNames.PartialAppManifest).Select(i => Full(JsonMembers.Text(i, MSBuildNames.FullPath) ?? JsonMembers.Text(i, "Identity"))).OfType<string>()],
            ChecksScenes = !string.Equals(Text(MSBuildNames.ValidateSceneManifest), "false", StringComparison.OrdinalIgnoreCase),
            MakesApp = True(MSBuildNames.CanOutputAppBundle) && !True(MSBuildNames.IsAppExtension),
            SdkRoot = Text(MSBuildNames.XamarinSdkRootDirectory),
            LinkMode = platform == FrameworkNames.MacCatalyst ? Text(MSBuildNames.LinkMode) : null,
            Avalonia = Version(said.Items(MSBuildNames.PackageReference)) ?? Version(said.Items(MSBuildNames.PackageVersion)),
        };

        // A MAUI head's folder as its package names it, which an unrestored project's evaluation lacks: Platforms/iOS by default.
        string MauiFolder() => Text(platform == FrameworkNames.MacCatalyst ? MSBuildNames.MacCatalystProjectFolder : MSBuildNames.IosProjectFolder)
            ?? Path.Combine(Text(MSBuildNames.PlatformsProjectFolder) ?? "Platforms", platform == FrameworkNames.MacCatalyst ? "MacCatalyst" : "iOS");

        // Avalonia's iOS package's version as an item of it gives it, its own or its central version's.
        static string? Version(IReadOnlyList<JsonElement> items) => items
            .Where(i => string.Equals(JsonMembers.Text(i, "Identity"), AvaloniaIos, StringComparison.OrdinalIgnoreCase))
            .Select(i => JsonMembers.Text(i, MSBuildNames.VersionOverride) is { Length: > 0 } over ? over : JsonMembers.Text(i, MSBuildNames.Version))
            .FirstOrDefault(v => v is { Length: > 0 });
    }

    /// <summary>The edits a project's heads need, from its text, the frameworks it writes and its heads' answers, the files
    /// it reads read through <paramref name="read"/> (null for one there is none of); null where they need none.</summary>
    public static HeadEdits? Edits(string project, string name, IReadOnlyList<HeadAnswer> answers, Func<string, string?> read)
    {
        if (read(project) is not { } text)
        {
            return null;
        }
        bool maui = answers.Any(a => a.Maui);
        // MAUI's packages take one .NET for every head, so its Windows head moves with its mobile ones.
        List<(string, string)> moves = [.. ProjectEdits.FrameworksWritten(text).Where(f => FrameworkNames.Major(f) is < MobilePlatforms.FirstOnCoreClr
            && FrameworkNames.PlatformOf(f) is { } platform && (Edited.Contains(platform) || (maui && platform == FrameworkNames.Windows)))
            .Select(f => (f, FrameworkNames.PlatformOf(f) == FrameworkNames.Windows ? $"net{MobilePlatforms.FirstOnCoreClr}.0{f[f.IndexOf('-')..]}" : OnCoreClr(f)))
            .DistinctBy(m => m.f, StringComparer.OrdinalIgnoreCase)];
        List<HeadMinimum> raised = [.. answers.Where(a => Below(a.Version, a.Least)).Select(a => a.Platform == FrameworkNames.Android
            ? new HeadMinimum(a.Platform, Level(a.Version!), Level(a.Least!)) : new HeadMinimum(a.Platform, a.Version!, a.Least!))];
        List<ManifestEdit> manifests = [];
        List<string> byHand = [];
        foreach (HeadAnswer apple in answers.Where(a => a.Platform != FrameworkNames.Android && a is { MakesApp: true, Manifest: not null }))
        {
            if (Manifest(apple, raised.FirstOrDefault(r => r.Platform == apple.Platform)?.Least ?? apple.Version, maui, answers, read, byHand) is { } edit)
            {
                manifests.Add(edit);
            }
        }
        bool trims = answers.Any(a => a is { Platform: FrameworkNames.MacCatalyst, LinkMode: { } mode } && !string.Equals(mode, "None", StringComparison.OrdinalIgnoreCase));
        HeadEdits edits = new(project, name)
        {
            Moves = moves,
            Raised = raised,
            Manifests = manifests,
            TrimsDebug = trims,
            TrimmedPlatform = trims && ProjectEdits.FrameworksWritten(text).Count > 1 ? FrameworkNames.MacCatalyst : null,
        };
        if (!edits.Needed)
        {
            return trims ? edits : null;
        }
        (string made, string? why) = moves.Count > 0 || raised.Count > 0 ? ProjectText(text, edits) : (text, null);
        if (why is not null)
        {
            byHand.Insert(0, why);
        }
        return edits with { WritesProject = made != text, ByHand = byHand.Count == 0 ? null : string.Join(". ", byHand) };
    }

    /// <summary>An Apple head's Info.plist edits, building for <paramref name="building"/>; null where it needs none.</summary>
    private static ManifestEdit? Manifest(HeadAnswer apple, string? building, bool maui, IReadOnlyList<HeadAnswer> answers, Func<string, string?> read, List<string> byHand)
    {
        if (read(apple.Manifest!) is not { } plist || AppManifests.Keys(plist) is not { } keys)
        {
            return null;
        }
        string key = apple.Platform == FrameworkNames.MacCatalyst ? AppManifests.MinimumSystemVersion : AppManifests.MinimumOSVersion;
        string? held = keys.GetValueOrDefault(key);
        string? wanted = building;
        if (held is not null && building is not null && apple.Platform == FrameworkNames.MacCatalyst)
        {
            // Mac Catalyst's Info.plist names the macOS its version runs on, as the workload maps them.
            wanted = apple.SdkRoot is { } root && read(Path.Combine(root, "Versions.plist")) is { } versions
                ? AppManifests.Strings(versions, CatalystVersions).FirstOrDefault(v => Same(v.Key, building)).Value : null;
            if (wanted is null)
            {
                byHand.Add($"Its {key} {held} is to be the macOS version of Mac Catalyst {building}");
            }
        }
        bool scenes = apple.ChecksScenes && Major(apple.SdkVersion) is >= ScenesAsked && !keys.ContainsKey(AppManifests.SceneManifest)
            && !apple.Laid.Any(l => read(l) is { } laid && AppManifests.Keys(laid)?.ContainsKey(AppManifests.SceneManifest) == true);
        string delegateFile = Path.Combine(Path.GetDirectoryName(apple.Manifest!)!, SceneDelegate + ".cs");
        string? delegated = read(delegateFile);
        SceneDelegation delegation = maui ? SceneDelegation.Maui
            : Leading(answers.Select(a => a.Avalonia).FirstOrDefault(v => v is not null)) is >= AvaloniaScenes ? SceneDelegation.AppDelegate
            : delegated is not null && RegistersDelegate().IsMatch(delegated) ? SceneDelegation.OwnClass
            : SceneDelegation.OwnToWrite;
        ManifestEdit edit = new(apple.Platform, apple.Manifest!)
        {
            Key = key,
            Was = held,
            Now = held is not null && wanted is not null && !Same(held, wanted) ? wanted : null,
            Building = building,
            SceneSdk = scenes ? apple.SdkVersion : null,
            Scenes = delegation,
            DelegateFile = scenes && delegation == SceneDelegation.Maui && delegated is null ? delegateFile : null,
            Namespace = apple.Namespace,
        };
        return edit.Now is not null || edit.SceneSdk is not null ? edit : null;
    }

    /// <summary>The project's text with its frameworks moved and each head's least version raised where
    /// <paramref name="edits"/> say, each where the project's own property sets it for that head, and why not for each set
    /// elsewhere (null for none).</summary>
    public static (string Text, string? Why) ProjectText(string text, HeadEdits edits)
    {
        string now = text;
        List<string> unmade = [];
        foreach (Func<string, string?> step in Steps(edits, unmade))
        {
            if (step(now) is { } changed)
            {
                now = changed;
            }
        }
        string left = string.Join("; ", unmade);
        return (now, left.Length == 0 ? null : char.ToUpperInvariant(left[0]) + left[1..]);
    }

    /// <summary>The project's edits, each a change of its text that is null where it cannot be made, said in
    /// <paramref name="unmade"/> as it is tried.</summary>
    private static IEnumerable<Func<string, string?>> Steps(HeadEdits edits, List<string> unmade)
    {
        if (edits.Moves.Count > 0)
        {
            yield return text => Unmade(ProjectEdits.WithFrameworksMoved(text, edits.Moves.ToDictionary(m => m.Framework, m => m.Moved, StringComparer.OrdinalIgnoreCase)),
                $"its frameworks {Plural.Listed([.. edits.Moves.Select(m => m.Framework)])} are set outside the project's own properties: set {Plural.Listed([.. edits.Moves.Select(m => m.Moved)])} there");
        }
        foreach (HeadMinimum raised in edits.Raised)
        {
            yield return text => Unmade(ProjectEdits.WithPlatformProperty(text, MSBuildNames.SupportedOSPlatformVersion, raised.Platform, raised.Version, raised.Least),
                $"its {MobilePlatforms.Named(raised.Platform)} {MSBuildNames.SupportedOSPlatformVersion} {raised.Version} is set outside the project's own properties: set {raised.Least} there");
        }

        string? Unmade(string? changed, string why)
        {
            if (changed is null)
            {
                unmade.Add(why);
            }
            return changed;
        }
    }

    /// <summary>The project's text with MtouchLink None for its Mac Catalyst head's Debug build alone, which keeps .NET's own
    /// assemblies whole for Passenger's inspector and update handlers' hook; null where the project sets MtouchLink itself.</summary>
    public static string? WholeText(string text, string? platform) => ProjectEdits.WithDebugProperty(text, MSBuildNames.MtouchLink, "None", platform);

    /// <summary>What making the Debug build whole comes to: the edit, and that the head's next build needs its obj and bin
    /// emptied first, as it fails on what a trimmed one left there.</summary>
    public static string Whole(string name) =>
        $"{name}'s Mac Catalyst Debug build keeps .NET's own assemblies whole ({MacCatalystApp.LinkNone}): delete its obj and bin folders before its next build, "
        + "which fails on what a trimmed one left there";

    /// <summary>The lines of a project naming its Mac Catalyst framework, where the fix keeping its Debug build whole stands.</summary>
    public static IReadOnlyList<TextRange> CatalystPlaces(string text)
    {
        List<TextRange> places = [];
        for (int at = text.IndexOf(FrameworkNames.MacCatalyst, StringComparison.OrdinalIgnoreCase); at >= 0;
            at = text.IndexOf(FrameworkNames.MacCatalyst, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            TextRange line = TextRange.FromBounds(TextNavigation.LineStart(text, at), TextNavigation.LineEnd(text, at));
            if (text[line.Start..line.End].Contains("<TargetFramework", StringComparison.Ordinal) && !places.Contains(line))
            {
                places.Add(line);
            }
        }
        return places;
    }

    /// <summary>Info.plist's text with its least version and scene manifest as <paramref name="edit"/> says; unchanged where
    /// it says nothing of it.</summary>
    public static string ManifestText(string text, ManifestEdit edit)
    {
        string now = text;
        if (edit is { Now: { } version, Key: { } key } && AppManifests.WithString(now, key, version) is { } set)
        {
            now = set;
        }
        if (edit is { SceneSdk: not null, Scenes: not SceneDelegation.OwnToWrite }
            && AppManifests.WithSceneManifest(now, edit.Scenes switch
            {
                SceneDelegation.Maui => (AppManifests.MauiSceneConfiguration, SceneDelegate),
                SceneDelegation.OwnClass => (DefaultConfiguration, SceneDelegate),
                _ => null,
            }) is { } scened)
        {
            now = scened;
        }
        return now;
    }

    /// <summary>The SceneDelegate a MAUI app's scene configuration names, in the project's namespace.</summary>
    public static string DelegateText(string? named) =>
        $"using Foundation;\n\n{(named is { Length: > 0 } ns ? $"namespace {ns};\n\n" : string.Empty)}[Register(\"{SceneDelegate}\")]\npublic class {SceneDelegate} : MauiUISceneDelegate\n{{\n}}\n";

    /// <summary>Where the edits of a file stand in its text, each its lines: the project's properties they change, and in an
    /// Info.plist its least version's line and, for a scene manifest it lacks, its dictionary's first line.</summary>
    public static IReadOnlyList<TextRange> Places(string file, string text, HeadEdits edits)
    {
        List<TextRange> places = [];
        if (PathNames.Same(file, edits.Project))
        {
            foreach (Func<string, string?> step in Steps(edits, []))
            {
                if (step(text) is { } changed && Changed(text, changed) is { } at)
                {
                    places.Add(at);
                }
            }
        }
        foreach (ManifestEdit manifest in edits.Manifests.Where(m => m.Writes && PathNames.Same(m.Path, file)))
        {
            if (manifest is { Now: { } now, Key: { } key } && AppManifests.WithString(text, key, now) is { } set && Changed(text, set) is { } at)
            {
                places.Add(at);
            }
            if (manifest.SceneSdk is not null && text.IndexOf("<dict", StringComparison.Ordinal) is >= 0 and int dict)
            {
                places.Add(TextRange.FromBounds(TextNavigation.LineStart(text, dict), TextNavigation.LineEnd(text, dict)));
            }
        }
        return places;

        // The lines a change of a text stands on, between where the two first and last differ.
        static TextRange? Changed(string was, string now)
        {
            int start = 0;
            while (start < was.Length && start < now.Length && was[start] == now[start])
            {
                start++;
            }
            int end = 0;
            while (end < was.Length - start && end < now.Length - start && was[^(end + 1)] == now[^(end + 1)])
            {
                end++;
            }
            return start == was.Length && start == now.Length ? null
                : TextRange.FromBounds(TextNavigation.LineStart(was, start), TextNavigation.LineEnd(was, Math.Max(start, was.Length - end)));
        }
    }

    /// <summary>An Android API level as Android names one (23 of the 23.0 an evaluation gives).</summary>
    private static string Level(string version) => version.EndsWith(".0", StringComparison.Ordinal) ? version[..^2] : version;

    /// <summary>Whether a version is below another, each as Apple and Android write them (15, 15.0, 15.0.1, 23); false where
    /// either is none.</summary>
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
