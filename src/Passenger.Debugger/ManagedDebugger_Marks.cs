using System.Globalization;
using Passenger.Debugger.ExpressionEvaluator.Cil;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>Marked objects (Mark Object): an object an expression's value is, held by a weak handle so it is found again
/// wherever the collector moves it and never kept alive by the debugger, named by its label (<c>$1</c>) in any expression as
/// the compiler's object ids are, and known by its address in every value read at a stop.</summary>
public partial class ManagedDebugger
{
    /// <summary>The objects marked, each by its label, in the order they were marked.</summary>
    private readonly List<(string Label, ICorDebugHandleValue Weak)> _marks = [];

    private int _lastMark;

    /// <summary>Marks the object an expression's value is in a frame, answering its label: the one it has where it is marked
    /// already. A value that is no object (null, a struct, a number) is refused, saying why.</summary>
    public async Task<string> MarkObject(string expression, int? frameId)
    {
        ArgumentNullException.ThrowIfNull(_expressionEvaluator);
        EvalStatus.Explicit = true;
        try
        {
            using EvaluationResult result = await _expressionEvaluator.Evaluate(expression, ContextOf(frameId));
            if (result.Error is { } error)
            {
                throw new InvalidOperationException(error);
            }
            ICorDebugValue? target = result.Text is null ? result.Value : null;
            if (target is ICorDebugReferenceValue reference)
            {
                if (reference.IsNull)
                {
                    throw new InvalidOperationException($"{expression} is null, no object to mark");
                }
                target = reference.Dereference();
            }
            if (result.Text is not null || target is ICorDebugStringValue)
            {
                throw new InvalidOperationException($"{expression} is a string, which is copied as it goes, no object to mark");
            }
            if (target is not ICorDebugHeapValue2 heap)
            {
                throw new InvalidOperationException($"{expression} is no object on the heap to mark: a struct or a number is copied as it goes");
            }
            if (MarkAt(target) is { } known)
            {
                return known;
            }
            string label = $"${(++_lastMark).ToString(CultureInfo.InvariantCulture)}";
            _marks.Add((label, heap.CreateHandle(CorDebugHandleType.HANDLE_WEAK_TRACK_RESURRECTION)));
            _logger?.Invoke($"Marked {expression} as {label}");
            return label;
        }
        finally
        {
            EvalStatus.Explicit = false;
        }
    }

    /// <summary>Lets a marked object go; a label no object has, nothing.</summary>
    public void UnmarkObject(string label)
    {
        int at = _marks.FindIndex(m => m.Label == label);
        if (at >= 0)
        {
            _marks[at].Weak.TryDispose();
            _marks.RemoveAt(at);
        }
    }

    /// <summary>The label of the marked object a value is, or holds a reference to; null for any other value.</summary>
    internal string? MarkOf(ICorDebugValue value)
    {
        if (_marks.Count == 0)
        {
            return null;
        }
        try
        {
            return value is ICorDebugReferenceValue reference ? reference.IsNull ? null : MarkAt(reference.Dereference())
                : value is ICorDebugHeapValue ? MarkAt(value) : null;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return null;
        }
    }

    /// <summary>The label of the marked object at an object's address.</summary>
    private string? MarkAt(ICorDebugValue target) =>
        target.TryGetAddress(out CORDB_ADDRESS address) == Cor.S_OK && address.Value != 0
            ? _marks.FirstOrDefault(m => Live(m.Weak) is { } marked && marked.TryGetAddress(out CORDB_ADDRESS at) == Cor.S_OK && at.Value == address.Value).Label
            : null;

    /// <summary>A marked object's handle, the object still there; null for a label no object has, or one collected.</summary>
    internal ICorDebugHandleValue? Marked(string label) =>
        _marks.FirstOrDefault(m => m.Label == label).Weak is { } weak && Live(weak) is not null ? weak : null;

    /// <summary>Why an expression that is a marked object's label alone reads nothing: its object was collected.</summary>
    private string? CollectedMark(string expression) =>
        _marks.Any(m => m.Label == expression) && Marked(expression) is null ? $"The object marked {expression} has been collected" : null;

    /// <summary>Each marked object still there, by its label and its type as the compiler is given an alias's.</summary>
    internal IReadOnlyList<(string Name, string Type)> MarkAliases() =>
        [.. _marks.Select(m => (m.Label, Object: Live(m.Weak))).Where(m => m.Object is ICorDebugObjectValue or ICorDebugArrayValue)
            .Select(m => (m.Label, AliasTypeName(m.Object!.ExactType)))];

    /// <summary>The object a weak handle holds, null once it is collected.</summary>
    private static ICorDebugValue? Live(ICorDebugHandleValue weak) =>
        weak.TryIsNull(out bool gone) == Cor.S_OK && !gone && weak.TryDereference(out ICorDebugValue? value) == Cor.S_OK ? value : null;
}
