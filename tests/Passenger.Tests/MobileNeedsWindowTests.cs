using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Mobile;
using Passenger.App.Templates;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>What a mobile project needs, said over the window as it opens and got from there: a project of a platform
/// whose workload the .NET lacks (tvOS on a Mac, Android elsewhere) says so, and Install Workloads asks, runs the install
/// (a stand-in of dotnet's) and loads the project again; an Android project whose workload's answers say it lacks its
/// SDK's platform and a JDK (stand-ins of the workload's targets in the project) offers the workload's installer, which
/// runs only once the person accepted the licences Google's repository (a stand-in) gives, and the banner goes once nothing
/// lacks; on a Mac an iOS project whose Xcode has no simulator runtime of its SDK says so with how Xcode installs one, and
/// one whose Xcode the workload refuses offers Get Xcode; New Project offers each mobile template a missing workload
/// brings, named by it. It sets a process-wide stand-in, ANDROID_HOME and DEVELOPER_DIR, so it runs alone.</summary>
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

    public void Dispose()
    {
        MainWindow.WorkloadInstallStandIn = null;
        Environment.SetEnvironmentVariable("ANDROID_HOME", androidHome);
        Environment.SetEnvironmentVariable(AndroidLicences.Variable, repository);
        Environment.SetEnvironmentVariable("DEVELOPER_DIR", developerDir);
        TestFolders.Delete(folder);
    }

    [AvaloniaFact]
    public void A_project_whose_workload_is_missing_says_so_and_Install_Workloads_asks_installs_and_loads_it_again()
    {
        // A platform this system builds, whose workload neither this Mac nor CI's runners have: tvOS builds only on a Mac.
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
            Assert.Skip($"This .NET has the {platform} workload, so nothing is lacking to say");
        }
        Assert.Equal([platform], window.MobileWant.Workloads);
        Assert.Equal($"{name} needs the .NET workload {platform}", window.MobileSaid);

        List<IReadOnlyList<string>> installed = [];
        MainWindow.WorkloadInstallStandIn = ids =>
        {
            installed.Add(ids);
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
        Assert.Equal([platform], Assert.Single(installed));
        Assert.True(window.Session.Models.Rounds > rounds, "The project was not evaluated again after the install");
        // The stand-in installs nothing, so the project still lacks it, and says so.
        Assert.Equal($"dotnet workload install ended, but the projects still lack {platform}", window.Status);
        Assert.Equal($"{name} needs the .NET workload {platform}", window.MobileSaid);
        window.Close();
    }

    [AvaloniaFact]
    public void An_Android_project_lacking_its_platform_and_a_JDK_installs_them_only_once_the_person_accepted_the_SDKs_licences()
    {
        string sdk = Directory.CreateDirectory(Path.Combine(folder, "android-sdk")).FullName;
        Directory.CreateDirectory(Path.Combine(sdk, "platform-tools"));
        Environment.SetEnvironmentVariable("ANDROID_HOME", sdk);
        using StandInHttp google = new(exchange => exchange.Path == "/repository2-3.xml"
            ? exchange.ReplyAsync(200, "application/xml", Repository)
            : exchange.ReplyAsync(404, "text/plain", "not here"));
        Environment.SetEnvironmentVariable(AndroidLicences.Variable, google.Root + "/repository2-3.xml");
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
        Assert.Equal("AcceptAndroidSDKLicenses=True", File.ReadAllText(Path.Combine(sdk, "installed.txt")).Trim());
        Assert.True(window.MobileSaid is null && !window.MobileWant.AndroidLacks, $"Once installed, the banner still says: {window.MobileSaid}");
        Assert.StartsWith("platforms;android-37.0 and Microsoft's build of OpenJDK 21.0.8", window.Status, StringComparison.Ordinal);
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
            <WriteLinesToFile File="$(AndroidSdkDirectory)/installed.txt" Lines="AcceptAndroidSDKLicenses=$(AcceptAndroidSDKLicenses)" Overwrite="true" />
            <MakeDir Directories="$(AndroidSdkDirectory)/platforms/android-37.0" />
            <Touch Files="$(AndroidSdkDirectory)/platforms/android-37.0/android.jar;$(AndroidSdkDirectory)/jdk-installed" AlwaysCreate="true" />
          </Target>
        </Project>
        """;
}
