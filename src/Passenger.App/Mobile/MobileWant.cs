using Passenger.Core;

namespace Passenger.App.Mobile;

/// <summary>What the folder's mobile projects need that this machine lacks, from what their workloads answered
/// (<see cref="ProjectNeeds"/>): the .NET workloads to install, the Android SDK, its packages and the JDK to install
/// through the Android workload's installer, the Xcode the Apple workloads want and the simulator runtimes it lacks, the
/// projects on a .NET whose mobile apps run on Mono, those whose Apple apps build only on a Mac, and those whose Android
/// apps this machine builds none of (<see cref="AndroidHosts"/>). Said once, in the banner over the window.</summary>
public sealed record MobileWant
{
    public static MobileWant Nothing { get; } = new();

    /// <summary>The projects needing what is lacking, by name, in the folder's order.</summary>
    public IReadOnlyList<string> Projects { get; init; } = [];

    /// <summary>The workload ids to install, each once; off a Mac, for a project that builds only on one, only its platforms' own.</summary>
    public IReadOnlyList<string> Workloads { get; init; } = [];

    /// <summary>The Android SDK's folder, the one found or the one it would be installed into; null where no project's
    /// Android app was asked of.</summary>
    public string? AndroidSdk { get; init; }

    /// <summary>Whether there is no Android SDK at <see cref="AndroidSdk"/>.</summary>
    public bool NoAndroidSdk { get; init; }

    /// <summary>The SDK packages a build needs that the SDK lacks, as sdkmanager names them.</summary>
    public IReadOnlyList<string> AndroidPackages { get; init; } = [];

    /// <summary>The Java version the workload wants, where it found no JDK; null where it found one.</summary>
    public string? Java { get; init; }

    /// <summary>The folder JAVA_HOME names, where the workload wants a JDK though JAVA_HOME names one: no JDK it can use,
    /// which its installer would install over.</summary>
    public string? JavaHome { get; init; }

    /// <summary>Why the Apple workload cannot build with the Xcode it found, in its words; null where it can.</summary>
    public string? Xcode { get; init; }

    /// <summary>An Xcode in Applications that none chose, where the workload found none.</summary>
    public string? UnchosenXcode { get; init; }

    /// <summary>The simulator runtimes the Apple apps need that Xcode has none of ("iOS 27.0"), each once.</summary>
    public IReadOnlyList<string> Simulators { get; init; } = [];

    /// <summary>The projects on .NET 10 or earlier, each with its first such framework.</summary>
    public IReadOnlyList<(string Project, string Framework)> OnMono { get; init; } = [];

    /// <summary>The projects whose every app is an Apple platform's, off a Mac.</summary>
    public IReadOnlyList<string> AppleOffMac { get; init; } = [];

    /// <summary>The projects with an Android app, where this machine builds none (<see cref="AndroidHost"/>).</summary>
    public IReadOnlyList<string> AndroidElsewhere { get; init; } = [];

    /// <summary>This machine as a person names it where it builds no Android app ("Linux on Arm64"); null where it does.</summary>
    public string? AndroidHost { get; init; }

    /// <summary>Whether the Android workload's installer has something to install.</summary>
    public bool AndroidLacks => NoAndroidSdk || AndroidPackages.Count > 0 || Java is not null;

    /// <summary>Whether anything is said.</summary>
    public bool Says => Workloads.Count > 0 || AndroidLacks || Xcode is not null || Simulators.Count > 0 || OnMono.Count > 0 || AppleOffMac.Count > 0
        || AndroidElsewhere.Count > 0;

    /// <summary>What the folder's projects need, from their answers: <paramref name="usualSdk"/> is where the Android SDK
    /// is looked for where the workload found none, <paramref name="exists"/> whether a file or folder is there, and
    /// <paramref name="androidHost"/> this machine where it builds no Android app (<see cref="AndroidHosts.Unbuilt(string?)"/>),
    /// whose projects' workloads are still offered, as they let them load to be edited, and never the Android SDK or a JDK.</summary>
    public static MobileWant Of(IReadOnlyList<ProjectNeeds> projects, bool onMac, Func<string, string?> environment, string usualSdk, Func<string, bool> exists,
        string? androidHost = null)
    {
        List<string> needing = [];
        List<string> workloads = [];
        List<(string, string)> mono = [];
        List<string> offMac = [];
        List<string> androidElsewhere = [];
        string? sdk = null;
        bool noSdk = false;
        List<string> packages = [];
        string? java = null;
        string? xcode = null;
        string? unchosen = null;
        List<string> simulators = [];
        foreach (ProjectNeeds project in projects)
        {
            bool needs = false;
            IReadOnlyList<string> mobile = MobilePlatforms.FrameworksOf(project.Frameworks);
            // Off a Mac an app only of Apple platforms takes only its platforms' own workloads, which let it load to be edited.
            bool appleOnly = !onMac && mobile.Count > 0 && mobile.All(f => MobilePlatforms.IsApple(MobilePlatforms.Of(f)!));
            IReadOnlyList<WorkloadLacked> lacking = appleOnly
                ? [.. project.Workloads.Where(w => w.Framework is { } framework && mobile.Contains(framework, StringComparer.OrdinalIgnoreCase))]
                : project.Workloads;
            foreach (WorkloadLacked workload in lacking.Where(w => !workloads.Contains(w.Id)))
            {
                workloads.Add(workload.Id);
            }
            needs |= lacking.Count > 0;
            if (mobile.FirstOrDefault(f => MobilePlatforms.DotnetOf(f) is < MobilePlatforms.FirstOnCoreClr) is { } old)
            {
                mono.Add((project.Name, old));
                needs = true;
            }
            if (appleOnly)
            {
                offMac.Add(project.Name);
                needs = true;
            }
            bool androidHere = androidHost is null || !mobile.Any(f => MobilePlatforms.Of(f) == MobilePlatforms.Android);
            if (!androidHere)
            {
                androidElsewhere.Add(project.Name);
                needs = true;
            }
            if (androidHere && project.Android is { } android)
            {
                string folder = AndroidFolders.Of(android, environment, usualSdk);
                bool none = android.Sdk is null && !exists(Path.Combine(folder, "platform-tools")) && !exists(Path.Combine(folder, "platforms"));
                IReadOnlyList<string> lacked = none ? [.. android.Packages.Where(p => !p.StartsWith("cmdline-tools", StringComparison.Ordinal))]
                    : AndroidFolders.Lacked(folder, android.Packages, exists);
                string? wanted = android.JavaUnfound && android.Java is null ? android.JavaVersion ?? "17 or later" : null;
                sdk ??= folder;
                noSdk |= none;
                packages.AddRange(lacked.Where(p => !packages.Contains(p)));
                java ??= wanted;
                needs |= none || lacked.Count > 0 || wanted is not null;
            }
            if (onMac && project.Apple is { } apple)
            {
                if (apple.Problem is { } problem)
                {
                    xcode ??= problem;
                    unchosen ??= apple.Unchosen;
                    needs = true;
                }
                simulators.AddRange(apple.Simulators.Where(r => !simulators.Contains(r)));
                needs |= apple.Simulators.Count > 0;
            }
            if (needs)
            {
                needing.Add(project.Name);
            }
        }
        return new MobileWant
        {
            Projects = needing,
            Workloads = workloads,
            AndroidSdk = sdk,
            NoAndroidSdk = noSdk,
            AndroidPackages = packages,
            Java = java,
            JavaHome = java is not null && environment("JAVA_HOME") is { Length: > 0 } javaHome ? javaHome : null,
            Xcode = xcode,
            UnchosenXcode = unchosen,
            Simulators = simulators,
            OnMono = mono,
            AppleOffMac = offMac,
            AndroidElsewhere = androidElsewhere,
            AndroidHost = androidElsewhere.Count > 0 ? androidHost : null,
        };
    }

    /// <summary>The banner's words: what the projects need that is lacking, then what Passenger cannot run of them; null
    /// with nothing to say.</summary>
    public string? Said
    {
        get
        {
            List<string> sentences = [];
            List<string> lacks = [];
            if (Workloads.Count > 0)
            {
                lacks.Add($"the .NET {Plural.Noun(Workloads.Count, "workload")} {Plural.Listed([.. Workloads])}");
            }
            if (Java is not null)
            {
                lacks.Add($"a JDK (Java {Java})");
            }
            if (NoAndroidSdk)
            {
                lacks.Add("the Android SDK");
            }
            else if (AndroidPackages.Count > 0)
            {
                lacks.Add($"the Android SDK's {Plural.Listed([.. AndroidPackages])}");
            }
            if (Xcode is not null)
            {
                lacks.Add(UnchosenXcode is { } found
                    ? $"Xcode, which is in {found} but not chosen (sudo xcode-select -s {found})"
                    : $"Xcode ({Xcode.TrimEnd('.')})");
            }
            if (Simulators.Count > 0)
            {
                lacks.Add($"the {Plural.Listed([.. Simulators])} simulator {Plural.Noun(Simulators.Count, "runtime")}, which Xcode installs "
                    + $"(Xcode › Settings › Components, or {Plural.Listed([.. Simulators.Select(r => r.Split(' ')[0]).Distinct().Select(p => $"xcodebuild -downloadPlatform {p}")])})");
            }
            List<string> needing = [.. Projects.Where(p => !OnMono.Any(m => m.Project == p) && !AppleOffMac.Contains(p) && !AndroidElsewhere.Contains(p))];
            if (lacks.Count > 0 && needing.Count > 0)
            {
                sentences.Add($"{Plural.Listed([.. needing])} {Plural.Agreeing(needing.Count, "needs", "need")} {Plural.Listed([.. lacks])}");
            }
            if (JavaHome is { } javaHome)
            {
                sentences.Add($"JAVA_HOME names {javaHome}, where the Android workload finds no JDK");
            }
            foreach ((string project, string framework) in OnMono)
            {
                sentences.Add($"{project} targets {framework}, whose mobile apps run on Mono: Passenger builds, runs and debugs mobile apps from .NET {MobilePlatforms.FirstOnCoreClr}, on CoreCLR");
            }
            if (AppleOffMac.Count > 0)
            {
                sentences.Add($"{Plural.Listed([.. AppleOffMac])} {Plural.Agreeing(AppleOffMac.Count, "builds", "build")} only on a Mac, with Xcode");
            }
            if (AndroidElsewhere.Count > 0)
            {
                sentences.Add($"{Plural.Listed([.. AndroidElsewhere])} {Plural.Agreeing(AndroidElsewhere.Count, "builds", "build")} for Android only on "
                    + $"{AndroidHosts.Builders}: {AndroidHosts.Why(AndroidHost!)}");
            }
            return sentences.Count == 0 ? null : string.Join(". ", sentences);
        }
    }

    /// <summary>Whether a load problem's words are of what a want says plainly: a workload, the Android SDK, a JDK or Xcode
    /// that a project's workloads could not find, or Mono.</summary>
    public static bool Explains(string message) => Lacking.Any(words => message.Contains(words, StringComparison.OrdinalIgnoreCase));

    /// <summary>What the SDK and the workloads say when what this want names is lacking.</summary>
    private static readonly string[] Lacking =
    [
        "workloads must be installed", "NETSDK1147", "NETSDK1178", "Java SDK", "JDK", "Android SDK", "android.jar", "XA5300", "XA5207", "Xcode",
        "MonoRuntimeNotSupportedForPlatform", "Mono runtime",
    ];
}
