using System.Buffers.Text;
using System.Text;

namespace Passenger.Core;

/// <summary>The header before each message of the Language Server and Debug Adapter Protocols: lines of a name, a colon and
/// a value, one named Content-Length giving the body's length in bytes, ended by a blank line.</summary>
public static class ContentLengthHeader
{
    /// <summary>The most bytes <see cref="Write"/> writes: the name, a long's 19 digits and the blank line.</summary>
    public const int Longest = 16 + 19 + 4;

    /// <summary>The blank line that ends a header, with the end of its last line before it.</summary>
    public static ReadOnlySpan<byte> End => "\r\n\r\n"u8;

    private static ReadOnlySpan<byte> Name => "Content-Length"u8;

    /// <summary>The body's length the first line of a header to give one in digits alone gives, its name in any case and either
    /// side trimmed; read as a long, so a length past what its reader takes is refused by its size rather than taken for none,
    /// and one past a long is <see cref="long.MaxValue"/>. Null where no line gives one.</summary>
    public static long? Read(ReadOnlySpan<byte> header)
    {
        ReadOnlySpan<byte> lines = header;
        while (lines.Length > 0)
        {
            int next = lines.IndexOf("\r\n"u8);
            ReadOnlySpan<byte> line = next < 0 ? lines : lines[..next];
            lines = next < 0 ? [] : lines[(next + 2)..];
            int colon = line.IndexOf((byte)':');
            if (colon > 0 && Ascii.EqualsIgnoreCase(line[..colon][Ascii.Trim(line[..colon])], Name))
            {
                ReadOnlySpan<byte> value = line[(colon + 1)..];
                value = value[Ascii.Trim(value)];
                if (value.Length > 0 && !value.ContainsAnyExceptInRange((byte)'0', (byte)'9'))
                {
                    return Utf8Parser.TryParse(value, out long length, out int read) && read == value.Length ? length : long.MaxValue;
                }
            }
        }
        return null;
    }

    /// <summary>Writes the header of a body of <paramref name="length"/> bytes, its blank line included, into
    /// <paramref name="into"/> (<see cref="Longest"/> bytes or more), and gives how many bytes it wrote.</summary>
    public static int Write(long length, Span<byte> into)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Name.CopyTo(into);
        ": "u8.CopyTo(into[Name.Length..]);
        int at = Name.Length + 2;
        Utf8Formatter.TryFormat(length, into[at..], out int digits);
        at += digits;
        End.CopyTo(into[at..]);
        return at + End.Length;
    }
}
