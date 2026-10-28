namespace Passenger.UnitTests;

/// <summary>Bytes read back in the pieces a pipe or socket might cut them into, each read given at most what
/// <paramref name="next"/> says (asked with the read's number), then the end.</summary>
internal sealed class ChunkedStream(byte[] data, Func<int, int> next) : Stream
{
    private int position;
    private int reads;

    public ChunkedStream(byte[] data, int chunk)
        : this(data, _ => chunk)
    {
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;

    public override long Position
    {
        get => position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int count = Math.Min(Math.Min(buffer.Length, Math.Max(1, next(reads++))), data.Length - position);
        data.AsSpan(position, count).CopyTo(buffer);
        position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
