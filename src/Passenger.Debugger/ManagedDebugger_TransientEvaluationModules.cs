using Passenger.Core;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    private readonly HashSet<Guid> _transientEvaluationModules = new();

    internal void RegisterTransientEvaluationModule(Guid moduleVersionId)
    {
        lock (_transientEvaluationModules)
        {
            _transientEvaluationModules.Add(moduleVersionId);
        }
    }

    private bool TryConsumeTransientEvaluationModule(Guid moduleVersionId)
    {
        lock (_transientEvaluationModules)
        {
            return _transientEvaluationModules.Remove(moduleVersionId);
        }
    }

    /// <summary>A test's hold: before the session's first lambda is made in the program, the debugger waits as long as an
    /// expression may take, as that making once took on a busy machine.</summary>
    public static bool HoldsFirstLambda { get; set; }

    private bool _firstLambdaHeld;

    /// <summary>The wait of <see cref="HoldsFirstLambda"/>, once a session.</summary>
    internal async Task HoldFirstLambdaAsync()
    {
        if (HoldsFirstLambda && !_firstLambdaHeld)
        {
            _firstLambdaHeld = true;
            _logger?.Invoke($"Held: the making of the session's first lambda in the program waits {Durations.Said(Extensions.EvaluationTimeout)} first, a test's hold");
            await Task.Delay(Extensions.EvaluationTimeout);
        }
    }
}
