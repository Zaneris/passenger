#nullable enable

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Passenger.Debugger.Interop.Extensions;

public static class CorDebugStringValueExtensions
{
    extension(ICorDebugStringValue instance)
    {
        /// <summary>The string's characters, every one its length counts: a buffer of that length holds no terminator, so a
        /// last NUL is the string's own.</summary>
        public string String
        {
            get
            {
                int num = instance.Length;
                if (num == 0)
                {
                    return string.Empty;
                }
                char[] array = new char[num];
                int num2;
                checked
                {
                    Marshal.ThrowExceptionForHR(instance.TryGetString((uint)num, out var pcchString, array));
                    if (pcchString > num)
                    {
                        throw new InvalidOperationException("Native buffer size exceeded the reported capacity.");
                    }
                    num2 = (int)pcchString;
                }
                return new string(array, 0, num2);
            }
        }

        /// <inheritdoc cref="M:Passenger.Debugger.Interop.ICorDebugStringValue.TryGetLength(System.UInt32@)" />
        public int Length
        {
            get
            {
                Marshal.ThrowExceptionForHR(instance.TryGetLength(out var pcchString));
                return checked((int)pcchString);
            }
        }
    }
}
