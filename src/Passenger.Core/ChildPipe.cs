using System.IO.Pipes;
using System.Net.Sockets;
using Microsoft.Win32.SafeHandles;

namespace Passenger.Core;

/// <summary>What a child writes to its output or errors on macOS or Linux, read from its pipe through a socket on the
/// pipe's descriptor, so a read that waits for the child allocates nothing. The descriptor is taken from the process's
/// reader before anything reads it, that reader's disposal leaving it open; this closes it once disposed, which ends a
/// read under way. One read at a time.</summary>
internal sealed class ChildPipe : Stream
{
    private readonly Socket pipe;

    public ChildPipe(PipeStream taken)
    {
        SafePipeHandle handle = taken.SafePipeHandle;
        nint descriptor = handle.DangerousGetHandle();
        handle.SetHandleAsInvalid();
        SafeSocketHandle owned = new(descriptor, ownsHandle: true);
        try
        {
            pipe = new Socket(owned);
        }
        catch
        {
            owned.Dispose();
            throw;
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>The socket's own read, which allocates nothing when it waits and fails as the socket fails, with a
    /// <see cref="SocketException"/>.</summary>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        pipe.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return pipe.Receive(buffer, SocketFlags.None);
        }
        catch (SocketException failed)
        {
            throw new IOException(failed.Message, failed);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            pipe.Dispose();
        }
        base.Dispose(disposing);
    }
}
