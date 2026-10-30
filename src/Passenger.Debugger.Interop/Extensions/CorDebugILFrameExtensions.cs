#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Passenger.Debugger.Interop.Extensions;

public static class CorDebugILFrameExtensions
{
    extension(ICorDebugILFrame instance)
    {
        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugILFrame.TryEnumerateArguments(Passenger.Debugger.Interop.ICorDebugValueEnum@)" />
        public IEnumerable<ICorDebugValue> EnumerateArguments()
        {
            Marshal.ThrowExceptionForHR(instance.TryEnumerateArguments(out ICorDebugValueEnum ppValueEnum));
            return CorDebugEnums.Values(ppValueEnum);
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugILFrame.TryEnumerateLocalVariables(Passenger.Debugger.Interop.ICorDebugValueEnum@)" />
        public IEnumerable<ICorDebugValue> EnumerateLocalVariables()
        {
            Marshal.ThrowExceptionForHR(instance.TryEnumerateLocalVariables(out ICorDebugValueEnum ppValueEnum));
            return CorDebugEnums.Values(ppValueEnum);
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugILFrame.TryEnumerateArguments(Passenger.Debugger.Interop.ICorDebugValueEnum@)" />
        public ICorDebugValue[] Arguments
        {
            get
            {
                return instance.EnumerateArguments().ToArray();
            }
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugILFrame.TryEnumerateLocalVariables(Passenger.Debugger.Interop.ICorDebugValueEnum@)" />
        public ICorDebugValue[] LocalVariables
        {
            get
            {
                return instance.EnumerateLocalVariables().ToArray();
            }
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugILFrame.TryGetArgument(System.UInt32,Passenger.Debugger.Interop.ICorDebugValue@)" />
        public ICorDebugValue GetArgument(int dwIndex)
        {
            Marshal.ThrowExceptionForHR(instance.TryGetArgument(checked((uint)dwIndex), out ICorDebugValue ppValue));
            return ppValue;
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugILFrame.TryGetIP(System.UInt32@,Passenger.Debugger.Interop.CorDebugMappingResult@)" />
        public (int pnOffset, CorDebugMappingResult pMappingResult) IP
        {
            get
            {
                Marshal.ThrowExceptionForHR(instance.TryGetIP(out var pnOffset, out var pMappingResult));
                return (pnOffset: checked((int)pnOffset), pMappingResult: pMappingResult);
            }
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugILFrame.TryGetLocalVariable(System.UInt32,Passenger.Debugger.Interop.ICorDebugValue@)" />
        public ICorDebugValue GetLocalVariable(int dwIndex)
        {
            Marshal.ThrowExceptionForHR(instance.TryGetLocalVariable(checked((uint)dwIndex), out ICorDebugValue ppValue));
            return ppValue;
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugILFrame.TrySetIP(System.UInt32)" />
        public void SetIP(int nOffset)
        {
            Marshal.ThrowExceptionForHR(instance.TrySetIP(checked((uint)nOffset)));
        }
    }
}
