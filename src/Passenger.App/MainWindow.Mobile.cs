using System.Diagnostics;
using Avalonia.Controls;
using Passenger.App.CSharp;
using Passenger.App.Mobile;
using Passenger.App.Templates;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>What the folder's mobile projects (MAUI, .NET for Android, iOS, Mac Catalyst) need that this machine lacks,
/// asked of their workloads off the window's thread as their projects evaluate (<see cref="MobileQuestions"/>) and said
/// once in the banner over the window (<see cref="MobileWant"/>), in place of the load errors it explains in the status
/// bar. Install Workloads installs the missing workloads into the .NET Passenger builds with; Install Android SDK and
/// Java runs the Android workload's own installer once the person accepted the SDK's licences; Get Xcode names where Xcode
/// comes from. Each install has the projects loaded again.</summary>
public partial class MainWindow
{
    /// <summary>What the folder's mobile projects need, as last asked.</summary>
    public MobileWant MobileWant { get; private set; } = MobileWant.Nothing;

    /// <summary>What the banner over the window says of the mobile projects' needs, null while it says nothing.</summary>
    public string? MobileSaid => MobileBanner.Said;

    /// <summary>The projects and frameworks last asked of, so an evaluation that changed none asks nothing again.</summary>
    private string? mobileAsked;

    /// <summary>The newest ask, whose answer stands.</summary>
    private int mobileAsks;

    /// <summary>The ask under way, for a test to wait on.</summary>
    private Task mobileAsking = Task.CompletedTask;

    /// <summary>The words the person hid, until the want changes.</summary>
    private string? mobileHidden;

    /// <summary>A load problem the want may explain, held from the status bar until the ask under way lands.</summary>
    private ServerSaid? mobileHeld;

    /// <summary>Lines the last install wrote, for a test.</summary>
    public IReadOnlyList<string> MobileInstallSaid { get; private set; } = [];

    private Button? workloadsLink;
    private Button? androidLink;
    private Button? xcodeLink;

    /// <summary>The workload install a test runs in place of dotnet's, given the ids.</summary>
    public static Func<IReadOnlyList<string>, ProcessStartInfo>? WorkloadInstallStandIn { get; set; }

    /// <summary>Where Xcode comes from.</summary>
    private static readonly Uri XcodePage = new("https://developer.apple.com/xcode/");

    private void AddMobile()
    {
        actions!.Add(new IdeAction(AppActions.InstallWorkloads, "Install Workloads…", "IDE", context => IdeErrors.Observe(InstallWorkloadsAsync()))
        {
            WhyNot = context => context.ByAgent ? Refusals.AgentInstallsMobile
                : MobileWant.Workloads.Count == 0 ? Refusals.NoWorkloadLacking
                : null,
            Menus = [new MenuPlace("Tools", 1, 2)],
            Words = ["dotnet workload", "MAUI", "Android", "iOS"],
            Works = AppActions.InstallWorkloads,
        });
        actions.Add(new IdeAction(AppActions.InstallAndroidDependencies, "Install Android SDK and Java…", "IDE", _ => IdeErrors.Observe(InstallAndroidAsync()))
        {
            WhyNot = context => context.ByAgent ? Refusals.AgentInstallsMobile
                : !MobileWant.AndroidLacks ? Refusals.NothingAndroidLacking
                : null,
            Menus = [new MenuPlace("Tools", 1, 3)],
            Words = ["Android SDK", "JDK", "Java", "sdkmanager", "InstallAndroidDependencies"],
            Works = AppActions.InstallAndroidDependencies,
        });
        workloadsLink = MobileBanner.Link(actions[AppActions.InstallWorkloads]!.Title, () => Request(AppActions.InstallWorkloads));
        androidLink = MobileBanner.Link(actions[AppActions.InstallAndroidDependencies]!.Title, () => Request(AppActions.InstallAndroidDependencies));
        xcodeLink = MobileBanner.Link("Get Xcode", () => Browse(XcodePage));
        MobileBanner.Link("Hide", () =>
        {
            mobileHidden = MobileWant.Said;
            ShowMobile();
        });
        // What the person installed outside Passenger is seen as they come back to it.
        Activated += (_, _) =>
        {
            if (MobileWant.Says)
            {
                AskMobile(again: true);
            }
        };
    }

    /// <summary>The session's mobile projects asked of as they evaluate.</summary>
    private void BindMobile(ProjectSession next)
    {
        mobileAsked = null;
        mobileHeld = null;
        MobileWant = MobileWant.Nothing;
        ShowMobile();
        next.ProjectsEvaluated += (_, _) =>
        {
            if (session == next)
            {
                AskMobile(again: false);
            }
        };
        AskMobile(again: false);
    }

    /// <summary>Asks what the mobile projects need, off the window's thread, unless the projects and frameworks are those
    /// asked of last and <paramref name="again"/> is false.</summary>
    private void AskMobile(bool again)
    {
        UiThread.Check();
        ProjectSession asking = session;
        List<ProjectModel> mobile = [.. asking.Projects.Where(p => p.IsEvaluated && MobilePlatforms.FrameworksOf(p.TargetFrameworks).Count > 0)];
        string key = string.Join('\n', mobile.Select(p => $"{p.Path}|{string.Join(';', p.TargetFrameworks)}"));
        if (!again && key == mobileAsked)
        {
            return;
        }
        mobileAsked = key;
        int mine = ++mobileAsks;
        if (mobile.Count == 0)
        {
            MobileWant = MobileWant.Nothing;
            ShowMobile();
            return;
        }
        List<(string Path, string Name, IReadOnlyList<string> Frameworks, Dictionary<string, string> Globals)> asked =
            [.. mobile.Select(p => (p.Path, p.Name, p.TargetFrameworks, ProjectModels.Properties(asking.Workspace, p)))];
        mobileAsking = AskingAsync();

        async Task AskingAsync()
        {
            bool onMac = OperatingSystem.IsMacOS();
            string usual = AndroidFolders.Usual(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            MobileWant want = await BackgroundWork.Run(async () =>
            {
                List<ProjectNeeds> needs = [];
                foreach ((string path, string name, IReadOnlyList<string> frameworks, Dictionary<string, string> globals) in asked)
                {
                    try
                    {
                        needs.Add(await MobileQuestions.AskAsync(path, name, frameworks, globals, onMac, CancellationToken.None));
                    }
                    catch (Exception error) when (error is IOException or FileNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // No .NET to ask (the banner over the window says so), or a project gone under it: nothing to say of it.
                        IdeErrors.Write($"What {name} needs could not be asked: {error.Message}");
                    }
                }
                return MobileWant.Of(needs, onMac, Environment.GetEnvironmentVariable, usual, at => File.Exists(at) || Directory.Exists(at));
            });
            if (mine != mobileAsks || session != asking || asking.IsEnded)
            {
                return;
            }
            MobileWant = want;
            ShowMobile();
        }
    }

    /// <summary>Waits until what the mobile projects need has been asked, as it stands now: a test's.</summary>
    public Task MobileAsked() => mobileAsking;

    /// <summary>The banner says what the projects lack, with the links that get it, unless the person hid those words;
    /// a load problem held from the status bar is said in its words, or in its own where it is not one of them.</summary>
    private void ShowMobile()
    {
        UiThread.Check();
        string? said = MobileWant.Said;
        MobileBanner.Say(said is not null && said != mobileHidden ? said : null);
        workloadsLink!.IsVisible = MobileWant.Workloads.Count > 0;
        androidLink!.IsVisible = MobileWant.AndroidLacks;
        xcodeLink!.IsVisible = MobileWant.Xcode is not null;
        if (mobileHeld is { } held)
        {
            mobileHeld = null;
            if (!SayMobile(held))
            {
                status.Say(held.Text, held.Severity, held.Problem is { } problem ? LoadProblemPlace(problem) : null);
            }
        }
    }

    /// <summary>A load problem of a mobile project said in the banner's words where they explain it, or held while what the
    /// projects need is still being asked; false where it is said as it is.</summary>
    private bool SayMobile(ServerSaid said)
    {
        if (said.Problem is not { } problem || !MobileWant.Explains(problem.Message)
            || session.Projects.FirstOrDefault(p => PathNames.Same(p.Path, problem.Project)) is not { } project
            || MobilePlatforms.FrameworksOf(project.TargetFrameworks).Count == 0)
        {
            return false;
        }
        if (!mobileAsking.IsCompleted)
        {
            mobileHeld = said;
            return true;
        }
        if (!MobileWant.Projects.Contains(project.Name, StringComparer.Ordinal) || MobileWant.Said is not { } words)
        {
            return false;
        }
        status.Say(words, said.Severity, null);
        return true;
    }

    /// <summary>Install Workloads: the person asked, then dotnet workload install of the workloads lacking into the .NET
    /// Passenger builds with, its lines said as its stages, and the projects loaded again with them.</summary>
    private async Task InstallWorkloadsAsync()
    {
        UiThread.Check();
        IReadOnlyList<string> ids = MobileWant.Workloads;
        if (ids.Count == 0 || work.Begin(AppActions.InstallWorkloads, "Asking to install the .NET workloads") is not { } installing)
        {
            return;
        }
        using (installing)
        {
            string root = Dotnet.Root(Folder);
            string named = $"{Plural.Noun(ids.Count, "workload")} {Plural.Listed([.. ids])}";
            string command = $"dotnet workload install {string.Join(' ', ids)}";
            if (WorkloadInstallStandIn is null && !await BackgroundWork.Run(() => Writable(root)))
            {
                Told(installing, false, Refusals.WorkloadsNeedAdministrator(root, command));
                return;
            }
            if (!await Confirm($"Install the .NET {named}?",
                $"Passenger runs {command} with the .NET in {PathNames.Shown(root, null)}, which downloads them from NuGet. "
                + $"{Plural.Listed([.. MobileWant.Projects])} then {Plural.Agreeing(MobileWant.Projects.Count, "loads", "load")} again.", "Install"))
            {
                Told(installing, false, $"The .NET {named} {Plural.Agreeing(ids.Count, "was", "were")} not installed");
                return;
            }
            installing.Say($"Installing the .NET {named}");
            ProcessStartInfo start = WorkloadInstallStandIn?.Invoke(ids) ?? ChildProcesses.DotnetCommand(["workload", "install", .. ids], Folder);
            (int code, IReadOnlyList<string> lines) = await RunInstallAsync(start, installing);
            if (installing.IsStopped)
            {
                Told(installing, false, $"The install of the .NET {named} was stopped");
                return;
            }
            if (code != 0)
            {
                Told(installing, false, $"The .NET {named} could not be installed ({ExitCodes.Said(code)}): {LastWords(lines)}");
                return;
            }
            installing.Say("Loading the projects again");
            await LoadAgainAsync();
            Told(installing, true, MobileWant.Workloads.Intersect(ids).Any()
                ? $"dotnet workload install ended, but the projects still lack {Plural.Listed([.. MobileWant.Workloads.Intersect(ids)])}"
                : $"The .NET {named} {Plural.Agreeing(ids.Count, "is", "are")} installed: the projects loaded again");
        }
    }

    /// <summary>Install Android SDK and Java: Google's licences of what is to be installed shown and accepted by the
    /// person, then the Android workload's InstallAndroidDependencies on a project lacking them, into the SDK folder
    /// found or the usual one, and the projects loaded again.</summary>
    private async Task InstallAndroidAsync()
    {
        UiThread.Check();
        MobileWant want = MobileWant;
        if (!want.AndroidLacks || want.AndroidSdk is not { } sdk
            || session.Projects.FirstOrDefault(p => want.Projects.Contains(p.Name, StringComparer.Ordinal)
                && p.TargetFrameworks.Any(f => MobilePlatforms.Of(f) == MobilePlatforms.Android)) is not { } project
            || work.Begin(AppActions.InstallAndroidDependencies, "Reading the Android SDK's licences") is not { } installing)
        {
            return;
        }
        using (installing)
        {
            string framework = project.TargetFrameworks.First(f => MobilePlatforms.Of(f) == MobilePlatforms.Android);
            IReadOnlyList<string> packages = want.AndroidPackages;
            IReadOnlyList<AndroidLicence> licences;
            try
            {
                licences = await BackgroundWork.Run(() => AndroidLicences.ReadAsync(packages, installing.Stopping));
            }
            catch (Exception error) when (error is HttpRequestException or IOException or System.Xml.XmlException or TaskCanceledException)
            {
                Told(installing, false, $"Nothing was installed: Google's licences of the Android SDK could not be read ({error.Message})");
                return;
            }
            string what = Plural.Listed([.. (want.NoAndroidSdk ? ["the Android SDK"] : Array.Empty<string>()), .. packages,
                .. (want.Java is { } java ? [$"Microsoft's build of OpenJDK {java} (GPL-2.0 with the Classpath Exception)"] : Array.Empty<string>())]);
            // The installer's own JDK folder is JAVA_HOME's, which it would install over, and on Linux, with none set, none at all.
            string? jdk = want.Java is { } version
                ? AndroidFolders.Jdk(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), version)
                : null;
            string text = $"Passenger runs the .NET android workload's installer (InstallAndroidDependencies) on {project.Name}, which downloads {what} "
                + $"and installs the Android SDK's part into {PathNames.Shown(sdk, null)}{(jdk is null ? string.Empty : $" and the JDK into {PathNames.Shown(jdk, null)}")}, "
                + "for you alone. Installing them means you accept "
                + $"{Plural.Listed([.. licences.Select(l => l.Id)])}, Google's {Plural.Noun(licences.Count, "licence")} of the Android SDK, below.";
            installing.Say("Asking to accept the Android SDK's licences");
            if (!await AcceptLicencesAsync("Accept the Android SDK's Licences?", text, licences))
            {
                Told(installing, false, "The Android SDK's licences were not accepted, so nothing was installed");
                return;
            }
            installing.Say($"Installing {what}");
            List<string> arguments = ["msbuild", project.Path, "-nologo", "-target:InstallAndroidDependencies", $"-property:{MSBuildNames.TargetFramework}={framework}",
                $"-property:AndroidSdkDirectory={sdk}", "-property:AcceptAndroidSDKLicenses=True",
                .. (want.Java is null ? (string[])["-property:AndroidInstallJavaDependencies=false"] : jdk is null ? [] : [$"-property:JavaSdkDirectory={jdk}"]),
                .. ProjectModels.Properties(session.Workspace, project).Where(g => g.Key is not MSBuildNames.TargetFramework).Select(g => $"-property:{g.Key}={g.Value}")];
            (int code, IReadOnlyList<string> lines) = await RunInstallAsync(ChildProcesses.DotnetCommand(arguments, project.Folder), installing);
            if (installing.IsStopped)
            {
                Told(installing, false, "The Android SDK's install was stopped");
                return;
            }
            if (code != 0)
            {
                Told(installing, false, $"The Android SDK could not be installed ({ExitCodes.Said(code)}): {LastWords(lines)}");
                return;
            }
            installing.Say("Loading the projects again");
            await LoadAgainAsync();
            Told(installing, !MobileWant.AndroidLacks, MobileWant.AndroidLacks
                ? $"The Android workload's installer ended, but {project.Name} still needs what the banner says"
                : $"{what} {Plural.Agreeing(packages.Count + (want.Java is null ? 0 : 1) + (want.NoAndroidSdk ? 1 : 0), "is", "are")} installed: the projects loaded again");
        }
    }

    /// <summary>The licences shown whole under the question, accepted only by the person's own answer.</summary>
    private Task<bool> AcceptLicencesAsync(string title, string text, IReadOnlyList<AndroidLicence> licences)
    {
        string whole = string.Join("\n\n", licences.Select(l => $"{l.Id}\n\n{l.Text}"));
        if (confirm is { } answer)
        {
            return answer(title, $"{text}\n\n{whole}", "Accept and Install");
        }
        TextBox shown = new()
        {
            Name = "AndroidLicences",
            Text = whole,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Height = 260,
        };
        return new ConfirmDialog(title, text, "Accept and Install", "Decline", shown) { EnterAgrees = false }.ShowDialog<bool>(this);
    }

    /// <summary>An install's program run with its lines said as the work's stages, stopped with it.</summary>
    private static async Task<(int Code, IReadOnlyList<string> Lines)> RunInstallAsync(ProcessStartInfo start, Working installing)
    {
        List<string> lines = [];
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        int code = await BackgroundWork.Run(async () =>
        {
            using Process process = await ChildProcesses.StartAsync(start);
            void Heard(string line)
            {
                if (line.Trim() is not { Length: > 0 } said)
                {
                    return;
                }
                lock (lines)
                {
                    lines.Add(said);
                }
                UiThread.Post(() => installing.Say(said));
            }
            Task hearing = ChildProcesses.Hear(process, Heard, Heard);
            using (installing.Stopping.Register(() => ChildProcesses.Kill(process)))
            {
                await process.WaitForExitAsync(CancellationToken.None);
                await hearing;
            }
            return process.ExitCode;
        });
        lock (lines)
        {
            return (code, [.. lines]);
        }
    }

    /// <summary>Has the projects loaded again after an install changed what the SDK resolves: the project hosts on this .NET
    /// ended, every project evaluated anew, the language server started again, the templates read again and the mobile
    /// projects asked of again.</summary>
    private async Task LoadAgainAsync()
    {
        string root = Dotnet.Root(Folder);
        await BackgroundWork.Run(() => Passenger.Workspace.ProjectHosts.EndUnderAsync(root));
        session.Models.EvaluateAll();
        session.Evaluate();
        session.CSharp?.Restart();
        ProjectTemplates.Forget();
        AskMobile(again: true);
        await mobileAsking;
    }

    /// <summary>Whether this user may write the .NET installation (a workload installs into it): a folder of their own,
    /// not one an administrator installed.</summary>
    private static bool Writable(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return programs.Length == 0 || !PathNames.IsUnder(root, programs);
        }
        return Libc.Access(root, Libc.MayWrite) == 0;
    }

    /// <summary>What an install's last words said went wrong: its first error, else its last line.</summary>
    private static string LastWords(IReadOnlyList<string> lines) =>
        MobileAnswers.FirstError(string.Join('\n', lines)) ?? (lines.Count > 0 ? lines[^1] : "it said nothing");
}
