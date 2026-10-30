using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public enum StoredReferenceKind
{
    Scope,
    SyntheticAsyncScope,
    StackVariable,
    StaticClassVariable, // This reference was stored as a pseudo variable for the static members of a "StackVariable" class
    RawView,
    EnumerableResults,
    ArrayRange,
    QuerySql, // An EF Core query's SQL row, read only as it is opened
    Pointer, // A pointer, opening to the value it points at
}

public readonly record struct ThreadId
{
    public readonly int Value;
    public ThreadId() => throw new ArgumentException("ThreadId must be initialized with a valid value");
    public ThreadId(int value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "ThreadId value must be greater than zero");
        }

        Value = value;
    }
};
public readonly record struct FrameStackDepth
{
    public readonly int Value;
    public FrameStackDepth() => throw new ArgumentException("FrameStackDepth must be initialized with a valid value");
    public FrameStackDepth(int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "FrameStackDepth value must be zero or greater");
        }

        Value = value;
    }
};
/// <summary>What a reference names (<see cref="ReferenceKind"/>): its value, where it was read, and, where known, the type the
/// value's variable is declared as (<see cref="Declared"/>) and the C# that reaches the value (<see cref="Expression"/>), which
/// its rows' expressions are made from.</summary>
public record struct VariablesReference(StoredReferenceKind ReferenceKind, ICorDebugValue? ObjectValue, ThreadId ThreadId, FrameStackDepth FrameStackDepth, ICorDebugValue? DebuggerProxyInstance, uint[]? ArrayIndices = null, uint? ArrayStartOffset = null, uint? ArrayCount = null, string? Name = null, DeclaredType? Declared = null,
    string? Expression = null);
/// <summary>The references of a stop's scopes and values. Each is numbered above every one before it in the session, and a
/// stop's are let go when the program goes on, so one from an earlier stop is refused rather than naming whatever value
/// took its number at this one.</summary>
public class VariableManager
{
    private int _nextReference = 1;

    /// <summary>The first reference of the stop the program is at: any below it was an earlier stop's.</summary>
    private int _firstOfStop = 1;
    private readonly Dictionary<int, VariablesReference> _references = new();
    private readonly Lock _lock = new();

    /// <summary>
    /// Create a reference for an object
    /// </summary>
    public int CreateReference(VariablesReference obj)
    {
        lock (_lock)
        {
            int reference = _nextReference++;
            _references[reference] = obj;
            return reference;
        }
    }

    /// <summary>
    /// Get an object by reference
    /// </summary>
    public VariablesReference? GetReference(int reference)
    {
        lock (_lock)
        {
            if (_references.TryGetValue(reference, out VariablesReference obj))
            {
                return obj;
            }
            return null;
        }
    }

    /// <summary>Gives a reference the C# that reaches its value, known once the row opening it has its own.</summary>
    public void Express(int reference, string? expression)
    {
        lock (_lock)
        {
            if (_references.TryGetValue(reference, out VariablesReference held))
            {
                _references[reference] = held with { Expression = expression };
            }
        }
    }

    /// <summary>Whether a reference no longer held was given at an earlier stop, rather than never given.</summary>
    public bool IsFromEarlierStop(int reference)
    {
        lock (_lock)
        {
            return reference > 0 && reference < _firstOfStop;
        }
    }

    /// <summary>
    /// Clear all references
    /// </summary>
    public void ClearAndDisposeHandleValues()
    {
        lock (_lock)
        {
            // Distinct: a handle with static members is stored again as their 'Static Members' row: disposed once.
            List<ICorDebugHandleValue> handleReferences = _references.Values.SelectMany(GetHandleValues).Distinct().ToList();
            handleReferences.ForEach(h => h.Dispose());
            _references.Clear();
            _firstOfStop = _nextReference;
        }
    }

    /// <summary>Forgets every reference without releasing its handles, for a process that has exited and
    /// took its handles with it.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _references.Clear();
            _firstOfStop = _nextReference;
        }
    }

    public void ClearAndTryDisposeHandleValues()
    {
        lock (_lock)
        {
            // Distinct: a handle with static members is stored again as their 'Static Members' row: disposed once.
            List<ICorDebugHandleValue> handleReferences = _references.Values.SelectMany(GetHandleValues).Distinct().ToList();
            handleReferences.ForEach(h => h.TryDispose());
            _references.Clear();
            _firstOfStop = _nextReference;
        }
    }

    private static IEnumerable<ICorDebugHandleValue> GetHandleValues(VariablesReference r)
    {
        if (r.ObjectValue is ICorDebugHandleValue ov)
        {
            yield return ov;
        }

        if (r.DebuggerProxyInstance is ICorDebugHandleValue dp)
        {
            yield return dp;
        }
    }
}
