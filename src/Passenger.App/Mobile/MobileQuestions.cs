using Passenger.Core;

namespace Passenger.App.Mobile;

/// <summary>What a mobile project needs, asked of the .NET Passenger builds with and of the project's own workloads,
/// through MSBuild's questions as the .NET CLI's (no build, no restore): the workloads it lacks
/// (<c>_GetRequiredWorkloads</c>), what its Android app needs (<c>GetAndroidDependencies</c>, Java's too) and whether its
/// Apple apps have the Xcode their workload wants (<c>_DetectSdkLocations</c>) and its simulator runtimes (that Xcode's
/// <c>simctl</c>). Only a framework on CoreCLR whose workload is installed is asked past the first, an Android one only
/// where this machine builds Android apps.</summary>
public static class MobileQuestions
{
    /// <summary>Where Xcode is installed when it is, on a Mac.</summary>
    private const string Applications = "/Applications";

    /// <summary>The project's needs, its MSBuild run with the solution's global properties.</summary>
    public static async Task<ProjectNeeds> AskAsync(string project, string name, IReadOnlyList<string> frameworks, IReadOnlyDictionary<string, string> globals,
        bool onMac, bool buildsAndroid, CancellationToken cancel)
    {
        IReadOnlyList<string> mobile = MobilePlatforms.FrameworksOf(frameworks);
        ChildResult required = await MSBuildAsync(project, null, ["-target:_GetRequiredWorkloads", "-getTargetResult:_GetRequiredWorkloads"], globals, cancel);
        IReadOnlyList<WorkloadLacked> workloads = MobileAnswers.Workloads(required.Output);
        bool Asked(string framework, Func<string, bool> platform) => platform(MobilePlatforms.Of(framework)!)
            && MobilePlatforms.DotnetOf(framework) is null or >= MobilePlatforms.FirstOnCoreClr
            && !workloads.Any(w => w.Framework is null || string.Equals(w.Framework, framework, StringComparison.OrdinalIgnoreCase));
        AndroidAnswer? android = null;
        if (buildsAndroid && mobile.FirstOrDefault(f => Asked(f, p => p == MobilePlatforms.Android)) is { } forAndroid)
        {
            ChildResult said = await MSBuildAsync(project, forAndroid, ["-target:GetAndroidDependencies", "-property:AndroidInstallJavaDependencies=true",
                "-getItem:AndroidDependency", "-getItem:JavaDependency", "-getProperty:AndroidSdkDirectory", "-getProperty:JavaSdkDirectory", "-getProperty:JavaSdkVersion"], globals, cancel);
            android = MobileAnswers.Android(said.Output, said.Error);
        }
        AppleAnswer? apple = null;
        if (onMac && mobile.FirstOrDefault(f => Asked(f, MobilePlatforms.IsApple)) is { } forApple)
        {
            ChildResult said = await AppleSdkAsync(project, forApple, globals, cancel);
            if (MobileAnswers.Apple(said) is { } problem)
            {
                apple = new AppleAnswer(problem, problem.Contains("developer path", StringComparison.OrdinalIgnoreCase) ? UnchosenXcode() : null);
            }
            else if (await SimulatorsLackingAsync(project, [.. mobile.Where(f => Asked(f, MobilePlatforms.HasSimulator))], forApple, said, globals, cancel) is { Count: > 0 } lacking)
            {
                apple = new AppleAnswer(null) { Simulators = lacking };
            }
        }
        return new ProjectNeeds(name, frameworks) { Workloads = workloads, Android = android, Apple = apple };
    }

    /// <summary>Longest a listing of the simulator runtimes is waited for, which may start the simulators' service first.</summary>
    private static readonly TimeSpan SimulatorsListed = TimeSpan.FromSeconds(60);

    /// <summary>An Apple workload's check of the Xcode it builds a framework's apps with, saying the developer folder and
    /// SDK version it found.</summary>
    private static Task<ChildResult> AppleSdkAsync(string project, string framework, IReadOnlyDictionary<string, string> globals, CancellationToken cancel) =>
        MSBuildAsync(project, framework, ["-target:_DetectSdkLocations", "-getProperty:_SdkDevPath", "-getProperty:_SdkVersion"], globals, cancel);

    /// <summary>The simulator runtimes the frameworks' apps need (one per platform, of its SDK's version) that the Xcode the
    /// workload found lists none of, asked of that Xcode's own simctl; none where it can't be asked.</summary>
    private static async Task<IReadOnlyList<string>> SimulatorsLackingAsync(string project, IReadOnlyList<string> frameworks, string asked, ChildResult said,
        IReadOnlyDictionary<string, string> globals, CancellationToken cancel)
    {
        if (frameworks.Count == 0 || MobileAnswers.AppleSdk(said.Output).Developer is not { } developer)
        {
            return [];
        }
        string simctl = Path.Combine(developer, "usr", "bin", "simctl");
        if (!File.Exists(simctl))
        {
            return [];
        }
        List<(string Platform, string? SdkVersion)> wanted = [];
        foreach (string framework in frameworks.DistinctBy(f => MobilePlatforms.Of(f)))
        {
            ChildResult sdk = string.Equals(framework, asked, StringComparison.OrdinalIgnoreCase) ? said : await AppleSdkAsync(project, framework, globals, cancel);
            wanted.Add((MobilePlatforms.Named(MobilePlatforms.Of(framework)!), MobileAnswers.AppleSdk(sdk.Output).SdkVersion));
        }
        try
        {
            ChildResult listed = await ChildProcesses.RunAsync(ChildProcesses.Command(simctl, ["list", "runtimes", "-j"], Path.GetDirectoryName(project)!),
                limit: SimulatorsListed, cancel: cancel);
            return listed.Succeeded ? MobileAnswers.SimulatorsLacking(listed.Output, wanted) : [];
        }
        catch (Exception error) when (error is TimeoutException or System.ComponentModel.Win32Exception)
        {
            // A listing that never came says nothing either way.
            return [];
        }
    }

    /// <summary>One MSBuild question of the project, for a framework or the project as a whole, never restoring: what it
    /// said, read whole.</summary>
    private static Task<ChildResult> MSBuildAsync(string project, string? framework, IReadOnlyList<string> asked, IReadOnlyDictionary<string, string> globals,
        CancellationToken cancel)
    {
        List<string> arguments = ["msbuild", project, "-nologo", "-property:SkipResolvePackageAssets=true", .. asked,
            .. globals.Where(g => g.Key is not MSBuildNames.TargetFramework).Select(g => $"-property:{g.Key}={g.Value}")];
        if (framework is not null)
        {
            arguments.Add($"-property:{MSBuildNames.TargetFramework}={framework}");
        }
        return ChildProcesses.RunAsync(ChildProcesses.DotnetQuestion(arguments, Path.GetDirectoryName(project)!), cancel: cancel);
    }

    /// <summary>An Xcode in Applications, where the system chose none (only the command-line tools, or nothing).</summary>
    private static string? UnchosenXcode() =>
        Directory.Exists(Applications)
            ? Directory.EnumerateDirectories(Applications, "Xcode*.app").Order(StringComparer.Ordinal).FirstOrDefault(x => Directory.Exists(Path.Combine(x, "Contents", "Developer")))
            : null;
}
