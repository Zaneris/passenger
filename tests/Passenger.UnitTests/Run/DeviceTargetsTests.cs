using System.Text;
using Passenger.App.Run;
using Passenger.App.Run.Devices;
using Passenger.App.Testing;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.UnitTests.Run;

/// <summary>Where an app of devices runs: the targets Android's workload lists (an emulator by its AVD whether or not
/// it runs, a device by its serial), the framework a target's platform builds, an installed app's adb commands for Run
/// and Debug, the Debug build's environment file with the relay's variables added and put back as the build wrote it,
/// a device test project's instrumentation run and what it tells of each test, and a launch profile tied to one platform.</summary>
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

    /// <summary>What MSBuild answered a question, as it wrote it on its output.</summary>
    private static MSBuildAnswer Answer(string output) => new(new ChildResult(0, output, string.Empty));

    [Fact]
    public void Android_devices_are_listed_from_the_workload_and_an_app_run_and_debugged_on_one_leaves_its_build_as_it_was() => Cases.All(
        ("the workloads devices are listed an emulator kept by its avd and one not running said so", The_workloads_devices_are_listed_an_emulator_kept_by_its_avd_and_one_not_running_said_so),
        ("Run starts the app afresh and writes its processes log until it ends", Run_starts_the_app_afresh_and_writes_its_processes_log_until_it_ends),
        ("Debug puts the relay in the apps own folder and takes it out putting back the builds link", Debug_puts_the_relay_in_the_apps_own_folder_and_takes_it_out_putting_back_the_builds_link),
        ("what an end that never reached the device left is taken out the builds environment linked back where a start left its own", What_an_end_that_never_reached_the_device_left_is_taken_out_the_builds_environment_linked_back_where_a_start_left_its_own),
        ("the debug environment keeps the builds variables and adds the relays profiler and token", The_debug_environment_keeps_the_builds_variables_and_adds_the_relays_profiler_and_token),
        ("an android app is runnable though its sdk makes its output a library", An_android_app_is_runnable_though_its_sdk_makes_its_output_a_library),
        ("a device test project is told apart by the app its tests run in", A_device_test_project_is_told_apart_by_the_app_its_tests_run_in),
        ("an app with no launcher activity starts through its instrumentation which Run and a test run run afresh", An_app_with_no_launcher_activity_starts_through_its_instrumentation_which_Run_and_a_test_run_run_afresh),
        ("an instrumentations lines tell each tests end as it comes and how the run ended", An_instrumentations_lines_tell_each_tests_end_as_it_comes_and_how_the_run_ended),
        ("a results file tells each test by its methods full name with its time and what it said", A_results_file_tells_each_test_by_its_methods_full_name_with_its_time_and_what_it_said));

    private void The_workloads_devices_are_listed_an_emulator_kept_by_its_avd_and_one_not_running_said_so()
    {
        IReadOnlyList<DeviceTarget> targets = DeviceTargets.Parse(TargetPlatform.Android, Answer(Listed));

        Assert.Equal(["Pixel 9", "Tablet (not running)", "Galaxy S24"], targets.Select(t => t.Shown));
        Assert.Equal(["pixel", "tablet", "R5CT1234"], targets.Select(t => t.Key));
        Assert.Equal([true, false, true], targets.Select(t => t.Running));
        Assert.Equal([TargetKind.Emulator, TargetKind.Emulator, TargetKind.Device], targets.Select(t => t.Kind));
    }

    [Fact]
    public void A_target_decides_the_framework_built_and_the_profile_tied_to_its_platform_and_nothing_chosen_runs_this_machines_head() => Cases.All(
        ("a targets platform decides the framework built", A_targets_platform_decides_the_framework_built),
        ("a profile running the project and naming one platform is tied to it", A_profile_running_the_project_and_naming_one_platform_is_tied_to_it),
        ("with nothing chosen a head of this machines platform runs here and any other project waits for a choice", With_nothing_chosen_a_head_of_this_machines_platform_runs_here_and_any_other_project_waits_for_a_choice));

    private void A_targets_platform_decides_the_framework_built()
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

    private void Run_starts_the_app_afresh_and_writes_its_processes_log_until_it_ends()
    {
        AndroidApp app = new("emulator-5554", "/sdk/platform-tools/adb", "com.shop", "crc64.MainActivity", "/src/Shop/obj/Debug/net11.0-android");

        IReadOnlyList<string> run = app.Run();

        Assert.Equal(["-s", "emulator-5554", "shell"], run.Take(3));
        Assert.Equal(4, run.Count);
        Assert.StartsWith("am force-stop com.shop; am start -n com.shop/crc64.MainActivity", run[3], StringComparison.Ordinal);
        Assert.Contains("p=$(pidof com.shop)", run[3], StringComparison.Ordinal);
        Assert.Contains("logcat -v tag --pid=$p", run[3], StringComparison.Ordinal);
        Assert.Contains("while [ -d /proc/$p ]", run[3], StringComparison.Ordinal);
        Assert.Equal(["-s", "emulator-5554", "shell", "am force-stop com.shop; i=0; while pidof com.shop >/dev/null && [ $i -lt 100 ]; do sleep 0.1; i=$((i+1)); done"],
            app.ForceStop());
        Assert.Equal(["-s", "emulator-5554", "shell", "am", "set-debug-app", "com.shop"], app.SetDebugApp());
        Assert.Equal(["-s", "emulator-5554", "shell", "[ \"$(dumpsys activity processes | grep -c 'mDebugApp=com.shop/')\" != 0 ] && am clear-debug-app; true"],
            app.ClearDebugApp());
    }

    private void Debug_puts_the_relay_in_the_apps_own_folder_and_takes_it_out_putting_back_the_builds_link()
    {
        AndroidApp app = new("emulator-5554", "adb", "com.shop", "crc64.MainActivity", "/src/Shop/obj/Debug/net11.0-android");

        string put = app.PutRelayIn("arm64-v8a")[^1];
        string taken = app.TakeRelayOut("arm64-v8a", "/data/local/tmp/fastdeploy2/com.shop/0/arm64-v8a/environment")[^1];

        Assert.StartsWith("run-as com.shop sh -c 'cd /data/user/0/com.shop/files/.__override__/arm64-v8a || exit 1; readlink environment || ", put, StringComparison.Ordinal);
        // A file a start left in the link's place: the build's environment is the one beside its assemblies' links.
        Assert.Contains("for f in *.dll; do d=$(readlink \"$f\") && { [ -e \"${d%/*}/environment\" ] && echo \"${d%/*}/environment\"; break; }; done; ", put, StringComparison.Ordinal);
        Assert.Contains("cp /data/local/tmp/passenger-com.shop-relay libpassenger-debug.so && rm -f environment && cp /data/local/tmp/passenger-com.shop-environment environment'", put,
            StringComparison.Ordinal);
        Assert.EndsWith("; s=$?; rm -f /data/local/tmp/passenger-com.shop-relay /data/local/tmp/passenger-com.shop-environment; exit $s", put, StringComparison.Ordinal);
        Assert.EndsWith("rm -f libpassenger-debug.so environment && ln -s /data/local/tmp/fastdeploy2/com.shop/0/arm64-v8a/environment environment'", taken, StringComparison.Ordinal);
        Assert.EndsWith("rm -f libpassenger-debug.so environment'", app.TakeRelayOut("arm64-v8a", null)[^1], StringComparison.Ordinal);
        Assert.Equal("android-arm64", AndroidApp.RelayRuntime("arm64-v8a"));
        Assert.Equal("android-x64", AndroidApp.RelayRuntime("x86_64"));
        Assert.Null(AndroidApp.RelayRuntime("armeabi-v7a"));
    }

    private void What_an_end_that_never_reached_the_device_left_is_taken_out_the_builds_environment_linked_back_where_a_start_left_its_own()
    {
        AndroidApp app = new("emulator-5554", "adb", "com.shop", "crc64.MainActivity", "/obj");

        IReadOnlyList<string> unleft = app.TakeOutLeft();

        Assert.Equal(["-s", "emulator-5554", "shell"], unleft.Take(3));
        Assert.StartsWith("run-as com.shop sh -c 'rm -rf /data/user/0/com.shop/files/.passenger; cd /data/user/0/com.shop/files/.__override__ 2>/dev/null || exit 0; ", unleft[3],
            StringComparison.Ordinal);
        // In each ABI's folder: the relay, and an environment that is a file, never the build's own link.
        Assert.Contains("for a in *; do [ -d \"$a\" ] || continue; ( cd \"$a\" || exit 1; rm -f libpassenger-debug.so; [ -L environment ] || [ ! -e environment ] || { rm -f environment; ",
            unleft[3], StringComparison.Ordinal);
        Assert.Contains("for f in *.dll; do d=$(readlink \"$f\") && { [ -e \"${d%/*}/environment\" ] && ln -s \"${d%/*}/environment\" environment; break; }; done; } ) || exit 1; done'",
            unleft[3], StringComparison.Ordinal);
    }

    private void The_debug_environment_keeps_the_builds_variables_and_adds_the_relays_profiler_and_token()
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

    private void A_profile_running_the_project_and_naming_one_platform_is_tied_to_it()
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

    private void With_nothing_chosen_a_head_of_this_machines_platform_runs_here_and_any_other_project_waits_for_a_choice()
    {
        string[] maui = ["net11.0-android", "net11.0-ios", "net11.0-maccatalyst", "net11.0-windows10.0.19041.0"];

        Assert.Equal(KeptTarget.Of(DeviceTargets.Here(TargetPlatform.Windows)), DeviceTargets.Unchosen(maui, TargetPlatform.Windows));
        Assert.Null(DeviceTargets.Unchosen(maui, null));
        Assert.Null(DeviceTargets.Unchosen(["net11.0-android", "net11.0-ios"], TargetPlatform.Windows));
        Assert.Null(DeviceTargets.Unchosen(["net11.0-windows"], TargetPlatform.Windows));
    }

    private void An_android_app_is_runnable_though_its_sdk_makes_its_output_a_library()
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

    private void A_device_test_project_is_told_apart_by_the_app_its_tests_run_in()
    {
        ProjectModel Tests(string framework, string output) => new($"/src/{framework}/Tests.csproj", "Tests", output, [framework], [("MSTest", "4.5.1")], [], true);

        Assert.True(DeviceTargets.TestsInApp(Tests("net11.0-ios", "Exe")));
        Assert.True(DeviceTargets.TestsInApp(Tests("net11.0-maccatalyst", "Exe")));
        Assert.False(DeviceTargets.TestsInApp(Tests("net11.0", "Exe")));
        Assert.False(DeviceTargets.TestsInApp(Tests("net11.0-ios", "Library")));
    }

    private void An_app_with_no_launcher_activity_starts_through_its_instrumentation_which_Run_and_a_test_run_run_afresh()
    {
        (AndroidApp? app, string? refused) = AndroidApp.Read(Answer("""
            {
              "Properties": { "_AndroidPackage": "com.companyname.DeviceTests", "AndroidLaunchActivity": "", "IntermediateOutputPath": "obj/Debug/net11.0-android/",
                "AndroidInstrumentation": "com.companyname.DeviceTests.TestInstrumentation" }
            }
            """), "emulator-5554", "adb", "/src/DeviceTests");

        Assert.Null(refused);
        Assert.Equal(("", "com.companyname.DeviceTests.TestInstrumentation"), (app!.Activity, app.Instrumentation));
        Assert.Equal(["-s", "emulator-5554", "shell",
            "am force-stop com.companyname.DeviceTests; am instrument -w -r com.companyname.DeviceTests/com.companyname.DeviceTests.TestInstrumentation"], app.Instrument());
        Assert.Equal(app.Instrument(), app.Run());
        (AndroidApp? none, string? why) = AndroidApp.Read(Answer("""{ "Properties": { "_AndroidPackage": "com.shop", "IntermediateOutputPath": "obj/" } }"""), "emulator-5554", "adb", "/src");
        Assert.Equal((null, "com.shop has no launcher activity to start"), (none, why));
    }

    private void An_instrumentations_lines_tell_each_tests_end_as_it_comes_and_how_the_run_ended()
    {
        // As the androidtest template's instrumentation writes them, then as the workload's runner reads its own protocol.
        string[] written =
        [
            "INSTRUMENTATION_STATUS: outcome=passed", "INSTRUMENTATION_STATUS: test=DeviceTests.Test1.TestMethod1", "INSTRUMENTATION_STATUS_CODE: 0",
            "INSTRUMENTATION_STATUS: event=start", "INSTRUMENTATION_STATUS: test=DeviceTests.Test1.TestMethod2", "INSTRUMENTATION_STATUS_CODE: 1",
            "INSTRUMENTATION_STATUS: event=finish", "INSTRUMENTATION_STATUS: outcome=failed", "INSTRUMENTATION_STATUS: test=DeviceTests.Test1.TestMethod2",
            $"INSTRUMENTATION_STATUS: message-b64={Convert.ToBase64String(Encoding.UTF8.GetBytes("Assert.Fail failed. Expected to fail"))}",
            "INSTRUMENTATION_STATUS: stack=   at DeviceTests.Test1.TestMethod2()", "   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs()", "INSTRUMENTATION_STATUS_CODE: 0",
            "INSTRUMENTATION_RESULT: failed=1", "INSTRUMENTATION_RESULT: passed=1",
            "INSTRUMENTATION_RESULT: resultsPath=/storage/emulated/0/Android/data/com.companyname.DeviceTests/files/TestResults/app_process64_net11.0_arm64.trx\r",
            "INSTRUMENTATION_CODE: -1",
        ];
        InstrumentationLines lines = new();

        List<(string, TestState, string?, string?)?> ended = [.. written.Select(lines.Read).OfType<IReadOnlyDictionary<string, string>>().Select(InstrumentationLines.Ended)];

        Assert.Equal([("DeviceTests.Test1.TestMethod1", TestState.Passed, null, null), null,
            ("DeviceTests.Test1.TestMethod2", TestState.Failed, "Assert.Fail failed. Expected to fail", "   at DeviceTests.Test1.TestMethod2()\n   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs()")],
            ended);
        Assert.Equal("/storage/emulated/0/Android/data/com.companyname.DeviceTests/files/TestResults/app_process64_net11.0_arm64.trx", lines.ResultsPath);
        Assert.Null(lines.Problem());
        Assert.Equal("its app ended as it ran: Process crashed.", Read("INSTRUMENTATION_RESULT: shortMsg=Process crashed.", "INSTRUMENTATION_CODE: 0"));
        Assert.Equal("the instrumentation failed: System.InvalidOperationException: No test framework", Read("INSTRUMENTATION_RESULT: error=System.InvalidOperationException: No test framework",
            "   at DeviceTests.TestInstrumentation.OnStart()", "INSTRUMENTATION_CODE: 0"));
        Assert.Equal("the instrumentation could not start: Unable to find instrumentation info for: ComponentInfo{com.shop/com.shop.Tests}", Read("INSTRUMENTATION_STATUS: id=ActivityManagerService",
            "INSTRUMENTATION_STATUS: Error=Unable to find instrumentation info for: ComponentInfo{com.shop/com.shop.Tests}", "INSTRUMENTATION_STATUS_CODE: -1",
            "android.util.AndroidException: INSTRUMENTATION_FAILED: com.shop/com.shop.Tests"));
        Assert.Equal("the instrumentation ended before it finished: its app may have crashed", Read("INSTRUMENTATION_STATUS: outcome=passed"));

        static string? Read(params string[] said)
        {
            InstrumentationLines read = new();
            foreach (string line in said)
            {
                read.Read(line);
            }
            return read.Problem();
        }
    }

    private void A_results_file_tells_each_test_by_its_methods_full_name_with_its_time_and_what_it_said()
    {
        const string Trx = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun id="32f83118-e801-4701-9b26-8da6239c6aec" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="1" testName="TestMethod1" duration="00:00:00.0165334" outcome="Passed"><Output><StdOut>Said on the device</StdOut></Output></UnitTestResult>
                <UnitTestResult testId="2" testName="TestMethod2" duration="00:00:00.0712250" outcome="Failed">
                  <Output><ErrorInfo><Message>Assertion failed.
            This test is expected to fail</Message><StackTrace>   at DeviceTests.Test1.TestMethod2() in /src/DeviceTests/Test1.cs:line 14</StackTrace></ErrorInfo></Output>
                </UnitTestResult>
                <UnitTestResult testId="3" testName="Adds (1,2)" duration="00:00:00.0250297" outcome="NotExecuted" />
                <UnitTestResult testId="4" testName="Waits" outcome="InProgress" />
              </Results>
              <TestDefinitions>
                <UnitTest name="TestMethod1" id="1"><TestMethod className="DeviceTests.Test1" name="TestMethod1" /></UnitTest>
                <UnitTest name="TestMethod2" id="2"><TestMethod className="DeviceTests.Test1" name="TestMethod2" /></UnitTest>
                <UnitTest name="Adds (1,2)" id="3"><TestMethod className="DeviceTests.Sums" name="Adds" /></UnitTest>
                <UnitTest name="Waits" id="4"><TestMethod className="DeviceTests.Sums" name="Waits" /></UnitTest>
              </TestDefinitions>
            </TestRun>
            """;

        IReadOnlyList<TrxResult> results = TrxResults.Read(new MemoryStream(Encoding.UTF8.GetBytes(Trx)));

        Assert.Equal([
            new TrxResult("DeviceTests.Test1.TestMethod1", "TestMethod1", TestState.Passed, TimeSpan.FromTicks(165334), null, null, "Said on the device"),
            new TrxResult("DeviceTests.Test1.TestMethod2", "TestMethod2", TestState.Failed, TimeSpan.FromTicks(712250), "Assertion failed.\nThis test is expected to fail",
                "   at DeviceTests.Test1.TestMethod2() in /src/DeviceTests/Test1.cs:line 14", null),
            new TrxResult("DeviceTests.Sums.Adds", "Adds (1,2)", TestState.Skipped, TimeSpan.FromTicks(250297), null, null, null)], results);
        Assert.Throws<System.Xml.XmlException>(() => TrxResults.Read(new MemoryStream(Encoding.UTF8.GetBytes("<!DOCTYPE TestRun [<!ENTITY a \"b\">]><TestRun>&a;</TestRun>"))));
    }
}
