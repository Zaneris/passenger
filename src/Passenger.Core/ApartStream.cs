using System.Runtime.ExceptionServices;

namespace Passenger.Core;

/// <summary>A stream whose reads block their thread (a pipe opened for blocking reads, a console's input) read on a thread
/// of its own (<see cref="ApartThreads"/>), never the pool's: an asynchronous read of such a stream waits on a thread of the
/// pool, and one read for as long as a conversation or a program lasts would hold that thread all that while. Read one
/// read at a time; a read given up (its token cancelled) leaves what its thread reads to the next. Writes go to the
/// stream as they are.</summary>
public sealed class ApartStream(Stream stream, string name) : Stream
{
    private readonly Lock gate = new();
    private readonly byte[] held = new byte[64 * 1024];
    private readonly SemaphoreSlim wanted = new(0);

    /// <summary>What was read and not yet handed out: <see cref="held"/> from <see cref="start"/> to <see cref="end"/>.</summary>
    private int start;
    private int end;
    private bool ended;
    private ExceptionDispatchInfo? failed;

    /// <summary>The read the thread is making, done once it has read; null while none is asked for.</summary>
    private TaskCompletionSource? reading;
    private bool serving;
    private bool disposed;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => stream.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Task arriving;
        lock (gate)
        {
            if (end > start || ended || failed is not null || buffer.Length == 0)
            {
                return Take(buffer.Span);
            }
            if (reading is null)
            {
                reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!serving)
                {
                    serving = true;
                    ApartThreads.Start(name, Serve);
                }
                wanted.Release();
            }
            arriving = reading.Task;
        }
        await arriving.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            return Take(buffer.Span);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Waits for its own thread's read: a caller reading synchronously asked to be held.</summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        Task<int> read = ReadAsync(buffer.AsMemory(offset, count)).AsTask();
        Blocking.Until(read);
        if (read.Exception?.InnerException is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
        return read.IsCompletedSuccessfully ? read.Result : 0;
    }

    public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        stream.WriteAsync(buffer, cancellationToken);

    public override void Flush() => stream.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => stream.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (gate)
            {
                disposed = true;
            }
            // A thread waiting to be asked for a read goes; one in a read goes once the stream gives it up.
            wanted.Release();
            stream.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>What is held, as much as fits; else the failure, or nothing at the stream's end. Under <see cref="gate"/>.</summary>
    private int Take(Span<byte> into)
    {
        if (end > start)
        {
            int taken = Math.Min(into.Length, end - start);
            held.AsSpan(start, taken).CopyTo(into);
            start += taken;
            return taken;
        }
        failed?.Throw();
        return 0;
    }

    /// <summary>The thread's reads, each once asked for, into <see cref="held"/>, which nothing holds while it reads.</summary>
    private void Serve()
    {
        while (true)
        {
            wanted.Wait();
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
            }
            int read = 0;
            ExceptionDispatchInfo? error = null;
            try
            {
                read = stream.Read(held);
            }
            catch (Exception fault)
            {
                // Kept for the reader, to whom it is thrown.
                error = ExceptionDispatchInfo.Capture(fault);
            }
            TaskCompletionSource done;
            lock (gate)
            {
                start = 0;
                end = read;
                ended = read == 0 && error is null;
                failed = error;
                done = reading!;
                reading = null;
            }
            done.SetResult();
            if (read == 0)
            {
                return;
            }
        }
    }
}
