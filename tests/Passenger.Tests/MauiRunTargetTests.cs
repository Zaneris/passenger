using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Run;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>The .NET MAUI template's app in the window: its run configuration is named for the project, never for the
/// template's Windows profile, and the target chosen beside it says where it runs. An Android emulator's plan builds
/// the app's Android head with no Windows profile; on Windows, Windows chosen from the widget's menu runs the template's
/// own profile on this machine, as it does with nothing chosen; on a Mac, nothing chosen runs its Mac Catalyst head on
/// this machine, with no Windows profile; on Linux a target must be chosen. An app's Avalonia desktop head runs on this
/// machine with nothing chosen, on every system. Plans are worked out with no build, so no emulator is needed; where
/// MSBuild cannot say what a head builds for want of MAUI's workloads, it skips there, saying so.</summary>
[AboutMacOS]
public sealed class MauiRunTargetTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(2);

    /// <summary>Where a mobile app runs: the target chosen beside its configuration says it, a desktop head runs here with
    /// nothing chosen, and a device chosen outlasts the folder's state written again (<see cref="DeviceChoicesKeptTests"/>).</summary>
    [AvaloniaFact]
    public void Where_a_mobile_app_runs_is_chosen_beside_its_configuration_said_and_kept_with_the_folder()
    {
        Flow.Step("a device chosen for a project outlasts the folder's state written again and is found by the next window",
            DeviceChoicesKeptTests.A_device_chosen_for_a_project_outlasts_the_folders_state_written_again_and_is_found_by_the_next_window);
        Flow.Step("the MAUI template's configuration is named for its project and the target chosen says where it runs",
            The_maui_templates_configuration_is_named_for_its_project_and_the_target_chosen_says_where_it_runs);
        Flow.Step("a MAUI app's Avalonia desktop head runs on this machine with nothing chosen, and again once chosen after a device",
            A_maui_apps_avalonia_desktop_head_runs_on_this_machine_with_nothing_chosen_and_again_once_chosen_after_a_device);
    }

    private static void The_maui_templates_configuration_is_named_for_its_project_and_the_target_chosen_says_where_it_runs()
    {
        string folder = PathNames.Canonical(TestFolders.Create("maui-run-"));
        TestFolders.Write(folder, "MauiApp1/MauiApp1.csproj", Project);
        TestFolders.Write(folder, "MauiApp1/Properties/launchSettings.json", Settings);
        TestFolders.Write(folder, "MauiApp1/MauiProgram.cs", "namespace MauiApp1;\n\npublic static class MauiProgram\n{\n}\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Session.Projects.Any(p => p.IsEvaluated) && window.Configurations.Count > 0,
                () => $"the app's project evaluated and its configurations made; it has {string.Join(", ", window.Configurations.Select(c => c.Name))}", Slow);
            Assert.Equal(["MauiApp1"], window.Configurations.Select(c => c.Name));
            Assert.Equal("MauiApp1", window.Session.Configurations.Chosen?.Name);
            ProjectModel app = window.Session.Projects.Single();

            // With nothing chosen, Windows runs its own head here; elsewhere a target must be chosen.
            (LaunchPlan? unchosen, string? refused) = Ui.Await(window.Builds.ResolvePlanAsync(window.Session.Configurations.Chosen!, null), "the app's plan", Slow);
            if (OperatingSystem.IsWindows())
            {
                Assert.True(unchosen is not null, $"MauiApp1 with no target chosen was refused on Windows: {refused}");
                Assert.Null(unchosen.Device);
                Assert.Equal("net11.0-windows10.0.19041.0", unchosen.Framework);
                Assert.Equal("Windows Machine", unchosen.Environment.GetValueOrDefault(LaunchProfile.NameVariable));
                Assert.Equal(DeviceTargets.Here(TargetPlatform.Windows).Shown, window.FindControl<TextBlock>("RunTargetName")!.Text);
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (unchosen is null)
                {
                    SkipWithoutWorkloads(window, refused);
                }
                LaunchPlan mac = unchosen ?? throw new Xunit.Sdk.XunitException($"MauiApp1 with no target chosen was refused on a Mac: {refused}");
                Assert.Null(mac.Device);
                Assert.Equal("net11.0-maccatalyst", mac.Framework);
                Assert.Equal("net11.0-maccatalyst", mac.MacCatalyst?.Framework);
                Assert.False(mac.Environment.ContainsKey(LaunchProfile.NameVariable),
                    $"The Mac Catalyst head's run took the launch profile {mac.Environment.GetValueOrDefault(LaunchProfile.NameVariable)}, the template's Windows one");
                Assert.Equal(DeviceTargets.Here(TargetPlatform.MacCatalyst).Shown, window.FindControl<TextBlock>("RunTargetName")!.Text);
            }
            else
            {
                Assert.Null(unchosen);
                Assert.Equal(Refusals.NoRunTarget("MauiApp1").Shown, refused);
            }

            window.ChooseRunTarget(app, new DeviceTarget(TargetPlatform.Android, "emulator-5554", "Pixel 9", TargetKind.Emulator, true) { Avd = "Pixel_9" });
            Ui.Settle();
            (LaunchPlan? planned, string? unplanned) = Ui.Await(window.Builds.ResolvePlanAsync(window.Session.Configurations.Chosen!, null), "the app's plan", Slow);
            if (planned is null)
            {
                SkipWithoutWorkloads(window, unplanned);
            }
            LaunchPlan android = planned ?? throw new Xunit.Sdk.XunitException($"MauiApp1 has no plan for the Android emulator: {unplanned}");
            Assert.Equal("MauiApp1", android.Name);
            Assert.Equal("Pixel 9", android.Device?.Target.Name);
            Assert.Equal("net11.0-android", android.Framework);
            Assert.False(android.Environment.ContainsKey(LaunchProfile.NameVariable),
                $"The Android emulator's run took the launch profile {android.Environment.GetValueOrDefault(LaunchProfile.NameVariable)}, the template's Windows one");

            if (!OperatingSystem.IsWindows())
            {
                return;
            }
            Ui.Click(window.FindControl<Button>("RunTargetButton")!);
            Ui.WaitFor(() => window.OpenContextMenu is not null, "the menu of where the app runs", Slow);
            Ui.Choose(window.OpenContextMenu, DeviceTargets.Here(TargetPlatform.Windows).Shown);
            LaunchPlan windows = Plan(window);
            Assert.Equal("MauiApp1", windows.Name);
            Assert.Null(windows.Device);
            Assert.Equal("net11.0-windows10.0.19041.0", windows.Framework);
            Assert.Equal("Windows Machine", windows.Environment.GetValueOrDefault(LaunchProfile.NameVariable));
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>A MAUI app with an Avalonia desktop head (<c>net11.0</c> carrying Avalonia.Controls.Maui.Desktop) and an Android
    /// head runs its desktop head on this machine with nothing chosen, on every system, Linux too, where the template's app
    /// waits for a device: the widget names it, and chosen again after an Android emulator, it runs there again.</summary>
    private static void A_maui_apps_avalonia_desktop_head_runs_on_this_machine_with_nothing_chosen_and_again_once_chosen_after_a_device()
    {
        string folder = PathNames.Canonical(TestFolders.Create("maui-desktop-"));
        TestFolders.Write(folder, "MauiApp1/MauiApp1.csproj", DesktopProject);
        TestFolders.Write(folder, "MauiApp1/MauiProgram.cs", "namespace MauiApp1;\n\npublic static class MauiProgram\n{\n}\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Session.Projects.Any(p => p.IsEvaluated && p.Evaluated!.Dependencies.Count > 1) && window.Configurations.Count > 0,
                () => $"the app's project evaluated and its configurations made; it has {string.Join(", ", window.Configurations.Select(c => c.Name))}", Slow);
            ProjectModel app = window.Session.Projects.Single();
            DeviceTarget desktop = DeviceTargets.Here(TargetPlatform.Desktop);

            (LaunchPlan? unchosen, string? refused) = Ui.Await(window.Builds.ResolvePlanAsync(window.Session.Configurations.Chosen!, null), "the app's plan", Slow);
            if (unchosen is null)
            {
                SkipWithoutWorkloads(window, refused);
            }
            LaunchPlan here = unchosen ?? throw new Xunit.Sdk.XunitException($"MauiApp1 with no target chosen was refused, though its desktop head runs here: {refused}");
            Assert.Null(here.Device);
            Assert.Null(here.MacCatalyst);
            Assert.Equal("net11.0", here.Framework);
            Assert.Equal(DesktopPrograms.Avalonia, here.DesktopProject);
            Assert.Equal(desktop.Shown, window.FindControl<TextBlock>("RunTargetName")!.Text);

            window.ChooseRunTarget(app, new DeviceTarget(TargetPlatform.Android, "emulator-5554", "Pixel 9", TargetKind.Emulator, true) { Avd = "Pixel_9" });
            Ui.Settle();
            Assert.Equal("net11.0-android", Plan(window).Framework);

            window.ChooseRunTarget(app, desktop);
            Ui.Settle();
            LaunchPlan again = Plan(window);
            Assert.Null(again.Device);
            Assert.Equal("net11.0", again.Framework);
            Assert.Equal(desktop.Shown, window.FindControl<TextBlock>("RunTargetName")!.Text);
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Skips, saying which, where this .NET lacks a workload of the app's heads, without which MSBuild cannot say what
    /// a head builds (<paramref name="unplanned"/>).</summary>
    private static void SkipWithoutWorkloads(MainWindow window, string? unplanned)
    {
        Ui.Await(window.MobileAsked(), "what the app's heads need to be asked", Slow);
        if (window.MobileWant.Workloads.Count > 0)
        {
            Assert.Skip($"This .NET lacks the workloads {string.Join(", ", window.MobileWant.Workloads)}, without which MSBuild cannot say what the app's heads build ({unplanned})");
        }
    }

    /// <summary>The chosen configuration's plan, as Run and Debug work it out before their build.</summary>
    private static LaunchPlan Plan(MainWindow window)
    {
        (LaunchPlan? plan, string? problem) = Ui.Await(window.Builds.ResolvePlanAsync(window.Session.Configurations.Chosen!, null), "the app's plan", Slow);
        return plan ?? throw new Xunit.Sdk.XunitException($"MauiApp1 has no plan: {problem}");
    }

    /// <summary>The template's project as <c>dotnet new maui</c> writes it, its frameworks those of the system it is on.</summary>
    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFrameworks>net11.0-android</TargetFrameworks>
            <TargetFrameworks Condition="!$([MSBuild]::IsOSPlatform('linux'))">$(TargetFrameworks);net11.0-ios;net11.0-maccatalyst</TargetFrameworks>
            <TargetFrameworks Condition="$([MSBuild]::IsOSPlatform('windows'))">$(TargetFrameworks);net11.0-windows10.0.19041.0</TargetFrameworks>
            <OutputType>Exe</OutputType>
            <RootNamespace>MauiApp1</RootNamespace>
            <UseMaui>true</UseMaui>
            <SingleProject>true</SingleProject>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <ApplicationTitle>MauiApp1</ApplicationTitle>
            <ApplicationId>com.companyname.mauiapp1</ApplicationId>
            <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
            <ApplicationVersion>1</ApplicationVersion>
            <WindowsPackageType>None</WindowsPackageType>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.Maui.Controls" Version="$(MauiVersion)" />
          </ItemGroup>
        </Project>
        """;

    /// <summary>A MAUI app of an Avalonia desktop head beside its Android head, as Avalonia.Controls.Maui's template writes
    /// one, its frameworks the same on every system.</summary>
    private const string DesktopProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFrameworks>net11.0;net11.0-android</TargetFrameworks>
            <OutputType>Exe</OutputType>
            <RootNamespace>MauiApp1</RootNamespace>
            <UseMaui>true</UseMaui>
            <SingleProject>true</SingleProject>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <ApplicationId>com.companyname.mauiapp1</ApplicationId>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.Maui.Controls" Version="$(MauiVersion)" />
            <PackageReference Include="Avalonia.Controls.Maui" Version="11.0.0-rc.3.26301.1434" />
            <PackageReference Include="Avalonia.Controls.Maui.Desktop" Version="11.0.0-rc.3.26301.1434" Condition="'$(TargetFramework)' == 'net11.0'" />
          </ItemGroup>
        </Project>
        """;

    /// <summary>The template's launch settings: its one profile, for Windows.</summary>
    private const string Settings = """
        {
          "profiles": {
            "Windows Machine": {
              "commandName": "Project",
              "nativeDebugging": false
            }
          }
        }
        """;
}
