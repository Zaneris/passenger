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
/// own profile on this machine, as it does with nothing chosen, where elsewhere a target must be chosen. Plans are worked
/// out with no build, so no emulator is needed, but MAUI's workloads are: without them it skips, saying so.</summary>
public sealed class MauiRunTargetTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(2);

    [AvaloniaFact]
    public void The_maui_templates_configuration_is_named_for_its_project_and_the_target_chosen_says_where_it_runs()
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
            Ui.Await(window.MobileAsked(), "what the app's heads need to be asked", Slow);
            if (window.MobileWant.Workloads.Count > 0)
            {
                Assert.Skip($"This .NET lacks the workloads {string.Join(", ", window.MobileWant.Workloads)}, without which MSBuild cannot say what the app's heads build");
            }

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
            else
            {
                Assert.Null(unchosen);
                Assert.Equal(Refusals.NoRunTarget("MauiApp1"), refused);
            }

            window.ChooseRunTarget(app, new DeviceTarget(TargetPlatform.Android, "emulator-5554", "Pixel 9", TargetKind.Emulator, true) { Avd = "Pixel_9" });
            Ui.Settle();
            LaunchPlan android = Plan(window);
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
