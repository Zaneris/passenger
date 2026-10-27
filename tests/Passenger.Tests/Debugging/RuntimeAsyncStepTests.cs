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

    [Fact]
    public async Task A_step_over_an_await_that_suspends_stops_on_the_next_line_once_the_method_goes_on()
    {
        await AssertStops(InnerAwaited, [(Step.Over, AfterInner)]);
        await AssertStops(TotalAwaited, [(Step.Over, TotalWritten)]);
    }

    [Fact]
    public async Task A_step_over_an_await_done_at_once_stops_on_the_next_line()
    {
        await AssertStops(DoneAwaited, [(Step.Over, DoneAdded), (Step.Over, SumReturned)]);
    }

    [Fact]
    public async Task Step_Into_an_awaited_method_then_over_its_await_and_out_stop_where_its_run_goes()
    {
        await AssertStops(InnerAwaited, [(Step.Into, InnerOpened), (Step.Over, BeforeSet), (Step.Over, DelayAwaited), (Step.Over, Doubled), (Step.Out, AfterInner)]);
    }

    [Fact]
    public async Task A_step_out_of_a_method_before_it_suspends_stops_in_its_caller_once_it_completes()
    {
        await AssertStops(BeforeSet, [(Step.Out, AfterInner)]);
        await AssertStops(SumStarted, [(Step.Out, TotalWritten)]);
    }

    [Fact]
    public async Task A_step_past_the_end_of_a_method_that_went_on_after_an_await_stops_in_the_caller_that_awaits_it()
    {
        await AssertStops(Doubled, [(Step.Over, DoubledReturned), (Step.Over, InnerClosed), (Step.Over, AfterInner)]);
        await AssertStops(SumReturned, [(Step.Over, OuterClosed), (Step.Over, TotalWritten)]);
    }

    [Fact]
    public async Task A_step_over_an_await_in_a_method_the_runtime_called_stops_on_the_next_line_once_the_method_goes_on()
    {
        await AssertStops(RunDelayed, [(Step.Over, RunEnded)]);
    }

    /// <summary>Two runs of a method wait at the same await, the other going on first: the step stops in its own run.</summary>
    [Fact]
    public async Task A_step_over_an_await_stops_in_its_own_run_when_another_run_of_the_method_goes_on_first()
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

    /// <summary>.NET 11's libraries are compiled with runtime async: Process.WaitForExitAsync, its module decompiled, steps over
    /// its await for the child to end (which the debuggee lets end only once the method has suspended there) to a line of
    /// its own after the await, rather than to its caller.</summary>
    [Fact]
    public async Task Without_Just_My_Code_a_step_over_an_await_in_a_decompiled_library_method_stops_in_it_once_it_goes_on()
    {
        string cache = TestFolders.Create("symbols");
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper, cache);
        await host.InitializeAsync().LaunchAsync(Debuggees.Program(RuntimeAsync), stopAtEntry: false, justMyCode: false).WaitForInitializedEvent();
        string source = Debuggees.Source(RuntimeAsync, "Program.cs");
        await host.BreakpointsAsync(ChildIdRead, source).ConfigurationDoneAsync();
        StoppedEvent read = await host.WaitForStoppedEvent();
        await host.ClearBreakpointsAsync(source);
        StoppedEvent inId = await host.StepInAsync(read.ThreadId!.Value).WaitForStoppedEvent();
        Assert.True(inId.Source?.Path?.EndsWith("Process.cs", StringComparison.Ordinal) is true, $"A step into Process.Id stopped at {inId.Source?.Path}:{inId.Line}");
        // Process's module has its symbols now, so a function breakpoint binds in it.
        await host.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints,
            new SetFunctionBreakpointsArguments([new DapFunctionBreakpoint("System.Diagnostics.Process.WaitForExitAsync")]));
        StoppedEvent entered = await host.ContinueAsync().WaitForStoppedEvent();
        await host.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments([]));
        string? decompiled = entered.Source?.Path;
        Assert.True(entered.Reason == StopReasons.Breakpoint && decompiled == inId.Source?.Path,
            $"Process.WaitForExitAsync's breakpoint stopped for {entered.Reason} at {decompiled}:{entered.Line}");
        string[] lines = DecompiledLines(cache, decompiled!);
        List<int> stops = [entered.Line ?? 0];
        StoppedEvent stop = entered;
        for (int steps = 0; steps < 60 && !IsAwait(lines, stop.Line); steps++)
        {
            stop = await host.StepOverAsync(stop.ThreadId!.Value).WaitForStoppedEvent();
            stops.Add(stop.Line ?? 0);
            Assert.True(stop.Source?.Path == decompiled, $"Stepping over WaitForExitAsync's lines toward its await left it for {stop.Source?.Path}:{stop.Line}; its stops: {string.Join(", ", stops)}");
        }
        int awaited = stop.Line ?? 0;
        Assert.True(IsAwait(lines, awaited), $"Stepping over WaitForExitAsync's lines met no await; its stops: {string.Join(", ", stops)}");
        stop = await host.StepOverAsync(stop.ThreadId!.Value).WaitForStoppedEvent();
        Assert.True(stop.Reason == StopReasons.Step && stop.Source?.Path == decompiled && stop.Line > awaited,
            $"A step over WaitForExitAsync's await at line {awaited} ({lines[awaited - 1].Trim()}) stopped for {stop.Reason} at {stop.Source?.Path}:{stop.Line}, "
            + "not on a line of it after the await");
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
