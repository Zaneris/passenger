using System.Globalization;

namespace Passenger.Dap;

/// <summary>What an expression of a tracepoint's message or a [DebuggerDisplay] read where the program stands, as every
/// debugger tells it (<see cref="DisplayFormat.Shown"/>): the value as its debugger shows it, and what a format specifier
/// may show of it instead, a string's own text or a whole number's value and the bytes the program holds it in.</summary>
public sealed record DisplayValue
{
    private DisplayValue(string shown)
    {
        Shown = shown;
    }

    /// <summary>The value as its debugger shows it: a string quoted, a whole number in decimal.</summary>
    public string Shown { get; }

    /// <summary>A string's own text, unquoted; null for any other value.</summary>
    public string? Text { get; private init; }

    /// <summary>A whole number's value; null for any other value.</summary>
    public Int128? Whole { get; private init; }

    /// <summary>The bytes the program holds a whole number in: 4 for an int.</summary>
    public int Bytes { get; private init; }

    /// <summary>A string, shown quoted as its debugger quotes one.</summary>
    public static DisplayValue OfText(string text, string quoted) => new(quoted) { Text = text };

    /// <summary>A whole number the program holds in so many bytes (1 to 16), shown in decimal.</summary>
    public static DisplayValue OfWhole(Int128 value, int bytes) => bytes is >= 1 and <= 16
        ? new(value.ToString(CultureInfo.InvariantCulture)) { Whole = value, Bytes = bytes }
        : throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "A whole number is held in 1 to 16 bytes");

    /// <summary>Any other value, or why none was read, as its debugger says it, which no specifier changes.</summary>
    public static DisplayValue Said(string shown) => new(shown);
}
