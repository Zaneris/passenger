#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Passenger.Debugger.Interop.Extensions;

public static class CorDebugEval2Extensions
{
    extension(ICorDebugEval2 instance)
    {
        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugEval2.TryCallParameterizedFunction(Passenger.Debugger.Interop.ICorDebugFunction,System.UInt32,Passenger.Debugger.Interop.ICorDebugType[],System.UInt32,Passenger.Debugger.Interop.ICorDebugValue[])" />
        public void CallParameterizedFunction(ICorDebugFunction pFunction, int nTypeArgs, ICorDebugType[]? ppTypeArgs, int nArgs, ICorDebugValue[] ppArgs)
        {
            Marshal.ThrowExceptionForHR(checked(instance.TryCallParameterizedFunction(pFunction, (uint)nTypeArgs, ppTypeArgs, (uint)nArgs, ppArgs)));
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugEval2.TryNewParameterizedArray(Passenger.Debugger.Interop.ICorDebugType,System.UInt32,System.UInt32[],System.UInt32[])" />
        public void NewParameterizedArray(ICorDebugType pElementType, int rank, uint[] dims, uint[] lowBounds)
        {
            Marshal.ThrowExceptionForHR(instance.TryNewParameterizedArray(pElementType, checked((uint)rank), dims, lowBounds));
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugEval2.TryNewParameterizedObject(Passenger.Debugger.Interop.ICorDebugFunction,System.UInt32,Passenger.Debugger.Interop.ICorDebugType[],System.UInt32,Passenger.Debugger.Interop.ICorDebugValue[])" />
        public void NewParameterizedObject(ICorDebugFunction pConstructor, int nTypeArgs, ICorDebugType[]? ppTypeArgs, int nArgs, ICorDebugValue[] ppArgs)
        {
            Marshal.ThrowExceptionForHR(checked(instance.TryNewParameterizedObject(pConstructor, (uint)nTypeArgs, ppTypeArgs, (uint)nArgs, ppArgs)));
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugEval2.TryNewParameterizedObjectNoConstructor(Passenger.Debugger.Interop.ICorDebugClass,System.UInt32,Passenger.Debugger.Interop.ICorDebugType[])" />
        public void NewParameterizedObjectNoConstructor(ICorDebugClass pClass, int nTypeArgs, ICorDebugType[]? ppTypeArgs)
        {
            Marshal.ThrowExceptionForHR(instance.TryNewParameterizedObjectNoConstructor(pClass, checked((uint)nTypeArgs), ppTypeArgs));
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugEval2.TryNewStringWithLength(System.String,System.UInt32)" />
        public void NewStringWithLength(string @string)
        {
            Marshal.ThrowExceptionForHR(instance.TryNewStringWithLength(@string, checked((uint)@string.Length)));
        }
    }
}
