using System.Globalization;

namespace Passenger.Dap;

/// <summary>The .NET adapter's memory references are hexadecimal addresses. The window accepts them with or without 0x.</summary>
public static class MemoryAddresses
{
    public static string Show(ulong address) => string.Create(CultureInfo.InvariantCulture, $"0x{address:X16}");

    public static bool TryParse(ReadOnlySpan<char> text, out ulong address)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }
        return ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
    }

    public static bool TryOffset(ulong start, long offset, out ulong address)
    {
        ulong distance = unchecked((ulong)(offset < 0 ? -offset : offset));
        if (offset < 0 ? start < distance : start > ulong.MaxValue - distance)
        {
            address = 0;
            return false;
        }
        address = offset < 0 ? start - distance : start + distance;
        return true;
    }
}
