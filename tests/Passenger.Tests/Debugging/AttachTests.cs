using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>Attaching, as the IDE's Debug Test does: the debugger has hold of the program by the time configurationDone
/// answers, whether it was running or held at its start (which the attach lets go once the debugger is there), so a
/// breakpoint set before then sees the program's every pass; an attach that cannot be done answers with why.</summary>
[Collection(DebugSession.Collection)]
public class AttachTests(ITestOutputHelper testOutputHelper)
{
    private static readonly int AfterCount = Debuggees.Line("DebuggableConsoleApp", "HitConditionClass.cs", "// breakpoint here");

    [Fact]
    public async Task A_program_held_at_its_start_is_let_go_by_the_attach_and_its_breakpoints_first_pass_is_seen()
    {
        using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended: true, out Process debuggee);
        await host
            .WithInitializeRequest()
            .WithAttachRequest(debuggee.Id)
            .WaitForInitializedEvent();
        // The test never lets the program go itself: the attach does, once the debugger is registered for its runtime.
        host
            .WithBreakpointsRequest(AfterCount, Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs"))
            .WithConfigurationDoneRequest();

        StoppedEvent stopped = await host.WaitForStoppedEvent();
        host.WithEvaluateRequest(host.GetTopStackFrame(stopped.ThreadId!.Value).Id, "_count", out EvaluateResponse passes);
        Assert.True(passes.Result == "1",
            $"The first stop came on the breakpoint's pass {passes.Result}: the program ran past it before the debugger had hold of it");
    }

    [Fact]
    public async Task A_program_attached_to_runs_on_when_the_client_goes_without_disconnecting_and_can_be_attached_to_again()
    {
        string counted = Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs");
        using DebugSession first = DebugSession.ForAttach(testOutputHelper, startSuspended: true, out Process debuggee);
        await first
            .WithInitializeRequest()
            .WithAttachRequest(debuggee.Id)
            .WaitForInitializedEvent();
        first.WithBreakpointsRequest(AfterCount, counted).WithConfigurationDoneRequest();
        StoppedEvent stopped = await first.WaitForStoppedEvent();
        first.WithEvaluateRequest(first.GetTopStackFrame(stopped.ThreadId!.Value).Id, "_count", out EvaluateResponse before);

        // Held at its breakpoint as the IDE crashes or is ended, before any disconnect.
        first.GoWithoutDisconnecting();
        Assert.False(debuggee.HasExited, "The adapter ended the program it had only attached to when its client went without disconnecting");

        // Let go whole: it runs on, and another debugger can take hold of it.
        using DebugSession second = DebugSession.ForAttach(testOutputHelper, Process.GetProcessById(debuggee.Id));
        await second
            .WithInitializeRequest()
            .WithAttachRequest(debuggee.Id)
            .WaitForInitializedEvent();
        second.WithBreakpointsRequest(AfterCount, counted).WithConfigurationDoneRequest();
        StoppedEvent again = await second.WaitForStoppedEvent();
        second.WithEvaluateRequest(second.GetTopStackFrame(again.ThreadId!.Value).Id, "_count", out EvaluateResponse after);
        Assert.True(int.Parse(after.Result, System.Globalization.CultureInfo.InvariantCulture) > int.Parse(before.Result, System.Globalization.CultureInfo.InvariantCulture),
            $"The program let go at pass {before.Result} had not run on when attached to again (pass {after.Result})");
    }

    [Fact]
    public async Task A_running_program_is_the_debuggers_once_configurationDone_answers_so_a_pause_at_once_stops_it()
    {
        using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended: false, out Process debuggee);
        await host
            .WithInitializeRequest()
            .WithAttachRequest(debuggee.Id)
            .WaitForInitializedEvent();
        host.WithConfigurationDoneRequest();

        // Before any event of the program's could say the attach was done.
        host.Request(DapCommands.Pause, new ThreadArguments(0));
        Assert.Equal(StopReasons.Pause, (await host.WaitForStoppedEvent()).Reason);
    }

    [Fact]
    public async Task An_attached_programs_end_says_its_exit_code_where_the_system_tells_it_and_makes_none_up()
    {
        // Held at its start until the attach lets it go, so it cannot end before the debugger has it; its output is read.
        ProcessStartInfo start = Passenger.Core.ChildProcesses.Command(Debuggees.Apphost("PassengerCases"), ["flood"]);
        start.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
        start.RedirectStandardOutput = true;
        Process program = Passenger.Core.ChildProcesses.Start(start);
        _ = program.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        using DebugSession host = DebugSession.ForAttach(testOutputHelper, program);
        await host
            .WithInitializeRequest()
            .WithAttachRequest(program.Id)
            .WaitForInitializedEvent();
        host.WithConfigurationDoneRequest();

        (_, int? exitCode) = await host.WaitForEnd();
        // Windows tells whoever holds a process's handle how it ended; macOS and Linux tell its parent alone.
        int? told = OperatingSystem.IsWindows() ? 42 : null;
        Assert.True(exitCode == told, $"The attached program ended with 42, and the debugger said {(exitCode is int code ? code.ToString() : "nothing")}");
    }

    /// <summary>An attach to a program held at its start that ends before the debugger takes hold of it (Stop or Detach as it
    /// starts, Run Service without Debugger, an agent's stop) lets the program go, which runs on undebugged rather than wait
    /// for good for a debugger that is not coming.</summary>
    [Fact]
    public async Task A_program_held_at_its_start_runs_on_when_its_attach_ends_before_taking_hold_of_it()
    {
        ProcessStartInfo start = Passenger.Core.ChildProcesses.Command(Debuggees.Apphost("PassengerCases"), ["child"]);
        start.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
        start.RedirectStandardOutput = true;
        using Process program = Passenger.Core.ChildProcesses.Start(start);
        Task<string> written = program.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        using DebugSession host = DebugSession.ForAttach(testOutputHelper, program);
        await host
            .WithInitializeRequest()
            .WithAttachRequest(program.Id)
            .WaitForInitializedEvent();

        // Disconnected before configurationDone, which would have taken hold of it and let it go.
        Assert.True(host.Disconnect(DebugSession.RequestLimit), "The disconnect was not answered");
        Assert.True(program.WaitForExit(TimeSpan.FromSeconds(30).Scaled()), "The program held at its start was left held when its attach ended before it took hold");
        Assert.Equal("child ran", (await written).Trim());
    }

    /// <summary>As above, for a program whose runtime opens its diagnostic port only after the attach has ended, as one a
    /// busy machine starts late does: the end waits for the port and lets the program go once it is there.</summary>
    [Fact]
    public async Task A_program_held_at_its_start_whose_runtime_starts_after_its_attach_ended_runs_on()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no exec: a program a shell starts late has an id of its own, not the shell's attached to");
        // The shell becomes the program a second after it starts, keeping its id, so the attach ends before the runtime is there.
        ProcessStartInfo start = Passenger.Core.ChildProcesses.Command("/bin/sh", ["-c", "sleep 1 && exec \"$0\" child", Debuggees.Apphost("PassengerCases")]);
        start.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
        start.RedirectStandardOutput = true;
        using Process program = Passenger.Core.ChildProcesses.Start(start);
        Task<string> written = program.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        using DebugSession host = DebugSession.ForAttach(testOutputHelper, program);
        await host
            .WithInitializeRequest()
            .WithAttachRequest(program.Id)
            .WaitForInitializedEvent();

        Assert.True(host.Disconnect(DebugSession.RequestLimit), "The disconnect was not answered");
        Assert.True(program.WaitForExit(TimeSpan.FromSeconds(30).Scaled()), "The program whose runtime started after its attach ended was left held at its start");
        Assert.Equal("child ran", (await written).Trim());
    }

    [Fact]
    public async Task An_attach_to_a_program_that_is_not_dotnet_fails_its_configurationDone_saying_why()
    {
        Process other = Debuggees.NotDotnet();
        using DebugSession host = DebugSession.ForAttach(testOutputHelper, other);
        await host
            .WithInitializeRequest()
            .WithAttachRequest(other.Id)
            .WaitForInitializedEvent();

        DapException failed = Assert.Throws<DapException>(() => host.WithConfigurationDoneRequest());
        Assert.Contains($"Could not attach to process {other.Id}", failed.Reason, StringComparison.Ordinal);
        Assert.Contains("not a .NET program", failed.Reason, StringComparison.Ordinal);
        Assert.False(other.HasExited, "The attach that failed ended the program it could not take hold of");
    }
}
