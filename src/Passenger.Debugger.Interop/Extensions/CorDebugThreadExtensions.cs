#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Passenger.Debugger.Interop.Extensions;

public static class CorDebugThreadExtensions
{
    extension(ICorDebugThread instance)
    {
        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugThread.TryEnumerateChains(Passenger.Debugger.Interop.ICorDebugChainEnum@)" />
        public IEnumerable<ICorDebugChain> EnumerateChains()
        {
            Marshal.ThrowExceptionForHR(instance.TryEnumerateChains(out ICorDebugChainEnum ppChains));
            return CorDebugEnums.Walk<ICorDebugChain>(ppChains.TryNext);
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugThread.TryCreateEval(Passenger.Debugger.Interop.ICorDebugEval@)" />
        public ICorDebugEval CreateEval()
        {
            Marshal.ThrowExceptionForHR(instance.TryCreateEval(out ICorDebugEval ppEval));
            return ppEval;
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugThread.TryCreateStepper(Passenger.Debugger.Interop.ICorDebugStepper@)" />
        public ICorDebugStepper CreateStepper()
        {
            Marshal.ThrowExceptionForHR(instance.TryCreateStepper(out ICorDebugStepper ppStepper));
            return ppStepper;
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugThread.TryGetActiveFrame(Passenger.Debugger.Interop.ICorDebugFrame@)" />
        public ICorDebugFrame ActiveFrame
        {
            get
            {
                Marshal.ThrowExceptionForHR(instance.TryGetActiveFrame(out ICorDebugFrame ppFrame));
                return ppFrame;
            }
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugThread.TryGetAppDomain(Passenger.Debugger.Interop.ICorDebugAppDomain@)" />
        public ICorDebugAppDomain AppDomain
        {
            get
            {
                Marshal.ThrowExceptionForHR(instance.TryGetAppDomain(out ICorDebugAppDomain ppAppDomain));
                return ppAppDomain;
            }
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugThread.TryGetID(System.UInt32@)" />
        public int Id
        {
            get
            {
                Marshal.ThrowExceptionForHR(instance.TryGetID(out var pdwThreadId));
                return checked((int)pdwThreadId);
            }
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugThread.TryGetProcess(Passenger.Debugger.Interop.ICorDebugProcess@)" />
        public ICorDebugProcess Process
        {
            get
            {
                Marshal.ThrowExceptionForHR(instance.TryGetProcess(out ICorDebugProcess ppProcess));
                return ppProcess;
            }
        }
    }
}
