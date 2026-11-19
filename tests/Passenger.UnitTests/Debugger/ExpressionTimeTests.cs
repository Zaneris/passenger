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

    [Fact]
    public void One_call_that_stood_still_is_told_from_the_rest_of_the_expressions_time()
    {
        Assert.Equal("2 evaluations in the program took 3 s, the longest 2.9 s, and the debugger 50 ms",
            Timed(2.9, 0.1).Spent(TimeSpan.FromSeconds(3.05)));
    }

    [Fact]
    public void The_longest_call_says_how_long_the_program_took_to_end_it_and_how_long_the_debugger_took_to_hear_its_end()
    {
        ExpressionTime time = new(Deadline.In(TimeSpan.FromSeconds(3), 0));
        time.Ran(new EvaluationTime(TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.09), TimeSpan.FromMilliseconds(1)));
        time.Ran(new EvaluationTime(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(1.6)));
        Assert.Equal("2 evaluations in the program took 2.1 s, the longest 2 s (its end came 400 ms after the program was let go, and was heard 1.6 s"
            + " after it came), and the debugger 50 ms", time.Spent(TimeSpan.FromSeconds(2.15)));
    }

    [Fact]
    public void Time_the_program_had_no_part_in_is_said_to_be_the_debuggers_own()
    {
        Assert.Equal("3.1 s of its own in the debugger, no evaluation having run in the program", Timed().Spent(TimeSpan.FromSeconds(3.1)));
    }

    [Fact]
    public void A_clock_read_before_the_last_evaluation_counted_never_says_the_debugger_took_less_than_nothing()
    {
        Assert.Equal("1 evaluation in the program took 3 s, the longest 3 s, and the debugger 0 ms", Timed(3).Spent(TimeSpan.FromSeconds(2.999)));
    }
}
