using System.Buffers;

namespace Passenger.Core;

/// <summary>A stream's lines as the UTF-8 bytes they came in, read into one array from the shared pool, which a long line
/// grows and which goes back to its usual size after a very long one. A line comes without its end ("\n" or "\r\n") and
/// holds only until the next is read. One reader at a time.</summary>
public sealed class Utf8Lines(Stream input) : IDisposable
{
    private const int Usual = 64 * 1024;

    /// <summary>The most the buffer is kept at once a line has grown it, so lines of the same length that follow read without
    /// growing it again.</summary>
    private const int MostKept = 4 * Usual;

    private byte[] buffer = ArrayPool<byte>.Shared.Rent(Usual);

    /// <summary>Where the next line begins in the buffer, and how far it is filled.</summary>
    private int start;
    private int filled;

    /// <summary>How far past <see cref="start"/> the bytes read hold no line's end.</summary>
    private int searched;

    /// <summary>The next line, null once the stream has ended; a last line with no end is a line too.</summary>
    public async ValueTask<ReadOnlyMemory<byte>?> NextAsync(CancellationToken cancel = default)
    {
        while (true)
        {
            int end = buffer.AsSpan(start + searched, filled - start - searched).IndexOf((byte)'\n');
            if (end >= 0)
            {
                int from = start;
                end += start + searched;
                start = end + 1;
                searched = 0;
                return buffer.AsMemory(from, end > from && buffer[end - 1] == (byte)'\r' ? end - from - 1 : end - from);
            }
            searched = filled - start;
            MakeRoom();
            int read = await input.ReadAsync(buffer.AsMemory(filled), cancel).ConfigureAwait(false);
            if (read == 0)
            {
                if (filled == start)
                {
                    return null;
                }
                int from = start;
                start = filled;
                searched = 0;
                return buffer.AsMemory(from, filled - from);
            }
            filled += read;
        }
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(buffer);
        buffer = [];
        start = filled = searched = 0;
    }

    /// <summary>The line begun moved to the front, into an array of the usual size again once a very long one has gone, or
    /// into one twice as large when it fills this one.</summary>
    private void MakeRoom()
    {
        int kept = filled - start;
        byte[] next = kept == buffer.Length ? ArrayPool<byte>.Shared.Rent(buffer.Length * 2)
            : buffer.Length > MostKept && kept <= Usual / 2 ? ArrayPool<byte>.Shared.Rent(Usual)
            : buffer;
        if (next == buffer && start == 0)
        {
            return;
        }
        buffer.AsSpan(start, kept).CopyTo(next);
        if (next != buffer)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = next;
        }
        start = 0;
        filled = kept;
    }
}
