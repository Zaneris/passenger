using System.IO.Compression;
using System.Reflection.Metadata;
using System.Text;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>Steps through the awaits of a .NET 11 program compiled with runtime async (no state machines), each held to
/// the line it stops at and to where the same program compiled with state machines stops.</summary>
[Collection(DebugSession.Collection)]
public class RuntimeAsyncStepTests(ITestOutputHelper testOutputHelper)
{
    private const string RuntimeAsync = "RuntimeAsyncConsoleApp";
    private const string StateMachines = "StateMachineAsyncConsoleApp";

    private static readonly int TotalAwaited = Line("int total = await OuterAsync(3);");
    private static readonly int SumStarted = Line("int sum = 0;");
    private static readonly int InnerAwaited = Line("sum += await InnerAsync(i);");
    private static readonly int AfterInner = Line("Console.WriteLine($\"after {i} sum {sum}\");");
    private static readonly int DoneAwaited = Line("int done = await DoneAsync(n);");
    private static readonly int DoneAdded = Line("sum += done;");
    private static readonly int SumReturned = Line("return sum;");
    private static readonly int InnerOpened = Line("static async Task<int> InnerAsync(int i)") + 1;
    private static readonly int BeforeSet = Line("int before = i;");
    private static readonly int DelayAwaited = Line("await Task.Delay(10);");
    private static readonly int Doubled = Line("int doubled = before * 2;");
    private static readonly int DoubledReturned = Line("return doubled;");
    private static readonly int InnerClosed = DoubledReturned + 1;
    private static readonly int OuterClosed = SumReturned + 1;
    private static readonly int TotalWritten = Line("Console.WriteLine($\"total {total}\");");
    private static readonly int RunDelayed = Line("await Task.Delay(20);");
    private static readonly int RunEnded = Line("int ended = started + 1;");
    private static readonly int ChildIdRead = Line("int id = child.Id;");
    private static readonly int FirstSet = Line("int first = which;");
    private static readonly int SecondSet = Line("int second = first * 10;");
    private static readonly int TripledWritten = Line("Console.WriteLine($\"tripled {tripled}\");");

    /// <summary>Runtime-async awaits (.NET 11): a step over one that suspends or completes at once stops on the next
    /// line, as in a method the runtime called and when another run of the method goes on first; Step Into an awaited
    /// method then over its await and out stops where its run goes; a step out before it suspends and past the end of
    /// a method that went on after an await stop in the caller; and a recording keeps each call's value where it completes.</summary>
    [Fact]
    public async Task A_step_over_into_or_out_of_a_runtime_async_await_stops_where_its_own_run_goes_on()
    {
        await Flow.StepApartAsync<RuntimeAsyncStepTests>("a step over an await that suspends stops on the next line once the method goes on", () => new RuntimeAsyncStepTests(testOutputHelper), t => t.A_step_over_an_await_that_suspends_stops_on_the_next_line_once_the_method_goes_on());
        await Flow.StepApartAsync<RuntimeAsyncStepTests>("a step over an await done at once stops on the next line", () => new RuntimeAsyncStepTests(testOutputHelper), t => t.A_step_over_an_await_done_at_once_stops_on_the_next_line());
        await Flow.StepApartAsync<RuntimeAsyncStepTests>("step Into an awaited method then over its await and out stop where its run goes", () => new RuntimeAsyncStepTests(testOutputHelper), t => t.Step_Into_an_awaited_method_then_over_its_await_and_out_stop_where_its_run_goes());
        await Flow.StepApartAsync<RuntimeAsyncStepTests>("a step out of a method before it suspends stops in its caller once it completes", () => new RuntimeAsyncStepTests(testOutputHelper), t => t.A_step_out_of_a_method_before_it_suspends_stops_in_its_caller_once_it_completes());
        await Flow.StepApartAsync<RuntimeAsyncStepTests>("a step past the end of a method that went on after an await stops in the caller that awaits it", () => new RuntimeAsyncStepTests(testOutputHelper), t => t.A_step_past_the_end_of_a_method_that_went_on_after_an_await_stops_in_the_caller_that_awaits_it());
        await Flow.StepApartAsync<RuntimeAsyncStepTests>("a step over an await in a method the runtime called stops on the next line once the method goes on", () => new RuntimeAsyncStepTests(testOutputHelper), t => t.A_step_over_an_await_in_a_method_the_runtime_called_stops_on_the_next_line_once_the_method_goes_on());
        await Flow.StepApartAsync<RuntimeAsyncStepTests>("a step over an await stops in its own run when another run of the method goes on first", () => new RuntimeAsyncStepTests(testOutputHelper), t => t.A_step_over_an_await_stops_in_its_own_run_when_another_run_of_the_method_goes_on_first());
        await Flow.StepApartAsync<RuntimeAsyncStepTests>("a recording keeps each runtime-async calls value where it completes in the run that called it", () => new RuntimeAsyncStepTests(testOutputHelper), t => t.A_recording_keeps_each_runtime_async_calls_value_where_it_completes_in_the_run_that_called_it());
        await Flow.StepApartAsync<RuntimeAsyncStepTests>("a stop in a method gone on after its await lists the methods awaiting it as the state machine build does (#2339)", () => new RuntimeAsyncStepTests(testOutputHelper), t => t.A_stop_in_a_method_gone_on_after_its_await_lists_the_methods_awaiting_it_as_the_state_machine_build_does());
    }

    /// <summary>A stop in InnerAsync once its await went on, its run resumed by the runtime rather than called: the Call
    /// Stack lists OuterAsync awaiting it at its await, then the program's Main awaiting OuterAsync, each at the line it
    /// waits at, in the runtime-async build as in the state-machine build, whose awaiting methods are read from their tasks.</summary>
    private async Task A_stop_in_a_method_gone_on_after_its_await_lists_the_methods_awaiting_it_as_the_state_machine_build_does()
    {
        IReadOnlyList<(string Name, int Line)> stateMachines = await OwnFrames(StateMachines, Doubled);
        IReadOnlyList<(string Name, int Line)> runtimeAsync = await OwnFrames(RuntimeAsync, Doubled);
        Assert.True(stateMachines.Count == 3 && stateMachines.Select(frame => frame.Line).SequenceEqual([Doubled, InnerAwaited, TotalAwaited]),
            $"The state-machine build's stop in InnerAsync lists [{string.Join(" < ", stateMachines)}], not it, OuterAsync and Main at the lines each waits at");
        Assert.True(runtimeAsync.SequenceEqual(stateMachines),
            $"The runtime-async build's stop in InnerAsync lists [{string.Join(" < ", runtimeAsync)}], where the state-machine build lists [{string.Join(" < ", stateMachines)}]");

        async Task<IReadOnlyList<(string Name, int Line)>> OwnFrames(string debuggee, int breakpoint)
        {
            string source = Debuggees.Source(RuntimeAsync, "Program.cs");
            await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
            await host.InitializeAsync().LaunchAsync(Debuggees.Program(debuggee), stopAtEntry: false, justMyCode: true).WaitForInitializedEvent();
            await host.BreakpointsAsync(breakpoint, source).ConfigurationDoneAsync();
            StoppedEvent stop = await host.WaitForStoppedEvent();
            Assert.Equal((StopReasons.Breakpoint, breakpoint), (stop.Reason, stop.Line));
            StackTraceResponse stack = await host.StackTraceAsync(stop.ThreadId!.Value, null);
            string own = $"{debuggee}.dll!";
            return [.. stack.StackFrames.Where(frame => frame.Name.StartsWith(own, StringComparison.Ordinal)).Select(frame => (frame.Name[own.Length..], frame.Line))];
        }
    }

    private async Task A_step_over_an_await_that_suspends_stops_on_the_next_line_once_the_method_goes_on()
    {
        await AssertStops(InnerAwaited, [(Step.Over, AfterInner)]);
        await AssertStops(TotalAwaited, [(Step.Over, TotalWritten)]);
    }

    private async Task A_step_over_an_await_done_at_once_stops_on_the_next_line()
    {
        await AssertStops(DoneAwaited, [(Step.Over, DoneAdded), (Step.Over, SumReturned)]);
    }

    private async Task Step_Into_an_awaited_method_then_over_its_await_and_out_stop_where_its_run_goes()
    {
        await AssertStops(InnerAwaited, [(Step.Into, InnerOpened), (Step.Over, BeforeSet), (Step.Over, DelayAwaited), (Step.Over, Doubled), (Step.Out, AfterInner)]);
    }

    private async Task A_step_out_of_a_method_before_it_suspends_stops_in_its_caller_once_it_completes()
    {
        await AssertStops(BeforeSet, [(Step.Out, AfterInner)]);
        await AssertStops(SumStarted, [(Step.Out, TotalWritten)]);
    }

    private async Task A_step_past_the_end_of_a_method_that_went_on_after_an_await_stops_in_the_caller_that_awaits_it()
    {
        await AssertStops(Doubled, [(Step.Over, DoubledReturned), (Step.Over, InnerClosed), (Step.Over, AfterInner)]);
        await AssertStops(SumReturned, [(Step.Over, OuterClosed), (Step.Over, TotalWritten)]);
    }

    private async Task A_step_over_an_await_in_a_method_the_runtime_called_stops_on_the_next_line_once_the_method_goes_on()
    {
        await AssertStops(RunDelayed, [(Step.Over, RunEnded)]);
    }

    /// <summary>Two runs of a method wait at the same await, the other going on first: the step stops in its own run.</summary>
    private async Task A_step_over_an_await_stops_in_its_own_run_when_another_run_of_the_method_goes_on_first()
    {
        foreach (string debuggee in (string[])[StateMachines, RuntimeAsync])
        {
            string source = Debuggees.Source(RuntimeAsync, "Program.cs");
            await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
            await host.InitializeAsync().LaunchAsync(Debuggees.Program(debuggee), stopAtEntry: false, justMyCode: true).WaitForInitializedEvent();
            await host.BreakpointsAsync(source, [new DapSourceBreakpoint(FirstSet, Condition: "which == 1")]).ConfigurationDoneAsync();
            StoppedEvent stop = await host.WaitForStoppedEvent();
            await host.ClearBreakpointsAsync(source);
            stop = await host.StepOverAsync(stop.ThreadId!.Value).WaitForStoppedEvent();
            stop = await host.StepOverAsync(stop.ThreadId!.Value).WaitForStoppedEvent();
            List<DapVariable> locals = await host.LocalsAsync(stop);
            Assert.True(stop.Line == SecondSet && locals.Any(local => (local.Name, local.Value) == ("which", "1")),
                $"{debuggee}'s step over the first run's await stopped at line {stop.Line} with {string.Join(", ", locals.Select(local => $"{local.Name} = {local.Value}"))}, "
                + $"not at line {SecondSet} in the run with which = 1");
        }
    }

    private static bool IsAwait(string[] lines, int? line) =>
        line is int at && at >= 1 && at <= lines.Length && (lines[at - 1].Contains("await ", StringComparison.Ordinal) || lines[at - 1].Contains("Await(", StringComparison.Ordinal));

    /// <summary>A decompiled file's lines, as the PDB the debugger made of its module in the symbol cache embeds them.</summary>
    private static string[] DecompiledLines(string cache, string path)
    {
        Guid embedded = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
        foreach (string pdb in Directory.EnumerateFiles(cache, "*.pdb", SearchOption.AllDirectories))
        {
            using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdb));
            MetadataReader reader = provider.GetMetadataReader();
            foreach (DocumentHandle document in reader.Documents)
            {
                if (reader.GetString(reader.GetDocument(document).Name) != path)
                {
                    continue;
                }
                foreach (CustomDebugInformationHandle handle in reader.GetCustomDebugInformation(document))
                {
                    CustomDebugInformation information = reader.GetCustomDebugInformation(handle);
                    if (reader.GetGuid(information.Kind) != embedded)
                    {
                        continue;
                    }
                    byte[] blob = reader.GetBlobBytes(information.Value);
                    int size = BitConverter.ToInt32(blob, 0);
                    byte[] text = blob[4..];
                    if (size > 0)
                    {
                        using DeflateStream inflating = new(new MemoryStream(text), CompressionMode.Decompress);
                        using MemoryStream inflated = new();
                        inflating.CopyTo(inflated);
                        text = inflated.ToArray();
                    }
                    return Encoding.UTF8.GetString(text).Split('\n');
                }
            }
        }
        throw new InvalidOperationException($"No PDB in {cache} embeds {path}");
    }

    private enum Step
    {
        Over,
        Into,
        Out,
    }

    /// <summary>Stops at a breakpoint in each build of the program and takes the steps from there, holding each stop to its
    /// line, the two builds alike, and the locals to no value an await helper returned.</summary>
    private async Task AssertStops(int breakpoint, IReadOnlyList<(Step Step, int Line)> steps)
    {
        IReadOnlyList<int> stateMachines = await Stops(StateMachines, breakpoint, steps);
        IReadOnlyList<int> runtimeAsync = await Stops(RuntimeAsync, breakpoint, steps);
        Assert.True(steps.Select(step => step.Line).SequenceEqual(stateMachines),
            $"The state-machine build stopped at lines {string.Join(", ", stateMachines)}, not {string.Join(", ", steps.Select(step => step.Line))}");
        Assert.True(stateMachines.SequenceEqual(runtimeAsync),
            $"The runtime-async build stopped at lines {string.Join(", ", runtimeAsync)}, where the state-machine build stopped at {string.Join(", ", stateMachines)}");
    }

    /// <summary>A recording of the runtime-async program stopped where TripleAsync's value is written: InnerAsync's returns
    /// are 0, 2 and 4, each read where it completes after its await and none where it suspended, OuterAsync's 10 and
    /// TripleAsync's 12, all in the stop's run, and of TwiceAsync's two runs at once, the one that returned 20 was called
    /// with 2.</summary>
    private async Task A_recording_keeps_each_runtime_async_calls_value_where_it_completes_in_the_run_that_called_it()
    {
        string source = Debuggees.Source(RuntimeAsync, "Program.cs");
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host.InitializeAsync().LaunchAsync(Debuggees.Program(RuntimeAsync), stopAtEntry: false, justMyCode: true).WaitForInitializedEvent();
        await host.RequestAsync(DapCommands.Record, new RecordArguments(true));
        await host.BreakpointsAsync(TripledWritten, source).ConfigurationDoneAsync();
        StoppedEvent stop = await host.WaitForStoppedEvent();
        TimelineResponse timeline = await host.RequestAsync<TimelineResponse>(DapCommands.Timeline, new TimelineArguments(stop.ThreadId));
        string[] said = [.. timeline.Moments.Select(m => m.Said)];
        Assert.True(said.Where(each => each.StartsWith("InnerAsync() returned", StringComparison.Ordinal)).SequenceEqual(["InnerAsync() returned 0", "InnerAsync() returned 2", "InnerAsync() returned 4"])
            && said.Contains("OuterAsync() returned 10") && said.Contains("TripleAsync() returned 12"),
            $"The stop's run held {string.Join(" · ", said)}, where InnerAsync returned 0, 2 and 4 and nothing where it suspended, OuterAsync 10 and TripleAsync 12");
        DapMoment? twenty = timeline.Moments.LastOrDefault(m => m.Said == "TwiceAsync() returned 20");
        SnapshotEvent? back = twenty is null ? null : await host.RequestAsync<SnapshotEvent>(DapCommands.Moment, new MomentArguments(twenty.Index));
        string? which = back?.Values[back.Frames[0].Locals].FirstOrDefault(v => v.Name == "which")?.Value;
        Assert.True(which == "2" && said.Contains("TwiceAsync() returned 10"),
            $"The stop's run held {string.Join(" · ", said)}, where TwiceAsync returned 10 and 20, the 20 called with 2 (it shows which = {which})");
        testOutputHelper.WriteLine($"Recording held the program {timeline.HeldMilliseconds / timeline.Recorded:F3} ms a moment over {timeline.Recorded} moments");
    }

    private async Task<IReadOnlyList<int>> Stops(string debuggee, int breakpoint, IReadOnlyList<(Step Step, int Line)> steps)
    {
        // Both builds compile RuntimeAsyncConsoleApp's Program.cs.
        string source = Debuggees.Source(RuntimeAsync, "Program.cs");
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host.InitializeAsync().LaunchAsync(Debuggees.Program(debuggee), stopAtEntry: false, justMyCode: true).WaitForInitializedEvent();
        await host.BreakpointsAsync(breakpoint, source).ConfigurationDoneAsync();
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.Equal((StopReasons.Breakpoint, breakpoint), (stop.Reason, stop.Line));
        await host.ClearBreakpointsAsync(source);
        List<int> lines = [];
        foreach ((Step step, int line) in steps)
        {
            int thread = stop.ThreadId!.Value;
            Task<DebugSession> stepped = step switch
            {
                Step.Over => host.StepOverAsync(thread),
                Step.Into => host.StepInAsync(thread),
                _ => host.StepOutAsync(thread),
            };
            stop = await stepped.WaitForStoppedEvent();
            lines.Add(stop.Line ?? 0);
            if (stop.Reason != StopReasons.Step || stop.Line != line)
            {
                break;
            }
            List<DapVariable> locals = await host.LocalsAsync(stop);
            Assert.False(locals.Any(local => local.Name.StartsWith("Await", StringComparison.Ordinal)),
                $"{debuggee}'s locals after {step} to line {line} show what an await helper returned: {string.Join(", ", locals.Select(local => $"{local.Name} = {local.Value}"))}");
        }
        return lines;
    }

    private static int Line(string code) => Debuggees.Line(RuntimeAsync, code);
}
