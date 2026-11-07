using Passenger.Core;

namespace Passenger.Debugger.ExpressionEvaluator.Cil;

/// <summary>The time of one expression under way: when it is given up, and how long each evaluation it ran in the program
/// took, which tells an expression past its time through one call that stood still from one of many calls, and either
/// from time the debugger itself spent.</summary>
internal sealed class ExpressionTime(Deadline due)
{
    private readonly List<TimeSpan> evaluations = [];

    public Deadline Due { get; } = due;

    /// <summary>An evaluation of the expression has ended, one way or another, after the time given.</summary>
    public void Ran(TimeSpan took) => evaluations.Add(took);

    /// <summary>Where <paramref name="whole"/>, the time the expression has taken, went.</summary>
    public string Spent(TimeSpan whole)
    {
        if (evaluations.Count == 0)
        {
            return $"{Seconds(whole)} of its own in the debugger, no evaluation having run in the program";
        }
        TimeSpan inProgram = evaluations.Aggregate(TimeSpan.Zero, (sum, took) => sum + took);
        TimeSpan own = whole > inProgram ? whole - inProgram : TimeSpan.Zero;
        return $"{Plural.Of(evaluations.Count, "evaluation")} in the program took {Seconds(inProgram)}, the longest {Seconds(evaluations.Max())}, and the debugger {Seconds(own)}";
    }

    /// <summary>A time as the engine's log gives it.</summary>
    internal static string Seconds(TimeSpan time) => $"{time.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)} s";
}
