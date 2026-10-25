using System.Diagnostics;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class LaunchTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task Stop_at_entry_stops_in_Main_saying_so()
    {
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        string program = Debuggees.Program("DebuggableConsoleApp");

        await host
            .WithInitializeRequest()
            .WithLaunchRequest(program, stopAtEntry: true, justMyCode: true)
            .WaitForInitializedEvent();
        host.WithConfigurationDoneRequest();

        StoppedEvent stoppedEvent = await host.WaitForStoppedEvent();
        Assert.Equal(StopReasons.Entry, stoppedEvent.Reason);
        Assert.NotNull(stoppedEvent.ThreadId);
        DapStackFrame topFrame = host.GetTopStackFrame(stoppedEvent.ThreadId!.Value);
        Assert.Contains("Main", topFrame.Name);
        Assert.EndsWith("Program.cs", topFrame.Source?.Path);
    }

    [Fact]
    public async Task Stop_at_entry_in_top_level_statements_stops_at_their_first_saying_so()
    {
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        string program = Debuggees.Program("TopLevelStatementsConsoleApp");

        await host
            .WithInitializeRequest()
            .WithLaunchRequest(program, stopAtEntry: true, justMyCode: true)
            .WaitForInitializedEvent();
        host.WithConfigurationDoneRequest();

        StoppedEvent stoppedEvent = await host.WaitForStoppedEvent();
        Assert.Equal(StopReasons.Entry, stoppedEvent.Reason);
        Assert.NotNull(stoppedEvent.ThreadId);
        DapStackFrame topFrame = host.GetTopStackFrame(stoppedEvent.ThreadId!.Value);
        Assert.Contains("Main", topFrame.Name);
        Assert.EndsWith("Program.cs", topFrame.Source?.Path);
        Assert.Equal(Debuggees.Line("TopLevelStatementsConsoleApp", "int test = 4;"), topFrame.Line);
        host
            .WithScopesRequest(topFrame.Id, out ScopesResponse? scopesResponse)
            .WithVariablesRequest(scopesResponse.Scopes.Single().VariablesReference, out List<DapVariable>? variables);
        Assert.Contains(variables, variable => variable.Name == "test");
    }

    [Fact]
    public async Task Stop_at_entry_in_an_async_Main_stops_at_its_start_saying_so()
    {
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        string program = Debuggees.Program("AsyncEntryConsoleApp");

        await host
            .WithInitializeRequest()
            .WithLaunchRequest(program, stopAtEntry: true, justMyCode: true)
            .WaitForInitializedEvent();
        host.WithConfigurationDoneRequest();

        StoppedEvent stoppedEvent = await host.WaitForStoppedEvent();
        Assert.Equal(StopReasons.Entry, stoppedEvent.Reason);
        Assert.NotNull(stoppedEvent.ThreadId);
        DapStackFrame topFrame = host.GetTopStackFrame(stoppedEvent.ThreadId!.Value);
        Assert.Contains("Main", topFrame.Name);
        Assert.EndsWith("Program.cs", topFrame.Source?.Path);
        Assert.Equal(Debuggees.Line("AsyncEntryConsoleApp", "static async Task Main(") + 1, topFrame.Line);
        host
            .WithScopesRequest(topFrame.Id, out ScopesResponse? scopesResponse)
            .WithVariablesRequest(scopesResponse.Scopes.Single().VariablesReference, out List<DapVariable>? variables);
        Assert.Contains(variables, variable => variable.Name == "test");
    }

    [Fact]
    public async Task Stop_at_entry_in_async_top_level_statements_stops_at_their_first_saying_so()
    {
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        string program = Debuggees.Program("AsyncTopLevelStatementsConsoleApp");

        await host
            .WithInitializeRequest()
            .WithLaunchRequest(program, stopAtEntry: true, justMyCode: true)
            .WaitForInitializedEvent();
        host.WithConfigurationDoneRequest();

        StoppedEvent stoppedEvent = await host.WaitForStoppedEvent();
        Assert.Equal(StopReasons.Entry, stoppedEvent.Reason);
        Assert.NotNull(stoppedEvent.ThreadId);
        DapStackFrame topFrame = host.GetTopStackFrame(stoppedEvent.ThreadId!.Value);
        Assert.Contains("Main", topFrame.Name);
        Assert.EndsWith("Program.cs", topFrame.Source?.Path);
        Assert.Equal(Debuggees.Line("AsyncTopLevelStatementsConsoleApp", "int test = 4;"), topFrame.Line);
        host
            .WithScopesRequest(topFrame.Id, out ScopesResponse? scopesResponse)
            .WithVariablesRequest(scopesResponse.Scopes.Single().VariablesReference, out List<DapVariable>? variables);
        Assert.Contains(variables, variable => variable.Name == "test");
    }

    [Fact]
    public async Task A_launched_program_reads_its_own_input_never_the_adapters()
    {
        int written = Debuggees.Line("ReadsInputConsoleApp", "Console.WriteLine($\"read:");
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        string source = Debuggees.Source("ReadsInputConsoleApp", "Program.cs");

        await host
            .WithInitializeRequest()
            .WithLaunchRequest(Debuggees.Program("ReadsInputConsoleApp"), stopAtEntry: false, justMyCode: true)
            .WaitForInitializedEvent();
        host
            .WithBreakpointsRequest(written, source)
            .WithConfigurationDoneRequest();
        // While it waits on its console, every request still reaches the debugger.
        host.Request(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([]));
        host.Request(DapCommands.Input, new InputArguments("hello\n"));

        StoppedEvent stopped = await host.WaitForStoppedEvent();
        Assert.Equal(written, stopped.ReadStopInfo().line);
        host.WithStackTraceRequest(stopped.ThreadId!.Value, out StackTraceResponse stack)
            .WithEvaluateRequest(stack.StackFrames[0].Id, "line", out EvaluateResponse line);
        Assert.Equal("\"hello\"", line.Result);
    }

    [Fact]
    public async Task A_dotnet_program_the_debugged_one_starts_runs_as_it_would_undebugged()
    {
        // Nothing that holds the debugged program at its start may reach the programs it starts in turn.
        using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, "PassengerCases",
            Debuggees.Line("PassengerCases", "Console.WriteLine($\"child said: {said}\");"), []);
        StoppedEvent stopped = await host.WaitForStoppedEvent();
        host.WithStackTraceRequest(stopped.ThreadId!.Value, out StackTraceResponse stack)
            .WithEvaluateRequest(stack.StackFrames[0].Id, "said", out EvaluateResponse said)
            .WithEvaluateRequest(stack.StackFrames[0].Id, "Environment.GetEnvironmentVariable(\"DOTNET_DefaultDiagnosticPortSuspend\")", out EvaluateResponse hold);
        Assert.Equal("\"child ran\"", said.Result);
        Assert.True(hold.Result == "null", $"The program was held at its start by DOTNET_DefaultDiagnosticPortSuspend, which is still in its environment ({hold.Result}) for every .NET program it starts");
    }

    [Fact]
    public async Task A_program_writing_64_KB_runs_to_its_end_with_all_it_wrote_and_its_own_exit_code()
    {
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .WithInitializeRequest()
            .WithLaunchRequest(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: ["flood"])
            .WaitForInitializedEvent();
        host.WithConfigurationDoneRequest();

        (string output, int? exitCode) = await host.WaitForEnd();
        string expected = string.Concat(Enumerable.Range(0, 1024).Select(i => $"{i:D5} {new string((char)('a' + i % 26), 57)}\n"));
        Assert.True(output == expected, $"The program's 64 KB of output came {output.Length} characters long, ending \"{output[Math.Max(0, output.Length - 70)..]}\"");
        Assert.True(exitCode == 42, $"The program exited with 42, and the debugger said {(exitCode is int code ? code.ToString() : "nothing")}");
    }

    [Fact]
    public async Task The_thread_running_Main_is_the_one_named_Main_Thread()
    {
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);

        await host
            .WithInitializeRequest()
            .WithLaunchRequest(Debuggees.Program("TopLevelStatementsConsoleApp"), stopAtEntry: true, justMyCode: true)
            .WaitForInitializedEvent();
        host.WithConfigurationDoneRequest();

        StoppedEvent stopped = await host.WaitForStoppedEvent();
        IReadOnlyList<DapThread> threads = host.Request<ThreadsResponse>(DapCommands.Threads).Threads;
        Assert.Equal("Main Thread", Assert.Single(threads, t => t.Id == stopped.ThreadId).Name);
        Assert.Single(threads, t => t.Name == "Main Thread");
        Assert.Equal(stopped.ThreadId, Assert.Single(threads, t => t.IsMain == true).Id);
    }

    /// <summary>A program the client starts in its terminal starts held, and the debugger has hold of it before any of it
    /// runs: a breakpoint on Main's very first line stops it there, launch after launch, never missed by a Main that ran on.</summary>
    [Fact]
    public async Task A_program_started_in_the_clients_terminal_stops_at_a_breakpoint_on_Mains_first_line_every_time()
    {
        string source = Debuggees.Source("TerminalConsoleApp", "Program.cs");
        int first = Debuggees.Line("TerminalConsoleApp", "// Main's first line");
        for (int launch = 1; launch <= 5; launch++)
        {
            using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
            using Terminal terminal = new(host);
            await host
                .WithInitializeRequest()
                .WithLaunchRequest(Debuggees.Program("TerminalConsoleApp"), stopAtEntry: false, justMyCode: true, console: "integratedTerminal")
                .WaitForInitializedEvent();
            host.WithBreakpointsRequest(first, source).WithConfigurationDoneRequest();

            StoppedEvent stopped = await host.WaitForStoppedEvent();
            Assert.True((stopped.Reason, stopped.Line) == (StopReasons.Breakpoint, first),
                $"Launch {launch} in the terminal stopped for {stopped.Reason} at line {stopped.Line}, not at the breakpoint on Main's first line ({first})");
            Assert.True(terminal.Program is { HasExited: false } program && host.GetTopStackFrame(stopped.ThreadId!.Value).Name.Contains("Main", StringComparison.Ordinal),
                $"Launch {launch} stopped somewhere other than in the program the terminal started");
        }
    }

    /// <summary>A client that cannot start the program in its terminal fails configurationDone saying so, and the debugger
    /// goes on answering: the request never holds the engine.</summary>
    [Fact]
    public async Task A_client_that_cannot_start_the_program_in_its_terminal_fails_the_launch_saying_why()
    {
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        host.Connection.Handle<RunInTerminalArguments, RunInTerminalResponse>(DapCommands.RunInTerminal,
            _ => Task.FromException<RunInTerminalResponse>(new InvalidOperationException("no terminal here")));
        await host
            .WithInitializeRequest()
            .WithLaunchRequest(Debuggees.Program("TerminalConsoleApp"), stopAtEntry: false, justMyCode: true, console: "integratedTerminal")
            .WaitForInitializedEvent();

        DapException refused = Assert.Throws<DapException>(() => host.Request(DapCommands.ConfigurationDone));
        Assert.Contains("could not start TerminalConsoleApp.dll in its terminal: no terminal here", refused.Reason, StringComparison.Ordinal);
        Assert.Empty(host.Request<ThreadsResponse>(DapCommands.Threads, limit: TimeSpan.FromSeconds(10)).Threads);
    }

    /// <summary>A program whose runtime is slow to start (its first start on a machine reads it from a cold disk) is
    /// still the debugger's, however late its runtime answers: held here 6 s at its start.</summary>
    [Fact]
    public async Task A_program_whose_runtime_starts_late_is_still_debugged()
    {
        using StartHold hold = new();
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .WithInitializeRequest()
            .WithLaunchRequest(Debuggees.Program("DebuggableConsoleApp"), stopAtEntry: false, justMyCode: true, environment: hold.Environment)
            .WaitForInitializedEvent();
        host.WithBreakpointsRequest();
        Task configured = host.Send(DapCommands.ConfigurationDone);
        await hold.Held();

        // Six seconds in which the runtime is held, and the debugger must not give up on it.
        Assert.False(await Waiting.Within(configured, TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken, scaled: false), $"The debugger gave up on the program while its runtime was still starting: {configured.Exception?.InnerException?.Message}");
        await hold.LetGo();
        await configured.WaitAsync(DebugSession.RequestLimit, TestContext.Current.CancellationToken);
        StoppedEvent stopped = await host.WaitForStoppedEvent();
        Assert.True((stopped.Reason, stopped.Line) == (StopReasons.Breakpoint, Debuggees.MyMethodEnd), $"The program that started late stopped for {stopped.Reason} at line {stopped.Line}, not at its breakpoint");
    }

    /// <summary>A launch stopped while its program's runtime is still starting ends at once, the program with it, and says why:
    /// the debugger waits for a runtime as long as the session lasts, no longer.</summary>
    [Fact]
    public async Task A_launch_stopped_while_its_runtime_starts_ends_at_once_with_its_program()
    {
        using StartHold hold = new();
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .WithInitializeRequest()
            .WithLaunchRequest(Debuggees.Program("DebuggableConsoleApp"), stopAtEntry: false, justMyCode: true, environment: hold.Environment)
            .WaitForInitializedEvent();
        Task configured = host.Send(DapCommands.ConfigurationDone);
        int program = await hold.Held();

        Assert.True(host.Disconnect(TimeSpan.FromSeconds(10)), "The disconnect went unanswered 10 s while the program's runtime was starting");
        DapException failed = await Assert.ThrowsAsync<DapException>(() => configured);
        Assert.Contains("the session ended before its runtime told the debugger it had started", failed.Reason, StringComparison.Ordinal);
        await Waiting.UntilAsync(() => !Processes.IsRunning(program), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(100));
        Assert.False(Processes.IsRunning(program), $"The program (process {program}) outlived the session that was stopped as it started");
    }

    /// <summary>A program that ends before any runtime in it starts fails configurationDone as it ends, saying so and how it
    /// ended, rather than once a wait runs out.</summary>
    [Fact]
    public async Task A_program_that_ends_before_its_runtime_starts_fails_configurationDone_saying_how_it_ended()
    {
        (string program, string[] arguments) = OperatingSystem.IsWindows() ? ("cmd", (string[])["/c", "exit 3"]) : ("/bin/sh", ["-c", "exit 3"]);
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .WithInitializeRequest()
            .WithLaunchRequest(program, stopAtEntry: false, justMyCode: true, arguments)
            .WaitForInitializedEvent();

        DapException failed = Assert.Throws<DapException>(() => host.WithConfigurationDoneRequest());
        Assert.Contains("it ended before its runtime told the debugger it had started (the program had ended, with exit code 3)", failed.Reason, StringComparison.Ordinal);
    }

    /// <summary>The client's terminal, as the tests' client keeps one: it starts what runInTerminal asks for, with the
    /// variables asked for, its output read and dropped, and ends it with the test.</summary>
    private sealed class Terminal : IDisposable
    {
        public Terminal(DebugSession host) => host.Connection.Handle<RunInTerminalArguments, RunInTerminalResponse>(DapCommands.RunInTerminal, arguments =>
        {
            string program = arguments.Args[0] is "dotnet" ? Dotnet.Host() : arguments.Args[0];
            ProcessStartInfo start = ChildProcesses.Command(program, arguments.Args.Skip(1), arguments.Cwd);
            foreach ((string name, string value) in arguments.Env ?? new Dictionary<string, string>())
            {
                start.Environment[name] = value;
            }
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            Process started = ChildProcesses.Start(start);
            _ = started.StandardOutput.ReadToEndAsync();
            _ = started.StandardError.ReadToEndAsync();
            Program = started;
            return Task.FromResult(new RunInTerminalResponse(started.Id));
        });

        public Process? Program { get; private set; }

        public void Dispose()
        {
            if (Program is { } program)
            {
                ChildProcesses.Kill(program);
                program.Dispose();
            }
        }
    }
}
