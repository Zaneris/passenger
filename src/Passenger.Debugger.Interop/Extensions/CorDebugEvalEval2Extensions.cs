#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace Passenger.Debugger.Interop.Extensions;

public static class CorDebugEvalEval2Extensions
{
    extension(ICorDebugEval instance)
    {
        /// <inheritdoc cref="T:Passenger.Debugger.Interop.ICorDebugEval2" />
        public ICorDebugEval2 Eval2
        {
            get
            {
                return (instance as ICorDebugEval2) ?? throw new NotSupportedException("ICorDebugEval does not support ICorDebugEval2.");
            }
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugEval2.TryCallParameterizedFunction(Passenger.Debugger.Interop.ICorDebugFunction,System.UInt32,Passenger.Debugger.Interop.ICorDebugType[],System.UInt32,Passenger.Debugger.Interop.ICorDebugValue[])" />
        public void CallParameterizedFunction(ICorDebugFunction pFunction, int nTypeArgs, ICorDebugType[]? ppTypeArgs, int nArgs, ICorDebugValue[] ppArgs)
        {
            instance.Eval2.CallParameterizedFunction(pFunction, nTypeArgs, ppTypeArgs, nArgs, ppArgs);
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugEval2.TryNewParameterizedArray(Passenger.Debugger.Interop.ICorDebugType,System.UInt32,System.UInt32[],System.UInt32[])" />
        public void NewParameterizedArray(ICorDebugType pElementType, int rank, uint[] dims, uint[] lowBounds)
        {
            instance.Eval2.NewParameterizedArray(pElementType, rank, dims, lowBounds);
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugEval2.TryNewParameterizedObject(Passenger.Debugger.Interop.ICorDebugFunction,System.UInt32,Passenger.Debugger.Interop.ICorDebugType[],System.UInt32,Passenger.Debugger.Interop.ICorDebugValue[])" />
        public void NewParameterizedObject(ICorDebugFunction pConstructor, int nTypeArgs, ICorDebugType[]? ppTypeArgs, int nArgs, ICorDebugValue[] ppArgs)
        {
            instance.Eval2.NewParameterizedObject(pConstructor, nTypeArgs, ppTypeArgs, nArgs, ppArgs);
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugEval2.TryNewParameterizedObjectNoConstructor(Passenger.Debugger.Interop.ICorDebugClass,System.UInt32,Passenger.Debugger.Interop.ICorDebugType[])" />
        public void NewParameterizedObjectNoConstructor(ICorDebugClass pClass, int nTypeArgs, ICorDebugType[]? ppTypeArgs)
        {
            instance.Eval2.NewParameterizedObjectNoConstructor(pClass, nTypeArgs, ppTypeArgs);
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugEval2.TryNewStringWithLength(System.String,System.UInt32)" />
        public void NewStringWithLength(string @string)
        {
            instance.Eval2.NewStringWithLength(@string);
        }
    }
}
