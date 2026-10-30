#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Passenger.Debugger.Interop.Extensions;

/// <summary>The one walk of an ICorDebug enumerator, an element at a time. A failure is thrown whether or not anything was
/// fetched (a neutered enumerator, a process gone), so its caller hears why rather than taking it for the end; only
/// success with nothing fetched (S_FALSE) ends the walk.</summary>
internal static class CorDebugEnums
{
    /// <summary>An enumerator's Next: <paramref name="count"/> elements asked for into <paramref name="into"/>.</summary>
    internal delegate int Next<T>(uint count, T[] into, out uint fetched);

    /// <summary>The elements in order, a failure thrown; one that <paramref name="ends"/> names ends the walk instead.</summary>
    internal static IEnumerable<T> Walk<T>(Next<T> next, int? ends = null)
    {
        while (true)
        {
            T[] one = new T[1];
            int result = next(1u, one, out uint fetched);
            if (result == ends)
            {
                yield break;
            }
            Marshal.ThrowExceptionForHR(result);
            switch (fetched)
            {
                case 0u:
                    yield break;
                case 1u:
                    yield return one[0];
                    break;
                default:
                    throw new InvalidOperationException("Native debugger enumerator returned an invalid item count.");
            }
        }
    }

    /// <summary>A frame's arguments or locals in order, up to the first the frame has no home for at its IP (optimized
    /// code's): the runtime's enumerator steps past it, and those after it would no longer stand at their own indices.</summary>
    internal static IEnumerable<ICorDebugValue> Values(ICorDebugValueEnum enumerator) =>
        Walk<ICorDebugValue>(enumerator.TryNext, Cor.CORDBG_E_IL_VAR_NOT_AVAILABLE);
}
