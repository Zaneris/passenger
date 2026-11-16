using System.Text;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    private static string GetDecimalValueString(ICorDebugObjectValue corDebugObjectValue)
    {
        ICorDebugModule module = corDebugObjectValue.Class.Module;
        IMetaDataImport metaDataImport = module.GetMetaDataInterface<IMetaDataImport>();
        mdTypeDef classToken = corDebugObjectValue.Class.Token;

        uint lo = 0, mid = 0, hi = 0, flags = 0;
        bool hasLo = false, hasMid = false, hasHi = false, hasFlags = false;

        foreach (mdFieldDef fieldDef in metaDataImport.EnumFields(classToken))
        {
            (string szField, mdTypeDef pClass, CorFieldAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, CorElementType pdwCPlusTypeFlag, nint ppValue, int pcchValue) fieldProps = metaDataImport.GetFieldProps(fieldDef);
            string? name = fieldProps.szField;
            if (name is null)
            {
                continue;
            }

            // Read at the field's own size, the room the value's bytes are written into wherever they come from.
            uint ReadUInt32Field() =>
                BitConverter.ToUInt32(((ICorDebugGenericValue)corDebugObjectValue.GetFieldValue(corDebugObjectValue.Class, fieldDef).UnwrapDebugValue()).GetValueAsBytes());

            switch (name)
            {
                case "lo" or "_lo32":
                    lo = ReadUInt32Field();
                    hasLo = true;
                    break;
                case "mid":
                    mid = ReadUInt32Field();
                    hasMid = true;
                    break;
                case "hi" or "_hi32":
                    hi = ReadUInt32Field();
                    hasHi = true;
                    break;
                case "flags" or "_flags":
                    flags = ReadUInt32Field();
                    hasFlags = true;
                    break;
                case "_lo64":
                    // .NET 7+ layout: _lo64 is a ulong covering lo+mid
                    ulong raw64 = BitConverter.ToUInt64(((ICorDebugGenericValue)corDebugObjectValue.GetFieldValue(corDebugObjectValue.Class, fieldDef).UnwrapDebugValue()).GetValueAsBytes());
                    lo = (uint)(raw64 & 0xFFFFFFFF);
                    mid = (uint)(raw64 >> 32);

                    hasLo = true;
                    hasMid = true;
                    break;
            }
        }

        if (!hasLo || !hasMid || !hasHi || !hasFlags)
        {
            return "{decimal}"; // fallback if layout is unrecognised
        }

        // The 96-bit mantissa, hi:mid:lo, with its scale and sign.
        uint[] words = [lo, mid, hi]; // little-endian words for the big-integer arithmetic
        string digits = UInt96ToDecimalDigits(words);

        uint scale = (flags >> 16) & 0xFF;
        bool negative = (flags & 0x80000000u) != 0;

        if (scale > 0)
        {
            if (digits.Length > (int)scale)
            {
                digits = digits.Insert(digits.Length - (int)scale, ".");
            }
            else
            {
                // e.g. scale=3, digits="5" → "0.005"
                digits = "0." + digits.PadLeft((int)scale, '0');
            }
        }

        return negative ? $"-{digits}" : digits;
    }

    /// Converts a 96-bit unsigned integer (stored as three little-endian uint32 words: [lo, mid, hi])
    /// to its base-10 string representation.
    private static string UInt96ToDecimalDigits(uint[] words)
    {
        // Repeated division by 10 using 32-bit limbs
        uint[] limbs = (uint[])words.Clone();
        StringBuilder result = new System.Text.StringBuilder();
        while (limbs[0] != 0 || limbs[1] != 0 || limbs[2] != 0)
        {
            ulong carry = 0;
            for (int i = 2; i >= 0; i--)
            {
                ulong cur = carry * 0x100000000UL + limbs[i];
                limbs[i] = (uint)(cur / 10);
                carry = cur % 10;
            }
            result.Insert(0, (char)('0' + carry));
        }
        return result.Length == 0 ? "0" : result.ToString();
    }
}
