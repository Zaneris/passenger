using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Text;

namespace Passenger.Lsp;

/// <summary>A language server's input or output, passed through unchanged, each message that crosses it handed whole to
/// <see cref="LanguageServer.Trace"/> while one is set. The header-delimited frames are followed all along, so a trace
/// turned on mid-conversation starts at the next message; a frame that does not read as one ends the tracing of this
/// stream, never the connection. The output notes, as each message's last byte is read, how many had then crossed the
/// input (<paramref name="across"/>), the order the trace shows.</summary>
internal sealed class TracedStream(Stream inner, int server, string direction, TracedStream? across = null) : Stream
{
    /// <summary>The most of one message a trace keeps: a completion list or a file's colours can be megabytes.</summary>
    private const int Kept = 64 * 1024;

    /// <summary>The header read so far, in one buffer of the most a header may be: past it the frames no longer read.</summary>
    private readonly byte[] header = new byte[4097];
    private int headerLength;
    private readonly MemoryStream body = new();
    private readonly Lock gate = new();
    private int remaining = -1;
    private int length;
    private bool tracing;
    private bool lost;
    private long crossed;
    private readonly Queue<long> crossedAcross = [];

    /// <summary>How many messages have crossed whole so far, each counted once its last byte has; past any count once the
    /// frames no longer read, so that nothing is taken to have come before a message that may have crossed.</summary>
    public long Crossed
    {
        get
        {
            lock (gate)
            {
                return lost ? long.MaxValue : crossed;
            }
        }
    }

    /// <summary>How many messages had crossed <c>across</c> whole when the next message read off this one had, each told
    /// once and in order; past any count where none was noted, the frames no longer read.</summary>
    public long CrossedAcrossBeforeNext()
    {
        lock (gate)
        {
            return crossedAcross.TryDequeue(out long at) ? at : long.MaxValue;
        }
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer);
        Follow(buffer[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // Pooled: a long answer arrives in thousands of reads, each finishing later than it began, which would make a task each.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Follow(buffer.Span[..read]);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        Follow(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Follow(buffer.Span);
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();

    /// <summary>Follows the frames through bytes that crossed, in order; each message ends in the trace whole.</summary>
    private void Follow(ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            while (bytes.Length > 0 && !lost)
            {
                if (remaining < 0)
                {
                    int taken = Math.Min(header.Length - headerLength, bytes.Length);
                    bytes[..taken].CopyTo(header.AsSpan(headerLength));
                    // The blank line may begin in what was held before.
                    int from = Math.Max(0, headerLength - 3);
                    headerLength += taken;
                    int end = header.AsSpan(from, headerLength - from).IndexOf("\r\n\r\n"u8);
                    if (end >= 0)
                    {
                        int length = from + end + 4;
                        bytes = bytes[(taken - (headerLength - length))..];
                        headerLength = length;
                        Begin();
                    }
                    else
                    {
                        bytes = bytes[taken..];
                        lost = headerLength == header.Length;
                    }
                    continue;
                }
                int part = Math.Min(remaining, bytes.Length);
                if (tracing && body.Length < Kept)
                {
                    body.Write(bytes[..Math.Min(part, Kept - (int)body.Length)]);
                }
                remaining -= part;
                bytes = bytes[part..];
                if (remaining == 0)
                {
                    End();
                }
            }
        }
    }

    private void Begin()
    {
        ReadOnlySpan<byte> lines = header.AsSpan(0, headerLength);
        headerLength = 0;
        length = -1;
        while (lines.Length > 0)
        {
            int next = lines.IndexOf("\r\n"u8);
            ReadOnlySpan<byte> line = next < 0 ? lines : lines[..next];
            lines = next < 0 ? [] : lines[(next + 2)..];
            int colon = line.IndexOf((byte)':');
            if (colon > 0 && Ascii.EqualsIgnoreCase(line[..colon][Ascii.Trim(line[..colon])], "Content-Length"u8))
            {
                ReadOnlySpan<byte> value = line[(colon + 1)..];
                value = value[Ascii.Trim(value)];
                if (value.Length > 0 && !value.ContainsAnyExceptInRange((byte)'0', (byte)'9') && Utf8Parser.TryParse(value, out int found, out int read) && read == value.Length)
                {
                    length = found;
                }
            }
        }
        if (length < 0)
        {
            lost = true;
            return;
        }
        remaining = length;
        tracing = LanguageServer.Trace is not null;
        body.SetLength(0);
        if (remaining == 0)
        {
            End();
        }
    }

    private void End()
    {
        remaining = -1;
        crossed++;
        if (across is not null)
        {
            crossedAcross.Enqueue(across.Crossed);
        }
        if (tracing && LanguageServer.Trace is { } trace)
        {
            string message = Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
            trace(server, length > Kept ? $"{direction} {message}… ({length:N0} bytes in all)" : $"{direction} {message}");
        }
        body.SetLength(0);
    }
}
