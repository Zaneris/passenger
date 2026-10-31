using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks.Sources;

namespace Passenger.Core;

/// <summary>A stream's text as it comes, decoded as the process's own reader decodes a child's output: in its encoding,
/// unless the text begins with a byte order mark, which names another and is left out. A read of a buffer that waits
/// allocates nothing, waiting as itself (one task source, used again by every read) on its stream's own read, so over a
/// <see cref="ChildPipe"/> nothing at all; its reads of a line or of the whole text are a TextReader's own, each on a
/// thread of the pool. It owns its stream, let go once the text has ended; one read at a time.</summary>
internal sealed class ChildText : TextReader, IValueTaskSource<int>
{
    private const int Size = 4096;

    /// <summary>The byte order marks the process's reader takes, each with the encoding it names, a longer mark before the
    /// shorter one it begins with.</summary>
    private static readonly (byte[] Mark, Encoding Named)[] Marks =
    [
        ([0xFF, 0xFE, 0, 0], Encoding.UTF32),
        ([0xFF, 0xFE], Encoding.Unicode),
        ([0xFE, 0xFF], Encoding.BigEndianUnicode),
        ([0xEF, 0xBB, 0xBF], Encoding.UTF8),
        ([0, 0, 0xFE, 0xFF], new UTF32Encoding(bigEndian: true, byteOrderMark: true)),
    ];

    private readonly Stream stream;
    private readonly byte[] bytes = new byte[Size];
    private readonly Action came;
    private Decoder decoder;
    private char[] chars;

    /// <summary>The text's first bytes, held while they may still begin a byte order mark; none once it has been looked for.</summary>
    private int held;
    private bool looked;

    /// <summary>The characters decoded and not yet read, and whether the stream has ended.</summary>
    private int next;
    private int end;
    private bool ended;

    /// <summary>A read that waits: the stream's read it waits on, what it was given, and its answer once it has one.</summary>
    private ValueTask<int> reading;
    private Memory<char> wanted;
    private CancellationToken cancel;
    private ManualResetValueTaskSourceCore<int> answer;

    public ChildText(Stream stream, Encoding encoding)
    {
        this.stream = stream;
        decoder = encoding.GetDecoder();
        chars = new char[encoding.GetMaxCharCount(Size)];
        came = Came;
    }

    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return ValueTask.FromResult(0);
        }
        try
        {
            cancel = cancellationToken;
            if (Filled())
            {
                return ValueTask.FromResult(Take(buffer.Span));
            }
        }
        catch (Exception failed)
        {
            return ValueTask.FromException<int>(failed);
        }
        wanted = buffer;
        answer.Reset();
        reading.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(came);
        return new ValueTask<int>(this, answer.Version);
    }

    public override Task<int> ReadAsync(char[] buffer, int index, int count) => ReadAsync(buffer.AsMemory(index, count)).AsTask();

    public override int Read(Span<char> buffer)
    {
        while (next == end && !ended && !buffer.IsEmpty)
        {
            Decode(stream.Read(bytes.AsSpan(held)));
        }
        return Take(buffer);
    }

    public override int Read(char[] buffer, int index, int count) => Read(buffer.AsSpan(index, count));

    public override int Read()
    {
        int peeked = Peek();
        if (peeked >= 0)
        {
            next++;
        }
        return peeked;
    }

    public override int Peek()
    {
        while (next == end && !ended)
        {
            Decode(stream.Read(bytes.AsSpan(held)));
        }
        return next < end ? chars[next] : -1;
    }

    int IValueTaskSource<int>.GetResult(short token) => answer.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => answer.GetStatus(token);

    void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        answer.OnCompleted(continuation, state, token, flags);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            stream.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>The stream read, each read that ends at once decoded, until there are characters to read or the text has
    /// ended (true), or a read waits (false), kept as <see cref="reading"/>.</summary>
    private bool Filled()
    {
        while (next == end && !ended)
        {
            ValueTask<int> read = stream.ReadAsync(bytes.AsMemory(held), cancel);
            if (!read.IsCompleted)
            {
                reading = read;
                return false;
            }
            Decode(Brought(read));
        }
        return true;
    }

    /// <summary>The read waited on has ended: the text read on until the read waiting has its answer.</summary>
    private void Came()
    {
        int taken;
        try
        {
            ValueTask<int> read = reading;
            reading = default;
            Decode(Brought(read));
            if (!Filled())
            {
                reading.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(came);
                return;
            }
            taken = Take(wanted.Span);
        }
        catch (Exception failed)
        {
            wanted = default;
            answer.SetException(failed);
            return;
        }
        wanted = default;
        answer.SetResult(taken);
    }

    /// <summary>What a read that has ended brought, a failure at its stream's socket said as a stream's.</summary>
    private static int Brought(ValueTask<int> read)
    {
        try
        {
            return read.IsCompleted ? read.Result : throw new InvalidOperationException("A read was taken before it ended");
        }
        catch (SocketException failed)
        {
            throw new IOException(failed.Message, failed);
        }
    }

    /// <summary>The bytes a read brought after those held, decoded; none, the stream's end, whatever the decoder holds let
    /// out and the stream let go.</summary>
    private void Decode(int read)
    {
        int count = held + read;
        int from = 0;
        if (!looked)
        {
            if (MarkOf(bytes.AsSpan(0, count), read == 0) is not { } mark)
            {
                held = count;
                return;
            }
            looked = true;
            if (mark.Named is { } named)
            {
                decoder = named.GetDecoder();
                chars = new char[Math.Max(chars.Length, named.GetMaxCharCount(Size))];
                from = mark.Length;
            }
        }
        held = 0;
        next = 0;
        end = decoder.GetChars(bytes.AsSpan(from, count - from), chars, flush: read == 0);
        if (read == 0)
        {
            ended = true;
            stream.Dispose();
        }
    }

    private int Take(Span<char> into)
    {
        int taken = Math.Min(into.Length, end - next);
        chars.AsSpan(next, taken).CopyTo(into);
        next += taken;
        return taken;
    }

    /// <summary>The byte order mark the text begins with and the encoding it names, or no encoding where it begins with
    /// none; null while what came so far may still begin one, the text going on.</summary>
    internal static (Encoding? Named, int Length)? MarkOf(ReadOnlySpan<byte> first, bool ended)
    {
        foreach ((byte[] mark, Encoding named) in Marks)
        {
            if (first.StartsWith(mark))
            {
                return (named, mark.Length);
            }
            if (!ended && mark.AsSpan().StartsWith(first))
            {
                return null;
            }
        }
        return (null, 0);
    }
}
