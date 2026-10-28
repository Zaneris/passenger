#nullable enable

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Passenger.Debugger.Interop;

/// <summary>
/// <para>A walk of a stack's frames, one at a time: here, the frames a chain of runtime-async continuations stands for
/// (<see cref="ICorDebugProcess12.TryGetAsyncStack"/>).</para>
/// </summary>
[GeneratedComInterface]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("A0647DE9-55DE-4816-929C-385271C64CF7")]
public partial interface ICorDebugStackWalk
{
    /// <summary>The register context of the current frame; none for an async stack's frames.</summary>
    [PreserveSig]
    int TryGetContext(uint contextFlags, uint contextBufSize, out uint contextSize, nint contextBuf);

    /// <summary>Moves the walk to another register context.</summary>
    [PreserveSig]
    int TrySetContext(int flag, uint contextSize, nint context);

    /// <summary>Moves on to the next frame: CORDBG_S_AT_END_OF_STACK when there is none.</summary>
    [PreserveSig]
    int TryNext();

    /// <summary>The current frame: S_FALSE and none at a native frame.</summary>
    [PreserveSig]
    int TryGetFrame(out ICorDebugFrame? pFrame);
}
