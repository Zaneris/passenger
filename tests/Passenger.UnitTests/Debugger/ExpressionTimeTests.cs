using Passenger.Core;
using Passenger.Debugger.ExpressionEvaluator.Cil;

namespace Passenger.UnitTests.Debugger;

/// <summary>Where an expression past its time spent it, which tells one call that stood still from many short ones, either
/// from time the debugger took itself, and the program's part of the longest call from the debugger's hearing of its end.</summary>
public class ExpressionTimeTests
{
    private static ExpressionTime Timed(params double[] evaluations)
    {
        ExpressionTime time = new(Deadline.In(TimeSpan.FromSeconds(3), 0));
        foreach (double took in evaluations)
        {
            time.Ran(new EvaluationTime(TimeSpan.FromSeconds(took)));
        }
        return time;
    }

    /// <summary>Setting aside the making of its lambdas moves its end on; a clock read before the last evaluation counted
    /// never says the debugger took less than nothing.</summary>
    [Fact]
    public void An_expressions_time_tells_one_call_that_stood_still_from_the_rest_and_sets_aside_the_making_of_its_lambdas() => Cases.All(
        ("one call that stood still is told from the rest of the expression's time", () =>
            Assert.Equal("2 evaluations in the program took 3 s, the longest 2.9 s, and the debugger 50 ms", Timed(2.9, 0.1).Spent(TimeSpan.FromSeconds(3.05)))),
        ("the making of its lambdas set aside moves its end on and is said apart from its own time", () =>
        {
            ExpressionTime time = Timed(0.2);
            Deadline due = time.Due;
            time.SetAside(TimeSpan.FromSeconds(2.5));
            Assert.Equal(due.At + (long)(2.5 * System.Diagnostics.Stopwatch.Frequency), time.Due.At);
            Assert.Equal("1 evaluation in the program took 200 ms, the longest 200 ms, and the debugger 300 ms; the making of its lambdas in the program took 2.5 s more, set aside",
                time.Spent(TimeSpan.FromSeconds(3)));
        }),
        ("a clock read before the last evaluation counted never says the debugger took less than nothing", () =>
            Assert.Equal("1 evaluation in the program took 3 s, the longest 3 s, and the debugger 0 ms", Timed(3).Spent(TimeSpan.FromSeconds(2.999)))));
}
