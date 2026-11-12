using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>Stops under code its own attributes hide from debuggers and stack traces alike, the runtime's helpers the
/// compiled code calls and a method of the program's own, on the .NET the program targets and on the newest: the stack
/// leaves that code out, and a stop shows and steps from the frame that called it; without Just My Code a step that ends
/// in such code shows it on top, where it stopped, before anything after it runs, and a step leaving code the runtime's
/// native code called ends in the frame shown below it.</summary>
// Its stops read the exception's text by an evaluation the engine holds to 5 s, which a loaded machine can outrun.
[Collection(Alone.Name)]
public class HiddenFramesTests(ITestOutputHelper testOutputHelper)
{
    private const string Cases = "PassengerCases";
    private static string Source => Debuggees.Source(Cases, "HiddenFrames.cs");
    private static readonly int Indexed = Debuggees.Line(Cases, "HiddenFrames.cs", "numbers[5] = 1;");
    private static readonly int CaughtPastTheEnd = Debuggees.Line(Cases, "HiddenFrames.cs", "catch (IndexOutOfRangeException)");
    private static readonly int FailCalled = Debuggees.Line(Cases, "HiddenFrames.cs", "Fail(\"hidden\");");
    private static readonly int LaterInitializing = Debuggees.Line(Cases, "HiddenFrames.cs", "Value = 5;");
    private static readonly int LaterRead = Debuggees.Line(Cases, "HiddenFrames.cs", "int sum = Later.Value + Bump();");
    private static readonly int BumpDeclared = Debuggees.Line(Cases, "HiddenFrames.cs", "private static int Bump() => ++Bumped;");

    /// <summary>Names in the methods the stops run under, which their attributes hide.</summary>
    private static readonly string[] Hidden = ["ThrowHelpers", "InitHelpers", "StaticsHelpers", "System.Runtime.EH.", "HiddenFrames.Fail("];

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

    /// <summary>Without Just My Code, a step into a line that reads a class with a static constructor, then calls a method,
    /// stops in the runtime's hidden helper that finds the class's statics, shown on top where the stop is, with nothing
    /// past it run; a step out of the helper comes back to the line, and a step into it enters the method.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_step_into_a_line_reading_a_class_without_Just_My_Code_stops_in_the_helper_shown_and_goes_on_to_the_call(bool newestRuntime)
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, newestRuntime, justMyCode: false, null, LaterRead);
        StoppedEvent read = await host.WaitForStoppedEvent();
        await AssertOnRuntime(host, read, newestRuntime);

        StoppedEvent helper = await host.StepInAsync(read.ThreadId!.Value).WaitForStoppedEvent();
        await AssertStoppedInTopFrame(host, helper, "the step into the line reading the class");
        Assert.NotEqual(Source, helper.Source?.Path);
        StoppedEvent back = await host.StepOutAsync(helper.ThreadId!.Value).WaitForStoppedEvent();
        await AssertStoppedInTopFrame(host, back, "the step out of the helper");
        Assert.Equal((Source, LaterRead), (back.Source?.Path, back.Line));
        StoppedEvent bumping = await host.StepInAsync(back.ThreadId!.Value).WaitForStoppedEvent();
        await AssertStoppedInTopFrame(host, bumping, "the step into the rest of the line");
        Assert.Equal((Source, BumpDeclared), (bumping.Source?.Path, bumping.Line));
    }

    /// <summary>Without Just My Code, a step out of a static constructor stops before the rest of the line that read the
    /// class runs, in the stack's top frame: on the newest .NET in the runtime's hidden InitHelpers that called it, shown on
    /// top; on .NET 10, whose runtime calls it from native code, back in the code that read the class.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_step_out_of_a_static_constructor_without_Just_My_Code_stops_before_the_rest_of_the_line_runs(bool newestRuntime)
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, newestRuntime, justMyCode: false, null, LaterInitializing);
        StoppedEvent initializing = await host.WaitForStoppedEvent();
        await AssertOnRuntime(host, initializing, newestRuntime);

        StoppedEvent outOf = await host.StepOutAsync(initializing.ThreadId!.Value).WaitForStoppedEvent();
        await AssertStoppedInTopFrame(host, outOf, "the step out of the static constructor");
    }

    /// <summary>Without Just My Code, steps over, or out, from a static constructor come back to the line that read the
    /// class before the rest of it runs, each stopping in the stack's top frame, through the runtime's hidden code that
    /// called the constructor where a step stops in it (the newest .NET's CallClassConstructor): none is lost where that
    /// code, or the constructor itself, returns to the runtime's native code.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Steps_from_a_static_constructor_without_Just_My_Code_come_back_to_the_line_that_read_the_class_before_the_rest_runs(bool newestRuntime, bool over)
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, newestRuntime, justMyCode: false, null, LaterInitializing);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        await AssertOnRuntime(host, stop, newestRuntime);

        int thread = stop.ThreadId!.Value;
        string step = over ? "step over" : "step out";
        List<string> stops = [];
        for (int steps = 1; (stop.Source?.Path, stop.Line) != (Source, LaterRead) && steps <= 5; steps++)
        {
            stop = await (over ? host.StepOverAsync(thread) : host.StepOutAsync(thread)).WaitForStoppedEvent();
            await AssertStoppedInTopFrame(host, stop, $"{step} {steps} from the static constructor");
            stops.Add($"{Path.GetFileName(stop.Source?.Path)}:{stop.Line}");
        }
        Assert.True((stop.Source?.Path, stop.Line) == (Source, LaterRead),
            $"Each {step} from the static constructor stopped short of the line that read the class, at {string.Join(", then ", stops)}");
    }

    /// <summary>A step's stop is in the stack's top frame, at its line, and the line's later call, Bump, has not run.</summary>
    private static async Task AssertStoppedInTopFrame(DebugSession host, StoppedEvent stop, string what)
    {
        IReadOnlyList<DapStackFrame> frames = (await host.StackTraceAsync(stop.ThreadId!.Value, levels: null)).StackFrames;
        string told = string.Join(" < ", frames.Take(4).Select(f => $"{f.Name}:{f.Line}"));
        Assert.True(stop.Reason == StopReasons.Step && frames is [{ } top, ..] && top.Source?.Path == stop.Source?.Path && top.Line == stop.Line,
            $"{what} stopped for {stop.Reason} at {stop.Source?.Path}:{stop.Line}, which is not the top frame of {told}");
        string bumped = (await host.EvaluateAsync(frames[0].Id, "HiddenFrames.Bumped")).Result;
        Assert.True(bumped == "0", $"{what} stopped where HiddenFrames.Bumped reads {bumped}, not the 0 that says the line's later call has not run, at {told}");
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
