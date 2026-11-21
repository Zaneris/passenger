using System.Diagnostics;
using Passenger.Core;

namespace Passenger.Debugger.ExpressionEvaluator.Cil;

/// <summary>The time of one expression under way: when it is given up, and how long each evaluation it ran in the program
/// took, which tells an expression past its time through one call that stood still from one of many calls, either from
/// time the debugger itself spent, and the longest call's wait for the program from the debugger's hearing of its end.
/// The debugger's making of the expression's lambdas in the program is set aside (<see cref="AsideAsync{T}"/>).</summary>
internal sealed class ExpressionTime(Deadline due)
{
    private readonly List<EvaluationTime> evaluations = [];

    /// <summary>How long the work set aside took, and how deep within it the expression stands.</summary>
    private TimeSpan aside;
    private int asideDepth;

    public Deadline Due { get; private set; } = due;

    /// <summary>An evaluation of the expression has ended, one way or another, after the time given.</summary>
    public void Ran(EvaluationTime took) => evaluations.Add(took);

    /// <summary>Runs the debugger's own work of making what the expression's lambdas need in the program (their code loaded
    /// there, a delegate or closure made of it), which the expression's time does not count: <see cref="Due"/> moves on by
    /// what it took, and its evaluations, the expression's time taken out of <paramref name="status"/> meanwhile, are held
    /// to their own alone.</summary>
    public async Task<T> AsideAsync<T>(EvalStatus status, Func<Task<T>> work)
    {
        if (asideDepth++ > 0)
        {
            try
            {
                return await work();
            }
            finally
            {
                asideDepth--;
            }
        }
        long began = Stopwatch.GetTimestamp();
        ExpressionTime? timed = status.Expression;
        status.Expression = null;
        try
        {
            return await work();
        }
        finally
        {
            asideDepth--;
            SetAside(Stopwatch.GetElapsedTime(began));
            status.Expression = timed;
        }
    }

    /// <summary>Takes time the expression's own does not count: <see cref="Due"/> moves on by it.</summary>
    public void SetAside(TimeSpan took)
    {
        aside += took;
        Due = new Deadline(Due.At + Extensions.Ticks(took));
    }

    /// <summary>Where <paramref name="whole"/>, the time the expression has taken, went.</summary>
    public string Spent(TimeSpan whole)
    {
        TimeSpan counted = whole > aside ? whole - aside : TimeSpan.Zero;
        string setAside = aside > TimeSpan.Zero ? $"; the making of its lambdas in the program took {Durations.Said(aside)} more, set aside" : "";
        if (evaluations.Count == 0)
        {
            return $"{Durations.Said(counted)} of its own in the debugger, no evaluation having run in the program{setAside}";
        }
        TimeSpan inProgram = evaluations.Aggregate(TimeSpan.Zero, (sum, took) => sum + took.Whole);
        TimeSpan own = counted > inProgram ? counted - inProgram : TimeSpan.Zero;
        EvaluationTime longest = evaluations.MaxBy(e => e.Whole);
        return $"{Plural.Of(evaluations.Count, "evaluation")} in the program took {Durations.Said(inProgram)}, the longest {Durations.Said(longest.Whole)}{longest.Parts()}, and the debugger {Durations.Said(own)}{setAside}";
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
