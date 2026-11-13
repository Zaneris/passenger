using System.Text;
using Passenger.App.Run;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.UnitTests.Run;

/// <summary>Where an app of devices runs: the targets Android's workload lists (an emulator by its AVD whether or not it
/// runs, a device by its serial), the framework a target's platform builds, a choice found again in a later listing by
/// its key and said once gone, an installed app's adb commands for Run and Debug, the Debug build's environment file
/// with the relay's variables added, and a launch profile tied to one platform taken where that platform is the target
/// rather than making a configuration named for it.</summary>
public class DeviceTargetsTests
{
    private const string Listed = """
        {
          "Properties": { "AdbToolPath": "/sdk/platform-tools/", "EmulatorToolPath": "/sdk/emulator/" },
          "Items": {
            "Devices": [
              { "Identity": "emulator-5554", "Status": "Online", "Type": "Emulator", "AvdName": "pixel", "Description": "Pixel 9" },
              { "Identity": "tablet", "Status": "NotRunning", "Type": "Emulator", "AvdName": "tablet", "Description": "Tablet (Not Running)" },
              { "Identity": "R5CT1234", "Status": "Online", "Type": "Device", "Description": "Galaxy S24" },
              { "Status": "Online", "Type": "Device", "Description": "No identity" }
            ]
          }
        }
        """;

    [Fact]
    public void The_workloads_devices_are_listed_an_emulator_kept_by_its_avd_and_one_not_running_said_so()
    {
        IReadOnlyList<DeviceTarget> targets = DeviceTargets.Parse(TargetPlatform.Android, Listed);

        Assert.Equal(["Pixel 9", "Tablet (not running)", "Galaxy S24"], targets.Select(t => t.Shown));
        Assert.Equal(["pixel", "tablet", "R5CT1234"], targets.Select(t => t.Key));
        Assert.Equal([true, false, true], targets.Select(t => t.Running));
        Assert.Equal([TargetKind.Emulator, TargetKind.Emulator, TargetKind.Device], targets.Select(t => t.Kind));
    }

    [Fact]
    public void An_answer_with_no_devices_lists_none()
    {
        Assert.Empty(DeviceTargets.Parse(TargetPlatform.Android, """{ "Items": { } }"""));
        Assert.Empty(DeviceTargets.Parse(TargetPlatform.Android, """{ "Properties": { } }"""));
    }

    [Fact]
    public void A_listing_that_fails_for_want_of_the_platforms_android_jar_names_the_sdk_package_to_install()
    {
        const string Unix = "/sdk/packs/Microsoft.Android.Sdk.Darwin/37.2.31/tools/Xamarin.Android.Tooling.targets(85,5): error XA5207: Could not find android.jar "
            + "for API level 37.0. This means the Android SDK platform for API level 37.0 is not installed; it was expected to be in "
            + "`/Users/me/Library/Android/sdk/platforms/android-37.0/android.jar`. [/src/App/App.csproj::TargetFramework=net11.0-android]";
        const string Windows = "C:\\dotnet\\packs\\Microsoft.Android.Sdk.Windows\\37.2.31\\tools\\Xamarin.Android.Tooling.targets(85,5): error XA5207: "
            + "Could not find android.jar for API level 36.1. It was expected to be in `C:\\Android\\sdk\\platforms\\android-36.1\\android.jar`.";

        Assert.Equal("platforms;android-37.0", DeviceTargets.AndroidPlatformLacked("{ \"Items\": { } }\n" + Unix + "\r\n"));
        Assert.Equal("platforms;android-36.1", DeviceTargets.AndroidPlatformLacked(Windows));
        Assert.Null(DeviceTargets.AndroidPlatformLacked("error XA5300: The Java SDK directory could not be found."));
    }

    [Fact]
    public void A_targets_platform_decides_the_framework_built()
    {
        string[] maui = ["net11.0-android", "net11.0-ios", "net11.0-maccatalyst", "net11.0-windows10.0.19041.0"];

        Assert.Equal([TargetPlatform.Android, TargetPlatform.Ios, TargetPlatform.MacCatalyst, TargetPlatform.Windows], DeviceTargets.PlatformsOf(maui));
        Assert.Equal("net11.0-ios", DeviceTargets.FrameworkFor(maui, TargetPlatform.Ios));
        Assert.Equal(TargetPlatform.Android, DeviceTargets.PlatformOf("net11.0-android36.0"));
        Assert.Null(DeviceTargets.PlatformOf("net11.0"));
        Assert.Null(DeviceTargets.PlatformOf("net11.0-windows"));
        Assert.True(DeviceTargets.RunsOnlyElsewhere(["net11.0-android"]));
        Assert.False(DeviceTargets.RunsOnlyElsewhere(maui));
        Assert.False(DeviceTargets.RunsOnlyElsewhere(["net11.0"]));
    }

    [Fact]
    public void A_choice_is_found_again_by_its_avd_under_a_new_serial_and_said_once_gone()
    {
        KeptTarget chosen = KeptTarget.Of(new DeviceTarget(TargetPlatform.Android, "emulator-5554", "Pixel 9", TargetKind.Emulator, true) { Avd = "pixel" });
        IReadOnlyList<DeviceTarget> restarted = [new DeviceTarget(TargetPlatform.Android, "emulator-5556", "Pixel 9", TargetKind.Emulator, true) { Avd = "pixel" }];
        IReadOnlyList<DeviceTarget> unplugged = [new DeviceTarget(TargetPlatform.Android, "R5CT1234", "Galaxy S24", TargetKind.Device, true)];

        Assert.Equal("emulator-5556", DeviceTargets.Find(chosen, restarted)?.Id);
        Assert.Null(DeviceTargets.Gone(chosen, restarted, "Shop"));
        Assert.Null(DeviceTargets.Find(chosen, unplugged));
        Assert.Equal("Pixel 9, where Shop was chosen to run, is not connected now: choose another where it runs", DeviceTargets.Gone(chosen, unplugged, "Shop"));
        Assert.Null(DeviceTargets.Gone(null, unplugged, "Shop"));
        Assert.Null(DeviceTargets.Find(chosen with { Platform = TargetPlatform.Ios }, restarted));
    }

    [Fact]
    public void Run_starts_the_app_afresh_and_writes_its_processes_log_until_it_ends()
    {
        AndroidApp app = new("emulator-5554", "/sdk/platform-tools/adb", "com.shop", "crc64.MainActivity", "/src/Shop/obj/Debug/net11.0-android");

        IReadOnlyList<string> run = app.Run();

        Assert.Equal(["-s", "emulator-5554", "shell"], run.Take(3));
        Assert.Equal(4, run.Count);
        Assert.StartsWith("am force-stop com.shop; am start -n com.shop/crc64.MainActivity", run[3], StringComparison.Ordinal);
        Assert.Contains("p=$(pidof com.shop)", run[3], StringComparison.Ordinal);
        Assert.Contains("logcat -v tag --pid=$p", run[3], StringComparison.Ordinal);
        Assert.Contains("while [ -d /proc/$p ]", run[3], StringComparison.Ordinal);
        Assert.Equal(["-s", "emulator-5554", "shell", "am", "force-stop", "com.shop"], app.ForceStop());
        Assert.Equal(["-s", "emulator-5554", "shell", "am", "set-debug-app", "com.shop"], app.SetDebugApp());
        Assert.Equal(["-s", "emulator-5554", "shell", "[ \"$(dumpsys activity processes | grep -c 'mDebugApp=com.shop/')\" != 0 ] && am clear-debug-app; true"],
            app.ClearDebugApp());
    }

    [Fact]
    public void Debug_puts_the_relay_in_the_apps_own_folder_and_takes_it_out_putting_back_the_builds_link()
    {
        AndroidApp app = new("emulator-5554", "adb", "com.shop", "crc64.MainActivity", "/src/Shop/obj/Debug/net11.0-android");

        string put = app.PutRelayIn("arm64-v8a")[^1];
        string taken = app.TakeRelayOut("arm64-v8a", "/data/local/tmp/fastdeploy2/com.shop/0/arm64-v8a/environment")[^1];

        Assert.StartsWith("run-as com.shop sh -c 'cd /data/user/0/com.shop/files/.__override__/arm64-v8a || exit 1; readlink environment; ", put, StringComparison.Ordinal);
        Assert.Contains("cp /data/local/tmp/passenger-com.shop-relay libpassenger-debug.so && rm -f environment && cp /data/local/tmp/passenger-com.shop-environment environment'", put,
            StringComparison.Ordinal);
        Assert.EndsWith("; rm -f /data/local/tmp/passenger-com.shop-relay /data/local/tmp/passenger-com.shop-environment", put, StringComparison.Ordinal);
        Assert.EndsWith("rm -f libpassenger-debug.so environment && ln -s /data/local/tmp/fastdeploy2/com.shop/0/arm64-v8a/environment environment'", taken, StringComparison.Ordinal);
        Assert.EndsWith("rm -f libpassenger-debug.so environment'", app.TakeRelayOut("arm64-v8a", null)[^1], StringComparison.Ordinal);
        Assert.Equal("android-arm64", AndroidApp.RelayRuntime("arm64-v8a"));
        Assert.Equal("android-x64", AndroidApp.RelayRuntime("x86_64"));
        Assert.Null(AndroidApp.RelayRuntime("armeabi-v7a"));
    }

    [Fact]
    public void The_debug_environment_keeps_the_builds_variables_and_adds_the_relays_profiler_and_token()
    {
        AndroidApp app = new("emulator-5554", "adb", "com.shop", "crc64.MainActivity", "/obj");
        byte[] built = AndroidEnvironment.Write([new("mono.enable_assembly_preload", "0"), new(RuntimeVariables.ModifiableAssemblies, "Debug")]);
        Assert.Equal("0x0000001d\0" + "0x00000006\0", Encoding.ASCII.GetString(built, 0, 22));

        IReadOnlyList<KeyValuePair<string, string>> read = AndroidEnvironment.Read(AndroidEnvironment.Write(
            AndroidEnvironment.With(AndroidEnvironment.Read(built), app.RelayVariables("arm64-v8a", "passenger-debug-1a2b", "C0FFEE"))));

        Assert.Equal(["mono.enable_assembly_preload", RuntimeVariables.ModifiableAssemblies, RuntimeVariables.EnableProfiling, RuntimeVariables.Profiler,
            RuntimeVariables.ProfilerPath, AndroidApp.RelaySocketVariable, AndroidApp.RelayTokenVariable, AndroidApp.RelayWaitVariable], read.Select(v => v.Key));
        Assert.Equal("Debug", read.Single(v => v.Key == RuntimeVariables.ModifiableAssemblies).Value);
        Assert.Equal("/data/user/0/com.shop/files/.__override__/arm64-v8a/libpassenger-debug.so", read.Single(v => v.Key == RuntimeVariables.ProfilerPath).Value);
        Assert.Equal("C0FFEE", read.Single(v => v.Key == AndroidApp.RelayTokenVariable).Value);
        Assert.Equal(AndroidApp.RelayProfiler, read.Single(v => v.Key == RuntimeVariables.Profiler).Value);
    }

    [Fact]
    public void A_variable_the_build_already_names_takes_its_new_value_in_its_place()
    {
        IReadOnlyList<KeyValuePair<string, string>> merged = AndroidEnvironment.With([new("A", "1"), new("B", "2")], [new("B", "3"), new("C", "4")]);

        Assert.Equal(["A=1", "B=3", "C=4"], merged.Select(v => $"{v.Key}={v.Value}"));
        Assert.Empty(AndroidEnvironment.Read(new byte[10]));
        Assert.Throws<FormatException>(() => AndroidEnvironment.Read(Encoding.ASCII.GetBytes("0xzzzzzzzz\00x00000001\0AB")));
    }

    [Fact]
    public void The_apps_start_is_read_from_the_workloads_properties_and_one_without_an_activity_is_refused()
    {
        (AndroidApp? app, string? problem) = AndroidApp.Read("""
            { "Properties": { "AndroidLaunchActivity": "crc64.MainActivity", "_AndroidPackage": "com.shop", "_AdbToolPath": "/sdk/platform-tools/adb",
              "IntermediateOutputPath": "obj/Debug/net11.0-android/" } }
            """, "emulator-5554", "/src/Shop");

        Assert.Null(problem);
        Assert.Equal(("com.shop", "crc64.MainActivity", "/sdk/platform-tools/adb"), (app!.Package, app.Activity, app.Adb));
        Assert.Equal(PathNames.Canonical(Path.Combine("/src/Shop", "obj", "Debug", "net11.0-android")), app.Intermediate.TrimEnd('/', '\\'));
        Assert.Equal(Path.Combine(app.Intermediate, "android", "assets", "arm64-v8a"), app.Deployed("arm64-v8a"));

        (AndroidApp? none, string? why) = AndroidApp.Read("""
            { "Properties": { "_AndroidPackage": "com.shop", "_AdbToolPath": "adb", "AndroidInstrumentation": "Tests" } }
            """, "emulator-5554", "/src/Shop");
        Assert.Null(none);
        Assert.Equal("com.shop starts through its instrumentation, which Passenger does not run: give it a launcher activity", why);
    }

    /// <summary>The .NET MAUI template's launch settings: its one profile, for Windows.</summary>
    private const string MauiSettings = """
        {
          "profiles": {
            "Windows Machine": {
              "commandName": "Project",
              "nativeDebugging": false
            }
          }
        }
        """;

    [Fact]
    public void A_profile_running_the_project_and_naming_one_platform_is_tied_to_it()
    {
        TargetPlatform? Tied(string name, string command = "Project") =>
            DeviceTargets.TiedTo(new LaunchProfile(name, command, null, null, null, new Dictionary<string, string>(), null, false, null));

        Assert.Equal(TargetPlatform.Windows, Tied("Windows Machine"));
        Assert.Equal(TargetPlatform.Android, Tied("Android Emulator"));
        Assert.Equal(TargetPlatform.Ios, Tied("iOS Simulator"));
        Assert.Equal(TargetPlatform.MacCatalyst, Tied("Mac Catalyst"));
        Assert.Equal(TargetPlatform.MacCatalyst, Tied("MacCatalyst (debug)"));
        Assert.Null(Tied("http"));
        Assert.Null(Tied("WindowsAuthentication"));
        Assert.Null(Tied("Windows and Android"));
        Assert.Null(Tied("Windows Machine", "Executable"));
    }

    [Fact]
    public void The_maui_templates_windows_profile_makes_no_configuration_of_its_own_so_its_one_is_named_for_the_project()
    {
        IReadOnlyList<LaunchProfile> profiles = LaunchProfile.Parse(MauiSettings);
        IReadOnlyList<LaunchProfile> withHttp = LaunchProfile.Parse(MauiSettings.Replace("\"profiles\": {", "\"profiles\": {\n\"http\": { \"commandName\": \"Project\" },", StringComparison.Ordinal));
        string[] Own(string[] frameworks, IReadOnlyList<LaunchProfile> read) => [.. DeviceTargets.OwnProfiles(frameworks, read).Select(p => p.Name)];

        Assert.Empty(Own(["net11.0-android", "net11.0-ios", "net11.0-maccatalyst"], profiles));
        Assert.Empty(Own(["net11.0-android"], profiles));
        Assert.Empty(Own(["net11.0-android", "net11.0-windows10.0.19041.0"], profiles));
        Assert.Equal(["http"], Own(["net11.0-android", "net11.0-windows10.0.19041.0"], withHttp));
        // A desktop project has no target to tie a profile to.
        Assert.Equal(["Windows Machine"], Own(["net11.0-windows"], profiles));
        Assert.Equal(["Windows Machine"], Own(["net11.0"], profiles));
    }

    [Fact]
    public void Where_its_platform_is_the_target_the_profile_tied_to_it_is_found()
    {
        IReadOnlyList<LaunchProfile> profiles = LaunchProfile.Parse("""
            {
              "profiles": {
                "http": { "commandName": "Project" },
                "Windows Machine": { "commandName": "Project", "commandLineArgs": "--tied" }
              }
            }
            """);

        Assert.Null(DeviceTargets.ProfileFor(profiles, TargetPlatform.Android));
        Assert.Equal("Windows Machine", DeviceTargets.ProfileFor(profiles, TargetPlatform.Windows)?.Name);
        Assert.Equal(TargetKind.ThisMachine, DeviceTargets.Here(TargetPlatform.Windows).Kind);
    }

    [Fact]
    public void An_android_app_is_runnable_though_its_sdk_makes_its_output_a_library()
    {
        ProjectModel Evaluated(string output, string? android) => new("/src/Shop/Shop.csproj", "Shop", output, ["net11.0-android"], [], [], true)
        {
            Evaluated = new EvaluatedProject
            {
                Path = "/src/Shop/Shop.csproj",
                Properties = android is null ? new Dictionary<string, EvaluatedProperty>()
                    : new Dictionary<string, EvaluatedProperty> { [CarriedProperties.AndroidApplication] = new(android, false) },
            },
        };

        Assert.True(Evaluated("Library", "True").IsRunnable);
        Assert.False(Evaluated("Library", null).IsRunnable);
        Assert.False(Evaluated("Library", "false").IsRunnable);
    }
}
