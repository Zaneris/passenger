using Passenger.Core;
using Passenger.Debugger.ExpressionEvaluator.Cil;

namespace Passenger.UnitTests.Debugger;

/// <summary>Where an expression past its time spent it, which tells one call that stood still from many short ones, and
/// either from time the debugger took itself.</summary>
public class ExpressionTimeTests
{
    private static ExpressionTime Timed(params double[] evaluations)
    {
        ExpressionTime time = new(Deadline.In(TimeSpan.FromSeconds(3), 0));
        foreach (double took in evaluations)
        {
            time.Ran(TimeSpan.FromSeconds(took));
        }
        return time;
    }

    [Fact]
    public void One_call_that_stood_still_is_told_from_the_rest_of_the_expressions_time()
    {
        Assert.Equal("2 evaluations in the program took 3.000 s, the longest 2.900 s, and the debugger 0.050 s",
            Timed(2.9, 0.1).Spent(TimeSpan.FromSeconds(3.05)));
    }

    [Fact]
    public void Time_the_program_had_no_part_in_is_said_to_be_the_debuggers_own()
    {
        Assert.Equal("3.100 s of its own in the debugger, no evaluation having run in the program", Timed().Spent(TimeSpan.FromSeconds(3.1)));
    }

    [Fact]
    public void A_clock_read_before_the_last_evaluation_counted_never_says_the_debugger_took_less_than_nothing()
    {
        Assert.Equal("1 evaluation in the program took 3.000 s, the longest 3.000 s, and the debugger 0.000 s", Timed(3).Spent(TimeSpan.FromSeconds(2.999)));
    }
}
