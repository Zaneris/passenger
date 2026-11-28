using System.Diagnostics;
using Avalonia.Controls;
using Passenger.App.CSharp;
using Passenger.App.Mobile;
using Passenger.App.Run.Devices;
using Passenger.App.Templates;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>What the folder's mobile projects (MAUI, .NET for Android, iOS, Mac Catalyst) need that this machine lacks,
/// asked of their workloads off the window's thread as their projects evaluate (<see cref="MobileQuestions"/>) and said
/// once in the banner over the window (<see cref="MobileWant"/>), in place of the load errors it explains in the status
/// bar. Install Workloads installs the missing workloads where the .NET Passenger builds with keeps them (<see
/// cref="WorkloadFolders"/>), the one install of them New Project's Install runs too; Install Android SDK and Java runs the Android workload's own installer once the person
/// accepted the SDK's licences; Get Xcode names where Xcode comes from. Each install has the projects loaded again. Make the
/// Mobile Edits, or the fix on a line they change, makes the edits a project's heads need to build here in its project and
/// Info.plist, one command of Undo.</summary>
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

    private Button? workloadsLink;
    private Button? androidLink;
    private Button? xcodeLink;
    private Button? headsLink;

    /// <summary>The project whose run said its Mac Catalyst Debug build trims .NET's own assemblies, which Keep .NET's
    /// Assemblies Whole in Debug edits first.</summary>
    private string? wholeAsked;

    /// <summary>What a test's workload installs take in place of the .NET they install into, and of its dotnet.</summary>
    public static WorkloadStandIn? WorkloadStandIn { get; set; }

    /// <summary>Where Xcode comes from.</summary>
    private static readonly Uri XcodePage = new("https://developer.apple.com/xcode/");

    private void AddMobile()
    {
        actions!.Add(new IdeAction(AppActions.InstallWorkloads, ActionTitles.InstallWorkloads, ActionGroups.Ide, context => IdeErrors.Observe(InstallWorkloadsAsync()))
        {
            WhyNot = context => AskedByAgent(context) ? Refusals.AgentInstallsMobile
                : MobileWant.Workloads.Count == 0 ? Refusals.NoWorkloadLacking
                : null,
            Menus = [new MenuPlace("Tools", 1, 2)],
            Words = ["dotnet workload", "MAUI", "Android", "iOS"],
            Works = AppActions.InstallWorkloads,
        });
        actions.Add(new IdeAction(AppActions.InstallAndroidDependencies, ActionTitles.InstallAndroidDependencies, ActionGroups.Ide, _ => IdeErrors.Observe(InstallAndroidAsync()))
        {
            WhyNot = context => AskedByAgent(context) ? Refusals.AgentInstallsMobile
                : Untrusted() ?? (!MobileWant.AndroidLacks ? Refusals.NothingAndroidLacking : null),
            Menus = [new MenuPlace("Tools", 1, 3)],
            Words = ["Android SDK", "JDK", "Java", "sdkmanager", MSBuildNames.InstallAndroidDependencies],
            Works = AppActions.InstallAndroidDependencies,
        });
        actions.Add(new IdeAction(AppActions.MakeMobileEdits, ActionTitles.MakeMobileEdits, ActionGroups.Ide, _ => IdeErrors.Observe(MakeMobileEditsAsync(null)))
        {
            WhyNot = _ => MobileWant.Heads.Any(h => h.Makes) ? null : Refusals.NoMobileEdits,
            Words = ["iOS", "Android", "Mac Catalyst", "Info.plist", MSBuildNames.SupportedOSPlatformVersion, AppManifests.MinimumOSVersion, AppManifests.SceneManifest],
            Works = AppActions.MakeMobileEdits,
        });
        actions.Add(new IdeAction(AppActions.KeepAssembliesWhole, ActionTitles.KeepAssembliesWhole, ActionGroups.Ide,
            _ => IdeErrors.Observe(KeepWholeAsync(wholeAsked ?? MobileWant.Trimmed.FirstOrDefault()?.Project)))
        {
            WhyNot = _ => wholeAsked is not null || MobileWant.Trimmed.Count > 0 ? null : Refusals.NothingTrimmed,
            Words = ["Mac Catalyst", MSBuildNames.MtouchLink, "trimming", "Live Visual Tree", "Hot Reload"],
            Works = AppActions.KeepAssembliesWhole,
        });
        headsLink = MobileBanner.Link(actions[AppActions.MakeMobileEdits]!.Title, () => Request(AppActions.MakeMobileEdits));
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
            if (MobileWant.MayChange)
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
        next.TrustChanged += (_, _) =>
        {
            if (session == next)
            {
                AskMobile(again: true);
            }
        };
        AskMobile(again: false);
    }

    /// <summary>Asks what the mobile projects need, off the window's thread, unless the projects and frameworks are those
    /// asked of last and <paramref name="again"/> is false. A folder not trusted is asked nothing, its projects as the
    /// folder's cache kept them or not: MSBuild would run their targets.</summary>
    private void AskMobile(bool again)
    {
        UiThread.Check();
        ProjectSession asking = session;
        List<ProjectModel> mobile = [.. asking.Projects.Where(p => asking.Trusted && p.IsEvaluated && MobilePlatforms.FrameworksOf(p.TargetFrameworks).Count > 0)];
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
            string? folder = Folder;
            MobileWant want = await BackgroundWork.Run(async () =>
            {
                string? androidHost = await AndroidHosts.UnbuiltAsync(folder);
                List<ProjectNeeds> needs = [];
                foreach ((string path, string name, IReadOnlyList<string> frameworks, Dictionary<string, string> globals) in asked)
                {
                    try
                    {
                        needs.Add(await MobileQuestions.AskAsync(path, name, frameworks, globals, onMac, androidHost is null, CancellationToken.None));
                    }
                    catch (Exception error) when (error is IOException or FileNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // No .NET to ask (the banner over the window says so), or a project gone under it: nothing to say of it.
                        IdeErrors.Write($"What {name} needs could not be asked: {error.Message}");
                    }
                }
                return MobileWant.Of(needs, onMac, Environment.GetEnvironmentVariable, usual, at => File.Exists(at) || Directory.Exists(at), androidHost);
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
        xcodeLink!.IsVisible = MobileWant is { Xcode: not null, UnchosenXcode: null };
        headsLink!.IsVisible = MobileWant.Heads.Any(h => h.Makes);
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

    /// <summary>Make the Mobile Edits, of every project's heads or of <paramref name="only"/> one's: each project, Info.plist
    /// and SceneDelegate written as its edits say, read again as they stand once the gate is passed, as one command of Undo;
    /// then the projects are evaluated and what they need asked again. What it came to, as the status bar says it.</summary>
    private async Task<(bool Made, string Said)> MakeMobileEditsAsync(string? only)
    {
        UiThread.Check();
        List<HeadEdits> heads = [.. MobileWant.Heads.Where(h => h.Makes && (only is null || PathNames.Same(h.Project, only)))];
        if (heads.Count == 0)
        {
            return (false, Refusals.NoMobileEdits.Shown);
        }
        if (work.Begin(AppActions.MakeMobileEdits, "Making the mobile edits") is not { } making)
        {
            return (false, ActionWork.Busy(work.Of(AppActions.MakeMobileEdits)!).Shown);
        }
        using (making)
        {
            List<string> files = [.. heads.SelectMany(h => h.Files).Distinct(PathNames.Comparer)];
            string named = Plural.Listed([.. heads.Select(h => h.Name)]);
            // A SceneDelegate the person made since the edits were read is theirs: it stays as it is.
            List<string> kept = [];
            string? refused = await ChangeFileAsync($"Make the Mobile Edits of {named}", files, () =>
            {
                foreach (HeadEdits head in heads)
                {
                    foreach (ManifestEdit made in head.Manifests.Where(m => m is { SceneSdk: not null, Scenes: SceneDelegation.Maui, DelegateFile: not null }))
                    {
                        if (FileText.SaveNewOnly(made.DelegateFile!, MobileHeads.DelegateText(made.Namespace)) is null)
                        {
                            kept.Add(PathNames.Shown(made.DelegateFile!, Folder));
                        }
                    }
                    foreach (string file in head.Files.Where(f => !head.Manifests.Any(m => m.DelegateFile is { } made && PathNames.Same(made, f))))
                    {
                        if (FileText.Load(file) is not { } now)
                        {
                            return $"{PathNames.Shown(file, Folder)} could not be read";
                        }
                        string text = PathNames.Same(file, head.Project) ? MobileHeads.ProjectText(now.Text, head).Text
                            : head.Manifests.Where(m => PathNames.Same(m.Path, file)).Aggregate(now.Text, (edited, manifest) => MobileHeads.ManifestText(edited, manifest));
                        if (text != now.Text)
                        {
                            _ = FileText.SaveOver(file, now, text);
                        }
                    }
                }
                return null;
            });
            if (refused is not null)
            {
                return Came(false, $"The mobile edits of {named} were not made: {refused}");
            }
            making.Say("Asking what the projects need again");
            // The projects the edits wrote are evaluated again first, so the ask reads their frameworks as written.
            await session.Models.Idle;
            AskMobile(again: true);
            await mobileAsking;
            IReadOnlyList<string> left = [.. MobileWant.Heads.Where(h => h.Makes && heads.Any(made => made.Name == h.Name)).Select(h => h.Name)];
            string madeSince = kept.Count == 0 ? string.Empty
                : $"; {Plural.Listed([.. kept])}, made since the edits were read, {Plural.Agreeing(kept.Count, "is", "are")} kept as {Plural.Agreeing(kept.Count, "it was", "they were")}";
            return Came(left.Count == 0, left.Count == 0 ? $"The mobile edits of {named} are made{madeSince}"
                : $"The mobile edits were made, but {Plural.Listed([.. left])} still {Plural.Agreeing(left.Count, "needs", "need")} what the banner says{madeSince}");

            (bool, string) Came(bool made, string said)
            {
                Told(making, made, said);
                return (made, said);
            }
        }
    }

    /// <summary>A run of a project whose Mac Catalyst build trims .NET's own assemblies: the Run window offers, over what its
    /// start said, to keep them whole in its Debug build.</summary>
    private void OfferWhole(string project, string name)
    {
        UiThread.Check();
        wholeAsked = project;
        BottomPanel.Run.ShowOffer($"{name}'s Debug build trims .NET's own assemblies, so {MacCatalystApp.KeptOut}", actions![AppActions.KeepAssembliesWhole]!.Title.Shown,
            () => Request(AppActions.KeepAssembliesWhole));
    }

    /// <summary>Keep .NET's Assemblies Whole in Debug: MtouchLink None written for the project's Mac Catalyst head's Debug
    /// build alone, one command of Undo through the window's gate, saying its obj and bin are to be emptied before its next
    /// build; what it came to, as the status bar says it.</summary>
    private async Task<(bool Made, string Said)> KeepWholeAsync(string? project)
    {
        UiThread.Check();
        if (project is null || session.Projects.FirstOrDefault(p => PathNames.Same(p.Path, project)) is not { } model)
        {
            return (false, Refusals.NothingTrimmed.Shown);
        }
        if (work.Begin(AppActions.KeepAssembliesWhole, $"Keeping .NET's assemblies whole in {model.Name}'s Debug build") is not { } keeping)
        {
            return (false, ActionWork.Busy(work.Of(AppActions.KeepAssembliesWhole)!).Shown);
        }
        using (keeping)
        {
            string? platform = model.TargetFrameworks.Count > 1 ? FrameworkNames.MacCatalyst : null;
            string? refused = await ChangeFileAsync($"Keep .NET's Assemblies Whole in {model.Name}'s Debug Build", [project], () =>
            {
                if (FileText.Load(project) is not { } now)
                {
                    return $"{PathNames.Shown(project, Folder)} could not be read";
                }
                if (MobileHeads.WholeText(now.Text, platform) is not { } whole)
                {
                    return $"{model.Name} sets {MSBuildNames.MtouchLink} itself: set None there for its Mac Catalyst head's Debug build";
                }
                _ = FileText.SaveOver(project, now, whole);
                return null;
            });
            (bool, string) came = refused is null ? (true, MobileHeads.Whole(model.Name)) : (false, $"{model.Name}'s Debug build was left as it was: {refused}");
            Told(keeping, came.Item1, came.Item2);
            if (refused is null)
            {
                wholeAsked = null;
                BottomPanel.Run.ShowOffer(null);
                await session.Models.Idle;
                AskMobile(again: true);
            }
            return came;
        }
    }

    /// <summary>Make the Mobile Edits offered on a line of a project or Info.plist that one of its heads' edits changes, and
    /// Keep .NET's Assemblies Whole in Debug on a project's line naming a Mac Catalyst framework whose Debug build trims them,
    /// where Show Context Actions and an agent's code_actions find them.</summary>
    private IReadOnlyList<CSharp.EditorFix> MobileFixes(OpenDocument file, int offset)
    {
        string text = file.Text.Snapshot.ToString();
        int line = TextNavigation.LineStart(text, offset);
        List<CSharp.EditorFix> fixes = [];
        if (MobileWant.Trimmed.FirstOrDefault(h => PathNames.Same(h.Project, file.Path)) is { } trimmed
            && MobileHeads.CatalystPlaces(text).Any(p => line >= p.Start && line <= p.End))
        {
            fixes.Add(new CSharp.EditorFix(actions![AppActions.KeepAssembliesWhole]!.Title.Shown, $"{trimmed.Name}'s Debug build trims .NET's own assemblies, so {MacCatalystApp.KeptOut}",
                new TextRange(offset, 0), string.Empty)
            {
                Elsewhere = async (_, _) =>
                {
                    (bool made, string said) = await KeepWholeAsync(trimmed.Project);
                    return made ? new CSharp.RefactorOutcome(said, null, [new CSharp.RefactoredFile(trimmed.Project, CSharp.RefactoredChange.Changed, [])], [])
                        : CSharp.RefactorOutcome.Refused(said);
                },
            });
        }
        if (MobileWant.Heads.FirstOrDefault(h => h.Makes && h.Files.Any(f => PathNames.Same(f, file.Path))) is not { } head
            || !MobileHeads.Places(file.Path, text, head).Any(p => line >= p.Start && line <= p.End))
        {
            return fixes;
        }
        return [.. fixes, new CSharp.EditorFix(actions![AppActions.MakeMobileEdits]!.Title.Shown, head.Said, new TextRange(offset, 0), string.Empty)
        {
            Elsewhere = async (_, _) =>
            {
                (bool made, string said) = await MakeMobileEditsAsync(head.Project);
                return made ? new CSharp.RefactorOutcome(said, null, [.. head.Files.Select(f => new CSharp.RefactoredFile(f, CSharp.RefactoredChange.Changed, []))], [])
                    : CSharp.RefactorOutcome.Refused(said);
            },
        }];
    }

    /// <summary>Install Workloads: the workloads the projects lack, once the person agreed to install them.</summary>
    private async Task InstallWorkloadsAsync()
    {
        UiThread.Check();
        if (MobileWant.Workloads is { Count: > 0 } ids)
        {
            await InstallWorkloadsAsync(ids, asks: true, null, null, CancellationToken.None);
        }
    }

    /// <summary>dotnet workload install of <paramref name="ids"/> where the .NET Passenger builds with puts them (<see
    /// cref="WorkloadFoldersAsync"/>), Install Workloads' and New Project's one way: refused to an agent's input and where this
    /// user may not write there, asked where it <paramref name="asks"/>, its stages and lines handed on, the projects loaded
    /// again after; a workload set first where the .NET has none, downloads on disk (<see cref="WorkloadInstalls"/>). Whether
    /// installed, what it came to and any command left the person to run.</summary>
    public async Task<(bool Installed, string Said, string? ToRun)> InstallWorkloadsAsync(IReadOnlyList<string> ids, bool asks, Action<string>? stages, Action<string>? lines,
        CancellationToken stop)
    {
        UiThread.Check();
        if (RefusedToAgentsInput(Refusals.AgentInstallsMobile.Shown))
        {
            return (false, Refusals.AgentInstallsMobile.Shown, null);
        }
        string named = $"{Plural.Noun(ids.Count, "workload")} {Plural.Listed([.. ids])}";
        if (work.Begin(AppActions.InstallWorkloads, asks ? "Asking to install the .NET workloads" : $"Installing the .NET {named}") is not { } installing)
        {
            return (false, ActionWork.Busy(work.Of(AppActions.InstallWorkloads)!).Shown, null);
        }
        using (installing)
        {
            using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(installing.Stopping, stop);
            (bool, string, string?) Came(bool installed, string said, string? toRun = null)
            {
                Told(installing, installed, said);
                return (installed, said, toRun);
            }
            void Stage(string stage)
            {
                installing.Say(stage);
                stages?.Invoke(stage);
            }
            void Heard(string line)
            {
                installing.Say(line);
                lines?.Invoke(line);
            }
            WorkloadFolders folders = await WorkloadFoldersAsync();
            string command = $"dotnet workload install {string.Join(' ', ids)}";
            if (folders.UserLocal is null && !await BackgroundWork.Run(() => Writable(folders.Root)))
            {
                string asAdministrator = WorkloadInstalls.AsAdministrator(folders.Root, ids);
                return Came(false, Refusals.WorkloadsNeedAdministrator(folders.Root, asAdministrator).Shown, asAdministrator);
            }
            if (asks && !await ConfirmAsync($"Install the .NET {named}?",
                $"Passenger runs {command}, which downloads them from NuGet and installs them {WorkloadInstalls.Into(folders)}. "
                + $"{Plural.Listed([.. MobileWant.Projects])} then {Plural.Agreeing(MobileWant.Projects.Count, "loads", "load")} again.", "Install", null,
                Refusals.AgentInstallsMobile.Shown))
            {
                return Came(false, $"The .NET {named} {Plural.Agreeing(ids.Count, "was", "were")} not installed");
            }
            Stage($"Installing the .NET {named}");
            string temporary = Path.GetTempPath();
            string cache = Path.Combine(StateFolder, "cache");
            string? downloads = OperatingSystem.IsLinux() ? await BackgroundWork.Run(() => MadeDownloads(temporary, cache)) : null;
            int code;
            IReadOnlyList<string> said;
            try
            {
                (code, said) = await RunWorkloadAsync(["workload", "install", .. ids], downloads, Heard, stopping.Token);
                if (code != 0 && !stopping.IsCancellationRequested && WorkloadInstalls.Unknown(said))
                {
                    Stage("This .NET has no workload set installed: installing its newest first (dotnet workload update)");
                    (code, said) = await RunWorkloadAsync(["workload", "update"], downloads, Heard, stopping.Token);
                    if (code == 0 && !stopping.IsCancellationRequested)
                    {
                        Stage($"Installing the .NET {named}");
                        (code, said) = await RunWorkloadAsync(["workload", "install", .. ids], downloads, Heard, stopping.Token);
                    }
                }
            }
            finally
            {
                if (downloads is not null)
                {
                    await BackgroundWork.Run(() => DeleteDownloads(downloads));
                }
            }
            if (stopping.IsCancellationRequested)
            {
                return Came(false, $"The install of the .NET {named} was stopped");
            }
            if (code != 0)
            {
                return Came(false, WorkloadInstalls.NoRoom(said)
                    ? $"The .NET {named} could not be installed: its downloads found no room left in {PathNames.Shown(downloads ?? temporary, null)}. Make room there and install again"
                    : $"The .NET {named} could not be installed ({ExitCodes.Said(code)}): {LastWords(said)}");
            }
            Stage("Loading the projects again");
            await LoadAgainAsync();
            return Came(true, MobileWant.Workloads.Intersect(ids).Any()
                ? $"dotnet workload install ended, but the projects still lack {Plural.Listed([.. MobileWant.Workloads.Intersect(ids)])}"
                : $"The .NET {named} {Plural.Agreeing(ids.Count, "is", "are")} installed: the projects loaded again");
        }
    }

    /// <summary>Install Android SDK and Java: Google's licences of what is to be installed shown and accepted by the
    /// person, then the Android workload's InstallAndroidDependencies on a project lacking them, into the SDK folder
    /// found or the usual one, and the projects loaded again. Refused to an agent's input.</summary>
    private async Task InstallAndroidAsync()
    {
        UiThread.Check();
        MobileWant want = MobileWant;
        if (RefusedToAgentsInput(Refusals.AgentInstallsMobile.Shown) || !want.AndroidLacks || want.AndroidSdk is not { } sdk
            || session.Projects.FirstOrDefault(p => want.Projects.Contains(p.Name, StringComparer.Ordinal)
                && p.TargetFrameworks.Any(f => MobilePlatforms.Of(f) == FrameworkNames.Android)) is not { } project
            || work.Begin(AppActions.InstallAndroidDependencies, "Reading the Android SDK's licences") is not { } installing)
        {
            return;
        }
        using (installing)
        {
            string framework = project.TargetFrameworks.First(f => MobilePlatforms.Of(f) == FrameworkNames.Android);
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
            Dictionary<string, string> given = new() { [MSBuildNames.AndroidSdkDirectory] = sdk, [MSBuildNames.AcceptAndroidSdkLicenses] = "True" };
            if (want.Java is null)
            {
                given[MSBuildNames.AndroidInstallJavaDependencies] = "false";
            }
            else if (jdk is not null)
            {
                given[MSBuildNames.JavaSdkDirectory] = jdk;
            }
            ProcessStartInfo install = MSBuildQuestions.Command(project.Path, framework, ProjectModels.Properties(session.Workspace, project),
                new MSBuildQuestion([MSBuildNames.InstallAndroidDependencies]) { Given = given });
            (int code, IReadOnlyList<string> lines) = await RunInstallAsync(install, said => installing.Say(said), installing.Stopping);
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

    /// <summary>dotnet run for a workload install with these arguments (or the test's stand-in), downloading into
    /// <paramref name="downloads"/> where it is given, each line it writes <paramref name="heard"/>.</summary>
    private Task<(int Code, IReadOnlyList<string> Lines)> RunWorkloadAsync(IReadOnlyList<string> arguments, string? downloads, Action<string> heard,
        CancellationToken stopping)
    {
        ProcessStartInfo start = WorkloadStandIn?.Start(arguments) ?? ChildProcesses.DotnetCommand(arguments, Folder);
        if (downloads is not null)
        {
            start.Environment["TMPDIR"] = downloads;
        }
        return RunInstallAsync(start, heard, stopping);
    }

    /// <summary>The folder a workload install downloads into in place of <paramref name="temporary"/>, made, where that is
    /// held in memory; null where dotnet takes the temporary folder, or no folder can be made.</summary>
    private static string? MadeDownloads(string temporary, string cache)
    {
        if (WorkloadInstalls.DownloadsFolder(temporary, FormatOf, cache) is not { } downloads)
        {
            return null;
        }
        try
        {
            return Directory.CreateDirectory(downloads).FullName;
        }
        catch (Exception unmade) when (unmade is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"A workload install downloads into {temporary}, as {downloads} could not be made: {unmade.Message}");
            return null;
        }

        static string? FormatOf(string path)
        {
            try
            {
                return new DriveInfo(path).DriveFormat;
            }
            catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }
    }

    private static void DeleteDownloads(string downloads)
    {
        try
        {
            Directory.Delete(downloads, recursive: true);
        }
        catch (Exception kept) when (kept is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"A workload install's downloads were left in {downloads}: {kept.Message}");
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
        return new ConfirmDialog(title, text, "Accept and Install", "Decline", shown) { EnterAgrees = false, PersonsAlone = Refusals.AgentInstallsMobile.Shown }.ShowDialog<bool>(this);
    }

    /// <summary>An install's program run, each line it writes <paramref name="heard"/> on the window's thread, ended once
    /// <paramref name="stopping"/> is canceled.</summary>
    private static async Task<(int Code, IReadOnlyList<string> Lines)> RunInstallAsync(ProcessStartInfo start, Action<string> heard, CancellationToken stopping)
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
                UiThread.Post(() => heard(said));
            }
            Task hearing = ChildProcesses.Hear(process, Heard, Heard);
            using (stopping.Register(() => ChildProcesses.Kill(process)))
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

    /// <summary>Where Install Workloads puts the workloads (<see cref="WorkloadFolders.Installs"/>): the .NET the folder's
    /// projects take, or with none open the one dotnet takes in the home folder, or a test's stand-in; and the user's own
    /// folder where that .NET installs workloads for each user, needing no rights over the .NET itself.</summary>
    public Task<WorkloadFolders> WorkloadFoldersAsync()
    {
        string? folder = Folder;
        string? standIn = WorkloadStandIn?.Root;
        return BackgroundWork.Run(() => Dotnet.WorkloadsAsync(folder, standIn));
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
