#nullable enable

namespace Passenger.Debugger.Interop;

/// <summary>
/// <para>Indicates the native location type of a variable.</para>
/// </summary>
/// <remarks>
/// <para>A member of the <c>VariableLocationType</c> enumeration is returned by the <see cref="M:Passenger.Debugger.Interop.ICorDebugVariableHome.TryGetLocationType(Passenger.Debugger.Interop.VariableLocationType@)">ICorDebugVariableHome::GetLocationType</see> method.</para>
/// <para>Documentation adapted from <see href="https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/variablelocationtype-enumeration">Microsoft Learn</see>.</para>
/// </remarks>
public enum VariableLocationType
{
    /// <summary>
    /// <para>The variable is in a register.</para>
    /// </summary>
    VLT_REGISTER,
    /// <summary>
    /// <para>The variable is in a register-relative memory location.</para>
    /// </summary>
    VLT_REGISTER_RELATIVE,
    /// <summary>
    /// <para>The variable is not stored in a register or a register-relative memory location.</para>
    /// </summary>
    VLT_INVALID
}
