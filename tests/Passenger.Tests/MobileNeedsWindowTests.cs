using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Mobile;
using Passenger.App.Templates;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>What a mobile project needs, said over the window as it opens and got from there: a project whose workload the
/// .NET lacks (tvOS on a Mac, Android elsewhere) says so, and Install Workloads asks, runs the install (a stand-in of
/// dotnet's), a workload set first where the .NET has none, saying a download out of room plainly, and loads the project
/// again; an Android project whose workload's answers (stand-ins of its targets) say it lacks its SDK's platform and a JDK
/// offers the workload's installer, run only once the person accepted the licences Google's repository (a stand-in)
/// gives; the JDK it installs is found after with no JAVA_HOME, and a JAVA_HOME where the workload finds no JDK is said and
/// never given to the installer; on a Mac an iOS project lacking its simulator runtime says how Xcode installs one, and one
/// whose Xcode the workload refuses offers Get Xcode; New Project offers each mobile template a missing workload brings.
/// It sets a process-wide stand-in and the environment (ANDROID_HOME, DEVELOPER_DIR, HOME, JAVA_HOME, TMPDIR), so it runs
/// alone.</summary>
[Collection(Alone.Name)]
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

    [AvaloniaFact]
    public void A_project_whose_workload_is_missing_says_so_and_Install_Workloads_asks_installs_and_loads_it_again()
    {
        (MainWindow window, string name, string platform, string said) = LackingWorkload();

        List<IReadOnlyList<string>> installed = [];
        MainWindow.WorkloadStandIn = arguments =>
        {
            installed.Add(arguments);
            return ChildProcesses.DotnetCommand(["--version"]);
        };
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

    /// <summary>A .NET with no workload set installed knows no workload until one is (dotnet's own words, from a stand-in):
    /// Install Workloads installs its newest first, then the workload, whose download running out of room is said plainly
    /// with the folder it ran out in. Where the temporary folder is held in memory (Linux's /dev/shm stands for a tmpfs
    /// /tmp), every dotnet it runs downloads into a folder of Passenger's own cache, taken away after; elsewhere into the
    /// temporary folder.</summary>
    [AvaloniaFact]
    public void Install_Workloads_installs_a_workload_set_first_where_the_dotnet_has_none_and_says_a_download_out_of_room_plainly()
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
        MainWindow.WorkloadStandIn = arguments =>
        {
            int round = asked.Count;
            asked.Add(arguments);
            System.Diagnostics.ProcessStartInfo start = Saying($"round-{round}", said[Math.Min(round, said.Length - 1)], round == 1 ? 0 : 1);
            starts.Add(start);
            return start;
        };
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

    /// <summary>Skips a test of the Android SDK's install where this machine builds no Android app, which offers none.</summary>
    private void SkipWhereAndroidIsNotBuilt() =>
        Assert.SkipWhen(AndroidHosts.Unbuilt(folder) is not null, $"{AndroidHosts.Unbuilt(folder)} builds no Android app, so nothing of the Android SDK is offered there");

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
        string said = AndroidHosts.Unbuilt(folder) is { } host && platform == MobilePlatforms.Android
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

    [AvaloniaFact]
    public void An_Android_project_lacking_its_platform_and_a_JDK_installs_them_only_once_the_person_accepted_the_SDKs_licences()
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
        Assert.Null(window.Actions.WhyNot(AppActions.InstallAndroidDependencies, window.Actions.Context()));
        Assert.Equal(Refusals.AgentInstallsMobile, window.Actions.WhyNot(AppActions.InstallAndroidDependencies, window.Actions.Context().ForAgent()));

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

    [AvaloniaFact]
    public void Install_Android_SDK_and_Java_puts_the_JDK_where_a_window_opened_after_finds_it_with_no_JAVA_HOME()
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

    [AvaloniaFact]
    public void A_JAVA_HOME_where_the_Android_workload_finds_no_JDK_is_said_and_never_given_to_its_installer_to_install_over()
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

    [AvaloniaFact]
    public void An_iOS_project_whose_Xcode_has_no_simulator_runtime_of_its_SDK_says_so_with_how_Xcode_installs_one()
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

    [AvaloniaFact]
    public void The_iOS_simulator_runtime_this_Macs_own_Xcode_lacks_is_said_as_its_simctl_lists_them()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Xcode and its simulator runtimes are asked of only on a Mac");
        }
        string? xcode = Directory.Exists("/Applications")
            ? Directory.EnumerateDirectories("/Applications", "Xcode*.app").Order(StringComparer.Ordinal).Select(x => Path.Combine(x, "Contents", "Developer")).FirstOrDefault(Directory.Exists)
            : null;
        if (xcode is null)
        {
            Assert.Skip("This Mac has no Xcode in Applications to ask of its simulator runtimes");
        }
        Environment.SetEnvironmentVariable("DEVELOPER_DIR", xcode);
        // The workload's own check, its Xcode version passed over, so a Mac's Xcode of another version is asked too.
        File.WriteAllText(Path.Combine(folder, "Ios.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{Mobile}-ios</TargetFramework>
                <OutputType>Exe</OutputType>
                <ValidateXcodeVersion>false</ValidateXcodeVersion>
              </PropertyGroup>
            </Project>
            """);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.WaitFor(() => window.Projects.Any(p => p.IsEvaluated), "the project to evaluate", Slow);
        Ui.Await(window.MobileAsked(), "what the project needs to be asked", Slow);
        if (window.MobileWant.Workloads.Count > 0)
        {
            Assert.Skip($"This .NET lacks the ios workload, so its Xcode is not asked of: {window.MobileSaid}");
        }
        Assert.True(window.MobileWant.Xcode is null, $"The workload refused {xcode}: {window.MobileWant.Xcode}");
        if (window.MobileWant.Simulators.Count == 0)
        {
            Assert.Skip($"{xcode} has an iOS simulator runtime of its SDK's version, so nothing lacks");
        }
        Assert.Matches(@"^Ios needs the iOS \d+\.\d+ simulator runtime, which Xcode installs \(Xcode › Settings › Components, or xcodebuild -downloadPlatform iOS\)$",
            window.MobileSaid);
        window.Close();
    }

    [AvaloniaFact]
    public void An_iOS_project_whose_Xcode_the_workload_refuses_says_why_and_Get_Xcode_opens_where_Xcode_comes_from()
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

    [Fact]
    public void New_Project_offers_each_mobile_template_a_missing_workload_brings_naming_the_workload_that_installs_it()
    {
        List<ProjectTemplate> offered = [.. TemplatePacks.Offered([])];
        string id = OperatingSystem.IsLinux() ? "maui-android" : "maui";
        ProjectTemplate maui = offered.Single(t => t.ShortNames.Contains("maui"));
        Assert.True(maui.Pack is { IsWorkload: true } pack && pack.Id == id, $"The MAUI app is offered with {maui.Pack?.Id}");
        Assert.Equal($"the .NET {id} workload", maui.Pack!.Named);
        Assert.Equal($"dotnet workload install {id}", maui.Pack.Command);
        Assert.Equal(["workload", "install", id], maui.Pack.Install);
        Assert.Equal("android", offered.Single(t => t.ShortNames.Contains("android")).Pack!.Id);
        Assert.Equal(!OperatingSystem.IsLinux(), offered.Any(t => t.ShortNames.Contains("ios") && t.Pack!.Id == "ios"));
        Assert.Equal("Avalonia.Templates", offered.Single(t => t.ShortNames.Contains("avalonia.xplat")).Pack!.Command["dotnet new install ".Length..]);

        List<ProjectTemplate> again = [.. TemplatePacks.Offered([maui with { Pack = null }])];
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
