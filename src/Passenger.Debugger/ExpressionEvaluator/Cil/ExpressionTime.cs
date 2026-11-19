using Passenger.Core;

namespace Passenger.Debugger.ExpressionEvaluator.Cil;

/// <summary>The time of one expression under way: when it is given up, and how long each evaluation it ran in the program
/// took, which tells an expression past its time through one call that stood still from one of many calls, either from
/// time the debugger itself spent, and the longest call's wait for the program from the debugger's hearing of its end.</summary>
internal sealed class ExpressionTime(Deadline due)
{
    private readonly List<EvaluationTime> evaluations = [];

    public Deadline Due { get; } = due;

    /// <summary>An evaluation of the expression has ended, one way or another, after the time given.</summary>
    public void Ran(EvaluationTime took) => evaluations.Add(took);

    /// <summary>Where <paramref name="whole"/>, the time the expression has taken, went.</summary>
    public string Spent(TimeSpan whole)
    {
        if (evaluations.Count == 0)
        {
            return $"{Durations.Said(whole)} of its own in the debugger, no evaluation having run in the program";
        }
        TimeSpan inProgram = evaluations.Aggregate(TimeSpan.Zero, (sum, took) => sum + took.Whole);
        TimeSpan own = whole > inProgram ? whole - inProgram : TimeSpan.Zero;
        EvaluationTime longest = evaluations.MaxBy(e => e.Whole);
        return $"{Plural.Of(evaluations.Count, "evaluation")} in the program took {Durations.Said(inProgram)}, the longest {Durations.Said(longest.Whole)}{longest.Parts()}, and the debugger {Durations.Said(own)}";
    }
}

/// <summary>One evaluation's time, whole, and, where its end was heard, its parts: the runtime's word of its end came
/// <paramref name="Came"/> after the program was let go (any stop the program made meanwhile in it), and the debugger heard
/// it <paramref name="Heard"/> after that.</summary>
internal readonly record struct EvaluationTime(TimeSpan Whole, TimeSpan? Came = null, TimeSpan? Heard = null)
{
    /// <summary>Its parts in words, or nothing for an evaluation whose end was never heard.</summary>
    public string Parts() => Came is { } came && Heard is { } heard
        ? $" (its end came {Durations.Said(came)} after the program was let go, and was heard {Durations.Said(heard)} after it came)"
        : "";
}
