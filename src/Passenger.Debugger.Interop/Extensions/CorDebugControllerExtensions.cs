#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Passenger.Debugger.Interop.Extensions;

public static class CorDebugControllerExtensions
{
    extension(ICorDebugController instance)
    {
        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugController.TryIsRunning(System.Boolean@)" />
        public bool IsRunning
        {
            get
            {
                Marshal.ThrowExceptionForHR(instance.TryIsRunning(out var pbRunning));
                return pbRunning;
            }
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugController.TryContinue(System.Boolean)" />
        public void Continue(bool fIsOutOfBand)
        {
            Marshal.ThrowExceptionForHR(instance.TryContinue(fIsOutOfBand));
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugController.TryEnumerateThreads(Passenger.Debugger.Interop.ICorDebugThreadEnum@)" />
        public IEnumerable<ICorDebugThread> EnumerateThreads()
        {
            Marshal.ThrowExceptionForHR(instance.TryEnumerateThreads(out ICorDebugThreadEnum ppThreads));
            return CorDebugEnums.Walk<ICorDebugThread>(ppThreads.TryNext);
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugController.TrySetAllThreadsDebugState(Passenger.Debugger.Interop.CorDebugThreadState,Passenger.Debugger.Interop.ICorDebugThread)" />
        public void SetAllThreadsDebugState(CorDebugThreadState state, ICorDebugThread pExceptThisThread)
        {
            Marshal.ThrowExceptionForHR(instance.TrySetAllThreadsDebugState(state, pExceptThisThread));
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugController.TryStop(System.UInt32)" />
        public void Stop(int dwTimeoutIgnored)
        {
            Marshal.ThrowExceptionForHR(instance.TryStop(checked((uint)dwTimeoutIgnored)));
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugController.TryTerminate(System.UInt32)" />
        public void Terminate(int exitCode)
        {
            Marshal.ThrowExceptionForHR(instance.TryTerminate(checked((uint)exitCode)));
        }
    }
}
