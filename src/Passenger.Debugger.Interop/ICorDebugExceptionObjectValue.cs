#nullable enable

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Passenger.Debugger.Interop;

/// <summary>An exception object's value, whose stored stack the runtime's data access reads, running no code in the
/// program.</summary>
[GeneratedComInterface]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("AE4CA65D-59DD-42A2-83A5-57E8A08D8719")]
public partial interface ICorDebugExceptionObjectValue
{
    /// <summary>The frames the exception stores, as its stack trace was recorded while it was thrown.</summary>
    [PreserveSig]
    int TryEnumerateExceptionCallStack(out ICorDebugExceptionObjectCallStackEnum? ppCallStackEnum);
}
