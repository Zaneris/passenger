using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>Startup through the adapter's real protocol, with the runtime held at its diagnostics port before dbgshim's
/// handshake: a slow start still debugs, an end or cancellation answers the pending configuration with its reason.</summary>
[Collection(Alone.Name)]
public class RuntimeStartupTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_runtime_held_past_the_old_five_second_handshake_limit_still_stops_on_Mains_first_line()
    {
        using DebugSession host = DebugSession.ForLaunch(output);
        Task configuring = await HeldLaunch(host);
        using Process process = await LaunchedProcess(host);
        // Starts after Resume returned, so the delay is in the runtime, not in the adapter's launch request.
        await Task.Delay(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken);
        Assert.False(configuring.IsCompleted, $"The debugger gave up while the runtime was still starting. {host.EngineLog}");
        new DiagnosticsClient(process.Id).ResumeRuntime();
        await configuring.WaitAsync(DebugSession.RequestLimit, TestContext.Current.CancellationToken);

        StoppedEvent stopped = await host.WaitForStoppedEvent();
        Assert.Equal(("entry", 2), (stopped.Reason, stopped.Line));
        Assert.Contains("dbgshim's runtime-start callback entered", host.EngineLog, StringComparison.Ordinal);
        Assert.Contains("is being handled after", host.EngineLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_that_ends_before_the_runtime_handshake_answers_configuration_with_its_exit_code()
    {
        using DebugSession host = DebugSession.ForLaunch(output);
        Task configuring = await HeldLaunch(host);
        using Process process = await LaunchedProcess(host);
        process.Kill();
        DapException refused = await Assert.ThrowsAsync<DapException>(() => configuring.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Contains("ended", refused.Reason, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("exit code", refused.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Disconnect_cancels_a_runtime_start_without_waiting_for_the_engine_lock_it_holds()
    {
        using DebugSession host = DebugSession.ForLaunch(output);
        Task configuring = await HeldLaunch(host);
        using Process process = await LaunchedProcess(host);
        await host.Send<DapEmpty>("disconnect", new DisconnectArguments(TerminateDebuggee: true))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        DapException refused = await Assert.ThrowsAsync<DapException>(() => configuring);
        Assert.Contains("debug session ended while waiting", refused.Reason, StringComparison.Ordinal);
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(10)), "The cancelled launch left its held program behind");
    }

    private static async Task<Task> HeldLaunch(DebugSession host)
    {
        await host.WithInitializeRequest().WaitForInitializedEvent();
        host.Request("launch", new LaunchArguments
        {
            Program = Debuggees.Program("TopLevelStatementsConsoleApp"),
            JustMyCode = true,
            StopAtEntry = true,
            Env = new Dictionary<string, string> { ["DOTNET_DefaultDiagnosticPortSuspend"] = "1" },
        });
        return host.Send<DapEmpty>("configurationDone");
    }

    private static async Task<Process> LaunchedProcess(DebugSession host)
    {
        Stopwatch waiting = Stopwatch.StartNew();
        while (waiting.Elapsed < DebugSession.RequestLimit)
        {
            Match resumed = Regex.Match(host.EngineLog, @"launch.Resume\(\) returned for process (\d+)");
            if (resumed.Success)
            {
                Process process = Process.GetProcessById(int.Parse(resumed.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
                if (DiagnosticsClient.GetPublishedProcesses().Contains(process.Id))
                {
                    return process;
                }
                process.Dispose();
            }
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException($"The launched runtime never reached its diagnostic startup port. {host.EngineLog}");
    }
}
