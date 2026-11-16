using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Mobile;

/// <summary>What a mobile project needs, asked of the .NET Passenger builds with and of the project's own workloads,
/// through MSBuild's questions as the .NET CLI's (no build, no restore): the workloads it lacks
/// (<c>_GetRequiredWorkloads</c>), what its Android app needs (<c>GetAndroidDependencies</c>, Java's too) and whether its
/// Apple apps have the Xcode their workload wants (<c>_DetectSdkLocations</c>) and its simulator runtimes (that Xcode's
/// <c>simctl</c>), and the edits its heads need (<see cref="MobileHeads"/>). Only a framework on CoreCLR whose workload is
/// installed is asked past the first, an Android one only where this machine builds Android apps, and each head at the
/// framework it builds on CoreCLR.</summary>
public static class MobileQuestions
{
    /// <summary>Where Xcode is installed when it is, on a Mac.</summary>
    private const string Applications = "/Applications";

    /// <summary>The project's needs, its MSBuild run with the solution's global properties.</summary>
    public static async Task<ProjectNeeds> AskAsync(string project, string name, IReadOnlyList<string> frameworks, IReadOnlyDictionary<string, string> globals,
        bool onMac, bool buildsAndroid, CancellationToken cancel)
    {
        IReadOnlyList<string> mobile = MobilePlatforms.FrameworksOf(frameworks);
        MSBuildAnswer required = await QuestionAsync(project, null, new([MSBuildNames.GetRequiredWorkloads]) { TargetResults = [MSBuildNames.GetRequiredWorkloads] }, globals,
            cancel);
        IReadOnlyList<WorkloadLacked> workloads = MobileAnswers.Workloads(required);
        bool Asked(string framework, Func<string, bool> platform) => platform(MobilePlatforms.Of(framework)!)
            && FrameworkNames.Major(framework) is null or >= MobilePlatforms.FirstOnCoreClr
            && !workloads.Any(w => w.Framework is null || string.Equals(w.Framework, framework, StringComparison.OrdinalIgnoreCase));
        AndroidAnswer? android = null;
        if (buildsAndroid && mobile.FirstOrDefault(f => Asked(f, p => p == FrameworkNames.Android)) is { } forAndroid)
        {
            android = MobileAnswers.Android(await QuestionAsync(project, forAndroid, new([MSBuildNames.GetAndroidDependencies])
            {
                Items = [MSBuildNames.AndroidDependency, MSBuildNames.JavaDependency],
                Properties = [MSBuildNames.AndroidSdkDirectory, MSBuildNames.JavaSdkDirectory, MSBuildNames.JavaSdkVersion],
                Given = new Dictionary<string, string> { [MSBuildNames.AndroidInstallJavaDependencies] = "true" },
            }, globals, cancel));
        }
        AppleAnswer? apple = null;
        if (onMac && mobile.FirstOrDefault(f => Asked(f, MobilePlatforms.IsApple)) is { } forApple)
        {
            MSBuildAnswer said = await AppleSdkAsync(project, forApple, globals, cancel);
            if (MobileAnswers.Apple(said) is { } problem)
            {
                apple = new AppleAnswer(problem, MobileAnswers.FoundNoXcode(problem) ? UnchosenXcode() : null);
            }
            else if (await SimulatorsLackingAsync(project, [.. mobile.Where(f => Asked(f, MobilePlatforms.HasSimulator))], forApple, said, globals, cancel) is { Count: > 0 } lacking)
            {
                apple = new AppleAnswer(null) { Simulators = lacking };
            }
        }
        HeadEdits? heads = null;
        if (workloads.Count == 0 && MobileHeads.HeadsOf(frameworks, onMac) is { Count: > 0 } asked)
        {
            List<HeadAnswer> answers = [];
            foreach (string head in asked)
            {
                string platform = MobilePlatforms.Of(head)!;
                string built = MobileHeads.OnCoreClr(head);
                if (MobileHeads.Answer(platform, built, await QuestionAsync(project, built, MobileHeads.Question(platform), globals, cancel), Path.GetDirectoryName(project)!)
                    is { } answer)
                {
                    answers.Add(answer);
                }
            }
            heads = MobileHeads.Edits(project, name, answers, path => File.Exists(path) ? FileText.Load(path)?.Text : null);
        }
        return new ProjectNeeds(name, frameworks) { Workloads = workloads, Android = android, Apple = apple, Heads = heads };
    }

    /// <summary>Longest a listing of the simulator runtimes is waited for, which may start the simulators' service first.</summary>
    private static readonly TimeSpan SimulatorsListed = TimeSpan.FromSeconds(60);

    /// <summary>An Apple workload's check of the Xcode it builds a framework's apps with, saying the developer folder and
    /// SDK version it found.</summary>
    private static Task<MSBuildAnswer> AppleSdkAsync(string project, string framework, IReadOnlyDictionary<string, string> globals, CancellationToken cancel) =>
        QuestionAsync(project, framework, new([MSBuildNames.DetectSdkLocations]) { Properties = [MSBuildNames.SdkDevPath, MSBuildNames.SdkVersion] }, globals, cancel);

    /// <summary>The simulator runtimes the frameworks' apps need (one per platform, of its SDK's version) that the Xcode the
    /// workload found lists none of, asked of that Xcode's own simctl; none where it can't be asked.</summary>
    private static async Task<IReadOnlyList<string>> SimulatorsLackingAsync(string project, IReadOnlyList<string> frameworks, string asked, MSBuildAnswer said,
        IReadOnlyDictionary<string, string> globals, CancellationToken cancel)
    {
        if (frameworks.Count == 0 || MobileAnswers.AppleSdk(said).Developer is not { } developer)
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
            MSBuildAnswer sdk = string.Equals(framework, asked, StringComparison.OrdinalIgnoreCase) ? said : await AppleSdkAsync(project, framework, globals, cancel);
            wanted.Add((MobilePlatforms.Named(MobilePlatforms.Of(framework)!), MobileAnswers.AppleSdk(sdk).SdkVersion));
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

    /// <summary>One MSBuild question of the project, for a framework or the project as a whole, never restoring.</summary>
    private static Task<MSBuildAnswer> QuestionAsync(string project, string? framework, MSBuildQuestion question, IReadOnlyDictionary<string, string> globals,
        CancellationToken cancel) =>
        MSBuildQuestions.AskAsync(project, framework, globals,
            question with { Given = new Dictionary<string, string>(question.Given) { [MSBuildNames.SkipResolvePackageAssets] = "true" } }, cancel);

    /// <summary>An Xcode in Applications, where the system chose none (only the command-line tools, or nothing).</summary>
    private static string? UnchosenXcode() =>
        Directory.Exists(Applications)
            ? Directory.EnumerateDirectories(Applications, "Xcode*.app").Order(StringComparer.Ordinal).FirstOrDefault(x => Directory.Exists(Path.Combine(x, "Contents", "Developer")))
            : null;
}
