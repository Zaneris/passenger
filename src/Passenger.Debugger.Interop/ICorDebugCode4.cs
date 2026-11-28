#nullable enable

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Passenger.Debugger.Interop;

/// <summary>
/// <para>Provides a method that enables a debugger to enumerate the local variables and arguments in a function.</para>
/// </summary>
/// <remarks>
/// <para><b>Note:</b> This interface does not support being called remotely, either cross-machine or cross-process.</para>
/// <para>Documentation adapted from <see href="https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebugcode4-interface">Microsoft Learn</see>.</para>
/// </remarks>
[GeneratedComInterface]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("18221FA4-20CB-40FA-B19D-9F91C4FA8C14")]
public partial interface ICorDebugCode4
{
    /// <summary>
    /// <para>Gets an enumerator to the local variables and arguments in a function.</para>
    /// </summary>
    /// <param name="ppEnum">
    /// <para>A pointer to the address of an <see cref="T:Passenger.Debugger.Interop.ICorDebugVariableHomeEnum">ICorDebugVariableHomeEnum</see> interface object that is an enumerator for the local variables and arguments in a function.</para>
    /// </param>
    /// <remarks>
    /// <para>The <see cref="T:Passenger.Debugger.Interop.ICorDebugVariableHomeEnum">ICorDebugVariableHomeEnum</see> interface object is a standard enumerator derived from the "ICorDebugEnum" interface that allows you to enumerate <see cref="T:Passenger.Debugger.Interop.ICorDebugVariableHome">ICorDebugVariableHome</see> objects. The collection may include multiple <see cref="T:Passenger.Debugger.Interop.ICorDebugVariableHome">ICorDebugVariableHome</see> objects for the same slot or      argument index if they have different homes at different points in the      function.</para>
    /// <para>Documentation adapted from <see href="https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebugcode4-enumeratevariablehomes-method">Microsoft Learn</see>.</para>
    /// </remarks>
    [PreserveSig]
    int TryEnumerateVariableHomes(out ICorDebugVariableHomeEnum ppEnum);
}
