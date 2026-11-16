using Passenger.App.Run.Devices;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.UnitTests.Run;

/// <summary>Where an iOS app runs: the simulators and devices the iOS workload lists (each by its UDID, a shut-down one said
/// not running and named without the workload's own word on its state), and an installed app's start on a simulator:
/// simctl's launch with its output on simctl's own, the app's variables under simctl's prefix with simctl's own words
/// unbuffered, the process simctl says it launched, and the relay's variables for a Debug start.</summary>
public class SimulatorTargetsTests
{
    private const string Listed = """
        {
          "Properties": { "_SdkDevPath": "/Applications/Xcode.app/Contents/Developer" },
          "Items": {
            "Devices": [
              { "Identity": "03DEA474-B2A5-4EFF-9D2D-0DD5B669FB9A", "Status": "Shutdown", "Type": "Simulator", "Description": "iPad (A16) - iOS 27.2 (Shutdown)",
                "RuntimeIdentifier": "iossimulator-arm64", "UDID": "03DEA474-B2A5-4EFF-9D2D-0DD5B669FB9A" },
              { "Identity": "B7339FFC-A65B-4992-9FF7-440A256F2635", "Status": "Booted", "Type": "Simulator", "Description": "iPhone 17 - iOS 27.2 (Booted)",
                "RuntimeIdentifier": "iossimulator-arm64", "UDID": "B7339FFC-A65B-4992-9FF7-440A256F2635" },
              { "Identity": "00008140-00052C491441801C", "Status": "Paired", "Type": "Device", "Description": "Someone's iPhone - iOS 27.0",
                "RuntimeIdentifier": "ios-arm64", "UDID": "00008140-00052C491441801C" }
            ]
          }
        }
        """;

    private static readonly SimulatorApp App = new("B7339FFC-A65B-4992-9FF7-440A256F2635", "/Applications/Xcode.app/Contents/Developer/usr/bin/simctl",
        "com.companyname.mauiapp1", "/src/MauiApp1/bin/Debug/net11.0-ios/iossimulator-arm64/MauiApp1.app", "iossimulator-arm64");

    [Fact]
    public void The_ios_workloads_simulators_are_listed_by_their_udids_a_shut_down_one_said_not_running()
    {
        IReadOnlyList<DeviceTarget> targets = DeviceTargets.Parse(TargetPlatform.Ios, new MSBuildAnswer(new ChildResult(0, Listed, string.Empty)));

        Assert.Equal(["iPad (A16) - iOS 27.2 (not running)", "iPhone 17 - iOS 27.2", "Someone's iPhone - iOS 27.0"], targets.Select(t => t.Shown));
        Assert.Equal(["03DEA474-B2A5-4EFF-9D2D-0DD5B669FB9A", "B7339FFC-A65B-4992-9FF7-440A256F2635", "00008140-00052C491441801C"], targets.Select(t => t.Key));
        Assert.Equal([false, true, true], targets.Select(t => t.Running));
        Assert.Equal([TargetKind.Simulator, TargetKind.Simulator, TargetKind.Device], targets.Select(t => t.Kind));
        Assert.All(targets, t => Assert.Equal(TargetPlatform.Ios, t.Platform));
    }

    [Fact]
    public void A_simulator_chosen_is_found_again_by_its_udid_once_booted()
    {
        IReadOnlyList<DeviceTarget> targets = DeviceTargets.Parse(TargetPlatform.Ios, new MSBuildAnswer(new ChildResult(0, Listed, string.Empty)));
        KeptTarget chosen = KeptTarget.Of(targets[0]);

        Assert.Same(targets[0], DeviceTargets.Find(chosen, targets));
        Assert.Equal("iPad (A16) - iOS 27.2", DeviceTargets.Find(chosen, [targets[0] with { Running = true }])?.Shown);
        Assert.Null(DeviceTargets.Find(chosen with { Platform = TargetPlatform.Android }, targets));
    }

    [Fact]
    public void Run_launches_the_app_afresh_on_its_simulator_with_its_output_on_simctls_own()
    {
        Assert.Equal(["launch", "--console", "--terminate-running-process", "B7339FFC-A65B-4992-9FF7-440A256F2635", "com.companyname.mauiapp1"], App.Launch());
        Assert.Equal(["terminate", "B7339FFC-A65B-4992-9FF7-440A256F2635", "com.companyname.mauiapp1"], App.Terminate());
    }

    [Fact]
    public void The_apps_variables_go_to_simctl_under_its_prefix_and_simctl_says_its_launch_at_once()
    {
        Dictionary<string, string> given = SimulatorApp.Given(new Dictionary<string, string> { [RuntimeVariables.StartupHooks] = "/hook.dll", ["GREETING"] = "hello" });

        Assert.Equal(new Dictionary<string, string>
        {
            ["NSUnbufferedIO"] = "YES",
            [SimulatorApp.ChildPrefix + RuntimeVariables.StartupHooks] = "/hook.dll",
            ["SIMCTL_CHILD_GREETING"] = "hello",
        }, given);
    }

    [Fact]
    public void The_apps_process_is_read_from_simctls_line_saying_its_launch()
    {
        string launched = "/Applications/Xcode.app/Contents/Developer/usr/bin/simctl launch --console com.companyname.mauiapp1\n"
            + "com.companyname.mauiapp1: 75344\n2026-11-15 21:42:37.443 MauiApp1[75344:99122814] Started\n";

        Assert.Equal("75344", App.StartedIn(TextSnapshot.From(launched)));
        Assert.Null(App.StartedIn(TextSnapshot.From("/Applications/Xcode.app/Contents/Developer/usr/bin/simctl launch --console\n")));
        Assert.Null(App.StartedIn(TextSnapshot.From("com.companyname.mauiapp1: not a process\n")));
    }

    [Fact]
    public void A_debug_start_gives_the_app_the_relay_as_its_profiler_listening_on_a_port_of_this_mac()
    {
        IReadOnlyDictionary<string, string> relay = SimulatorApp.RelayVariables("/Passenger/debugger/relay/iossimulator-arm64/libpassenger-debug.dylib", 51141, "ABC");

        Assert.Equal("1", relay[RuntimeVariables.EnableProfiling]);
        Assert.Equal(AndroidApp.RelayProfiler, relay[RuntimeVariables.Profiler]);
        Assert.Equal("/Passenger/debugger/relay/iossimulator-arm64/libpassenger-debug.dylib", relay[RuntimeVariables.ProfilerPath]);
        Assert.Equal("51141", relay[SimulatorApp.RelayPortVariable]);
        Assert.Equal("ABC", relay[AndroidApp.RelayTokenVariable]);
        Assert.Equal("60000", relay[AndroidApp.RelayWaitVariable]);
    }
}
