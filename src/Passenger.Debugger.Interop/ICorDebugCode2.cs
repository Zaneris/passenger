#nullable enable

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Passenger.Debugger.Interop;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct CodeChunk(CORDB_ADDRESS Address, uint Length);

/// <summary>The addresses of the regions that <see cref="ICorDebugCode"/> reads as one stream.</summary>
[GeneratedComInterface]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("5F696509-452F-4436-A3FE-4D11FE7E2347")]
public partial interface ICorDebugCode2
{
    [PreserveSig]
    int TryGetCodeChunks(uint capacity, out uint count, [Out][MarshalUsing(CountElementName = nameof(capacity))] CodeChunk[]? chunks);

    [PreserveSig]
    int TryGetCompilerFlags(out uint flags);
}
