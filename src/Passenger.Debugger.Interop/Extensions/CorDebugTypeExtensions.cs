#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Passenger.Debugger.Interop.Extensions;

public static class CorDebugTypeExtensions
{
    extension(ICorDebugType instance)
    {
        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugType.TryEnumerateTypeParameters(Passenger.Debugger.Interop.ICorDebugTypeEnum@)" />
        public IEnumerable<ICorDebugType> EnumerateTypeParameters()
        {
            Marshal.ThrowExceptionForHR(instance.TryEnumerateTypeParameters(out ICorDebugTypeEnum ppTyParEnum));
            return CorDebugEnums.Walk<ICorDebugType>(ppTyParEnum.TryNext);
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugType.TryEnumerateTypeParameters(Passenger.Debugger.Interop.ICorDebugTypeEnum@)" />
        public ICorDebugType[] TypeParameters
        {
            get
            {
                return instance.EnumerateTypeParameters().ToArray();
            }
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugType.TryGetBase(Passenger.Debugger.Interop.ICorDebugType@)" />
        public ICorDebugType Base
        {
            get
            {
                Marshal.ThrowExceptionForHR(instance.TryGetBase(out ICorDebugType pBase));
                return pBase;
            }
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugType.TryGetClass(Passenger.Debugger.Interop.ICorDebugClass@)" />
        public ICorDebugClass Class
        {
            get
            {
                Marshal.ThrowExceptionForHR(instance.TryGetClass(out ICorDebugClass ppClass));
                return ppClass;
            }
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugType.TryGetFirstTypeParameter(Passenger.Debugger.Interop.ICorDebugType@)" />
        public ICorDebugType FirstTypeParameter
        {
            get
            {
                Marshal.ThrowExceptionForHR(instance.TryGetFirstTypeParameter(out ICorDebugType value));
                return value;
            }
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugType.TryGetRank(System.UInt32@)" />
        public int Rank
        {
            get
            {
                Marshal.ThrowExceptionForHR(instance.TryGetRank(out var pnRank));
                return checked((int)pnRank);
            }
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugType.TryGetStaticFieldValue(Passenger.Debugger.Interop.mdFieldDef,Passenger.Debugger.Interop.ICorDebugFrame,Passenger.Debugger.Interop.ICorDebugValue@)" />
        public ICorDebugValue GetStaticFieldValue(mdFieldDef fieldDef, ICorDebugFrame pFrame)
        {
            Marshal.ThrowExceptionForHR(instance.TryGetStaticFieldValue(fieldDef, pFrame, out ICorDebugValue ppValue));
            return ppValue;
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugType.TryGetType(Passenger.Debugger.Interop.CorElementType@)" />
        public CorElementType Type
        {
            get
            {
                Marshal.ThrowExceptionForHR(instance.TryGetType(out var ty));
                return ty;
            }
        }
    }
}
