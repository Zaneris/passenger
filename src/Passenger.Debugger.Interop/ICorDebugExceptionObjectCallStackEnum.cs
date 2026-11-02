#nullable enable

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Passenger.Debugger.Interop;

/// <summary>The frames an exception object stores (<see cref="ICorDebugExceptionObjectValue"/>), the newest first.</summary>
[GeneratedComInterface]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("ED775530-4DC4-41F7-86D0-9E2DEF7DFC66")]
public partial interface ICorDebugExceptionObjectCallStackEnum : ICorDebugEnum
{
    /// <summary>The next <paramref name="celt"/> frames, as many as are left (S_FALSE when fewer).</summary>
    [PreserveSig]
    int TryNext(uint celt, [Out][MarshalUsing(CountElementName = "celt")] CorDebugExceptionObjectStackFrame[] values, out uint pceltFetched);
}
