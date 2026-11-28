#nullable enable

using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Passenger.Debugger.Interop.Extensions;

public static class CorDebugCode4Extensions
{
    extension(ICorDebugCode4 instance)
    {
        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugCode4.TryEnumerateVariableHomes(Passenger.Debugger.Interop.ICorDebugVariableHomeEnum@)" />
        public IEnumerable<ICorDebugVariableHome> EnumerateVariableHomes()
        {
            Marshal.ThrowExceptionForHR(instance.TryEnumerateVariableHomes(out ICorDebugVariableHomeEnum ppEnum));
            return CorDebugEnums.Walk<ICorDebugVariableHome>(ppEnum.TryNext);
        }
    }
}
