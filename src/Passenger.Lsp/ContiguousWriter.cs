using System.IO.Pipelines;
using System.Numerics;

namespace Passenger.Lsp;

/// <summary>What is written to a stream, gathered in one buffer kept for the conversation and written to the stream at each
/// flush, so the one span a message handler asks for a message's whole content is never an array taken from a pool. The
/// buffer grows to the longest message written; the conversation's end hands it to the next. The stream is left open.</summary>
internal sealed class ContiguousWriter(Stream stream) : PipeWriter
{
    private const int FirstSize = 4 * 1024;

    /// <summary>The buffer of a conversation ended, which the next conversation takes rather than growing one anew.</summary>
    private static byte[]? spare;

    private byte[] buffer = Interlocked.Exchange(ref spare, null) ?? new byte[FirstSize];

    /// <summary>The bytes at the front of <see cref="buffer"/> written since the last flush.</summary>
    private int written;

    private bool completed;

    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        Room(sizeHint);
        return buffer.AsMemory(written);
    }

    public override Span<byte> GetSpan(int sizeHint = 0)
    {
        Room(sizeHint);
        return buffer.AsSpan(written);
    }

    public override void Advance(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, buffer.Length - written);
        written += bytes;
    }

    /// <summary>Writes what was written since the last flush to the stream, waiting until the stream has taken it.</summary>
    public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(completed, this);
        int length = written;
        written = 0;
        await stream.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new FlushResult(isCanceled: false, isCompleted: false);
    }

    /// <summary>Not taken: a flush is the stream's own write, which only its token cancels.</summary>
    public override void CancelPendingFlush() => throw new NotSupportedException("A flush is the stream's own write, canceled by its token");

    /// <summary>Ends the writing, with no flush under way: what is not flushed is dropped, and the buffer kept for the next
    /// conversation where it is larger than the one kept.</summary>
    public override void Complete(Exception? exception = null)
    {
        if (completed)
        {
            return;
        }
        completed = true;
        if (spare is not { } kept || kept.Length < buffer.Length)
        {
            spare = buffer;
        }
        buffer = [];
        written = 0;
    }

    /// <summary>Room for <paramref name="sizeHint"/> bytes (one at least) past what is written: a buffer that lacks it is
    /// replaced by one twice as large, or the next power of two holding it.</summary>
    private void Room(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(completed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        long needed = (long)written + Math.Max(sizeHint, 1);
        if (needed <= buffer.Length)
        {
            return;
        }
        byte[] larger = new byte[(int)Math.Min(Array.MaxLength, Math.Max(2L * buffer.Length, (long)BitOperations.RoundUpToPowerOf2((ulong)needed)))];
        Buffer.BlockCopy(buffer, 0, larger, 0, written);
        buffer = larger;
    }
}
