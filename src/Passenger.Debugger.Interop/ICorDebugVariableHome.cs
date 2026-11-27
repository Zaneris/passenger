#nullable enable

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Passenger.Debugger.Interop;

/// <summary>
/// <para>Represents a local variable or argument of a function.</para>
/// </summary>
/// <remarks>
/// <para>Documentation adapted from <see href="https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebugvariablehome-interface">Microsoft Learn</see>.</para>
/// </remarks>
[GeneratedComInterface]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("50847B8D-F43F-41B0-924C-6383A5F2278B")]
public partial interface ICorDebugVariableHome
{
    /// <summary>
    /// <para>Gets the "ICorDebugCode" instance that contains this <see cref="T:Passenger.Debugger.Interop.ICorDebugVariableHome">ICorDebugVariableHome</see> object.</para>
    /// </summary>
    /// <param name="ppCode">
    /// <para>A pointer to the address of the "ICorDebugCode" instance that contains this <see cref="T:Passenger.Debugger.Interop.ICorDebugVariableHome">ICorDebugVariableHome</see> object.</para>
    /// </param>
    /// <remarks>
    /// <para>Documentation adapted from <see href="https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebugvariablehome-getcode-method">Microsoft Learn</see>.</para>
    /// </remarks>
    [PreserveSig]
    int TryGetCode(out ICorDebugCode ppCode);

    /// <summary>
    /// <para>Gets the managed slot-index of a local variable.</para>
    /// </summary>
    /// <param name="pSlotIndex">
    /// <para>A pointer to the slot-index of a local variable.</para>
    /// </param>
    /// <returns>
    /// <para>The method returns the following values.</para>
    /// <list type="table">
    /// <listheader><term>Value</term><description>Description</description></listheader>
    /// <item><term><c>S_OK</c></term><description>The method call returned a slot-index value in <c>pSlotIndex</c>.</description></item>
    /// <item><term><c>E_FAIL</c></term><description>The current <see cref="T:Passenger.Debugger.Interop.ICorDebugVariableHome">ICorDebugVariableHome</see> instance represents a function argument.</description></item>
    /// </list>
    /// </returns>
    /// <remarks>
    /// <para>The slot-index can be used to retrieve the metadata for this local variable.</para>
    /// <para>Documentation adapted from <see href="https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebugvariablehome-getslotindex-method">Microsoft Learn</see>.</para>
    /// </remarks>
    [PreserveSig]
    int TryGetSlotIndex(out uint pSlotIndex);

    /// <summary>
    /// <para>Gets the index of a function argument.</para>
    /// </summary>
    /// <param name="pArgumentIndex">
    /// <para>A pointer to the argument index.</para>
    /// </param>
    /// <returns>
    /// <para>The method returns the following values.</para>
    /// <list type="table">
    /// <listheader><term>Value</term><description>Description</description></listheader>
    /// <item><term><c>S_OK</c></term><description>The method call returned a valid argument index.</description></item>
    /// <item><term><c>E_FAIL</c></term><description>The current <see cref="T:Passenger.Debugger.Interop.ICorDebugVariableHome">ICorDebugVariableHome</see> instance represents a local variable.</description></item>
    /// </list>
    /// </returns>
    /// <remarks>
    /// <para>The argument index can be used to retrieve metadata for this argument.</para>
    /// <para>Documentation adapted from <see href="https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebugvariablehome-getargumentindex-method">Microsoft Learn</see>.</para>
    /// </remarks>
    [PreserveSig]
    int TryGetArgumentIndex(out uint pArgumentIndex);

    /// <summary>
    /// <para>Gets the native range over which this variable is live.</para>
    /// </summary>
    /// <param name="pStartOffset">
    /// <para>The logical offset at which the variable is first live.</para>
    /// </param>
    /// <param name="pEndOffset">
    /// <para>The logical offset immediately after the point at which the variable is last live.</para>
    /// </param>
    /// <remarks>
    /// <para>Documentation adapted from <see href="https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebugvariablehome-getliverange-method">Microsoft Learn</see>.</para>
    /// </remarks>
    [PreserveSig]
    int TryGetLiveRange(out uint pStartOffset, out uint pEndOffset);

    /// <summary>
    /// <para>Gets the type of the variable's native location.</para>
    /// </summary>
    /// <param name="pLocationType">
    /// <para>A pointer to the type of the variable's native location.</para>
    /// </param>
    /// <remarks>
    /// <para>Documentation adapted from <see href="https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebugvariablehome-getlocationtype-method">Microsoft Learn</see>.</para>
    /// </remarks>
    [PreserveSig]
    int TryGetLocationType(out VariableLocationType pLocationType);

    /// <summary>
    /// <para>Gets the register that contains a variable with a location type of <c>VLT_REGISTER</c>, and the base register for a variable with a location type of <c>VLT_REGISTER_RELATIVE</c>.</para>
    /// </summary>
    /// <param name="pRegister">
    /// <para>A <see cref="T:Passenger.Debugger.Interop.CorDebugRegister">CorDebugRegister</see> enumeration value that indicates the register for a variable with a location type of <c>VLT_REGISTER</c>, and the base register for a variable with a location type of <c>VLT_REGISTER_RELATIVE</c>.</para>
    /// </param>
    /// <returns>
    /// <para>The method returns the following values:</para>
    /// <list type="table">
    /// <listheader><term>Value</term><description>Description</description></listheader>
    /// <item><term><c>S_OK</c></term><description>The variable is in the register indicated by the <c>pRegister</c> argument.</description></item>
    /// <item><term><c>E_FAIL</c></term><description>The variable is not in a register or a register-relative location.</description></item>
    /// </list>
    /// </returns>
    /// <remarks>
    /// <para>Documentation adapted from <see href="https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebugvariablehome-getregister-method">Microsoft Learn</see>.</para>
    /// </remarks>
    [PreserveSig]
    int TryGetRegister(out CorDebugRegister pRegister);

    /// <summary>
    /// <para>Gets the offset from the base register for a variable.</para>
    /// </summary>
    /// <param name="pOffset">
    /// <para>The offset from the base register.</para>
    /// </param>
    /// <returns>
    /// <para>The method returns the following values:</para>
    /// <list type="table">
    /// <listheader><term>Value</term><description>Description</description></listheader>
    /// <item><term><c>S_OK</c></term><description>The variable is in a register-relative memory location.</description></item>
    /// <item><term><c>E_FAIL</c></term><description>The variable is not in a register-relative memory location.</description></item>
    /// </list>
    /// </returns>
    /// <remarks>
    /// <para>Documentation adapted from <see href="https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebugvariablehome-getoffset-method">Microsoft Learn</see>.</para>
    /// </remarks>
    [PreserveSig]
    int TryGetOffset(out int pOffset);
}
