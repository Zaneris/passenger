using System.Buffers;
using System.IO.Pipelines;

namespace Passenger.Lsp;

/// <summary>What a stream says, read into one buffer kept for the conversation and handed out in one piece, so each
/// message read from it lies in one array and JSON parsed from it is read where it lies, with no copy made first. The
/// buffer grows to the most bytes unconsumed at once; what was consumed is moved off its front only to make room. The
/// stream is left open.</summary>
internal sealed class ContiguousReader(Stream stream) : PipeReader
{
    private const int FirstSize = 64 * 1024;

    /// <summary>The buffer of a conversation the other side ended with all it said consumed, which the next conversation
    /// takes rather than growing one anew.</summary>
    private static byte[]? spare;

    private readonly Lock gate = new();
    private byte[] buffer = Interlocked.Exchange(ref spare, null) ?? new byte[FirstSize];

    /// <summary>The first byte not consumed, the end of what was examined (a read waits for bytes past it), and the end of
    /// what was read, each an index into <see cref="buffer"/>.</summary>
    private int start;
    private int examined;
    private int end;
    private bool ended;
    private bool cancelNext;
    private CancellationTokenSource? reading;

    public override bool TryRead(out ReadResult result)
    {
        lock (gate)
        {
            if (cancelNext)
            {
                cancelNext = false;
                result = Result(canceled: true);
                return true;
            }
        }
        result = end > examined || ended ? Result(canceled: false) : default;
        return end > examined || ended;
    }

    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (end <= examined && !ended)
        {
            CancellationTokenSource asked;
            lock (gate)
            {
                if (cancelNext)
                {
                    cancelNext = false;
                    return Result(canceled: true);
                }
                if (reading is null || !reading.TryReset())
                {
                    reading?.Dispose();
                    reading = new CancellationTokenSource();
                }
                asked = reading;
            }
            Room();
            int read;
            using (cancellationToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), asked))
            {
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(end), asked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (asked.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // CancelPendingRead's, which this read answers.
                    lock (gate)
                    {
                        cancelNext = false;
                    }
                    return Result(canceled: true);
                }
            }
            ended = read == 0;
            end += read;
            if (ended && start == end && buffer.Length > 0)
            {
                // Nothing reads it again: what was read from it was consumed, its messages handled before this read.
                if (spare is not { } kept || kept.Length < buffer.Length)
                {
                    spare = buffer;
                }
                buffer = [];
                start = examined = end = 0;
            }
        }
        return Result(canceled: false);
    }

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        start = consumed.GetInteger();
        this.examined = Math.Max(start, examined.GetInteger());
        if (start == end)
        {
            start = this.examined = end = 0;
        }
    }

    public override void CancelPendingRead()
    {
        lock (gate)
        {
            cancelNext = true;
            reading?.Cancel();
        }
    }

    /// <summary>Nothing to let go: the stream is left open, and a read under way may still be canceled with its token.</summary>
    public override void Complete(Exception? exception = null)
    {
    }

    private ReadResult Result(bool canceled) => new(new ReadOnlySequence<byte>(buffer, start, end - start), canceled, ended);

    /// <summary>Room for a read past what is held: a quarter of the buffer at least, made by moving what is unconsumed to its
    /// front, or else in one twice as large.</summary>
    private void Room()
    {
        int unconsumed = end - start;
        if (buffer.Length - end >= buffer.Length / 4)
        {
            return;
        }
        if (buffer.Length - unconsumed < buffer.Length / 4)
        {
            byte[] larger = new byte[buffer.Length * 2];
            Buffer.BlockCopy(buffer, start, larger, 0, unconsumed);
            buffer = larger;
        }
        else
        {
            Buffer.BlockCopy(buffer, start, buffer, 0, unconsumed);
        }
        examined -= start;
        end = unconsumed;
        start = 0;
    }
}
