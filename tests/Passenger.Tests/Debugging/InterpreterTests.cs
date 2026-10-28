using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>Expressions the debugger's IL interpreter evaluates without running the program's code,
/// in the shapes upstream SharpDbg's #30 lists as wrong.</summary>
[Collection(DebugSession.Collection)]
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
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true)
            .WaitForInitializedEvent();
        await host.BreakpointsAsync([Line(bumper), Line(interpreter)], Debuggees.Source("PassengerCases", "Program.cs")).ConfigurationDoneAsync();

        int frame = await StoppedAt(host, bumper);
        await Evaluate(host, frame, "item.Bump()");
        Assert.Equal("1", (await Evaluate(host, frame, "((Bumped)(object)item).N")).Value);
        await host.ContinueAsync();
        frame = await StoppedAt(host, interpreter);

        Assert.Equal(("-6", "int"), await Evaluate(host, frame, "~myInt"));
        Assert.Equal(("7", "int"), await Evaluate(host, frame, "checked((int)myUInt)"));
        Assert.Equal("-1", (await Evaluate(host, frame, "(int)minusOne")).Value);
        Assert.Equal("65535", (await Evaluate(host, frame, "(int)wide")).Value);
        Assert.Equal("2", (await Evaluate(host, frame, "new Counter[2].Length")).Value);
        // A Pair<int> in a box is not a Pair<string>.
        Assert.Contains("InvalidCast", (await Evaluate(host, frame, "((Pair<string>)boxedPair).Value")).Value, StringComparison.Ordinal);
        await Evaluate(host, frame, "counter.Increment()");
        Assert.Equal("1", (await Evaluate(host, frame, "counter.Count")).Value);
        // An assignment is its new value, even of a struct a constructor made in the program (which neuters the frame).
        Assert.Equal("10", (await Evaluate(host, frame, "price = 10m")).Value);
        Assert.Equal("10", (await Evaluate(host, frame, "price")).Value);

        SetExpressionResponse set = await host.RequestAsync<SetExpressionResponse>(DapCommands.SetExpression, new SetExpressionArguments("pairOfInt.Value", "40 + 2", frame));
        Assert.Equal("42", set.Value);
        Assert.Equal("42", (await Evaluate(host, frame, "pairOfInt.Value")).Value);
        DapException refused = await Assert.ThrowsAsync<DapException>(() => host.RequestAsync<SetExpressionResponse>(DapCommands.SetExpression, new SetExpressionArguments("myInt", "\"text\"", frame)));
        Assert.NotEmpty(refused.Reason);
        Assert.Equal("5", (await Evaluate(host, frame, "myInt")).Value);

        // A multi-dimensional array's Get, Set and Address are the runtime's, which no metadata defines.
        Assert.Equal(("4", "int"), await Evaluate(host, frame, "grid[1, 1]"));
        Assert.Equal("11", (await Evaluate(host, frame, "grid[2, 0] + grid[2, 1]")).Value);
        SetExpressionResponse element = await host.RequestAsync<SetExpressionResponse>(DapCommands.SetExpression, new SetExpressionArguments("grid[0, 1]", "40 + 2", frame));
        Assert.Equal("42", element.Value);
        Assert.Equal("42", (await Evaluate(host, frame, "grid[0, 1]")).Value);
        Assert.Equal("7", (await Evaluate(host, frame, "pairGrid[1, 0].Value = 7")).Value);
        Assert.Equal("7", (await Evaluate(host, frame, "pairGrid[1, 0].Value")).Value);
        Assert.Contains("IndexOutOfRange", (await Evaluate(host, frame, "grid[3, 0]")).Value, StringComparison.Ordinal);

        // myInt is 5 and myUInt 7, each out of its target's range by its sign or by its size.
        foreach (string expression in (string[])["checked((ulong)(myInt - 6))", "checked((nuint)(myInt - 6))", "checked((byte)(myInt + 251))",
            "checked((sbyte)(myUInt + 121))", "checked((nint)(myInt * 1e30))", "checked((ulong)(myInt - 6.5))"])
        {
            string result = (await Evaluate(host, frame, expression)).Value;
            Assert.True(result.Contains("System.OverflowException", StringComparison.Ordinal) && !result.Contains("CIL execution failed", StringComparison.Ordinal),
                $"{expression} gave \"{result}\", where the program would throw an OverflowException");
        }
        Assert.Equal("0", (await Evaluate(host, frame, "checked((ulong)(myInt - 5))")).Value);
        Assert.Equal("4294967295", (await Evaluate(host, frame, "myUInt = uint.MaxValue")).Value);
        Assert.Equal("4294967295", (await Evaluate(host, frame, "myUInt")).Value);
    }

    /// <summary>A lambda watch evaluates with a large lambda as with a small one, and an expression that loops without end
    /// is stopped saying it took too long, the program as it was and evaluating on.</summary>
    [Fact]
    public async Task A_large_lambda_evaluates_as_a_small_one_does_and_an_expression_that_loops_without_end_is_stopped()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("DebuggableConsoleApp"), stopAtEntry: false, justMyCode: true)
            .WaitForInitializedEvent();
        // Stopped in MyMethod, where System.Linq is loaded and _intList is 1, 4, 8, 25.
        await host.BreakpointsAsync(Debuggees.MyMethodEnd, Debuggees.Source("DebuggableConsoleApp", "MyClass.cs")).ConfigurationDoneAsync();
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;

        // The first lambda also loads the debugger's own code into the program.
        Assert.Equal("3", (await Evaluate(host, frame, "_intList.Where(x => x > 2).Count()")).Value);
        Assert.Equal("2", (await Evaluate(host, frame, "_intList.Where(x => x > myInt).Count()")).Value);
        // A 32 K character string makes the assembly the lambda compiles to 64 KB larger.
        string text = new('x', 32 * 1024);
        Assert.Equal("4", (await Evaluate(host, frame, $"_intList.Where(x => \"{text}\".Length > x).Count()")).Value);

        CancellationToken cancelled = TestContext.Current.CancellationToken;
        Task<EvaluateResponse> evaluating = host.Connection.RequestAsync<EvaluateResponse>(DapCommands.Evaluate,
            new EvaluateArguments("Array.IndexOf<long>([.. System.Linq.Enumerable.Repeat(myInt, int.MaxValue)], 6L)", frame, "repl"), cancelled);
        Assert.True(await Waiting.Within(evaluating, TimeSpan.FromSeconds(60), cancelled),
            "An expression that loops without end was still evaluating after 60 s, holding the debugger until it ends, which it never does");
        Assert.Contains("The expression took too long to evaluate", (await evaluating).Result, StringComparison.Ordinal);
        Assert.Equal("5", (await Evaluate(host, frame, "myInt + 1")).Value);
    }

    /// <summary>A with on a record struct and on a tuple answers the changed copy and leaves the program's variable as it
    /// was: the copy is the evaluation's own, never the variable it was read from. A with on a struct the expression
    /// makes changes the struct it made.</summary>
    [Fact]
    public async Task With_on_a_struct_answers_the_changed_copy_and_leaves_the_programs_variable_as_it_was()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int frame = await StoppedInReadingAsync(host);

        Assert.Equal("Spot { X = 3, Y = 100 }", (await Evaluate(host, frame, "spot with { Y = 100 }")).Value);
        Assert.True((await Evaluate(host, frame, "spot.Y")).Value == "4", "spot with { Y = 100 } changed the program's spot, which a with only reads");
        Assert.Equal("(50, \"x\")", (await Evaluate(host, frame, "tuple with { Item1 = 50 }")).Value);
        Assert.True((await Evaluate(host, frame, "tuple.Item1")).Value == "1", "tuple with { Item1 = 50 } changed the program's tuple, which a with only reads");
        Assert.Equal("Spot { X = 1, Y = 5 }", (await Evaluate(host, frame, "new Spot(1, 2) with { Y = 5 }")).Value);
    }

    /// <summary>A char joined to a string, after it, before it and twice, answers the string the program would make: the
    /// span over the char the compiler makes keeps a value of the evaluation's own. An out argument the expression
    /// discards passes as the int it is, and a call that may keep a reference to a char the debugger lends it only for the
    /// call is refused, saying why, the program evaluating on.</summary>
    [Fact]
    public async Task A_char_joined_to_a_string_answers_the_string_the_program_would_make()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int frame = await StoppedInReadingAsync(host);

        Assert.Equal("\"helloA\"", (await Evaluate(host, frame, "word + letter")).Value);
        Assert.Equal("\"Ahello\"", (await Evaluate(host, frame, "letter + word")).Value);
        Assert.Equal("\"helloAA\"", (await Evaluate(host, frame, "word + letter + letter")).Value);
        Assert.True((await Evaluate(host, frame, "int.TryParse(\"5\", out _)")).Value == "true",
            "An out argument into a variable of the evaluation's own did not pass the int it is, which the program could not live through");
        string kept = (await Evaluate(host, frame, "System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref letter, 1).Length")).Value;
        Assert.True(kept.Contains("Not evaluated:", StringComparison.Ordinal) && kept.Contains("may keep a reference", StringComparison.Ordinal),
            $"A span over the char the debugger lends only for the call answered \"{kept}\", where it is refused, saying why");
    }

    /// <summary>The variables an expression declares, a pattern's and an out argument's, evaluate: each holds what the
    /// pattern took apart or the call put out.</summary>
    [Fact]
    public async Task Variables_a_pattern_or_an_out_argument_declares_evaluate()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int frame = await StoppedInReadingAsync(host);

        Assert.Equal("true", (await Evaluate(host, frame, "boxed is int k && k > 40")).Value);
        Assert.Equal("false", (await Evaluate(host, frame, "boxed is int k && k > 100")).Value);
        Assert.Equal("43", (await Evaluate(host, frame, "boxed is int k ? k + 1 : 0")).Value);
        Assert.Equal("true", (await Evaluate(host, frame, "word is string { Length: 5 } w && w[0] == 'h'")).Value);
        Assert.Equal("true", (await Evaluate(host, frame, "spot is { X: var x } && x == 3")).Value);
        Assert.Equal("5", (await Evaluate(host, frame, "int.TryParse(\"5\", out int parsed) ? parsed : -1")).Value);
    }

    /// <summary>A native integer evaluates as wide as the program's are, read, in arithmetic, converted to and set, each
    /// answering an nint or nuint as the program would hold it.</summary>
    [Fact]
    public async Task Native_integers_evaluate_as_wide_as_the_programs()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int frame = await StoppedInReadingAsync(host);

        Assert.Equal(("5000000000", "nint"), await Evaluate(host, frame, "native"));
        Assert.Equal(("5000000001", "nint"), await Evaluate(host, frame, "native + 1"));
        Assert.Equal(("10000000000", "nint"), await Evaluate(host, frame, "native * 2"));
        Assert.Equal(("5", "nint"), await Evaluate(host, frame, "(nint)5"));
        Assert.Equal(("18446744073709551615", "nuint"), await Evaluate(host, frame, "unsignedNative - 8"));
        Assert.Equal(("3", "nuint"), await Evaluate(host, frame, "unsignedNative / 2"));
        Assert.Equal("\"5000000000\"", (await Evaluate(host, frame, "native.ToString()")).Value);
        Assert.Equal("9", (await Evaluate(host, frame, "unsignedNative = 9")).Value);
        Assert.True((await Evaluate(host, frame, "unsignedNative")).Value == "9", "unsignedNative = 9 left the program's unsignedNative as it was");
    }

    /// <summary>A float cast to an integer it does not fit answers what the program's runtime makes of the cast, as Reading's
    /// casts holds it: the nearest value the integer holds, a type narrower than an int cut as that runtime's version cuts it.</summary>
    [Fact]
    public async Task A_float_cast_to_an_integer_it_does_not_fit_answers_what_the_programs_runtime_makes()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int frame = await StoppedInReadingAsync(host);

        string[] casts = ["(int)huge", "(int)-huge", "(uint)negOne", "(uint)(d300 * 1e8)", "(byte)(d300 * 1e8)", "(sbyte)-d300", "(ushort)(negOne * -70000)", "(nint)huge"];
        for (int cast = 0; cast < casts.Length; cast++)
        {
            string evaluated = (await Evaluate(host, frame, casts[cast])).Value;
            string run = (await Evaluate(host, frame, $"casts[{cast}]")).Value;
            Assert.True(run == $"\"{evaluated}\"", $"{casts[cast]} evaluated to {evaluated}, where the program's runtime makes {run}");
        }
    }

    /// <summary>A string the expression writes answers as the program's would, a call's receiver too, and a string's
    /// constructors make the string the program would, or throw as it would.</summary>
    [Fact]
    public async Task A_written_string_and_a_strings_constructors_answer_as_the_program_would()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int frame = await StoppedInReadingAsync(host);

        Assert.Equal(("3", "int"), await Evaluate(host, frame, "\"abc\".Length"));
        Assert.Equal(("98 'b'", "char"), await Evaluate(host, frame, "\"abc\"[1]"));
        Assert.Equal("\"ABC\"", (await Evaluate(host, frame, "\"abc\".ToUpper()")).Value);
        Assert.Equal("true", (await Evaluate(host, frame, "word.Contains(\"bell\".Substring(1))")).Value);
        Assert.Equal(("\"aaa\"", "string"), await Evaluate(host, frame, "new string('a', 3)"));
        Assert.Equal("\"AA\"", (await Evaluate(host, frame, "new string(letter, 2)")).Value);
        Assert.Equal("\"ab\"", (await Evaluate(host, frame, "new string(new[] { 'a', 'b' })")).Value);
        Assert.Equal("\"ell\"", (await Evaluate(host, frame, "new string(word.AsSpan(1, 3))")).Value);
        string negative = (await Evaluate(host, frame, "new string('a', -1)")).Value;
        Assert.True(negative.Contains("System.ArgumentOutOfRangeException", StringComparison.Ordinal),
            $"new string('a', -1) answered \"{negative}\", where the program would throw an ArgumentOutOfRangeException");
    }

    private static int Line(string code) => Debuggees.Line("PassengerCases", code);

    /// <summary>PassengerCases launched and stopped in Reading.Run once its rounds are done; the top frame.</summary>
    private static async Task<int> StoppedInReadingAsync(DebugSession host)
    {
        string reading = "Console.WriteLine(\"reading\");";
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true)
            .WaitForInitializedEvent();
        await host.BreakpointsAsync([Line(reading)], Debuggees.Source("PassengerCases", "Program.cs")).ConfigurationDoneAsync();
        return await StoppedAt(host, reading);
    }

    /// <summary>The next stop, at the line of this code; its top frame.</summary>
    private static async Task<int> StoppedAt(DebugSession host, string code)
    {
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.True(stop.ReadStopInfo().line == Line(code), $"The program was to stop at {code}, but stopped at the {stop.Reason} at line {stop.Line}");
        return (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;
    }

    private static async Task<(string Value, string? Type)> Evaluate(DebugSession host, int frame, string expression)
    {
        EvaluateResponse response = await host.EvaluateAsync(frame, expression);
        return (response.Result, response.Type);
    }
}
