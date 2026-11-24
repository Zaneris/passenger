using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class ConditionalBreakpointTests(ITestOutputHelper testOutputHelper)
{
    private static readonly int LogWritten = Debuggees.Line("DebuggableConsoleApp", "MyClass.cs", "Console.WriteLine($\"Log_MyMethod\");");
    private static readonly int CountUp = Debuggees.Line("DebuggableConsoleApp", "HitConditionClass.cs", "_count++;");
    private static readonly int AfterCount = Debuggees.Line("DebuggableConsoleApp", "HitConditionClass.cs", "// breakpoint here");
    private static readonly int TestEnd = AfterCount + 1;

    /// <summary>The gate's conditions, hit counts and tracepoints as one flow: a breakpoint stopping where its
    /// condition holds, never where it fails, and saying why where it cannot be told or is refused, a hit count of each
    /// form stopping on its hits and outlasting its file's breakpoints sent again, and a tracepoint writing its values
    /// and never stopping.</summary>
    [Gate]
    [Fact]
    public async Task The_gates_conditions_and_hit_counts_hold_as_one_flow()
    {
        await Flow.StepApartAsync("a condition with a lambda is refused saying why", () => new ConditionalBreakpointTests(testOutputHelper), t => t.A_condition_with_a_lambda_is_refused_saying_why());
        await Flow.StepApartAsync("a tracepoint writes its message with the values in it and never stops", () => new ConditionalBreakpointTests(testOutputHelper), t => t.A_tracepoint_writes_its_message_with_the_values_in_it_and_never_stops());
        await Flow.StepApartAsync("a breakpoint whose condition holds stops", () => new ConditionalBreakpointTests(testOutputHelper), t => t.A_breakpoint_whose_condition_holds_stops());
        await Flow.StepAsync("a breakpoint whose condition cannot be told stops and says why", () => Cases.EachAsync<string>(["noSuchName", "myInt"], row => Flow.StepApartAsync($"a breakpoint whose condition cannot be told stops and says why ({row})", () => new ConditionalBreakpointTests(testOutputHelper), t => t.A_breakpoint_whose_condition_cannot_be_told_stops_and_says_why(row))));
        await Flow.StepApartAsync("a breakpoint whose condition fails never stops", () => new ConditionalBreakpointTests(testOutputHelper), t => t.A_breakpoint_whose_condition_fails_never_stops());
        await Flow.StepAsync("a hit count of N stops on the Nth hit alone", () => Cases.EachAsync<string>(["==2", "2", "= 2"], row => Flow.StepApartAsync($"a hit count of N stops on the Nth hit alone ({row})", () => new ConditionalBreakpointTests(testOutputHelper), t => t.A_hit_count_of_N_stops_on_the_Nth_hit_alone(row))));
        await Flow.StepApartAsync("a breakpoints hit count and id outlast its files breakpoints being sent again", () => new ConditionalBreakpointTests(testOutputHelper), t => t.A_breakpoints_hit_count_and_id_outlast_its_files_breakpoints_being_sent_again());
        await Flow.StepApartAsync("a hit count the debugger cannot read is refused saying why", () => new ConditionalBreakpointTests(testOutputHelper), t => t.A_hit_count_the_debugger_cannot_read_is_refused_saying_why());
        await Flow.StepApartAsync("a hit count of at least N stops on every hit from the Nth", () => new ConditionalBreakpointTests(testOutputHelper), t => t.A_hit_count_of_at_least_N_stops_on_every_hit_from_the_Nth());
        await Flow.StepApartAsync("a hit count of every N stops on every Nth hit", () => new ConditionalBreakpointTests(testOutputHelper), t => t.A_hit_count_of_every_N_stops_on_every_Nth_hit());
    }

    private async Task A_condition_with_a_lambda_is_refused_saying_why()
    {
        bool startSuspended = true;
        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        SetBreakpointsResponse? response = await debugProtocolHost.SetBreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"),
            [new DapSourceBreakpoint(LogWritten, Condition: "_intList.Any(value => value == 4)")]);

        DapBreakpoint breakpoint = Assert.Single(response.Breakpoints);
        Assert.False(breakpoint.Verified);
        Assert.Equal("Lambda expressions are not supported in breakpoint conditions.", breakpoint.Message);
    }

    /// <summary>A condition nested past what the parser reads is refused saying so and the adapter runs on, and a
    /// condition reading a pattern's variable stops only where it holds; which hits stop, by what a breakpoint asks beside
    /// its condition: one waiting for another passes its hits by until that one takes a hit, a temporary one stops once, and
    /// a snapshot point never stops, capturing its hits' frames and values within its limit.</summary>
    [Fact]
    public async Task A_condition_the_parser_cannot_read_or_a_patterns_variable_reads_is_refused_or_held_where_it_holds()
    {
        await Flow.StepApartAsync<ConditionalBreakpointTests>("a condition nested past what the parser reads is refused saying so and the adapter runs on", () => new ConditionalBreakpointTests(testOutputHelper), t => t.A_condition_nested_past_what_the_parser_reads_is_refused_saying_so_and_the_adapter_runs_on());
        await Flow.StepApartAsync<ConditionalBreakpointTests>("a condition reading a pattern's variable stops only where it holds", () => new ConditionalBreakpointTests(testOutputHelper), t => t.A_condition_reading_a_patterns_variable_stops_only_where_it_holds());
        await Flow.StepApartAsync<ConditionalBreakpointTests>("a breakpoint waiting for a method's tracepoint stops only once the tracepoint took a hit", () => new ConditionalBreakpointTests(testOutputHelper), t => t.A_breakpoint_waiting_for_a_methods_tracepoint_stops_only_once_the_tracepoint_took_a_hit());
        await Flow.StepApartAsync<ConditionalBreakpointTests>("a temporary breakpoint stops once and the session is told it went", () => new ConditionalBreakpointTests(testOutputHelper), t => t.A_temporary_breakpoint_stops_once_and_the_session_is_told_it_went());
        await Flow.StepApartAsync<ConditionalBreakpointTests>("a snapshot point captures its hits frames and values within its limit and never stops", () => new ConditionalBreakpointTests(testOutputHelper), t => t.A_snapshot_point_captures_its_hits_frames_and_values_within_its_limit_and_never_stops());
    }

    /// <summary>A snapshot point at MyMethod's end, limited to 3 captures, with two expressions of its own: each of its first
    /// three hits is told as a capture, MyMethod on top, its locals and arguments read whole running none of the program's
    /// code (a struct's members, an auto-property through its field, a list's and a dictionary's items and a record's members
    /// from what each stores), its expressions read in its frame, and how long the program was held for it; its fourth hit
    /// tells the point its limit is reached, and the program never stops there, first stopping at Test's fourth call.</summary>
    private async Task A_snapshot_point_captures_its_hits_frames_and_values_within_its_limit_and_never_stops()
    {
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended: true, out Process program);
        await host.InitializeAsync().AttachAsync(program.Id).WaitForInitializedEvent();
        SetBreakpointsResponse set = await host.SetBreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"),
            [new DapSourceBreakpoint(Debuggees.MyMethodEnd, Condition: "myInt == 4", Snapshot: true, Captures: "myInt * 10\n\nanotherVar.Length", CaptureLimit: 3)]);
        int point = set.Breakpoints.Single().Id!.Value;
        await host
            .BreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs"), [new DapSourceBreakpoint(AfterCount, HitCondition: "==4")])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(program.Id, startSuspended: true);

        List<SnapshotEvent> taken = [];
        for (int i = 0; i < 3; i++)
        {
            taken.Add(await host.WaitForEvent<SnapshotEvent>());
        }
        foreach (SnapshotEvent snapshot in taken)
        {
            Assert.Equal(point, snapshot.BreakpointId);
            DapSnapshotFrame top = snapshot.Frames[0];
            Assert.True(top.Frame.Name.Contains("MyMethod", StringComparison.Ordinal) && top.Frame.Line == Debuggees.MyMethodEnd && top.Locals > 0,
                $"The snapshot's top frame is {top.Frame.Name} at line {top.Frame.Line}, its locals {top.Locals}, where MyMethod stands at line {Debuggees.MyMethodEnd} with its locals read");
            IReadOnlyList<DapVariable> locals = snapshot.Values[top.Locals];
            Assert.Equal(("4", "\"asdf\"", "13"), (Value(locals, "myInt"), Value(locals, "anotherVar"), Value(locals, "myParam")));
            Assert.Equal("5", Value(snapshot.Values[locals.Single(v => v.Name == "structVar").VariablesReference], "Id"));
            IReadOnlyList<DapVariable> self = snapshot.Values[locals.Single(v => v.Name == "this").VariablesReference];
            Assert.Equal("10", Value(self, "IntProperty"));
            // A list's, a dictionary's and a record's values read from what each stores, as no code of the program runs.
            DapVariable list = self.Single(v => v.Name == "_intList");
            DapVariable dictionary = self.Single(v => v.Name == "_intDictionary");
            Assert.True((list.Value, dictionary.Value, Value(self, "_recordField")) == ("Count = 4", "Count = 3", "MyRecord1 { X = 1, Y = 2 }"),
                $"The snapshot shows the list as {list.Value}, the dictionary as {dictionary.Value} and the record as {Value(self, "_recordField")}");
            Assert.Equal(["1", "4", "8", "25"], snapshot.Values[list.VariablesReference].Where(v => v.Name.StartsWith('[')).Select(v => v.Value));
            Assert.Equal([("[5]", "50"), ("[10]", "100"), ("[15]", "150")], snapshot.Values[dictionary.VariablesReference].Where(v => v.Name.StartsWith('[')).Select(v => (v.Name, v.Value)));
            Assert.Equal(["40", "4"], snapshot.Asked.Select(a => a.Value));
            Assert.True(snapshot.PausedMilliseconds > 0, $"The snapshot said the program was held {snapshot.PausedMilliseconds} ms for it");
            testOutputHelper.WriteLine($"The program was held {snapshot.PausedMilliseconds:F2} ms for a snapshot of {snapshot.Values.Sum(v => v.Value.Count)} values");
        }
        BreakpointEvent limit = await host.WaitForEvent<BreakpointEvent>(e => e.Breakpoint.Id == point && e.Breakpoint.Message is not null);
        Assert.Equal(BreakpointHits.LimitReached(3), limit.Breakpoint.Message);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.True(stop.Source?.Path?.EndsWith("HitConditionClass.cs", StringComparison.Ordinal) == true && stop.ReadStopInfo().line == AfterCount,
            $"The program's first stop was at {stop.Source?.Path}:{stop.Line}, where a snapshot point never stops and the breakpoint at Test's fourth call does");

        static string? Value(IReadOnlyList<DapVariable> rows, string name) => rows.FirstOrDefault(v => v.Name == name)?.Value;
    }

    /// <summary>A line breakpoint waiting for a method breakpoint, a tracepoint whose condition first holds as Test begins its
    /// third call, passes the first two calls by, then stops at the third and at each call after it.</summary>
    private async Task A_breakpoint_waiting_for_a_methods_tracepoint_stops_only_once_the_tracepoint_took_a_hit()
    {
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended: true, out Process program);
        await host.InitializeAsync().AttachAsync(program.Id).WaitForInitializedEvent();
        await host.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments(
            [new DapFunctionBreakpoint("DebuggableConsoleApp.HitConditionClass.Test", Condition: "_count == 2", LogMessage: "armed at {_count}", Suspend: false, Key: 1)]));
        await host
            .BreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs"), [new DapSourceBreakpoint(AfterCount, WaitFor: 1)])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(program.Id, startSuspended: true);

        Assert.Equal("armed at 2\n", (await host.WaitForEvent<OutputEvent>(o => o.Output.StartsWith("armed", StringComparison.Ordinal))).Output);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.Equal(AfterCount, stop.ReadStopInfo().line);
        string count = (await host.EvaluateAsync((await host.TopFrameAsync(stop.ThreadId!.Value)).Id, "_count")).Result;
        Assert.True(count == "3", $"The breakpoint waiting for Test's tracepoint stopped with the count at {count}, where the tracepoint first took a hit at 2 and the line after it counts 3");
        StoppedEvent next = await host.ContinueAsync().WaitForStoppedEvent();
        Assert.Equal("4", (await host.EvaluateAsync((await host.TopFrameAsync(next.ThreadId!.Value)).Id, "_count")).Result);
    }

    /// <summary>A temporary breakpoint stops at its first hit and the session is told it went; the program then passes its
    /// line by: after a method breakpoint's stop in the next pass of the loop, the stop after it is that method's again.</summary>
    private async Task A_temporary_breakpoint_stops_once_and_the_session_is_told_it_went()
    {
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended: true, out Process program);
        await host.InitializeAsync().AttachAsync(program.Id).WaitForInitializedEvent();
        await host.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments(
            [new DapFunctionBreakpoint("DebuggableConsoleApp.MyClassNoMembers.MyMethod", HitCondition: ">=2")]));
        SetBreakpointsResponse set = await host.SetBreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs"),
            [new DapSourceBreakpoint(AfterCount, Key: 5, Temporary: true)]);
        int temporary = set.Breakpoints.Single().Id!.Value;
        await host.ConfigurationDoneAsync().ResumeRuntimeAsync(program.Id, startSuspended: true);

        await host.WaitForEvent<BreakpointEvent>(e => e.Reason == EventReasons.Removed && e.Breakpoint.Id == temporary);
        StoppedEvent once = await host.WaitForStoppedEvent();
        Assert.Equal(AfterCount, once.ReadStopInfo().line);
        Assert.Equal("1", (await host.EvaluateAsync((await host.TopFrameAsync(once.ThreadId!.Value)).Id, "_count")).Result);
        Assert.EndsWith("MyClassNoMembers.cs", (await host.ContinueAsync().WaitForStoppedEvent()).Source?.Path);
        StoppedEvent after = await host.ContinueAsync().WaitForStoppedEvent();
        Assert.True(after.Source?.Path?.EndsWith("MyClassNoMembers.cs", StringComparison.Ordinal) == true,
            $"The temporary breakpoint stopped the program again, at {after.Source?.Path}:{after.Line}, after it had gone");
    }

    /// <summary>A condition nested past what the parser reads (interpolations within interpolations, each a level of a lexer
    /// that checks no stack) is refused when set, saying so, where reading it ended the adapter; the program then runs to a
    /// breakpoint after it, the refused one never bound.</summary>
    private async Task A_condition_nested_past_what_the_parser_reads_is_refused_saying_so_and_the_adapter_runs_on()
    {
        string deep = string.Concat(Enumerable.Repeat("$\"{", 20_000)) + "0" + string.Concat(Enumerable.Repeat("}\"", 20_000)) + " == \"0\"";
        bool startSuspended = true;
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process debuggee);
        await host
            .InitializeAsync()
            .AttachAsync(debuggee.Id)
            .WaitForInitializedEvent();

        SetBreakpointsResponse response = await host.SetBreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"),
            [new DapSourceBreakpoint(LogWritten, Condition: deep), new DapSourceBreakpoint(Debuggees.MyMethodEnd)]);
        DapBreakpoint refused = response.Breakpoints[0];
        Assert.True(!refused.Verified && refused.Message == "The condition nests deeper than the debugger reads.",
            $"A condition too deep to read was taken, or refused saying something else: {refused.Message}");

        await host.ConfigurationDoneAsync().ResumeRuntimeAsync(debuggee.Id, startSuspended);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.True(stop.ReadStopInfo().line == Debuggees.MyMethodEnd,
            $"The program stopped at line {stop.ReadStopInfo().line} of {stop.ReadStopInfo().filePath} before MyMethod's end: the refused breakpoint was bound");
    }

    private async Task A_tracepoint_writes_its_message_with_the_values_in_it_and_never_stops()
    {
        bool startSuspended = true;
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process debuggee);
        await host
            .InitializeAsync()
            .AttachAsync(debuggee.Id)
            .WaitForInitializedEvent();
        await host
            .BreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"),
            [
                new DapSourceBreakpoint(LogWritten, LogMessage: "myInt is {myInt}, and the list has {_intList.Count} \\{literally\\}"),
                new DapSourceBreakpoint(Debuggees.MyMethodEnd),
            ])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(debuggee.Id, startSuspended);

        OutputEvent logged = await host.WaitForEvent<OutputEvent>(o => o.Output.StartsWith("myInt is", StringComparison.Ordinal));
        Assert.Equal("myInt is 4, and the list has 4 {literally}\n", logged.Output);
        Assert.Equal(DapOutputCategories.Console, logged.Category);
        // The program went on past the tracepoint, to the breakpoint after it.
        Assert.Equal(Debuggees.MyMethodEnd, (await host.WaitForStoppedEvent()).ReadStopInfo().line);
    }

    private async Task A_breakpoint_whose_condition_holds_stops()
    {
        bool startSuspended = true;

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        await debugProtocolHost
            .BreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"), [new DapSourceBreakpoint(LogWritten, Condition: "myInt == 4")])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("MyClass.cs", stopInfo.filePath);
        Assert.Equal(LogWritten, stopInfo.line);
    }

    private async Task A_breakpoint_whose_condition_cannot_be_told_stops_and_says_why(string condition)
    {
        bool startSuspended = true;

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        SetBreakpointsResponse response = await debugProtocolHost.SetBreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"),
                [
                    new DapSourceBreakpoint(LogWritten, Condition: condition),
                    new DapSourceBreakpoint(Debuggees.MyMethodEnd)
                ]);
        await debugProtocolHost
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        // An unknown name, or a condition that is not a bool, stops rather than going missing unseen.
        int id = response.Breakpoints[0].Id!.Value;
        BreakpointEvent said = await debugProtocolHost.WaitForEvent<BreakpointEvent>(e => e.Breakpoint.Id == id && e.Breakpoint.Message is not null && e.Breakpoint.Verified);
        Assert.StartsWith(BreakpointConditions.NotEvaluated(condition), said.Breakpoint.Message, StringComparison.Ordinal);
        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        Assert.Equal(LogWritten, stoppedEvent.ReadStopInfo().line);
        Assert.Equal([id], stoppedEvent.HitBreakpointIds);
    }

    private async Task A_breakpoint_whose_condition_fails_never_stops()
    {
        bool startSuspended = true;

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        await debugProtocolHost
            .BreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"),
                [
                    new DapSourceBreakpoint(LogWritten, Condition: "myInt == 999"),
                    new DapSourceBreakpoint(Debuggees.MyMethodEnd)
                ])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        // Should skip the conditional breakpoint (false condition) and hit the unconditional one after it
        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("MyClass.cs", stopInfo.filePath);
        Assert.Equal(Debuggees.MyMethodEnd, stopInfo.line);
    }

    /// <summary>Equal to, as Rider's mode writes it, as a number alone, and as Visual Studio's "=".</summary>
    private async Task A_hit_count_of_N_stops_on_the_Nth_hit_alone(string hitCondition)
    {
        bool startSuspended = true;
        string hitConditionFilePath = Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs");

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        await debugProtocolHost
            .BreakpointsAsync(hitConditionFilePath, [new DapSourceBreakpoint(AfterCount, HitCondition: hitCondition)])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        // Should stop on 2nd hit, not 1st
        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("HitConditionClass.cs", stopInfo.filePath);
        Assert.Equal(AfterCount, stopInfo.line);

        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        int stackFrameId = stackTraceResponse.StackFrames!.First().Id;
        EvaluateResponse? evaluateResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_count");
        Assert.Equal("2", evaluateResponse.Result);
    }

    private async Task A_breakpoints_hit_count_and_id_outlast_its_files_breakpoints_being_sent_again()
    {
        bool startSuspended = true;
        string file = Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs");
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process debuggee);
        await host
            .InitializeAsync()
            .AttachAsync(debuggee.Id)
            .WaitForInitializedEvent();
        SetBreakpointsResponse first = await host.SetBreakpointsAsync(file, [new DapSourceBreakpoint(CountUp, HitCondition: "==3"), new DapSourceBreakpoint(AfterCount, HitCondition: "==5")]);
        await host
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(debuggee.Id, startSuspended);

        // The third pass stops at the count, with the breakpoint after it two of its five hits in.
        StoppedEvent third = await host.WaitForStoppedEvent();
        Assert.Equal(CountUp, third.ReadStopInfo().line);
        Assert.Equal("2", await Count(host, third));

        // A breakpoint set in the file, as a click in the gutter or Run to Cursor does, then taken away again with the count's.
        SetBreakpointsResponse added = await host.SetBreakpointsAsync(file,
            [new DapSourceBreakpoint(CountUp, HitCondition: "==3"), new DapSourceBreakpoint(AfterCount, HitCondition: "==5"), new DapSourceBreakpoint(TestEnd)]);
        StoppedEvent after = await host.ContinueAsync().WaitForStoppedEvent();
        Assert.Equal(TestEnd, after.ReadStopInfo().line);
        SetBreakpointsResponse removed = await host.SetBreakpointsAsync(file, [new DapSourceBreakpoint(AfterCount, HitCondition: "==5")]);

        // The one after the count counted on through both: it stops on the fifth pass, not five passes after the last of them.
        StoppedEvent fifth = await host.ContinueAsync().WaitForStoppedEvent();
        Assert.Equal(AfterCount, fifth.ReadStopInfo().line);
        Assert.Equal("5", await Count(host, fifth));
        Assert.Equal(first.Breakpoints[1].Id, added.Breakpoints[1].Id);
        Assert.Equal(first.Breakpoints[1].Id, removed.Breakpoints[0].Id);
        Assert.Equal(first.Breakpoints[0].Id, added.Breakpoints[0].Id);
        Assert.True(added.Breakpoints[2].Verified, "the breakpoint added while paused binds");
    }

    /// <summary>Hit counts the debugger cannot read, each set in turn with the program's code loaded, where one it took would
    /// bind at once: each is refused saying why, the breakpoint beside it binds, and the program never stops at it.</summary>
    private async Task A_hit_count_the_debugger_cannot_read_is_refused_saying_why()
    {
        bool startSuspended = true;
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process debuggee);
        await host
            .InitializeAsync()
            .AttachAsync(debuggee.Id)
            .WaitForInitializedEvent();
        await host
            .BreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"), [new DapSourceBreakpoint(Debuggees.MyMethodEnd)])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(debuggee.Id, startSuspended);
        Assert.Equal(Debuggees.MyMethodEnd, (await host.WaitForStoppedEvent()).ReadStopInfo().line);

        foreach (string hitCondition in new[] { "3 times", "abc", "%0", "==" })
        {
            SetBreakpointsResponse response = await host.SetBreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs"),
                [new DapSourceBreakpoint(AfterCount, HitCondition: hitCondition), new DapSourceBreakpoint(CountUp)]);
            DapBreakpoint refused = response.Breakpoints[0];
            Assert.False(refused.Verified, $"The hit count \"{hitCondition}\" was taken");
            Assert.Equal(BreakpointReasons.Failed, refused.Reason);
            Assert.Contains($"The hit count \"{hitCondition}\" is not one the debugger can read", refused.Message, StringComparison.Ordinal);
            Assert.True(response.Breakpoints[1].Verified, $"the breakpoint beside the hit count \"{hitCondition}\" binds");
        }

        // It never stops: from the count the program runs on round its loop to MyMethod's end.
        Assert.Equal(CountUp, (await host.ContinueAsync().WaitForStoppedEvent()).ReadStopInfo().line);
        StoppedEvent next = await host.ContinueAsync().WaitForStoppedEvent();
        Assert.EndsWith("MyClass.cs", next.ReadStopInfo().filePath, StringComparison.Ordinal);
        Assert.Equal(Debuggees.MyMethodEnd, next.ReadStopInfo().line);
    }

    /// <summary>HitConditionClass's count of its passes, where the program stopped.</summary>
    private static async Task<string> Count(DebugSession host, StoppedEvent stop)
    {
        StackTraceResponse stack = await host.StackTraceAsync(stop.ThreadId!.Value);
        EvaluateResponse value = await host.EvaluateAsync(stack.StackFrames!.First().Id, "_count");
        return value.Result;
    }

    private async Task A_hit_count_of_at_least_N_stops_on_every_hit_from_the_Nth()
    {
        bool startSuspended = true;
        string hitConditionFilePath = Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs");

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        await debugProtocolHost
            .BreakpointsAsync(hitConditionFilePath, [new DapSourceBreakpoint(AfterCount, HitCondition: ">=2")])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        // First stop should be on 2nd iteration (hit count >= 2)
        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("HitConditionClass.cs", stopInfo.filePath);
        Assert.Equal(AfterCount, stopInfo.line);

        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        int stackFrameId = stackTraceResponse.StackFrames!.First().Id;
        EvaluateResponse? evaluateResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_count");
        Assert.Equal("2", evaluateResponse.Result);

        // Continue - should stop again on 3rd iteration
        StoppedEvent stoppedEvent2 = await debugProtocolHost.ContinueAsync().WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo2 = stoppedEvent2.ReadStopInfo();
        Assert.EndsWith("HitConditionClass.cs", stopInfo2.filePath);
        Assert.Equal(AfterCount, stopInfo2.line);

        StackTraceResponse? stackTraceResponse2 = await debugProtocolHost.StackTraceAsync(stoppedEvent2.ThreadId!.Value);
        int stackFrameId2 = stackTraceResponse2.StackFrames!.First().Id;
        EvaluateResponse? evaluateResponse2 = await debugProtocolHost.EvaluateAsync(stackFrameId2, "_count");
        Assert.Equal("3", evaluateResponse2.Result);
    }

    private async Task A_hit_count_of_every_N_stops_on_every_Nth_hit()
    {
        bool startSuspended = true;
        string hitConditionFilePath = Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs");

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        await debugProtocolHost
            .BreakpointsAsync(hitConditionFilePath, [new DapSourceBreakpoint(AfterCount, HitCondition: "%2")])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        // First stop should be on 2nd iteration (2 % 2 == 0)
        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("HitConditionClass.cs", stopInfo.filePath);
        Assert.Equal(AfterCount, stopInfo.line);

        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        int stackFrameId = stackTraceResponse.StackFrames!.First().Id;
        EvaluateResponse? evaluateResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_count");
        Assert.Equal("2", evaluateResponse.Result);

        // Continue - should skip 3rd, stop on 4th (4 % 2 == 0)
        StoppedEvent stoppedEvent2 = await debugProtocolHost.ContinueAsync().WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo2 = stoppedEvent2.ReadStopInfo();
        Assert.EndsWith("HitConditionClass.cs", stopInfo2.filePath);
        Assert.Equal(AfterCount, stopInfo2.line);

        StackTraceResponse? stackTraceResponse2 = await debugProtocolHost.StackTraceAsync(stoppedEvent2.ThreadId!.Value);
        int stackFrameId2 = stackTraceResponse2.StackFrames!.First().Id;
        EvaluateResponse? evaluateResponse2 = await debugProtocolHost.EvaluateAsync(stackFrameId2, "_count");
        Assert.Equal("4", evaluateResponse2.Result);
    }

    /// <summary>A condition that reads a pattern's variable (boxed is int k &amp;&amp; …) stops only where it holds: never
    /// where it is false on every round, and on the one round where it is true, which the round then read says.</summary>
    private async Task A_condition_reading_a_patterns_variable_stops_only_where_it_holds()
    {
        string source = Debuggees.Source("PassengerCases", "Program.cs");
        int never = Debuggees.Line("PassengerCases", "Console.WriteLine($\"reading round {round}\");");
        int once = Debuggees.Line("PassengerCases", "Console.WriteLine($\"read round {round}\");");
        int after = Debuggees.Line("PassengerCases", "Console.WriteLine(\"reading\");");
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true)
            .WaitForInitializedEvent();
        await host
            .BreakpointsAsync(source,
            [
                new DapSourceBreakpoint(never, Condition: "boxed is int k && k > 100"),
                new DapSourceBreakpoint(once, Condition: "boxed is int k && k + round == 44"),
                new DapSourceBreakpoint(after),
            ])
            .ConfigurationDoneAsync();

        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.True(stop.ReadStopInfo().line == once,
            $"The program stopped at line {stop.ReadStopInfo().line} ({stop.Reason}) before its condition held on round 2 at line {once}: a pattern's variable read in a condition did not evaluate");
        int frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;
        Assert.Equal("2", (await host.EvaluateAsync(frame, "round")).Result);
        Assert.Equal(after, (await host.ContinueAsync().WaitForStoppedEvent()).ReadStopInfo().line);
    }
}
