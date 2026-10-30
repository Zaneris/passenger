#nullable enable

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Passenger.Debugger.Interop;

/// <summary>
/// <para>Provides a method that extends "ICorDebugCode" and "ICorDebugCode2" to provide information about a managed return value.</para>
/// </summary>
/// <remarks>
/// <para><b>Note:</b> This interface does not support being called remotely, either cross-machine or cross-process.</para>
/// <para>Documentation adapted from <see href="https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebugcode3-interface">Microsoft Learn</see>.</para>
/// </remarks>
[GeneratedComInterface]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("D13D3E88-E1F2-4020-AA1D-3D162DCBE966")]
public partial interface ICorDebugCode3
{
    /// <summary>
    /// <para>For a specified IL offset, gets the native offsets where a breakpoint should be placed so that the debugger can obtain the return value from a function.</para>
    /// </summary>
    /// <param name="ILoffset">
    /// <para>The IL offset. It must be a function call site or the function call will fail.</para>
    /// </param>
    /// <param name="bufferSize">
    /// <para>The number of offsets <c>pOffsets</c> holds: its length in elements, not in bytes.</para>
    /// </param>
    /// <param name="pFetched">
    /// <para>A pointer to the number of offsets actually returned. Usually, its value is 1, but a single IL instruction can map to multiple <c>CALL</c> assembly instructions.</para>
    /// </param>
    /// <param name="pOffsets">
    /// <para>An array of native offsets. Typically, <c>pOffsets</c> contains a single offset, although a single IL instruction can map to multiple map to multiple <c>CALL</c> assembly instructions.</para>
    /// </param>
    /// <remarks>
    /// <para>This method is used along with the <see cref="M:Passenger.Debugger.Interop.ICorDebugILFrame3.TryGetReturnValueForILOffset(System.UInt32,Passenger.Debugger.Interop.ICorDebugValue@)">ICorDebugILFrame3::GetReturnValueForILOffset</see> method to get the return value of a method that returns a reference type. Passing an IL offset to a function call site to this method returns one or more native offsets. The debugger can then set breakpoints on these native offsets in the function. When the debugger hits one of the breakpoints, you can then pass the same IL offset that you passed to this method to the <see cref="M:Passenger.Debugger.Interop.ICorDebugILFrame3.TryGetReturnValueForILOffset(System.UInt32,Passenger.Debugger.Interop.ICorDebugValue@)">ICorDebugILFrame3::GetReturnValueForILOffset</see> method to get the return value. The debugger should then clear all the breakpoints that it set.</para>
    /// <para><b>Note:</b> The  ICorDebugCode3::GetReturnValueLiveOffset  and  ICorDebugILFrame3 :: GetReturnValueForILOffset  methods allow you to get return value information for reference types only. Retrieving return value information from value types (that is, all types that derive from  ) is not supported.</para>
    /// <para>The function returns the <c>HRESULT</c> values shown in the following table.</para>
    /// <list type="table">
    /// <listheader><term><c>HRESULT</c> value</term><description>Description</description></listheader>
    /// <item><term><c>S_OK</c></term><description>Success.</description></item>
    /// <item><term><c>CORDBG_E_INVALID_OPCODE</c></term><description>The given IL offset site is not a call instruction, or the function returns <c>void</c>.</description></item>
    /// <item><term><c>CORDBG_E_UNSUPPORTED</c></term><description>The given IL offset is a proper call, but the return type is unsupported for getting a return value.</description></item>
    /// </list>
    /// <para>The <c>ICorDebugCode3::GetReturnValueLiveOffset</c> method is available only on x86-based and AMD64 systems.</para>
    /// <para>Documentation adapted from <see href="https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebugcode3-getreturnvalueliveoffset-method">Microsoft Learn</see>.</para>
    /// </remarks>
    [PreserveSig]
    int TryGetReturnValueLiveOffset(uint ILoffset, uint bufferSize, out uint pFetched, [Out][MarshalUsing(CountElementName = "bufferSize")] uint[] pOffsets);
}
