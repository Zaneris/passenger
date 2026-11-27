#nullable enable

using System.CodeDom.Compiler;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Passenger.Debugger.Interop;

/// <summary>
/// <para>Provides an enumerator to the local variables and arguments in a function.</para>
/// </summary>
/// <remarks>
/// <para>The <c>ICorDebugVariableHomeEnum</c> interface implements the ICorDebugEnum interface.</para>
/// <para>An <c>ICorDebugVariableHomeEnum</c> instance is populated with <see cref="T:Passenger.Debugger.Interop.ICorDebugVariableHome">ICorDebugVariableHome</see> instances by calling the <see cref="M:Passenger.Debugger.Interop.ICorDebugCode4.TryEnumerateVariableHomes(Passenger.Debugger.Interop.ICorDebugVariableHomeEnum@)">ICorDebugCode4::EnumerateVariableHomes</see> method. Each <see cref="T:Passenger.Debugger.Interop.ICorDebugVariableHome">ICorDebugVariableHome</see> instance in the collection represents a local variable or argument in a function. The  <see cref="T:Passenger.Debugger.Interop.ICorDebugVariableHome">ICorDebugVariableHome</see> objects in the collection can be enumerated by calling the <see cref="M:Passenger.Debugger.Interop.ICorDebugVariableHomeEnum.TryNext(System.UInt32,Passenger.Debugger.Interop.ICorDebugVariableHome[],System.UInt32@)">ICorDebugVariableHomeEnum::Next</see> method.</para>
/// <para>Documentation adapted from <see href="https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebugvariablehomeenum-interface">Microsoft Learn</see>.</para>
/// </remarks>
[GeneratedComInterface]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("E76B7A57-4F7A-4309-85A7-5D918C3DEAF7")]
public partial interface ICorDebugVariableHomeEnum : ICorDebugEnum
{
    /// <summary>
    /// <para>Gets the specified number of <see cref="T:Passenger.Debugger.Interop.ICorDebugVariableHome">ICorDebugVariableHome</see> instances that contain information about the local variables and arguments in a function.</para>
    /// </summary>
    /// <param name="celt">
    /// <para>The number of objects to be retrieved.</para>
    /// </param>
    /// <param name="homes">
    /// <para>An array of pointers, each of which points to a <see cref="T:Passenger.Debugger.Interop.ICorDebugVariableHome">ICorDebugVariableHome</see> object that provides information about  a local variable or argument of a function.</para>
    /// </param>
    /// <param name="pceltFetched">
    /// <para>The number of instances actually returned in objects.</para>
    /// </param>
    /// <returns>
    /// <para>The method returns the following values.</para>
    /// <list type="table">
    /// <listheader><term>HRESULT</term><description>Description</description></listheader>
    /// <item><term><c>S_OK</c></term><description>The method completed successfully.</description></item>
    /// <item><term><c>S_FALSE</c></term><description>The actual number of instances retrieved, as reflected in <c>pceltFetched</c>, is less than the number of instances requested.</description></item>
    /// </list>
    /// </returns>
    /// <remarks>
    /// <para>The <see cref="M:Passenger.Debugger.Interop.ICorDebugVariableHomeEnum.TryNext(System.UInt32,Passenger.Debugger.Interop.ICorDebugVariableHome[],System.UInt32@)">ICorDebugVariableHomeEnum::Next</see> method retrieves a maximum of  <c>celt</c> objects starting at the current position of the enumerator. When the method returns, <c>pceltFetched</c> contains the actual number of objects retrieved.</para>
    /// <para>Documentation adapted from <see href="https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebugvariablehomeenum-next-method">Microsoft Learn</see>.</para>
    /// </remarks>
    [PreserveSig]
    int TryNext(uint celt, [Out][MarshalUsing(CountElementName = "celt")] ICorDebugVariableHome[] homes, out uint pceltFetched);

}
