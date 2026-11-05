using Microsoft.CodeAnalysis.CSharp;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>An exception's stack trace as the debugger reads it from what the exception stores, running no code in the
/// program (PassengerCases' Traces.cs): the same text its StackTrace getter writes, frame for frame and line for line,
/// in every shape a trace takes; and, with implicit evaluation off on a loaded machine, still there with its lines in
/// the stop's description and in Variables' $exception.</summary>
[Collection(DebugSession.Collection)]
public class StoredTracesTests(ITestOutputHelper testOutputHelper)
{
    private static string Source => Debuggees.Source("PassengerCases", "Traces.cs");

    private static int Line(string code) => Debuggees.Line("PassengerCases", "Traces.cs", code);

    /// <summary>At each exception's stop, the trace the stop's description gives is what $exception.StackTrace evaluates to,
    /// and $exception's value what its ToString() does; caught, its whole trace, in the caught exception's value and its
    /// StackTrace row, is what e.ToString() and e.StackTrace do: generic methods and types, a nested type, a lambda, a local
    /// function, an iterator (+MoveNext()), an await and its rethrow, a rethrow's end of the earlier trace, hidden and
    /// inlined frames, a fault, an inner exception and parameters by reference, arrays and pointers.</summary>
    [Fact]
    public async Task Each_exceptions_stack_trace_read_from_what_it_stores_is_what_its_StackTrace_getter_writes()
    {
        await using DebugSession host = await Launch("traces", "Console.WriteLine(\"traces done\");", "Console.WriteLine($\"caught {e.GetType().Name}\");");
        List<string> compared = await ComparedAsync(host, Line("Console.WriteLine(\"traces done\");"));
        Assert.True(compared.Count(c => c.StartsWith("e:", StringComparison.Ordinal)) == 12 && compared.Count(c => c.StartsWith("thrown", StringComparison.Ordinal)) >= 14,
            $"Traces.Run throws at least 14 exceptions and catches 12, where these were compared: {string.Join("; ", compared)}");
    }

    /// <summary>An exception thrown through .NET 11's runtime-async methods, which have no state machines and go on as
    /// continuations: its trace, read from what it stores, is what its getter writes at each stop and once caught.</summary>
    [Fact]
    public async Task A_stack_trace_through_runtime_async_methods_is_what_its_getter_writes()
    {
        string source = Debuggees.Source("RuntimeAsyncConsoleApp", "Program.cs");
        int caught = Debuggees.Line("RuntimeAsyncConsoleApp", "Console.WriteLine($\"caught {e.GetType().Name}\");");
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("RuntimeAsyncConsoleApp"), stopAtEntry: false, justMyCode: true, arguments: ["throws"])
            .WaitForInitializedEvent();
        await host.RequestAsync(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([DapExceptionFilters.All]));
        await host.BreakpointsAsync([caught], source).ConfigurationDoneAsync();
        List<string> compared = await ComparedAsync(host, caught, last: true);
        Assert.True(compared.Count(c => c.StartsWith("thrown", StringComparison.Ordinal)) >= 1 && compared.Any(c => c.StartsWith("e:", StringComparison.Ordinal)),
            $"The runtime-async exception was compared only as {string.Join("; ", compared)}");
    }

    /// <summary>On a machine kept busy, with implicit evaluation off (an evaluation ran past its time), so no getter or
    /// ToString can run: the stop's description still gives the exception's trace and its inner exception's, each frame at
    /// its line, and Variables' $exception reads as its ToString would, its StackTrace row the same trace.</summary>
    [Fact]
    public async Task On_a_busy_machine_with_implicit_evaluation_off_a_stop_still_gives_the_stack_trace_with_its_lines()
    {
        using CancellationTokenSource done = new(TimeSpan.FromSeconds(60));
        Thread[] busy = [.. Enumerable.Range(0, Math.Max(2, Environment.ProcessorCount / 2)).Select(_ => new Thread(() => Spin(done.Token)) { IsBackground = true })];
        foreach (Thread thread in busy)
        {
            thread.Start();
        }
        try
        {
            await using DebugSession host = await Launch("wrapped", "Console.WriteLine(\"wrapped done\");");
            StoppedEvent stop = await host.WaitForStoppedEvent();
            // The inner exception's stop, then the wrapping one's.
            stop = await host.ContinueAsync().WaitForStoppedEvent();
            Assert.Equal(StopReasons.Exception, stop.Reason);
            int thread = stop.ThreadId!.Value;
            EvaluateResponse slow = await host.EvaluateAsync((await host.TopFrameAsync(thread)).Id, "Slow.Forever");
            Assert.True(slow.Result.Contains("longer than", StringComparison.Ordinal), $"The endless getter's evaluation said {slow.Result}, not that it ran past its time");

            // Thrown in the user's code, it stops in its first frame, before the frames above are walked.
            string wraps = $"   at Traces.Wraps() in {Source}:line {Line("throw new InvalidOperationException(\"wrapped\", e);")}";
            string inner = $"   at Traces.Outer.Inner.Fail() in {Source}:line {Line("public static void Fail() => throw new KeyNotFoundException")}{Environment.NewLine}"
                + $"   at Traces.Wraps() in {Source}:line {Line("Outer.Inner.Fail();")}";
            ExceptionInfoResponse info = await host.RequestAsync<ExceptionInfoResponse>(DapCommands.ExceptionInfo, new ThreadArguments(thread));
            Assert.Contains("Implicit evaluation is disabled", info.Description, StringComparison.Ordinal);
            Assert.True(info.Details?.StackTrace == wraps, $"With implicit evaluation off, the stop's trace is\n{info.Details?.StackTrace}\nwhere\n{wraps}\nwas to be read");
            DapExceptionDetails? innermost = info.Details?.InnerException?.SingleOrDefault();
            Assert.True(innermost?.StackTrace == inner, $"The inner exception's trace is\n{innermost?.StackTrace}\nwhere\n{inner}\nwas to be read");

            string text = $"System.InvalidOperationException: wrapped{Environment.NewLine} ---> System.Collections.Generic.KeyNotFoundException: from a nested type{Environment.NewLine}{inner}"
                + $"{Environment.NewLine}   --- End of inner exception stack trace ---{Environment.NewLine}{wraps}";
            DapVariable exception = Assert.Single(await host.LocalsAsync(stop), v => v.Name == DapRows.Exception);
            Assert.True(exception.Value == text, $"With implicit evaluation off, Variables' $exception reads\n{exception.Value}\nwhere\n{text}\nwas to be read");
            DapVariable trace = Assert.Single(await host.VariablesAsync(exception.VariablesReference), v => v.Name == "StackTrace");
            Assert.True(trace.Value == Quoted(wraps), $"$exception's StackTrace row reads {trace.Value}, not the trace the stop gives");
        }
        finally
        {
            done.Cancel();
            foreach (Thread thread in busy)
            {
                thread.Join();
            }
        }
    }

    /// <summary>At each stop until the one at <paramref name="done"/> (compared too where <paramref name="last"/>), the
    /// exception's trace as the stop gives it against $exception.StackTrace's, and the exception's value (or a caught one's,
    /// e) and its StackTrace row against what its ToString() and StackTrace evaluate to; none given by its getter.</summary>
    private async Task<List<string>> ComparedAsync(DebugSession host, int done, bool last = false)
    {
        List<string> compared = [];
        for (StoppedEvent stop = await host.WaitForStoppedEvent(); ; stop = await host.ContinueAsync().WaitForStoppedEvent())
        {
            bool atEnd = stop.Reason == StopReasons.Breakpoint && stop.ReadStopInfo().line == done;
            if (atEnd && !last)
            {
                break;
            }
            int thread = stop.ThreadId!.Value;
            int frame = (await host.TopFrameAsync(thread)).Id;
            List<DapVariable> locals = await host.LocalsAsync(stop);
            if (stop.Reason == StopReasons.Exception)
            {
                ExceptionInfoResponse info = await host.RequestAsync<ExceptionInfoResponse>(DapCommands.ExceptionInfo, new ThreadArguments(thread));
                string read = info.Details?.StackTrace ?? "";
                string written = (await host.EvaluateAsync(frame, "$exception.StackTrace")).Result;
                Assert.True(Quoted(read) == written, $"The {info.Details?.FullTypeName}'s stack trace read from what it stores is\n{read}\nwhere its getter writes\n{written}");
                compared.Add($"thrown {info.Details?.FullTypeName}: {read.Split('\n').Length} lines");
            }
            string name = stop.Reason == StopReasons.Exception ? DapRows.Exception : "e";
            DapVariable exception = Assert.Single(locals, v => v.Name == name);
            string text = (await host.EvaluateAsync(frame, $"{name}.ToString()")).Result;
            Assert.True(Quoted(exception.Value) == text, $"Variables' {name} reads\n{exception.Value}\nwhere its ToString() writes\n{text}");
            DapVariable trace = Assert.Single(await host.VariablesAsync(exception.VariablesReference), v => v.Name == "StackTrace");
            string stackTrace = (await host.EvaluateAsync(frame, $"{name}.StackTrace")).Result;
            Assert.True(trace.Value == stackTrace, $"{name}'s StackTrace row reads\n{trace.Value}\nwhere its getter writes\n{stackTrace}");
            compared.Add($"{name}: {exception.Value.Split('\n').Length} lines");
            if (atEnd)
            {
                break;
            }
        }
        testOutputHelper.WriteLine(string.Join('\n', compared));
        Assert.True(!host.EngineLog().Contains("was not read whole", StringComparison.Ordinal),
            $"A stack trace was given by its getter, not read from what the exception stores: {string.Join(" | ", host.EngineLog().Split('\n').Where(l => l.Contains("stored", StringComparison.Ordinal)))}");
        return compared;
    }

    private static string Quoted(string text) => SymbolDisplay.FormatLiteral(text, quote: true);

    private static void Spin(CancellationToken until)
    {
        double total = 0;
        while (!until.IsCancellationRequested)
        {
            for (int i = 1; i < 100_000; i++)
            {
                total += Math.Sqrt(i);
            }
        }
        GC.KeepAlive(total);
    }

    private async Task<DebugSession> Launch(string mode, params string[] stops)
    {
        DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: [mode])
            .WaitForInitializedEvent();
        await host.RequestAsync(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([DapExceptionFilters.All]));
        await host.BreakpointsAsync([.. stops.Select(Line)], Source).ConfigurationDoneAsync();
        return host;
    }
}
