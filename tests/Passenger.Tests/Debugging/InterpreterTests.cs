using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>Expressions the debugger's IL interpreter evaluates without running the program's code,
/// in the shapes upstream SharpDbg's #30 lists as wrong.</summary>
[Collection(Alone.Name)]
public class InterpreterTests(ITestOutputHelper testOutputHelper)
{
    /// <summary>In one program, at Bumper.Work and then at Interpreter.Run's end: a constrained call on a struct argument,
    /// integers, enums, conversions and unboxing, Set Value, a multi-dimensional array's elements, and checked conversions.
    /// Each part names itself in its failure, and leaves the locals the next reads as they were.</summary>
    [Fact]
    public async Task Integers_enums_conversions_unboxing_arrays_and_set_value_evaluate_as_the_language_says()
    {
        string bumper = "Console.WriteLine(\"bumper\");";
        string interpreter = "Console.WriteLine(\"interpreter\");";
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .WithInitializeRequest()
            .WithLaunchRequest(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true)
            .WaitForInitializedEvent();
        host.WithBreakpointsRequest([Line(bumper), Line(interpreter)], Debuggees.Source("PassengerCases", "Program.cs")).WithConfigurationDoneRequest();

        int frame = await StoppedAt(host, bumper);
        Evaluate(host, frame, "item.Bump()");
        Assert.Equal("1", Evaluate(host, frame, "((Bumped)(object)item).N").Value);
        host.WithContinueRequest();
        frame = await StoppedAt(host, interpreter);

        Assert.Equal(("-6", "int"), Evaluate(host, frame, "~myInt"));
        Assert.Equal(("7", "int"), Evaluate(host, frame, "checked((int)myUInt)"));
        Assert.Equal("-1", Evaluate(host, frame, "(int)minusOne").Value);
        Assert.Equal("65535", Evaluate(host, frame, "(int)wide").Value);
        Assert.Equal("2", Evaluate(host, frame, "new Counter[2].Length").Value);
        // A Pair<int> in a box is not a Pair<string>.
        Assert.Contains("InvalidCast", Evaluate(host, frame, "((Pair<string>)boxedPair).Value").Value, StringComparison.Ordinal);
        Evaluate(host, frame, "counter.Increment()");
        Assert.Equal("1", Evaluate(host, frame, "counter.Count").Value);
        // An assignment is its new value, even of a struct a constructor made in the program (which neuters the frame).
        Assert.Equal("10", Evaluate(host, frame, "price = 10m").Value);
        Assert.Equal("10", Evaluate(host, frame, "price").Value);

        SetExpressionResponse set = host.Request<SetExpressionResponse>(DapCommands.SetExpression, new SetExpressionArguments("pairOfInt.Value", "40 + 2", frame));
        Assert.Equal("42", set.Value);
        Assert.Equal("42", Evaluate(host, frame, "pairOfInt.Value").Value);
        DapException refused = Assert.Throws<DapException>(() => host.Request<SetExpressionResponse>(DapCommands.SetExpression, new SetExpressionArguments("myInt", "\"text\"", frame)));
        Assert.NotEmpty(refused.Reason);
        Assert.Equal("5", Evaluate(host, frame, "myInt").Value);

        // A multi-dimensional array's Get, Set and Address are the runtime's, which no metadata defines.
        Assert.Equal(("4", "int"), Evaluate(host, frame, "grid[1, 1]"));
        Assert.Equal("11", Evaluate(host, frame, "grid[2, 0] + grid[2, 1]").Value);
        SetExpressionResponse element = host.Request<SetExpressionResponse>(DapCommands.SetExpression, new SetExpressionArguments("grid[0, 1]", "40 + 2", frame));
        Assert.Equal("42", element.Value);
        Assert.Equal("42", Evaluate(host, frame, "grid[0, 1]").Value);
        Assert.Equal("7", Evaluate(host, frame, "pairGrid[1, 0].Value = 7").Value);
        Assert.Equal("7", Evaluate(host, frame, "pairGrid[1, 0].Value").Value);
        Assert.Contains("IndexOutOfRange", Evaluate(host, frame, "grid[3, 0]").Value, StringComparison.Ordinal);

        // myInt is 5 and myUInt 7, each out of its target's range by its sign or by its size.
        foreach (string expression in (string[])["checked((ulong)(myInt - 6))", "checked((nuint)(myInt - 6))", "checked((byte)(myInt + 251))",
            "checked((sbyte)(myUInt + 121))", "checked((nint)(myInt * 1e30))", "checked((ulong)(myInt - 6.5))"])
        {
            string result = Evaluate(host, frame, expression).Value;
            Assert.True(result.Contains("System.OverflowException", StringComparison.Ordinal) && !result.Contains("CIL execution failed", StringComparison.Ordinal),
                $"{expression} gave \"{result}\", where the program would throw an OverflowException");
        }
        Assert.Equal("0", Evaluate(host, frame, "checked((ulong)(myInt - 5))").Value);
        Assert.Equal("4294967295", Evaluate(host, frame, "myUInt = uint.MaxValue").Value);
        Assert.Equal("4294967295", Evaluate(host, frame, "myUInt").Value);
    }

    /// <summary>A lambda watch evaluates as quickly with a large lambda as with a small one, and an expression that loops
    /// without end is stopped saying it took too long, the program as it was and evaluating on.</summary>
    [Fact]
    public async Task A_large_lambda_evaluates_as_quickly_as_a_small_one_and_an_expression_that_loops_without_end_is_stopped()
    {
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .WithInitializeRequest()
            .WithLaunchRequest(Debuggees.Program("DebuggableConsoleApp"), stopAtEntry: false, justMyCode: true)
            .WaitForInitializedEvent();
        // Stopped in MyMethod, where System.Linq is loaded and _intList is 1, 4, 8, 25.
        host.WithBreakpointsRequest(Debuggees.MyMethodEnd, Debuggees.Source("DebuggableConsoleApp", "MyClass.cs")).WithConfigurationDoneRequest();
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int frame = host.GetTopStackFrame(stop.ThreadId!.Value).Id;

        // The first lambda also loads the debugger's own code into the program.
        Assert.Equal("3", Evaluate(host, frame, "_intList.Where(x => x > 2).Count()").Value);
        Stopwatch watch = Stopwatch.StartNew();
        Assert.Equal("2", Evaluate(host, frame, "_intList.Where(x => x > myInt).Count()").Value);
        TimeSpan small = watch.Elapsed;
        // A 32 K character string makes the assembly the lambda compiles to 64 KB larger.
        string text = new('x', 32 * 1024);
        watch.Restart();
        Assert.Equal("4", Evaluate(host, frame, $"_intList.Where(x => \"{text}\".Length > x).Count()").Value);
        TimeSpan large = watch.Elapsed;
        testOutputHelper.WriteLine($"Small lambda {small.TotalMilliseconds:0} ms, large lambda {large.TotalMilliseconds:0} ms");
        Assert.True(large < small + TimeSpan.FromSeconds(1),
            $"A lambda 64 KB larger took {large.TotalMilliseconds:0} ms to evaluate, where a small one took {small.TotalMilliseconds:0} ms: its code went into the program a byte at a time");

        CancellationToken cancelled = TestContext.Current.CancellationToken;
        Task<EvaluateResponse> evaluating = host.Connection.RequestAsync<EvaluateResponse>(DapCommands.Evaluate,
            new EvaluateArguments("Array.IndexOf<long>([.. System.Linq.Enumerable.Repeat(myInt, int.MaxValue)], 6L)", frame, "repl"), cancelled);
        Assert.True(await Waiting.Within(evaluating, TimeSpan.FromSeconds(60), cancelled),
            "An expression that loops without end was still evaluating after 60 s, holding the debugger until it ends, which it never does");
        Assert.Contains("The expression took too long to evaluate", (await evaluating).Result, StringComparison.Ordinal);
        Assert.Equal("5", Evaluate(host, frame, "myInt + 1").Value);
    }

    private static int Line(string code) => Debuggees.Line("PassengerCases", code);

    /// <summary>The next stop, at the line of this code; its top frame.</summary>
    private static async Task<int> StoppedAt(DebugSession host, string code)
    {
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.True(stop.ReadStopInfo().line == Line(code), $"The program was to stop at {code}, but stopped at the {stop.Reason} at line {stop.Line}");
        return host.GetTopStackFrame(stop.ThreadId!.Value).Id;
    }

    private static (string Value, string? Type) Evaluate(DebugSession host, int frame, string expression)
    {
        host.WithEvaluateRequest(frame, expression, out EvaluateResponse response);
        return (response.Result, response.Type);
    }
}
