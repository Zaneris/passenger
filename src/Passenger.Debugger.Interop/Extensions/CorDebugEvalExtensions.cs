#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Passenger.Debugger.Interop.Extensions;

public static class CorDebugEvalExtensions
{
    extension(ICorDebugEval instance)
    {
        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugEval.TryCreateValue(Passenger.Debugger.Interop.CorElementType,Passenger.Debugger.Interop.ICorDebugClass,Passenger.Debugger.Interop.ICorDebugValue@)" />
        public ICorDebugValue CreateValue(CorElementType elementType, ICorDebugClass? pElementClass)
        {
            Marshal.ThrowExceptionForHR(instance.TryCreateValue(elementType, pElementClass, out ICorDebugValue ppValue));
            return ppValue;
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugEval.TryGetResult(Passenger.Debugger.Interop.ICorDebugValue@)" />
        public ICorDebugValue Result
        {
            get
            {
                Marshal.ThrowExceptionForHR(instance.TryGetResult(out ICorDebugValue ppResult));
                return ppResult;
            }
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugEval.TryGetThread(Passenger.Debugger.Interop.ICorDebugThread@)" />
        public ICorDebugThread Thread
        {
            get
            {
                Marshal.ThrowExceptionForHR(instance.TryGetThread(out ICorDebugThread ppThread));
                return ppThread;
            }
        }
    }
}
