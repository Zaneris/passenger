using Passenger.Core;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger.ExpressionEvaluator.Cil;

/// <summary>The System.Type the program made for each type an expression named, kept for the session so the next needs no
/// call in the program: by the type's identity in the runtime, which tells one assembly's types in two load contexts
/// apart and stays through Hot Reload's updates, and held weakly, so a collectible type still unloads. All are let go as
/// a module unloads, after which an identity may name another type, and as the program is let go. Under the engine's lock.</summary>
internal sealed class SystemTypes
{
    private readonly Dictionary<COR_TYPEID, ICorDebugHandleValue> kept = [];

    /// <summary>The System.Type kept for <paramref name="type"/>, still in the program, or null.</summary>
    public ICorDebugHandleValue? Find(ICorDebugType type)
    {
        if (!Identity(type, out COR_TYPEID id) || !kept.TryGetValue(id, out ICorDebugHandleValue? held))
        {
            return null;
        }
        if (held.TryIsNull(out bool gone) is Cor.S_OK && !gone)
        {
            return held;
        }
        kept.Remove(id);
        held.TryDispose();
        return null;
    }

    /// <summary>Whether <paramref name="handle"/> is one kept here, which holds its System.Type only weakly.</summary>
    public bool Holds(ICorDebugHandleValue handle) => kept.ContainsValue(handle);

    /// <summary>Keeps <paramref name="made"/>, the program's System.Type for <paramref name="type"/>, where it can be held.</summary>
    public void Keep(ICorDebugType type, ICorDebugValue made)
    {
        if (!Identity(type, out COR_TYPEID id) || kept.ContainsKey(id) || made.UnwrapDebugValue() is not ICorDebugHeapValue2 heap)
        {
            return;
        }
        try
        {
            kept[id] = heap.CreateHandle(CorDebugHandleType.HANDLE_WEAK_TRACK_RESURRECTION);
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            // Not kept: the next asks the program again.
        }
    }

    /// <summary>Lets go of every one kept, their handles too unless the program has <paramref name="exited"/>.</summary>
    public void LetGo(bool exited = false)
    {
        if (!exited)
        {
            foreach (ICorDebugHandleValue held in kept.Values)
            {
                held.TryDispose();
            }
        }
        kept.Clear();
    }

    private static bool Identity(ICorDebugType type, out COR_TYPEID id)
    {
        try
        {
            return type.TryGetTypeID(out id) is Cor.S_OK;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            id = default;
            return false;
        }
    }
}
