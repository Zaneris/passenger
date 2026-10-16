using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_startup_answers_configuration_before_disconnect_can_answer_and_end_the_adapter(bool logFails)
    {
        string name = $"passenger-dap-{Guid.NewGuid():N}";
        // Unix named pipes accept an absolute socket path. macOS's per-user temp path plus CoreFxPipe_ and this name
        // exceeds its 104-byte socket limit; use the short shared temp root, as Passenger's profiler does, keeping the GUID.
        string pipe = OperatingSystem.IsWindows() ? name : Path.Combine(Directory.Exists("/tmp") ? "/tmp" : Path.GetTempPath(), name);
        using NamedPipeServerStream adapterStream = new(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using NamedPipeClientStream clientStream = new(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task accepting = adapterStream.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await clientStream.ConnectAsync(TestContext.Current.CancellationToken);
        await accepting;
        ConcurrentQueue<string> logged = new();
        string Log() => string.Join('\n', logged);
        TaskCompletionSource failing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim letFailureAnswer = new();
        void EngineLog(string message)
        {
            logged.Enqueue(message);
            if (message.StartsWith("DAP configurationDone failed:", StringComparison.Ordinal))
            {
                failing.TrySetResult();
                // The real error response is held here, after the handler failed and before the transport writes it.
                if (!letFailureAnswer.Wait(DebugSession.RequestLimit, TestContext.Current.CancellationToken))
                {
                    throw new TimeoutException("The test did not release the configuration failure's response");
                }
                if (logFails)
                {
                    throw new IOException("The engine log could not be written");
                }
            }
        }
        await using DapConnection server = new(adapterStream, adapterStream, EngineLog);
        await using DapConnection client = new(clientStream, clientStream);
        Passenger.DebugAdapter.DebugAdapter adapter = new(server, EngineLog);
        adapter.ShutdownRequested += () => shutdown.TrySetResult();
        server.Start();
        client.Start();
        List<Task> requests = [];
        try
        {
            await client.RequestAsync<DapCapabilities>("initialize", DebugSession.InitializeArguments, TestContext.Current.CancellationToken);
            await client.RequestAsync("launch", HeldArguments(), TestContext.Current.CancellationToken);
            Task configuring = client.RequestAsync("configurationDone", new DapEmpty(), TestContext.Current.CancellationToken);
            requests.Add(configuring);
            using Process process = await LaunchedProcess(Log);
            Task terminating = client.RequestAsync("terminate", new DapEmpty(), TestContext.Current.CancellationToken);
            requests.Add(terminating);
            await failing.Task.WaitAsync(DebugSession.RequestLimit, TestContext.Current.CancellationToken);

            Task disconnecting = client.RequestAsync("disconnect", new DisconnectArguments(TerminateDebuggee: true), TestContext.Current.CancellationToken);
            requests.Add(disconnecting);
            // This unlocked request proves the dispatcher passed disconnect, which must still wait behind the error.
            DapException barrier = await Assert.ThrowsAsync<DapException>(() => client.RequestAsync("startup-order-barrier", new DapEmpty(), TestContext.Current.CancellationToken)
                .WaitAsync(DebugSession.RequestLimit, TestContext.Current.CancellationToken));
            Assert.Contains("not supported", barrier.Reason, StringComparison.Ordinal);
            Assert.False(disconnecting.IsCompleted, $"Disconnect answered before the failed configuration could answer. {Log()}");
            Assert.False(shutdown.Task.IsCompleted, $"The adapter ended before the failed configuration could answer. {Log()}");

            letFailureAnswer.Set();
            DapException refused = await Assert.ThrowsAsync<DapException>(() => configuring.WaitAsync(DebugSession.RequestLimit, TestContext.Current.CancellationToken));
            Assert.Contains("debug session ended while waiting", refused.Reason, StringComparison.Ordinal);
            await Task.WhenAll(terminating, disconnecting, shutdown.Task).WaitAsync(DebugSession.RequestLimit, TestContext.Current.CancellationToken);
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(10)), "The cancelled launch left its held program behind");
        }
        finally
        {
            letFailureAnswer.Set();
            foreach (Task request in requests)
            {
                try
                {
                    await request.WaitAsync(DebugSession.RequestLimit, TestContext.Current.CancellationToken);
                }
                catch (Exception error) when (error is DapException or TimeoutException or OperationCanceledException)
                {
                }
            }
            await adapter.AbortAsync();
            output.WriteLine(Log());
        }
    }

    private static async Task<Task> HeldLaunch(DebugSession host)
    {
        await host.WithInitializeRequest().WaitForInitializedEvent();
        host.Request("launch", HeldArguments());
        return host.Send<DapEmpty>("configurationDone");
    }

    private static LaunchArguments HeldArguments() => new()
    {
        Program = Debuggees.Program("TopLevelStatementsConsoleApp"),
        JustMyCode = true,
        StopAtEntry = true,
        Env = new Dictionary<string, string> { ["DOTNET_DefaultDiagnosticPortSuspend"] = "1" },
    };

    private static Task<Process> LaunchedProcess(DebugSession host) => LaunchedProcess(() => host.EngineLog);

    private static async Task<Process> LaunchedProcess(Func<string> log)
    {
        Stopwatch waiting = Stopwatch.StartNew();
        while (waiting.Elapsed < DebugSession.RequestLimit)
        {
            Match resumed = Regex.Match(log(), @"launch.Resume\(\) returned for process (\d+)");
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
        throw new TimeoutException($"The launched runtime never reached its diagnostic startup port. {log()}");
    }
}
