using Passenger.Core;
using Passenger.Debugger.Interop;

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

    /// <summary>The evaluations' own assemblies as the program loaded them: none is the program's, so none is listed, bound or
    /// stepped into, but the types an evaluation made there (an anonymous type's) are found in it, and a value of one is read
    /// through it while the program holds it.</summary>
    private readonly List<ModuleInfo> _evaluationModules = [];

    /// <summary>Keeps an evaluation's assembly the program has loaded, apart from the program's modules.</summary>
    private void KeepEvaluationModule(ModuleInfo module)
    {
        lock (_evaluationModules)
        {
            _evaluationModules.Add(module);
        }
    }

    /// <summary>The evaluation's assembly of this id as the program loaded it; null where the program has not loaded it.</summary>
    internal ModuleInfo? EvaluationModule(Guid moduleVersionId)
    {
        lock (_evaluationModules)
        {
            return _evaluationModules.FirstOrDefault(module => module.MetadataReader.Mvid == moduleVersionId);
        }
    }

    /// <summary>The evaluation's assembly the runtime names, by its object for it, else by its image's address; null for any
    /// other module.</summary>
    private ModuleInfo? FindEvaluationModule(ICorDebugModule module)
    {
        lock (_evaluationModules)
        {
            return _evaluationModules.FirstOrDefault(kept => ReferenceEquals(kept.Module, module))
                ?? (module.BaseAddress is { Value: not 0 } address ? _evaluationModules.FirstOrDefault(kept => kept.BaseAddress == address) : null);
        }
    }

    /// <summary>Lets go of every evaluation's assembly, as the session ends.</summary>
    private void LetGoOfEvaluationModules()
    {
        lock (_evaluationModules)
        {
            foreach (ModuleInfo module in _evaluationModules)
            {
                module.Dispose();
            }
            _evaluationModules.Clear();
        }
    }

    /// <summary>Lets go of an evaluation's assembly the program has unloaded; false for any other module.</summary>
    private bool LetGoOfEvaluationModule(ICorDebugModule module)
    {
        ModuleInfo? found = FindEvaluationModule(module);
        if (found is null)
        {
            return false;
        }
        lock (_evaluationModules)
        {
            _evaluationModules.Remove(found);
        }
        found.Dispose();
        return true;
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
