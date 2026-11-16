using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Choose Where It Runs for an app with an Android head where no Android device is connected: with no emulator
/// made, Android has a line saying so and how to get one, beside this machine's head on a Mac, and an agent asking where
/// the app runs is told the same; an emulator made then (an AVD of an installed system image) is listed by Look Again, not
/// running, the line gone, and chosen to be started by Run and Debug. The AVDs are the test's own, in the folder
/// ANDROID_AVD_HOME names, and none is booted. Skips, saying why, without the Android workload, its SDK and a JDK, with a
/// device connected, or with no system image for this machine's processor. It sets the process's ANDROID_AVD_HOME, so it
/// runs alone.</summary>
[Collection(Alone.Name)]
public sealed class AndroidTargetChoiceTests : IDisposable
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(4);

    /// <summary>The variable the Android SDK's tools read AVDs from in place of the user's own folder.</summary>
    private const string AvdHome = "ANDROID_AVD_HOME";

    /// <summary>The AVD the test makes, by its name and as the workload describes it.</summary>
    private const string Avd = "Passenger_Listing";

    private const string AvdShown = "Passenger Listing";

    private readonly string folder = PathNames.Canonical(TestFolders.Create("android-targets-"));
    private readonly string? avdHome = Environment.GetEnvironmentVariable(AvdHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AvdHome, avdHome);
        TestFolders.Delete(folder);
    }

    [AvaloniaFact]
    public void With_no_device_or_emulator_android_has_a_line_saying_how_to_get_one_and_an_emulator_made_then_is_listed_to_start()
    {
        string avds = Directory.CreateDirectory(Path.Combine(folder, "avd")).FullName;
        Environment.SetEnvironmentVariable(AvdHome, avds);
        string project = PathNames.Canonical(TestFolders.Write(folder, "Droid/Droid.csproj", Project));
        (IReadOnlyList<DeviceTarget> connected, AndroidTools? tools, string? unlisted) = Ui.Await(Waiting.Apart(() =>
            AndroidDevices.ListAsync(project, MauiApp.Android, new Dictionary<string, string>(), CancellationToken.None)), "the Android workload's devices", Slow);
        if (tools is null)
        {
            Assert.Skip($"Android's devices cannot be listed here: {unlisted}");
        }
        if (tools.Lacks is { } lacks)
        {
            Assert.Skip($"No Android app can be built here: {Refusals.NoAndroidPlatform(lacks)}");
        }
        if (connected.Count > 0)
        {
            Assert.Skip($"Android has somewhere to run here, {Plural.Listed([.. connected.Select(t => t.Shown)])}, so no line says it has none");
        }
        string abi = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64-v8a" : "x86_64";
        // The workload names the emulator in the SDK's emulator folder.
        string sdk = Path.GetDirectoryName(Path.GetDirectoryName(tools.Emulator))!;
        string? image = SystemImage(sdk, abi);
        if (image is null)
        {
            Assert.Skip($"The Android SDK in {sdk} has no {abi} system image, of which an emulator is made");
        }

        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Session.Projects.Any(p => p.IsEvaluated) && window.Configurations.Count > 0,
                () => $"the app's project evaluated and its configuration made; it has {string.Join(", ", window.Configurations.Select(c => c.Name))}: {Ui.Said(window)}", Slow);
            string lacking = Refusals.NoAndroidTarget(tools.AvdManager);
            string? here = OperatingSystem.IsMacOS() ? DeviceTargets.Here(TargetPlatform.MacCatalyst).Shown : null;

            Ui.Click(window.FindControl<Button>("RunTargetButton")!);
            Ui.WaitFor(() => window.OpenContextMenu is not null, () => $"the menu of where the app runs: {Ui.Said(window)}", Slow);
            IReadOnlyList<string> offered = Offered(window.OpenContextMenu!);
            Assert.True(offered.Contains(lacking), $"The menu says nothing of what Android lacks where no device is connected and no emulator is made: {string.Join(" | ", offered)}");
            if (here is not null)
            {
                Assert.Contains(here, offered);
            }
            window.OpenContextMenu!.Close();
            string told = Ui.Await(new PassengerTools(window).IdeAction(AppActions.ChooseRunTarget), "an agent's question of where the app runs", Slow);
            Assert.True(told.Contains(lacking, StringComparison.Ordinal), $"The agent was told nothing of what Android lacks: {told}");

            MakeAvd(avds, sdk, image, abi);
            string shown = new DeviceTarget(TargetPlatform.Android, Avd, AvdShown, TargetKind.Emulator, Running: false).Shown;
            Ui.Click(window.FindControl<Button>("RunTargetButton")!);
            Ui.WaitFor(() => window.OpenContextMenu is not null, () => $"the menu of where the app runs, again: {Ui.Said(window)}", Slow);
            Ui.Choose(window.OpenContextMenu, "Look Again");
            Ui.WaitFor(() => window.OpenContextMenu is { } menu && Offered(menu).Contains(shown),
                () => $"Look Again to list the emulator made; the menu has {(window.OpenContextMenu is { } menu ? string.Join(" | ", Offered(menu)) : "closed")}: {Ui.Said(window)}", Slow);
            offered = Offered(window.OpenContextMenu!);
            Assert.False(offered.Contains(lacking), $"The menu still says Android has no emulator, where it lists {shown}: {string.Join(" | ", offered)}");
            if (here is not null)
            {
                Assert.Contains(here, offered);
            }

            Ui.Choose(window.OpenContextMenu, shown);
            ProjectModel app = window.Session.Projects.Single();
            Assert.Equal(Avd, window.Session.Targets.Of(app)?.Key);
            Assert.Equal($"Droid runs on {AvdShown}, which Run and Debug start", window.Status);
            Assert.Equal(shown, window.FindControl<TextBlock>("RunTargetName")!.Text);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The words of a menu's items, a line of text's too.</summary>
    private static IReadOnlyList<string> Offered(ContextMenu menu) =>
        [.. menu.Items.OfType<MenuItem>().Select(m => m.Header switch
        {
            string words => words,
            TextBlock block => block.Text ?? string.Empty,
            _ => string.Empty,
        })];

    /// <summary>A system image of the SDK's for the ABI, as its folder (system-images/android-36.1/google_apis/arm64-v8a);
    /// null where the SDK has none.</summary>
    private static string? SystemImage(string sdk, string abi)
    {
        string images = Path.Combine(sdk, "system-images");
        return Directory.Exists(images)
            ? Directory.EnumerateDirectories(images).SelectMany(Directory.EnumerateDirectories).Select(tag => Path.Combine(tag, abi))
                .FirstOrDefault(f => File.Exists(Path.Combine(f, "system.img")))
            : null;
    }

    /// <summary>An AVD of the system image in <paramref name="avds"/>, as the SDK's tools write one: its .ini naming its
    /// folder, and the folder's config.ini naming the image and the AVD's name as the workload describes it.</summary>
    private static void MakeAvd(string avds, string sdk, string image, string abi)
    {
        string platform = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(image)))!;
        string tag = Path.GetFileName(Path.GetDirectoryName(image))!;
        string made = Path.Combine(avds, $"{Avd}.avd");
        TestFolders.Write(avds, $"{Avd}.avd/config.ini", $"""
            AvdId={Avd}
            avd.ini.displayname={AvdShown}
            abi.type={abi}
            hw.cpu.arch={(abi == "arm64-v8a" ? "arm64" : "x86_64")}
            image.sysdir.1={PathNames.Relative(image, sdk)}/
            tag.id={tag}
            target={platform}

            """);
        TestFolders.Write(avds, $"{Avd}.ini", $"""
            avd.ini.encoding=UTF-8
            path={made}
            target={platform}

            """);
    }

    /// <summary>An app with an Android head and, on a Mac, a Mac Catalyst head this machine runs.</summary>
    private static string Project => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFrameworks>{MauiApp.Android}</TargetFrameworks>
            <TargetFrameworks Condition="$([MSBuild]::IsOSPlatform('osx'))">$(TargetFrameworks);{MauiApp.Android.Replace("-android", "-maccatalyst", StringComparison.Ordinal)}</TargetFrameworks>
            <OutputType>Exe</OutputType>
            <ApplicationId>net.passengeride.tests.droid</ApplicationId>
          </PropertyGroup>
        </Project>
        """;
}
