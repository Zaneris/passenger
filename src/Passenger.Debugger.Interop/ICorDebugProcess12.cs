#nullable enable

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Passenger.Debugger.Interop;

/// <summary>
/// <para>Reads a runtime-async chain of continuations (.NET 11) as the frames of the methods waiting in it.</para>
/// </summary>
[GeneratedComInterface]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("4DCD6FB9-3CF0-43F0-9EDF-E833070FE644")]
public partial interface ICorDebugProcess12
{
    /// <summary>A walk of the frames of the continuation at an address and those after it: each an IL frame of the method
    /// that waits there, at the IL of the await it waits at.</summary>
    [PreserveSig]
    int TryGetAsyncStack(CORDB_ADDRESS continuationAddress, out ICorDebugStackWalk? ppStackWalk);
}
