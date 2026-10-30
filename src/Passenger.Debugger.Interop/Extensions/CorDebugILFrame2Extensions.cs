#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Passenger.Debugger.Interop.Extensions;

public static class CorDebugILFrame2Extensions
{
    extension(ICorDebugILFrame2 instance)
    {
        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugILFrame2.TryEnumerateTypeParameters(Passenger.Debugger.Interop.ICorDebugTypeEnum@)" />
        public IEnumerable<ICorDebugType> EnumerateTypeParameters()
        {
            Marshal.ThrowExceptionForHR(instance.TryEnumerateTypeParameters(out ICorDebugTypeEnum ppTyParEnum));
            return CorDebugEnums.Walk<ICorDebugType>(ppTyParEnum.TryNext);
        }
    }
}
