using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Mobile;
using Passenger.App.Templates;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>What a mobile project needs, said over the window as it opens and got from there: a project whose workload
/// the .NET lacks (tvOS on a Mac, Android elsewhere) says so, and Install Workloads asks, runs the install (a stand-in
/// of dotnet's), a workload set first where the .NET has none, saying a download out of room plainly, and loads the
/// project again, into the user's own .dotnet where the .NET installs for each user, whose template packs are heard; an
/// Android project lacking its SDK's platform and a JDK (stand-ins of its targets) offers the workload's installer, run
/// once the person accepted the licences Google's repository (a stand-in) gives; the JDK it installs is found after with
/// no JAVA_HOME, one where the workload finds no JDK said and never given to it; on a Mac an iOS project lacking its
/// simulator runtime says how Xcode installs one, one whose Xcode the workload refuses offers Get Xcode; New Project
/// offers each mobile template a missing workload brings. It sets process-wide stand-ins (the .NET's too) and the
/// environment (ANDROID_HOME, DEVELOPER_DIR, HOME, JAVA_HOME, TMPDIR): it runs alone.</summary>
[Collection(Alone.Name)]
[AboutMacOS]
public sealed class MobileNeedsWindowTests : IDisposable
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(3);

    /// <summary>The first .NET whose mobile apps Passenger runs, which the projects target.</summary>
    private static readonly string Mobile = $"net{MobilePlatforms.FirstOnCoreClr}.0";

    private readonly string folder = PathNames.Canonical(TestFolders.Create("mobile-"));
    private readonly string? androidHome = Environment.GetEnvironmentVariable("ANDROID_HOME");
    private readonly string? repository = Environment.GetEnvironmentVariable(AndroidLicences.Variable);
    private readonly string? developerDir = Environment.GetEnvironmentVariable("DEVELOPER_DIR");
    private readonly Dictionary<string, string?> javaVariables = JavaVariables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
    private readonly string? home = Environment.GetEnvironmentVariable("HOME");

    /// <summary>The variables the Android workload takes a JDK from before looking in its usual places.</summary>
    private static readonly string[] JavaVariables = ["JAVA_HOME", "JDK_HOME", "JI_JAVA_HOME"];

    public void Dispose()
    {
        MainWindow.WorkloadStandIn = null;
        Environment.SetEnvironmentVariable("ANDROID_HOME", androidHome);
        Environment.SetEnvironmentVariable(AndroidLicences.Variable, repository);
        Environment.SetEnvironmentVariable("DEVELOPER_DIR", developerDir);
        foreach ((string name, string? value) in javaVariables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
        Environment.SetEnvironmentVariable("HOME", home);
        TestFolders.Delete(folder);
    }

    /// <summary>A workload a mobile project lacks: offered by New Project, said in the window, installed by Install
    /// Workloads (its workload set first, a download out of room said plainly) and the projects loaded again; refused
    /// where the .NET is not writable, and needing no administrator where each user's own folder takes it. The last
    /// steps skip where this machine's .NET can't stand in for those.</summary>
    [AvaloniaFact]
    public void A_workload_a_project_lacks_is_said_and_installed_by_Install_Workloads_as_the_dotnet_takes_it_with_no_administrator_where_none_is_needed()
    {
        Flow.StepApart<MobileNeedsWindowTests>("New Project offers each mobile template a missing workload brings, naming the workload that installs it", t => t.New_Project_offers_each_mobile_template_a_missing_workload_brings_naming_the_workload_that_installs_it());
        Flow.StepApart<MobileNeedsWindowTests>("a project whose workload is missing says so, and Install Workloads asks, installs and loads it again", t => t.A_project_whose_workload_is_missing_says_so_and_Install_Workloads_asks_installs_and_loads_it_again());
        Flow.StepApart<MobileNeedsWindowTests>("Install Workloads installs a workload set first where the .NET has none, and says a download out of room plainly", t => t.Install_Workloads_installs_a_workload_set_first_where_the_dotnet_has_none_and_says_a_download_out_of_room_plainly());
        Flow.StepApart<MobileNeedsWindowTests>("New Project's Install of a workload's template is Install Workloads' own, refused where the .NET is not writable", t => t.New_Projects_Install_of_a_workloads_template_is_Install_Workloads_own_refused_where_the_dotnet_is_not_writable_and_loading_the_projects_again());
        Flow.StepApart<MobileNeedsWindowTests>("Install Workloads needs no administrator where the .NET installs workloads into each user's own folder", t => t.Install_Workloads_needs_no_administrator_where_the_dotnet_installs_workloads_into_each_users_own_folder());
    }

    /// <summary>An Android project's SDK packages and JDK: a JAVA_HOME the workload finds no JDK in is said and never
    /// installed over, they are installed only once the person accepted their licences, and the JDK put where a window
    /// opened after finds it. Skipped where this machine builds no Android app.</summary>
    [AvaloniaFact]
    public void An_Android_projects_SDK_and_JDK_are_installed_only_once_the_person_accepted_their_licences_never_over_a_JAVA_HOME()
    {
        Flow.StepApart<MobileNeedsWindowTests>("a JAVA_HOME where the Android workload finds no JDK is said and never given to its installer to install over", t => t.A_JAVA_HOME_where_the_Android_workload_finds_no_JDK_is_said_and_never_given_to_its_installer_to_install_over());
        Flow.StepApart<MobileNeedsWindowTests>("an Android project lacking its platform and a JDK installs them only once the person accepted the SDK's licences", t => t.An_Android_project_lacking_its_platform_and_a_JDK_installs_them_only_once_the_person_accepted_the_SDKs_licences());
        Flow.StepApart<MobileNeedsWindowTests>("Install Android SDK and Java puts the JDK where a window opened after finds it, with no JAVA_HOME", t => t.Install_Android_SDK_and_Java_puts_the_JDK_where_a_window_opened_after_finds_it_with_no_JAVA_HOME());
    }

    /// <summary>An iOS project whose Xcode the workload refuses, or which has no simulator runtime of its SDK, says so
    /// with how to get one. On a Mac only.</summary>
    [AvaloniaFact]
    public void An_iOS_projects_Xcode_refused_or_lacking_a_simulator_runtime_is_said_with_how_to_get_one()
    {
        Flow.StepApart<MobileNeedsWindowTests>("an iOS project whose Xcode the workload refuses says why, and Get Xcode opens where Xcode comes from", t => t.An_iOS_project_whose_Xcode_the_workload_refuses_says_why_and_Get_Xcode_opens_where_Xcode_comes_from());
        Flow.StepApart<MobileNeedsWindowTests>("an iOS project whose Xcode has no simulator runtime of its SDK says so, with how Xcode installs one", t => t.An_iOS_project_whose_Xcode_has_no_simulator_runtime_of_its_SDK_says_so_with_how_Xcode_installs_one());
    }

    private void A_project_whose_workload_is_missing_says_so_and_Install_Workloads_asks_installs_and_loads_it_again()
    {
        (MainWindow window, string name, string platform, string said) = LackingWorkload();

        List<IReadOnlyList<string>> installed = [];
        MainWindow.WorkloadStandIn = new WorkloadStandIn(folder, arguments =>
        {
            installed.Add(arguments);
            return ChildProcesses.DotnetCommand(["--version"]);
        });
        List<string> asked = [];
        window.Confirm = (title, _, _) =>
        {
            asked.Add(title);
            return Task.FromResult(true);
        };
        int rounds = window.Session.Models.Rounds;
        Ui.ClickButton(window, "Install Workloads…");
        Ui.WaitFor(() => window.Actions.Work.Of(AppActions.InstallWorkloads) is null, () => $"the install to end: {window.Status}", Slow);

        Assert.Equal([$"Install the .NET workload {platform}?"], asked);
        Assert.Equal(["workload", "install", platform], Assert.Single(installed));
        Assert.True(window.Session.Models.Rounds > rounds, "The project was not evaluated again after the install");
        // The stand-in installs nothing, so the project still lacks it, and says so.
        Assert.Equal($"dotnet workload install ended, but the projects still lack {platform}", window.Status);
        Assert.Equal(said, window.MobileSaid);
        window.Close();
    }

    /// <summary>A .NET this user may not write whose SDK installs workloads into each user's own folder (its userlocal
    /// marker, which Fedora's packaged .NET carries) is no reason to send the person to an administrator: Install Workloads
    /// asks and runs the install (a stand-in of dotnet's) as for a .NET of their own, where sudo would install it for root.</summary>
    private void Install_Workloads_needs_no_administrator_where_the_dotnet_installs_workloads_into_each_users_own_folder()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A .NET no user may write is Program Files' on Windows, where the test can't put a marker");
            return;
        }
        (MainWindow window, _, string platform, _) = LackingWorkload();
        string sdk = Ui.Await(Dotnet.SdkAsync(folder), "the SDK the folder takes", Slow);
        string shared = Directory.CreateDirectory(Path.Combine(folder, "obj", "shared-dotnet")).FullName;
        string marker = WorkloadFolders.UserLocalMarker(shared, sdk) ?? throw new InvalidOperationException($"dotnet --version named {sdk}, which names no feature band");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, string.Empty);
        File.SetUnixFileMode(shared, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.SkipWhen(Libc.Access(shared, Libc.MayWrite) == 0, $"This user may write {shared} whatever its mode says, so no .NET stands in as another's");
            List<IReadOnlyList<string>> installed = [];
            MainWindow.WorkloadStandIn = new WorkloadStandIn(shared, arguments =>
            {
                installed.Add(arguments);
                return ChildProcesses.DotnetCommand(["--version"]);
            });
            List<(string Title, string Text)> asked = [];
            window.Confirm = (title, text, _) =>
            {
                asked.Add((title, text));
                return Task.FromResult(true);
            };
            Ui.ClickButton(window, "Install Workloads…");
            Ui.WaitFor(() => window.Actions.Work.Of(AppActions.InstallWorkloads) is null, () => $"the install to end: {window.Status}", Slow);

            Assert.True(asked.Count == 1, $"Install Workloads was refused as needing an administrator, where the .NET installs for each user: {window.Status}");
            Assert.Equal($"Install the .NET workload {platform}?", asked[0].Title);
            string own = PathNames.Canonical(Path.Combine(Dotnet.CliHome()!, ".dotnet"));
            Assert.Contains($"installs them into {own}, for you alone, as the .NET in {shared} installs workloads for each user", asked[0].Text, StringComparison.Ordinal);
            Assert.Equal(["workload", "install", platform], Assert.Single(installed));
            window.Close();
        }
        finally
        {
            File.SetUnixFileMode(shared, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>A .NET with no workload set installed knows no workload until one is (dotnet's own words, from a stand-in):
    /// Install Workloads installs its newest first, then the workload, whose download running out of room is said plainly
    /// with the folder it ran out in. Where the temporary folder is held in memory (Linux's /dev/shm stands for a tmpfs
    /// /tmp), every dotnet it runs downloads into a folder of Passenger's own cache, taken away after; elsewhere into the
    /// temporary folder.</summary>
    private void Install_Workloads_installs_a_workload_set_first_where_the_dotnet_has_none_and_says_a_download_out_of_room_plainly()
    {
        (MainWindow window, _, string platform, _) = LackingWorkload();
        List<IReadOnlyList<string>> asked = [];
        List<System.Diagnostics.ProcessStartInfo> starts = [];
        string[] said =
        [
            $"Workload installation failed: Workload ID {platform} is not recognized.",
            "Successfully updated workload(s): .",
            $"Workload installation failed: One or more errors occurred. (Disk quota exceeded : '{Path.Combine("tmp", "dotnet-sdk-advertising-temp", "pack.nupkg")}')",
        ];
        MainWindow.WorkloadStandIn = new WorkloadStandIn(folder, arguments =>
        {
            int round = asked.Count;
            asked.Add(arguments);
            System.Diagnostics.ProcessStartInfo start = Saying($"round-{round}", said[Math.Min(round, said.Length - 1)], round == 1 ? 0 : 1);
            starts.Add(start);
            return start;
        });
        window.Confirm = (_, _, _) => Task.FromResult(true);
        string? temporary = Environment.GetEnvironmentVariable("TMPDIR");
        string? memory = OperatingSystem.IsLinux() && new DriveInfo("/dev/shm").DriveFormat == "tmpfs" ? "/dev/shm" : null;
        if (memory is not null)
        {
            Environment.SetEnvironmentVariable("TMPDIR", memory);
        }
        try
        {
            Ui.ClickButton(window, "Install Workloads…");
            Ui.WaitFor(() => window.Actions.Work.Of(AppActions.InstallWorkloads) is null, () => $"the install to end: {window.Status}", Slow);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", temporary);
        }

        Assert.Equal([["workload", "install", platform], ["workload", "update"], ["workload", "install", platform]], asked);
        Assert.StartsWith($"The .NET workload {platform} could not be installed: its downloads found no room left in ", window.Status, StringComparison.Ordinal);
        Assert.EndsWith(". Make room there and install again", window.Status, StringComparison.Ordinal);
        string? given = starts[0].Environment.TryGetValue("TMPDIR", out string? set) ? set : null;
        Assert.True(starts.All(s => (s.Environment.TryGetValue("TMPDIR", out string? each) ? each : null) == given), "Each dotnet of the install was given its own temporary folder");
        if (memory is null)
        {
            Assert.True(given == temporary, $"With the temporary folder on disk, the install was given {given} in its place");
            window.Close();
            return;
        }
        string cache = Path.Combine(window.StateFolder, "cache", WorkloadInstalls.Downloads);
        Assert.True(given is not null && PathNames.IsUnder(given, cache), $"With the temporary folder in memory, the install downloaded into {given}, not a folder under {cache}");
        Assert.False(Directory.Exists(given), $"The install's downloads folder {given} was left behind");
        window.Close();
    }

    /// <summary>New Project's Install of a template a workload brings (a .NET lacking the workload stood in by dotnet
    /// new without the SDK's templates, its dotnet by a stand-in), offered saying where it goes, is Install Workloads'
    /// own: refused where the .NET is not this user's to write, in the status bar and the dialog, which copies the
    /// command to run; else run asking nothing more, a workload set first, its lines shown, the project loaded again,
    /// dotnet new still listing none of its templates. None is offered where this machine builds no Android app.</summary>
    private void New_Projects_Install_of_a_workloads_template_is_Install_Workloads_own_refused_where_the_dotnet_is_not_writable_and_loading_the_projects_again()
    {
        File.WriteAllText(Path.Combine(folder, "Desk.csproj"), TestProjects.Library());
        string templates = PathNames.Canonical(TestFolders.Create("workload-templates-"));
        string unwritable = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")
            : Directory.CreateDirectory(Path.Combine(templates, "dotnet")).FullName;
        string? hive = ProjectTemplates.Hive;
        ProjectTemplates.Hive = Directory.CreateDirectory(Path.Combine(templates, "hive")).FullName;
        ProjectTemplates.SdkTemplates = false;
        ProjectTemplates.Forget();
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(unwritable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                Assert.SkipWhen(Libc.Access(unwritable, Libc.MayWrite) == 0, $"This user may write {unwritable} whatever its mode says, so no .NET stands in as another's");
            }
            // dotnet new lists none at all without one of the SDK's or the hive's, which the dialog takes for no templates.
            ChildResult made = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ProjectTemplates.NewCommand(["install", WritePlainTemplate(templates)], templates),
                limit: TimeSpan.FromMinutes(2))), "dotnet new install of the test's template", Slow);
            Assert.True(made.Succeeded, $"dotnet new install of the test's template failed:\n{made.Output}{made.Error}");
            string[] said = ["Workload installation failed: Workload ID android is not recognized.", "Successfully updated workload(s): .", "Successfully installed workload(s) android."];
            List<IReadOnlyList<string>> asked = [];
            MainWindow.WorkloadStandIn = new WorkloadStandIn(unwritable, arguments =>
            {
                int round = asked.Count;
                asked.Add(arguments);
                return Saying($"new-project-{round}", said[Math.Min(round, said.Length - 1)], round == 0 ? 1 : 0);
            });
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            Ui.WaitFor(() => window.Projects.Any(p => p.IsEvaluated), "the project to evaluate", Slow);
            List<string> questions = [];
            window.Confirm = (title, _, _) =>
            {
                questions.Add(title);
                return Task.FromResult(true);
            };
            window.Perform(AppActions.NewSolution);
            Ui.Settle();
            NewProjectDialog dialog = Assert.Single(window.OwnedWindows.OfType<NewProjectDialog>(), d => d.IsVisible);
            Ui.WaitFor(() => dialog.TemplatesRead.IsCompleted, "dotnet new's templates to be read", Slow);
            ProjectTemplate android = Assert.Single(dialog.Templates, t => t.ShortName == "android");
            Assert.True(android.Pack is { Id: "android", IsWorkload: true }, $"Android's app is offered with {android.Pack?.Id ?? "nothing"}, though dotnet new lists none of the SDK's");
            Ui.Click(dialog.FamilyRow(android.Family)!);
            if (dialog.Chosen != android)
            {
                dialog.TypeChooser.SelectedItem = android;
                Ui.Settle();
            }
            Assert.Equal(android.Name, dialog.Chosen?.Name);
            if (Unbuilt() is { } host)
            {
                Assert.False(dialog.InstallButton.IsEffectivelyVisible, $"{host}, which builds no Android app, was offered the Android workload's install");
                Assert.StartsWith($"{android.Name} builds for Android only on {AndroidHosts.Builders}: {AndroidHosts.Why(host)}\n", dialog.Offer, StringComparison.Ordinal);
                dialog.Cancel();
                window.Close();
                return;
            }
            string into = WorkloadInstalls.Into(new WorkloadFolders(unwritable, null));
            Ui.WaitFor(() => dialog.Offer?.Contains($"which installs it from NuGet {into}.", StringComparison.Ordinal) == true,
                () => $"the offer to say where Install puts the workload, {into}: {dialog.Offer}");

            Ui.Click(dialog.InstallButton);
            Ui.WaitFor(() => !dialog.IsInstalling, () => $"the install to end: {window.Status}", Slow);
            string toRun = WorkloadInstalls.AsAdministrator(unwritable, ["android"]);
            string refused = Refusals.WorkloadsNeedAdministrator(unwritable, toRun).Shown;
            Assert.Empty(asked);
            Assert.Equal(refused, dialog.Problem);
            Assert.Equal(refused, window.Status);
            Assert.True(dialog.CopyCommandButton.IsEffectivelyVisible, "The refusal asks the person to run a command, but offers no Copy Command beside it");
            Ui.Click(dialog.CopyCommandButton);
            Ui.WaitFor(() => Ui.Clipboard(dialog) == toRun, () => $"Copy Command to put {toRun} on the clipboard, which holds {Ui.Clipboard(dialog)}");
            Assert.Equal("Copied", dialog.CopyCommandButton.Content);

            MainWindow.WorkloadStandIn = MainWindow.WorkloadStandIn with { Root = templates };
            int rounds = window.Session.Models.Rounds;
            Ui.Click(dialog.InstallButton);
            Ui.WaitFor(() => !dialog.IsInstalling, () => $"the install to end: {window.Status}", Slow);
            Assert.Equal([["workload", "install", "android"], ["workload", "update"], ["workload", "install", "android"]], asked);
            Assert.Empty(questions);
            Assert.True(window.Session.Models.Rounds > rounds, "The folder's project was not evaluated again after the install");
            Assert.Contains(said[^1], dialog.InstallOutput, StringComparison.Ordinal);
            Assert.Equal("The .NET workload android is installed: the projects loaded again", window.Status);
            Assert.False(dialog.CopyCommandButton.IsVisible, "Copy Command stayed after the install, which left no command to run");
            // The stand-in installs nothing, so dotnet new still lists none of the workload's templates.
            Assert.Equal($"the .NET android workload is installed, but dotnet new has no {android.Name} (android) from it", dialog.Problem);
            dialog.Cancel();
            window.Close();
        }
        finally
        {
            ProjectTemplates.Hive = hive;
            ProjectTemplates.SdkTemplates = true;
            ProjectTemplates.Forget();
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(unwritable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            TestFolders.Delete(templates);
        }
    }

    /// <summary>A project template of the test's own in <paramref name="under"/>, for dotnet new to list with none of the SDK's.</summary>
    private static string WritePlainTemplate(string under)
    {
        string template = Directory.CreateDirectory(Path.Combine(under, "PlainTemplate", ".template.config")).Parent!.FullName;
        File.WriteAllText(Path.Combine(template, ".template.config", "template.json"), """
            {
              "$schema": "http://json.schemastore.org/template",
              "author": "Passenger's tests",
              "classifications": [ "Library" ],
              "identity": "Passenger.Tests.Plain.CSharp",
              "name": "Plain Library",
              "shortName": "probe-plain",
              "sourceName": "Plain",
              "tags": { "language": "C#", "type": "project" }
            }
            """);
        File.WriteAllText(Path.Combine(template, "Plain.csproj"), TestProjects.Library());
        return template;
    }

    /// <summary>Skips a test of the Android SDK's install where this machine builds no Android app, which offers none.</summary>
    private void SkipWhereAndroidIsNotBuilt()
    {
        string? host = Unbuilt();
        Assert.SkipWhen(host is not null, $"{host} builds no Android app, so nothing of the Android SDK is offered there");
    }

    /// <summary>This machine as a person names it where it builds no Android app, asked of the folder's .NET; null where it does.</summary>
    private string? Unbuilt() => Ui.Await(AndroidHosts.UnbuiltAsync(folder), "whether this machine builds Android apps");

    /// <summary>A window on a project of a platform whose workload this .NET lacks, as it is said; skipped where it has it.
    /// tvOS on a Mac, which neither this Mac nor CI's runners have, else Android, which builds there (on Linux on Arm, it
    /// says too that it builds only elsewhere).</summary>
    private (MainWindow Window, string Name, string Platform, string Said) LackingWorkload()
    {
        (string name, string platform) = OperatingSystem.IsMacOS() ? ("Tv", "tvos") : ("Droid", "android");
        File.WriteAllText(Path.Combine(folder, $"{name}.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{Mobile}-{platform}</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
            </Project>
            """);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.WaitFor(() => window.Projects.Any(p => p.IsEvaluated), "the project to evaluate", Slow);
        Ui.Await(window.MobileAsked(), "what the project needs to be asked", Slow);
        if (window.MobileWant.Workloads.Count == 0)
        {
            window.Close();
            Assert.Skip($"This .NET has the {platform} workload, so nothing is lacking to say");
        }
        Assert.Equal([platform], window.MobileWant.Workloads);
        string said = Unbuilt() is { } host && platform == FrameworkNames.Android
            ? $"{name} builds for Android only on {AndroidHosts.Builders}: .NET's Android build tools are x64 programs, which {host} does not run"
            : $"{name} needs the .NET workload {platform}";
        Assert.Equal(said, window.MobileSaid);
        return (window, name, platform, said);
    }

    /// <summary>A stand-in of dotnet that writes <paramref name="words"/> (from a file, so no shell reads them) and ends with
    /// <paramref name="code"/>, kept in the project's obj folder, which no project reads.</summary>
    private System.Diagnostics.ProcessStartInfo Saying(string name, string words, int code)
    {
        string kept = Directory.CreateDirectory(Path.Combine(folder, "obj", "stand-in")).FullName;
        string text = Path.Combine(kept, $"{name}.txt");
        File.WriteAllText(text, words + "\n");
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(kept, $"{name}.cmd");
            File.WriteAllText(script, $"@echo off\r\ntype \"{text}\"\r\nexit /b {code}\r\n");
            return ChildProcesses.Command(script, []);
        }
        return ChildProcesses.Command("/bin/sh", ["-c", "cat \"$1\"; exit $2", "sh", text, code.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
    }

    private void An_Android_project_lacking_its_platform_and_a_JDK_installs_them_only_once_the_person_accepted_the_SDKs_licences()
    {
        SkipWhereAndroidIsNotBuilt();
        string sdk = Directory.CreateDirectory(Path.Combine(folder, "android-sdk")).FullName;
        Directory.CreateDirectory(Path.Combine(sdk, "platform-tools"));
        Environment.SetEnvironmentVariable("ANDROID_HOME", sdk);
        foreach (string name in JavaVariables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
        using StandInHttp google = StandInRepository();
        File.WriteAllText(Path.Combine(folder, "Droid.csproj"), StandInAndroid);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.WaitFor(() => window.Projects.Any(p => p.IsEvaluated), "the project to evaluate", Slow);
        Ui.Await(window.MobileAsked(), "what the project needs to be asked", Slow);
        Assert.Equal("Droid needs a JDK (Java 21.0.8) and the Android SDK's platforms;android-37.0", window.MobileSaid);
        Assert.Null(window.Actions.WhyNot(AppActions.InstallAndroidDependencies, window.Actions.Context())?.Shown);
        Assert.Equal(Refusals.AgentInstallsMobile.Shown, window.Actions.WhyNot(AppActions.InstallAndroidDependencies, window.Actions.Context().ForAgent())?.Shown);

        List<string> questions = [];
        bool accept = false;
        window.Confirm = (title, text, yes) =>
        {
            questions.Add($"{title}\n{text}\n{yes}");
            return Task.FromResult(accept);
        };
        Ui.ClickButton(window, "Install Android SDK and Java…");
        Ui.WaitFor(() => window.Actions.Work.Of(AppActions.InstallAndroidDependencies) is null, () => $"the install to end: {window.Status}", Slow);
        string question = Assert.Single(questions);
        Assert.Contains("Accept the Android SDK's Licences?", question, StringComparison.Ordinal);
        Assert.Contains("you accept android-sdk-license", question, StringComparison.Ordinal);
        Assert.Contains("This is the Android Software Development Kit License Agreement", question, StringComparison.Ordinal);
        Assert.Contains("platforms;android-37.0 and Microsoft's build of OpenJDK 21.0.8", question, StringComparison.Ordinal);
        Assert.Equal("The Android SDK's licences were not accepted, so nothing was installed", window.Status);
        Assert.False(File.Exists(Path.Combine(sdk, "installed.txt")), "The installer ran though the licences were declined");

        accept = true;
        Ui.ClickButton(window, "Install Android SDK and Java…");
        Ui.WaitFor(() => window.Actions.Work.Of(AppActions.InstallAndroidDependencies) is null, () => $"the install to end: {window.Status}", Slow);
        Assert.Equal(2, questions.Count);
        Assert.Contains("AcceptAndroidSDKLicenses=True", File.ReadAllLines(Path.Combine(sdk, "installed.txt")));
        Assert.True(window.MobileSaid is null && !window.MobileWant.AndroidLacks, $"Once installed, the banner still says: {window.MobileSaid}");
        Assert.StartsWith("platforms;android-37.0 and Microsoft's build of OpenJDK 21.0.8", window.Status, StringComparison.Ordinal);
        window.Close();
    }

    private void Install_Android_SDK_and_Java_puts_the_JDK_where_a_window_opened_after_finds_it_with_no_JAVA_HOME()
    {
        SkipWhereAndroidIsNotBuilt();
        if (OperatingSystem.IsWindows())
        {
            // Windows' folder for it is under the person's own LocalApplicationData, which no variable moves.
            Assert.Skip("Only a Mac's or Linux's JDK folder for the workload's installer can be moved into the test's own home");
        }
        // The folder Passenger gives the workload's installer is under the home folder, which this test stands in.
        string person = Directory.CreateDirectory(Path.Combine(folder, "home")).FullName;
        string sdk = Directory.CreateDirectory(Path.Combine(folder, "android-sdk")).FullName;
        Directory.CreateDirectory(Path.Combine(sdk, "platform-tools"));
        Environment.SetEnvironmentVariable("ANDROID_HOME", sdk);
        Environment.SetEnvironmentVariable("HOME", person);
        foreach (string name in JavaVariables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
        using StandInHttp google = StandInRepository();
        File.WriteAllText(Path.Combine(folder, "Droid.csproj"), StandInInstaller);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.WaitFor(() => window.Projects.Any(p => p.IsEvaluated), "the project to evaluate", Slow);
        Ui.Await(window.MobileAsked(), "what the project needs to be asked", Slow);
        if (window.MobileWant.Workloads.Contains("android"))
        {
            Assert.Skip("This .NET lacks the android workload, which finds the JDK");
        }
        string? java = window.MobileWant.Java;
        if (java is null)
        {
            Assert.Skip($"The android workload found a JDK of this machine's own outside the home folder: {window.MobileSaid}");
        }
        StandInJdk(Path.Combine(folder, "jdk-stand-in"), java);
        window.Confirm = (_, _, _) => Task.FromResult(true);
        Ui.ClickButton(window, "Install Android SDK and Java…");
        Ui.WaitFor(() => window.Actions.Work.Of(AppActions.InstallAndroidDependencies) is null, () => $"the install to end: {window.Status}", Slow);
        string major = java.Split('.')[0];
        string jdk = OperatingSystem.IsMacOS() ? Path.Combine(person, "Library", "Android", $"microsoft-{major}.jdk") : Path.Combine(person, "android-toolchain", $"jdk-{major}");
        Assert.Equal(jdk, File.ReadAllText(Path.Combine(folder, "installed.txt")).Trim());
        Assert.True(window.MobileWant.Java is null, $"The JDK installed in {jdk} was not found once the projects loaded again, the window saying: {window.MobileSaid}; {window.Status}");
        window.Close();

        MainWindow again = Ui.OpenFolder(folder, languageServer: false);
        Ui.WaitFor(() => again.Projects.Any(p => p.IsEvaluated), "the project to evaluate", Slow);
        Ui.Await(again.MobileAsked(), "what the project needs to be asked", Slow);
        Assert.True(again.MobileWant.AndroidSdk is not null, $"The Android workload was not asked again: {again.MobileSaid}");
        Assert.True(again.MobileWant.Java is null, $"The JDK in {jdk} was not found by a window opened after, the window saying: {again.MobileSaid}");
        Assert.DoesNotContain("JDK", again.MobileSaid ?? string.Empty, StringComparison.Ordinal);
        again.Close();
    }

    private void A_JAVA_HOME_where_the_Android_workload_finds_no_JDK_is_said_and_never_given_to_its_installer_to_install_over()
    {
        SkipWhereAndroidIsNotBuilt();
        string sdk = Directory.CreateDirectory(Path.Combine(folder, "android-sdk")).FullName;
        Directory.CreateDirectory(Path.Combine(sdk, "platform-tools"));
        Environment.SetEnvironmentVariable("ANDROID_HOME", sdk);
        foreach (string name in JavaVariables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
        // A JDK of the person's own whose tools are gone, which the workload passes over and its installer would delete.
        string broken = Directory.CreateDirectory(Path.Combine(folder, "broken-jdk")).FullName;
        File.WriteAllText(Path.Combine(broken, "release"), "JAVA_VERSION=\"17.0.2\"\n");
        Environment.SetEnvironmentVariable("JAVA_HOME", broken);
        using StandInHttp google = StandInRepository();
        File.WriteAllText(Path.Combine(folder, "Droid.csproj"), StandInAndroid);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.WaitFor(() => window.Projects.Any(p => p.IsEvaluated), "the project to evaluate", Slow);
        Ui.Await(window.MobileAsked(), "what the project needs to be asked", Slow);
        Assert.Equal($"Droid needs a JDK (Java 21.0.8) and the Android SDK's platforms;android-37.0. JAVA_HOME names {broken}, where the Android workload finds no JDK",
            window.MobileSaid);

        string? asked = null;
        window.Confirm = (_, text, _) =>
        {
            asked = text;
            return Task.FromResult(true);
        };
        Ui.ClickButton(window, "Install Android SDK and Java…");
        Ui.WaitFor(() => window.Actions.Work.Of(AppActions.InstallAndroidDependencies) is null, () => $"the install to end: {window.Status}", Slow);
        string jdk = AndroidFolders.Jdk(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "21.0.8")!;
        Assert.Contains($"JavaSdkDirectory={jdk}", File.ReadAllLines(Path.Combine(sdk, "installed.txt")));
        Assert.Contains($" and the JDK into {PathNames.Shown(jdk, null)}, for you alone.", asked, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(broken, "release")), "The JDK JAVA_HOME names was installed over");
        window.Close();
    }

    private void An_iOS_project_whose_Xcode_has_no_simulator_runtime_of_its_SDK_says_so_with_how_Xcode_installs_one()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Xcode and its simulator runtimes are asked of only on a Mac; elsewhere an iOS app is said to build only on one");
        }
        // Xcode's own simctl, standing in, lists an older iOS runtime only.
        string developer = Directory.CreateDirectory(Path.Combine(folder, "Xcode.app", "Contents", "Developer")).FullName;
        StandInSimctl(developer, """{ "runtimes" : [ { "platform" : "iOS", "version" : "18.0", "isAvailable" : true, "name" : "iOS 18.0" } ] }""");
        File.WriteAllText(Path.Combine(folder, "Ios.csproj"), StandInIos(developer, error: null));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.WaitFor(() => window.Projects.Any(p => p.IsEvaluated), "the project to evaluate", Slow);
        Ui.Await(window.MobileAsked(), "what the project needs to be asked", Slow);

        Assert.Equal("Ios needs the iOS 27.0 simulator runtime, which Xcode installs (Xcode › Settings › Components, or xcodebuild -downloadPlatform iOS)",
            window.MobileSaid);
        Assert.Null(window.MobileWant.Xcode);
        Assert.DoesNotContain(ShownButtons(window), b => b == "Get Xcode");
        window.Close();
    }

    private void An_iOS_project_whose_Xcode_the_workload_refuses_says_why_and_Get_Xcode_opens_where_Xcode_comes_from()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Xcode is asked of only on a Mac; elsewhere an iOS app is said to build only on one");
        }
        string developer = Directory.CreateDirectory(Path.Combine(folder, "Xcode.app", "Contents", "Developer")).FullName;
        const string refused = "This version of .NET for iOS (27.2.11039) requires Xcode 27.2. The current version of Xcode is 27.0.";
        File.WriteAllText(Path.Combine(folder, "Ios.csproj"), StandInIos(developer, refused));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.WaitFor(() => window.Projects.Any(p => p.IsEvaluated), "the project to evaluate", Slow);
        Ui.Await(window.MobileAsked(), "what the project needs to be asked", Slow);
        Assert.Equal($"Ios needs Xcode ({refused.TrimEnd('.')})", window.MobileSaid);

        Ui.ClickButton(window, "Get Xcode");
        Assert.Equal("https://developer.apple.com/xcode/", Assert.Single(window.Browsed).AbsoluteUri);
        window.Close();
    }

    private void New_Project_offers_each_mobile_template_a_missing_workload_brings_naming_the_workload_that_installs_it()
    {
        List<ProjectTemplate> offered = [.. TemplatePacks.Offered([], androidHost: null)];
        string id = OperatingSystem.IsLinux() ? "maui-android" : "maui";
        ProjectTemplate maui = offered.Single(t => t.ShortNames.Contains("maui"));
        Assert.True(maui.Pack is { IsWorkload: true } pack && pack.Id == id, $"The MAUI app is offered with {maui.Pack?.Id}");
        Assert.Equal($"the .NET {id} workload", maui.Pack!.Named);
        Assert.Equal($"dotnet workload install {id}", maui.Pack.Command);
        Assert.Equal("android", offered.Single(t => t.ShortNames.Contains("android")).Pack!.Id);
        Assert.Equal(!OperatingSystem.IsLinux(), offered.Any(t => t.ShortNames.Contains("ios") && t.Pack!.Id == "ios"));
        Assert.Equal("Avalonia.Templates", offered.Single(t => t.ShortNames.Contains("avalonia.xplat")).Pack!.Command["dotnet new install ".Length..]);

        List<ProjectTemplate> again = [.. TemplatePacks.Offered([maui with { Pack = null }], androidHost: null)];
        Assert.DoesNotContain(again, t => t.ShortNames.Contains("maui"));
        Assert.Contains(again, t => t.ShortNames.Contains("maui-blazor"));
    }

    /// <summary>The words of the buttons the window shows.</summary>
    private static IEnumerable<string> ShownButtons(MainWindow window) =>
        window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content).OfType<string>();

    /// <summary>A stand-in of a JDK's home folder as the Android workload checks one: its tools, its launcher library
    /// (macOS's) and its virtual machine (Linux's), and its release file naming the version, its java saying its
    /// properties as a JDK's does.</summary>
    private static void StandInJdk(string jdk, string version)
    {
        string bin = Directory.CreateDirectory(Path.Combine(jdk, "bin")).FullName;
        foreach (string tool in (string[])["jar", "javac", "java", "jarsigner"])
        {
            string program = Path.Combine(bin, tool);
            File.WriteAllText(program, $"#!/bin/sh\necho 'Property settings:' >&2\necho '    java.home = {jdk}' >&2\necho '    java.vendor = Microsoft' >&2\n"
                + $"echo '    java.version = {version}' >&2\necho 'openjdk version \"{version}\"' >&2\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(jdk, "lib")).FullName, "libjli.dylib"), string.Empty);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(jdk, "lib", "server")).FullName, "libjvm.so"), string.Empty);
        File.WriteAllText(Path.Combine(jdk, "release"), $"IMPLEMENTOR=\"Microsoft\"\nJAVA_VERSION=\"{version}\"\n");
    }

    /// <summary>A stand-in of Xcode's simctl in a developer folder, listing these runtimes.</summary>
    private static void StandInSimctl(string developer, string runtimes)
    {
        string simctl = Path.Combine(Directory.CreateDirectory(Path.Combine(developer, "usr", "bin")).FullName, "simctl");
        File.WriteAllText(simctl, $"#!/bin/sh\ncat <<'LISTED'\n{runtimes}\nLISTED\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(simctl, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>An iOS project whose workload's check of Xcode is a stand-in after the SDK's (whose own check of the Xcode's
    /// version is passed over): it lacks no workload, and finds the Xcode in <paramref name="developer"/> with the iOS 27.0
    /// SDK, or refuses it with <paramref name="error"/>.</summary>
    private static string StandInIos(string developer, string? error) => $"""
        <Project>
          <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
          <PropertyGroup>
            <TargetFramework>{Mobile}-ios</TargetFramework>
            <OutputType>Exe</OutputType>
            <ValidateXcodeVersion>false</ValidateXcodeVersion>
          </PropertyGroup>
          <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
          <Target Name="_GetRequiredWorkloads" />
          <Target Name="_DetectSdkLocations">
            <PropertyGroup>
              <_SdkDevPath>{developer}</_SdkDevPath>
              <_SdkVersion>27.0</_SdkVersion>
            </PropertyGroup>
            {(error is null ? string.Empty : $"<Error Text=\"{error}\" />")}
          </Target>
        </Project>
        """;

    /// <summary>A stand-in of Google's repository (<see cref="Repository"/>), which the window is told to read.</summary>
    private static StandInHttp StandInRepository()
    {
        StandInHttp google = new(exchange => exchange.Path == "/repository2-3.xml"
            ? exchange.ReplyAsync(200, "application/xml", Repository)
            : exchange.ReplyAsync(404, "text/plain", "not here"));
        Environment.SetEnvironmentVariable(AndroidLicences.Variable, google.Root + "/repository2-3.xml");
        return google;
    }

    /// <summary>An Android project on the Android workload whose installer is a stand-in after the workload's own targets,
    /// which writes the JDK folder it was given to installed.txt and copies the stand-in JDK beside the project into it.</summary>
    private static string StandInInstaller => $"""
        <Project>
          <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
          <PropertyGroup>
            <TargetFramework>{Mobile}-android</TargetFramework>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
          <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
          <Target Name="InstallAndroidDependencies">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/installed.txt" Lines="$(JavaSdkDirectory)" Overwrite="true" />
            <Exec Command="mkdir -p &quot;$(JavaSdkDirectory)&quot; &amp;&amp; cp -R &quot;$(MSBuildProjectDirectory)/jdk-stand-in/.&quot; &quot;$(JavaSdkDirectory)&quot;" />
          </Target>
        </Project>
        """;

    /// <summary>A Google repository of one licence, which the platform uses.</summary>
    private const string Repository = """
        <?xml version="1.0" encoding="UTF-8"?>
        <sdk:sdk-repository xmlns:sdk="http://schemas.android.com/sdk/android/repo/repository2/03">
          <license id="android-sdk-license" type="text">This is the Android Software Development Kit License Agreement</license>
          <remotePackage path="platforms;android-37.0"><uses-license ref="android-sdk-license"/></remotePackage>
        </sdk:sdk-repository>
        """;

    /// <summary>An Android project whose workload's targets are stand-ins after the SDK's: it lacks no workload, its
    /// dependencies are API 37's platform and a JDK, found once its installer, which records how it was asked, ran.</summary>
    private static string StandInAndroid => $"""
        <Project>
          <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
          <PropertyGroup>
            <TargetFramework>{Mobile}-android</TargetFramework>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
          <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
          <Target Name="_GetRequiredWorkloads" />
          <Target Name="GetAndroidDependencies">
            <ItemGroup>
              <AndroidDependency Include="platforms/android-37.0" />
              <AndroidDependency Include="platform-tools" />
              <JavaDependency Include="jdk" Version="21.0.8" />
            </ItemGroup>
            <Warning Code="XA5300" Text="The Java SDK directory could not be found. Install the Java SDK by following the instructions at: https://aka.ms/dotnet-android-install-sdk"
                     Condition="!Exists('$(ANDROID_HOME)/jdk-installed')" />
          </Target>
          <Target Name="InstallAndroidDependencies">
            <WriteLinesToFile File="$(AndroidSdkDirectory)/installed.txt" Lines="AcceptAndroidSDKLicenses=$(AcceptAndroidSDKLicenses);JavaSdkDirectory=$(JavaSdkDirectory)" Overwrite="true" />
            <MakeDir Directories="$(AndroidSdkDirectory)/platforms/android-37.0" />
            <Touch Files="$(AndroidSdkDirectory)/platforms/android-37.0/android.jar;$(AndroidSdkDirectory)/jdk-installed" AlwaysCreate="true" />
          </Target>
        </Project>
        """;
}
