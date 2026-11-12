using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>Stops under code its own attributes hide from debuggers and stack traces alike, the runtime's helpers the
/// compiled code calls and a method of the program's own: the stack leaves that code out, on the .NET the program targets
/// and on the newest, and a stop or step shows and goes from the frame that called it.</summary>
// Its stops read the exception's text by an evaluation the engine holds to 5 s, which a loaded machine can outrun.
[Collection(Alone.Name)]
public class HiddenFramesTests(ITestOutputHelper testOutputHelper)
{
    private const string Cases = "PassengerCases";
    private static string Source => Debuggees.Source(Cases, "HiddenFrames.cs");
    private static readonly int Indexed = Debuggees.Line(Cases, "HiddenFrames.cs", "numbers[5] = 1;");
    private static readonly int CaughtPastTheEnd = Debuggees.Line(Cases, "HiddenFrames.cs", "catch (IndexOutOfRangeException)");
    private static readonly int FailCalled = Debuggees.Line(Cases, "HiddenFrames.cs", "Fail(\"hidden\");");
    private static readonly int Initializing = Debuggees.Line(Cases, "HiddenFrames.cs", "Value = 7;");
    /// <summary>Names in the methods the stops run under, which their attributes hide.</summary>
    private static readonly string[] Hidden = ["ThrowHelpers", "InitHelpers", "StaticsHelpers", "System.Runtime.EH.", "HiddenFrames.Fail("];
    private static readonly int InitializedRead = Debuggees.Line(Cases, "HiddenFrames.cs", "Console.WriteLine(Initialized.Value);");

    /// <summary>The breakpoint on every exception stops at the index the runtime's helper throws for, in the async method
    /// with its locals, and at the call of the program's hidden method that throws, in its caller.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_exception_a_runtime_helper_or_the_programs_hidden_method_throws_stops_in_the_frame_that_called_it(bool newestRuntime)
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, newestRuntime, justMyCode: true, new SetExceptionBreakpointsArguments([DapExceptionFilters.All]));

        StoppedEvent indexed = await host.WaitForStoppedEvent();
        await AssertStoppedIn(host, indexed, StopReasons.Exception, Indexed, "IndexesPastTheEndAsync", "the index past the array's end");
        Assert.Single(await host.LocalsAsync(indexed), v => v.Name == "numbers");
        await AssertOnRuntime(host, indexed, newestRuntime);

        StoppedEvent failed = await host.ContinueAsync().WaitForStoppedEvent();
        for (int stops = 0; failed.Line != FailCalled && stops < 4; stops++)
        {
            failed = await host.ContinueAsync().WaitForStoppedEvent();
        }
        await AssertStoppedIn(host, failed, StopReasons.Exception, FailCalled, "RunAsync", "the program's hidden method's throw");
    }

    /// <summary>A step out of the async method from the exception the runtime's helper threw in it, stopped at by the
    /// breakpoint the IDE sets on every exception, handled in the user's code too, and turned off before the step so the
    /// step alone stops: the step ends where the awaiting caller catches it, as the async step out of the method shown.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_step_out_of_an_async_method_from_an_exception_a_runtime_helper_threw_stops_at_the_callers_catch(bool newestRuntime)
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        DapExceptionOptions handled = new([new DapExceptionPathSegment(["CLR"])], DapBreakModes.Always)
        {
            Scenarios = ExceptionScenarioNames.Of(ExceptionScenarios.All),
        };
        await Launch(host, newestRuntime, justMyCode: true, new SetExceptionBreakpointsArguments([], ExceptionOptions: [handled]));

        StoppedEvent indexed = await host.WaitForStoppedEvent();
        await AssertStoppedIn(host, indexed, StopReasons.Exception, Indexed, "IndexesPastTheEndAsync", "the index past the array's end");
        await AssertOnRuntime(host, indexed, newestRuntime);
        await host.RequestAsync(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([], ExceptionOptions: []));
        StoppedEvent outOf = await host.StepOutAsync(indexed.ThreadId!.Value).WaitForStoppedEvent();
        await AssertStoppedIn(host, outOf, StopReasons.Step, CaughtPastTheEnd, "RunAsync", "the step out of the async method");
    }

    /// <summary>Without Just My Code, a step out of a static constructor, which from .NET 11 the runtime's hidden InitHelpers
    /// run, goes on out of them to the program's code that read the class, rather than ending in a frame the stack leaves
    /// out. On the newest .NET only: .NET 10 runs the constructor from native code, out of which such a step ends nowhere.</summary>
    [Fact]
    public async Task A_step_out_of_a_static_constructor_without_Just_My_Code_stops_where_the_class_was_read()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, newestRuntime: true, justMyCode: false, null, Initializing);

        StoppedEvent initializing = await host.WaitForStoppedEvent();
        await AssertStoppedIn(host, initializing, StopReasons.Breakpoint, Initializing, ".cctor", "the breakpoint in the static constructor");
        await AssertOnRuntime(host, initializing, newestRuntime: true);
        StoppedEvent outOf = await host.StepOutAsync(initializing.ThreadId!.Value).WaitForStoppedEvent();
        await AssertStoppedIn(host, outOf, StopReasons.Step, InitializedRead, "RunAsync", "the step out of the static constructor");
    }

    private static async Task Launch(DebugSession host, bool newestRuntime, bool justMyCode, SetExceptionBreakpointsArguments? exceptions, int? breakpoint = null)
    {
        await host.InitializeAsync()
            .LaunchAsync(Debuggees.Program(Cases), stopAtEntry: false, justMyCode, ["hidden frames"], environment: newestRuntime ? Debuggees.NewestRuntime : null)
            .WaitForInitializedEvent();
        if (exceptions is not null)
        {
            await host.RequestAsync(DapCommands.SetExceptionBreakpoints, exceptions);
        }
        if (breakpoint is int line)
        {
            await host.BreakpointsAsync(line, Source);
        }
        await host.ConfigurationDoneAsync();
    }

    /// <summary>The stop is at the line given, its top frame the named method's at that line, and no frame of the stack is
    /// one the stop's code hides.</summary>
    private static async Task AssertStoppedIn(DebugSession host, StoppedEvent stop, string reason, int line, string method, string what)
    {
        IReadOnlyList<DapStackFrame> frames = (await host.StackTraceAsync(stop.ThreadId!.Value, levels: null)).StackFrames;
        string told = string.Join(" < ", frames.Take(6).Select(f => $"{f.Name}:{f.Line}"));
        Assert.True(stop.Reason == reason && stop.Line == line && frames is [{ } top, ..] && top.Line == line && top.Name.Contains($"{method}(", StringComparison.Ordinal),
            $"{what} stopped for {stop.Reason} at line {stop.Line}, where {reason} at line {line} in {method} was expected, its frames {told}");
        Assert.False(frames.Any(f => Hidden.Any(name => f.Name.Contains(name, StringComparison.Ordinal))),
            $"{what} listed code its attributes hide among the frames the person picks from: {told}");
    }

    private static async Task AssertOnRuntime(DebugSession host, StoppedEvent stop, bool newestRuntime)
    {
        if (newestRuntime)
        {
            Assert.Equal("true", (await host.EvaluateAsync((await host.TopFrameAsync(stop.ThreadId!.Value)).Id, Debuggees.RunsOnNewestRuntime)).Result);
        }
    }
}
