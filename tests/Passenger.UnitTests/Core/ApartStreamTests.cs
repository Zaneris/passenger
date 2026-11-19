using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A stream whose reads block their thread, read through <see cref="ApartStream"/>: its reads happen on a thread of
/// its own, never the pool's, and a read given up leaves what its thread read to the next, then the stream's end.</summary>
public class ApartStreamTests
{
    [Fact]
    public async Task Reads_happen_on_a_thread_of_its_own_and_a_read_given_up_leaves_its_bytes_to_the_next()
    {
        using Blocked blocked = new("hello"u8.ToArray());
        await using ApartStream apart = new(blocked, "apart");
        byte[] buffer = new byte[16];
        using (CancellationTokenSource givenUp = new())
        {
            Task<int> first = apart.ReadAsync(buffer.AsMemory(), givenUp.Token).AsTask();
            blocked.Asked.Wait(TestContext.Current.CancellationToken);
            await givenUp.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }
        blocked.LetGo.Set();
        int read = await apart.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken);
        Assert.True(read == 5 && buffer.AsSpan(0, read).SequenceEqual("hello"u8),
            $"After a read given up, the next read gave {read} bytes, \"{System.Text.Encoding.UTF8.GetString(buffer, 0, read)}\": what the given-up read's thread read was lost");
        Assert.True(blocked.ReadOnPool == false, "The stream's blocking read ran on a thread of the pool, which it would hold for as long as it waits");
        Assert.True(await apart.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken) == 0, "The stream's end was not told after its bytes");
    }

    /// <summary>A stream whose first read blocks until let go, then gives its bytes, then its end.</summary>
    private sealed class Blocked(byte[] bytes) : Stream
    {
        private bool given;

        public ManualResetEventSlim Asked { get; } = new();

        public ManualResetEventSlim LetGo { get; } = new();

        public bool? ReadOnPool { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (given)
            {
                return 0;
            }
            ReadOnPool = Thread.CurrentThread.IsThreadPoolThread;
            Asked.Set();
            LetGo.Wait();
            given = true;
            bytes.CopyTo(buffer, offset);
            return bytes.Length;
        }

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
                LetGo.Set();
                Asked.Dispose();
                LetGo.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
