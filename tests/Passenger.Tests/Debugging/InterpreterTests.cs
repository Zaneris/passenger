using Passenger.Core;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>Expressions the debugger's IL interpreter evaluates without running the program's code,
/// in the shapes upstream SharpDbg's #30 lists as wrong.</summary>
[Collection(DebugSession.Collection)]
public class InterpreterTests(ITestOutputHelper testOutputHelper)
{
    /// <summary>The evaluator's C#: integers, enums, conversions, unboxing, arrays and Set Value as the language says; a
    /// pattern's or out argument's variables; With on a struct leaving the program's variable; arrays made as the program
    /// makes them; a delegate given to a constructor and an anonymous type made in the program; a large lambda as a small
    /// one, one that loops without end stopped, the making of a session's first lambda not counted in its time; and a call
    /// that throws, an index outside an array and a null named with the exception the program would throw.</summary>
    [Fact]
    public async Task An_expression_evaluates_as_the_language_says_and_one_that_loops_or_throws_is_stopped_or_named_as_the_program_would()
    {
        await Flow.StepApartAsync<InterpreterTests>("integers enums conversions unboxing arrays and set value evaluate as the language says", () => new InterpreterTests(testOutputHelper), t => t.Integers_enums_conversions_unboxing_arrays_and_set_value_evaluate_as_the_language_says());
        await Flow.StepApartAsync<InterpreterTests>("variables a pattern or an out argument declares evaluate", () => new InterpreterTests(testOutputHelper), t => t.Variables_a_pattern_or_an_out_argument_declares_evaluate());
        await Flow.StepApartAsync<InterpreterTests>("with on a struct answers the changed copy and leaves the program's variable as it was", () => new InterpreterTests(testOutputHelper), t => t.With_on_a_struct_answers_the_changed_copy_and_leaves_the_programs_variable_as_it_was());
        await Flow.StepApartAsync<InterpreterTests>("an array initializer of constants makes the array the program would", () => new InterpreterTests(testOutputHelper), t => t.An_array_initializer_of_constants_makes_the_array_the_program_would());
        await Flow.StepApartAsync<InterpreterTests>("a multi dimensional array is made as the program would make it", () => new InterpreterTests(testOutputHelper), t => t.A_multi_dimensional_array_is_made_as_the_program_would_make_it());
        await Flow.StepApartAsync<InterpreterTests>("a delegate given to a constructor is made in the program as one given to a method is", () => new InterpreterTests(testOutputHelper), t => t.A_delegate_given_to_a_constructor_is_made_in_the_program_as_one_given_to_a_method_is());
        await Flow.StepApartAsync<InterpreterTests>("a large lambda evaluates as a small one does and an expression that loops without end is stopped", () => new InterpreterTests(testOutputHelper), t => t.A_large_lambda_evaluates_as_a_small_one_does_and_an_expression_that_loops_without_end_is_stopped());
        await Flow.StepApartAsync<InterpreterTests>("the making of a session's first lambda in the program is not counted in the expression's time", () => new InterpreterTests(testOutputHelper), t => t.The_making_of_a_sessions_first_lambda_in_the_program_is_not_counted_in_the_expressions_time());
        await Flow.StepApartAsync<InterpreterTests>("a call that throws in the program is named with what its exception says", () => new InterpreterTests(testOutputHelper), t => t.A_call_that_throws_in_the_program_is_named_with_what_its_exception_says());
        await Flow.StepApartAsync<InterpreterTests>("an index outside an array throws the IndexOutOfRangeException the program would", () => new InterpreterTests(testOutputHelper), t => t.An_index_outside_an_array_throws_the_IndexOutOfRangeException_the_program_would());
        await Flow.StepApartAsync<InterpreterTests>("a null array or reference throws the NullReferenceException the program would", () => new InterpreterTests(testOutputHelper), t => t.A_null_array_or_reference_throws_the_NullReferenceException_the_program_would());
    }

    /// <summary>In one program, at Bumper.Work and then at Interpreter.Run's end: a constrained call on a struct argument,
    /// integers, enums, conversions and unboxing, Set Value, a multi-dimensional array's elements, and checked conversions.
    /// Each part names itself in its failure, and leaves the locals the next reads as they were.</summary>
    private async Task Integers_enums_conversions_unboxing_arrays_and_set_value_evaluate_as_the_language_says()
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

    /// <summary>A lambda watch evaluates with a large lambda as with a small one, an anonymous type, the evaluation's own, is
    /// made by a lambda in the program and by the expression itself, and an expression that loops without end is stopped
    /// saying it took too long, the program as it was and evaluating on.</summary>
    private async Task A_large_lambda_evaluates_as_a_small_one_does_and_an_expression_that_loops_without_end_is_stopped()
    {
        // The first lambda loads the debugger's own code into the program, slow on a loaded machine.
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper, environment: DebugSession.ScaledEvaluations);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("DebuggableConsoleApp"), stopAtEntry: false, justMyCode: true)
            .WaitForInitializedEvent();
        // Stopped in MyMethod, where System.Linq is loaded and _intList is 1, 4, 8, 25.
        await host.BreakpointsAsync(Debuggees.MyMethodEnd, Debuggees.Source("DebuggableConsoleApp", "MyClass.cs")).ConfigurationDoneAsync();
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;

        Assert.Equal("3", (await Evaluate(host, frame, "_intList.Where(x => x > 2).Count()")).Value);
        Assert.Equal("2", (await Evaluate(host, frame, "_intList.Where(x => x > myInt).Count()")).Value);
        // A 32 K character string makes the assembly the lambda compiles to 64 KB larger.
        string text = new('x', 32 * 1024);
        Assert.Equal("4", (await Evaluate(host, frame, $"_intList.Where(x => \"{text}\".Length > x).Count()")).Value);
        Assert.Equal("12", (await Evaluate(host, frame, "_intList.Select(x => new { Half = x / 2 }).Last().Half")).Value);
        Assert.Equal("{ A = 4, B = \"two\" }", (await Evaluate(host, frame, "new { A = myInt, B = \"two\" }")).Value);
        Assert.Equal("\"{ }\"", (await Evaluate(host, frame, "new { }.ToString()")).Value);

        CancellationToken cancelled = TestContext.Current.CancellationToken;
        Task<EvaluateResponse> evaluating = host.Connection.RequestAsync<EvaluateResponse>(DapCommands.Evaluate,
            new EvaluateArguments("Array.IndexOf<long>([.. System.Linq.Enumerable.Repeat(myInt, int.MaxValue)], 6L)", frame, EvaluateContexts.Repl), cancelled);
        Assert.True(await Waiting.Within(evaluating, TimeSpan.FromSeconds(60), cancelled),
            "An expression that loops without end was still evaluating after 60 s, holding the debugger until it ends, which it never does");
        Assert.Contains("The expression took too long to evaluate", (await evaluating).Result, StringComparison.Ordinal);
        Assert.Equal("5", (await Evaluate(host, frame, "myInt + 1")).Value);
    }

    /// <summary>The debugger's making of an expression's lambda in the program, its own code compiled and loaded there, is
    /// no part of the expression's time: held as long as an expression may take, as a busy machine once took it, the
    /// session's first lambda still answers, and so does the next, on the run's own limit.</summary>
    private async Task The_making_of_a_sessions_first_lambda_in_the_program_is_not_counted_in_the_expressions_time()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper, environment: new Dictionary<string, string> { [AdapterEnvironment.HoldLambdaVariable] = "1" });
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("DebuggableConsoleApp"), stopAtEntry: false, justMyCode: true)
            .WaitForInitializedEvent();
        // Stopped in MyMethod, where System.Linq is loaded and _intList is 1, 4, 8, 25.
        await host.BreakpointsAsync(Debuggees.MyMethodEnd, Debuggees.Source("DebuggableConsoleApp", "MyClass.cs")).ConfigurationDoneAsync();
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;

        (string first, _) = await Evaluate(host, frame, "_intList.Where(x => x > 2).Count()");
        Assert.True(first == "3", $"The session's first lambda, its making in the program held past the expression's whole time, was answered {first}; the engine's log:\n{host.EngineLog()}");
        Assert.Contains("Held: the making of the session's first lambda", host.EngineLog(), StringComparison.Ordinal);
        Assert.Equal("2", (await Evaluate(host, frame, "_intList.Where(x => x > myInt).Count()")).Value);
    }

    /// <summary>A with on a record struct and on a tuple answers the changed copy and leaves the program's variable as it
    /// was: the copy is the evaluation's own, never the variable it was read from. A with on a struct the expression
    /// makes changes the struct it made.</summary>
    private async Task With_on_a_struct_answers_the_changed_copy_and_leaves_the_programs_variable_as_it_was()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int frame = await StoppedInReadingAsync(host);

        Assert.Equal("Spot { X = 3, Y = 100 }", (await Evaluate(host, frame, "spot with { Y = 100 }")).Value);
        Assert.True((await Evaluate(host, frame, "spot.Y")).Value == "4", "spot with { Y = 100 } changed the program's spot, which a with only reads");
        Assert.Equal("(50, \"x\")", (await Evaluate(host, frame, "tuple with { Item1 = 50 }")).Value);
        Assert.True((await Evaluate(host, frame, "tuple.Item1")).Value == "1", "tuple with { Item1 = 50 } changed the program's tuple, which a with only reads");
        Assert.Equal("Spot { X = 1, Y = 5 }", (await Evaluate(host, frame, "new Spot(1, 2) with { Y = 5 }")).Value);
    }

    /// <summary>The variables an expression declares, a pattern's and an out argument's, evaluate: each holds what the
    /// pattern took apart or the call put out.</summary>
    private async Task Variables_a_pattern_or_an_out_argument_declares_evaluate()
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

    /// <summary>An array initializer of constants makes the array the program would, of any length and element type, and
    /// a span over constants likewise: each element is the constant written for it.</summary>
    private async Task An_array_initializer_of_constants_makes_the_array_the_program_would()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int frame = await StoppedInReadingAsync(host);

        Assert.Equal(("3", "int"), await Evaluate(host, frame, "new[] { 1, 2, 3 }.Length"));
        Assert.Equal(("3", "int"), await Evaluate(host, frame, "new[] { 1, 2, 3 }[2]"));
        Assert.Equal("20", (await Evaluate(host, frame, "new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 }[19]")).Value);
        Assert.Equal("\"bc\"", (await Evaluate(host, frame, "new string(new[] { 'a', 'b', 'c' }, 1, 2)")).Value);
        Assert.Equal("5000000000", (await Evaluate(host, frame, "new long[] { 1, 2, 5000000000 }[2]")).Value);
        Assert.Equal("2.5", (await Evaluate(host, frame, "new[] { 1.5, 2.5, 3.5 }[1]")).Value);
        Assert.Equal("-3", (await Evaluate(host, frame, "new sbyte[] { -1, -2, -3 }[2]")).Value);
        Assert.Equal("MinusOne", (await Evaluate(host, frame, "new[] { SignedEnum.MinusOne, SignedEnum.MinusOne, SignedEnum.MinusOne }[2]")).Value);
        // Of types that derive from nothing, object and an interface.
        Assert.Equal("\"two\"", (await Evaluate(host, frame, "new object[] { 1, \"two\" }[1]")).Value);
        Assert.Equal("2", (await Evaluate(host, frame, "new IComparable[] { 1, \"two\" }.Length")).Value);
        Assert.Equal("5", (await Evaluate(host, frame, "new[] { 1, 2, 3, word.Length }[3]")).Value);
        Assert.Equal("5", (await Evaluate(host, frame, "word.Split('e', 'l', 'o').Length")).Value);
        Assert.Equal("98", (await Evaluate(host, frame, "\"abc\"u8[1]")).Value);
    }

    /// <summary>A multi-dimensional array the expression makes is the one the program would make: of its rank and lengths,
    /// filled as its initializer says, of any element type, its elements read and written; a negative length throws as
    /// the program's would.</summary>
    private async Task A_multi_dimensional_array_is_made_as_the_program_would_make_it()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int frame = await StoppedInReadingAsync(host);

        Assert.Equal(("3", "int"), await Evaluate(host, frame, "new int[,] { {1,2},{3,4} }[1,0]"));
        Assert.Equal("6", (await Evaluate(host, frame, "new int[,,] { { { 1, 2 }, { 3, 4 } }, { { 5, 6 }, { 7, 8 } } }[1, 0, 1]")).Value);
        Assert.Equal("2.5", (await Evaluate(host, frame, "new double[,] { { 1.5, 2.5 }, { 3.5, 4.5 } }[0, 1]")).Value);
        Assert.Equal("MinusOne", (await Evaluate(host, frame, "new SignedEnum[,] { { SignedEnum.MinusOne, SignedEnum.MinusOne }, { SignedEnum.MinusOne, SignedEnum.MinusOne } }[1, 1]")).Value);
        Assert.Equal("\"hello\"", (await Evaluate(host, frame, "new string[,] { { \"a\", \"b\" }, { \"c\", word } }[1, 1]")).Value);
        Assert.Equal("7", (await Evaluate(host, frame, "new Spot[,] { { spot, new Spot(7, 8) } }[0, 1].X")).Value);
        Assert.Equal("6", (await Evaluate(host, frame, "new int[2, 3].Length")).Value);
        Assert.Equal("3", (await Evaluate(host, frame, "new Spot[2, 3].GetLength(1)")).Value);
        Assert.Equal("9", (await Evaluate(host, frame, "new int[,] { { 1, 2 }, { 3, 4 } }[1, 1] = 9")).Value);
        string negative = (await Evaluate(host, frame, "new int[letter - 'B', 2]")).Value;
        Assert.True(negative.StartsWith("error: Evaluation threw System.OverflowException", StringComparison.Ordinal),
            $"new int[letter - 'B', 2] answered \"{negative}\", where the program would throw an OverflowException for the length of -1");
    }

    /// <summary>Evaluates <paramref name="expression"/>, which is to answer <paramref name="answer"/> having run
    /// <paramref name="calls"/> calls in the program, as the engine's log tells each one's end.</summary>
    private static async Task Called(DebugSession host, int frame, string expression, string answer, int calls)
    {
        string answered = (await Evaluate(host, frame, expression)).Value;
        string log = host.EngineLog();
        string[] lines = log[Math.Max(0, log.LastIndexOf($"Evaluate: {expression}", StringComparison.Ordinal))..].Split('\n');
        int ran = lines.Count(line => line.Contains("Event: EvalComplete", StringComparison.Ordinal) || line.Contains("Event: EvalException", StringComparison.Ordinal));
        Assert.True(answered == answer && ran == calls,
            $"{expression} answered {answered} after {Plural.Of(ran, "call")} in the program, where it was to answer {answer} after {calls}; the engine's log:\n{string.Join('\n', lines)}");
    }

    /// <summary>A call that throws in the program is named with what its exception says, as the program's own catch reads
    /// it, the value it was given too, on one line.</summary>
    private async Task A_call_that_throws_in_the_program_is_named_with_what_its_exception_says()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int frame = await StoppedInReadingAsync(host);

        string caught = (await Evaluate(host, frame, "outOfRange.ReplaceLineEndings(\" \")")).Value;
        string thrown = (await Evaluate(host, frame, "new string(letter, -1)")).Value;
        Assert.True(thrown == $"error: Evaluation threw System.ArgumentOutOfRangeException: {caught.Trim('"')}",
            $"new string(letter, -1) answered \"{thrown}\", where the program's catch read the exception's message as {caught}");
        string parsed = (await Evaluate(host, frame, "int.Parse(word)")).Value;
        Assert.True(parsed.StartsWith("error: Evaluation threw System.FormatException: ", StringComparison.Ordinal) && parsed.Contains("'hello'", StringComparison.Ordinal),
            $"int.Parse(word) answered \"{parsed}\", where the program's FormatException names the text it could not read");
    }

    /// <summary>An index outside an array throws the IndexOutOfRangeException the program would, named with what the
    /// program's own catch reads: past the end or before the start, read, written or an element's field read, of a
    /// vector, a multi-dimensional array or an array of structs, by an int or a native integer.</summary>
    private async Task An_index_outside_an_array_throws_the_IndexOutOfRangeException_the_program_would()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int frame = await StoppedInReadingAsync(host);

        string caught = (await Evaluate(host, frame, "outsideArray")).Value;
        string expected = $"error: Evaluation threw System.IndexOutOfRangeException: {caught.Trim('"')}";
        foreach (string expression in (string[])["casts[10]", "casts[-1]", "casts[^9]", "casts[native]", "casts[8] = word",
            "new[] { spot, spot }[2]", "new[] { spot, spot }[-1].X", "new int[,] { { 1, 2 }, { 3, 4 } }[0, 2]"])
        {
            string thrown = (await Evaluate(host, frame, expression)).Value;
            Assert.True(thrown == expected, $"{expression} answered \"{thrown}\", where the program's catch read the exception's message as {caught}");
        }
        Assert.Equal((await Evaluate(host, frame, "casts[7]")).Value, (await Evaluate(host, frame, "casts[unsignedNative]")).Value);
        Assert.Equal("4", (await Evaluate(host, frame, "new[] { spot, spot }[1].Y")).Value);
    }

    /// <summary>An index, a store or the length of a null array, and a field, a store or a call through a null reference,
    /// throw the NullReferenceException the program would, named with what the program's own catch reads.</summary>
    private async Task A_null_array_or_reference_throws_the_NullReferenceException_the_program_would()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int frame = await StoppedInReadingAsync(host);

        string caught = (await Evaluate(host, frame, "nullArray")).Value;
        string expected = $"error: Evaluation threw System.NullReferenceException: {caught.Trim('"')}";
        foreach (string expression in (string[])["none[0]", "none[1] = 2", "none.Length", "none[^1]", "((int[,])null)[0, 0]",
            "nobody.Value", "nobody.Value = 1", "nobody.ToString()", "new[] { nobody }[0].Value", "(int)(object)nobody"])
        {
            string thrown = (await Evaluate(host, frame, expression)).Value;
            Assert.True(thrown == expected, $"{expression} answered \"{thrown}\", where the program's catch read the exception's message as {caught}");
        }
        Assert.Equal("null", (await Evaluate(host, frame, "nobody?.Value")).Value);
    }

    /// <summary>A lambda, one reading the frame's locals and a method group given to a constructor (Lazy's, Thread's,
    /// Timer's) are made delegates in the program, as those given to a method are, and the objects made read on.</summary>
    private async Task A_delegate_given_to_a_constructor_is_made_in_the_program_as_one_given_to_a_method_is()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper, environment: DebugSession.ScaledEvaluations);
        int frame = await StoppedInReadingAsync(host);

        Assert.Equal(("1", "int"), await Evaluate(host, frame, "new Lazy<int>(() => 1).Value"));
        Assert.Equal(("7", "int"), await Evaluate(host, frame, "new Lazy<int>(() => spot.X + spot.Y).Value"));
        Assert.Equal(("\"hello\"", "string"), await Evaluate(host, frame, "new Lazy<string>(word.Trim).Value"));
        (string id, string? type) = await Evaluate(host, frame, "new Thread(() => { }).ManagedThreadId");
        Assert.True(type == "int" && int.TryParse(id, out int managed) && managed > 0, $"A thread made with a lambda has the managed id {id} ({type})");
        (string timer, string? timerType) = await Evaluate(host, frame, "new System.Threading.Timer(_ => { })");
        Assert.True(timerType == "System.Threading.Timer", $"A timer made with a lambda reads {timer} ({timerType})");
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
