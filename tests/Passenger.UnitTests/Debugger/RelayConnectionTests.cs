using System.Buffers.Binary;
using System.Collections.Concurrent;
using Passenger.Core;
using Passenger.Debugger.Relay;

namespace Passenger.UnitTests.Debugger;

/// <summary>The debugger's end of the relay in an app on a device: a call answered by the frame that names its number, and
/// once the app has gone, every call failing, the one waiting and each made after it, as an app that died mid-call leaves
/// a forward that still takes what is written.</summary>
public class RelayConnectionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_call_is_answered_by_the_frame_naming_its_number()
    {
        StandInRelay app = new() { Answers = true };
        using RelayConnection relay = new(app, token: null, log: null);
        Assert.True(relay.ProcessId == StandInRelay.ProcessId, $"The relay's hello named process {relay.ProcessId}, not {StandInRelay.ProcessId}");
        RelayReply reply = await CallAsync(relay, "A call the relay answered");
        Assert.True(reply.Hr == StandInRelay.Answer, $"A call came back with 0x{reply.Hr:X8}, not the 0x{StandInRelay.Answer:X8} the relay answered it with");
    }

    [Fact]
    public async Task Once_the_apps_relay_goes_away_the_call_waiting_fails_and_so_does_every_call_after_it()
    {
        StandInRelay app = new();
        using RelayConnection relay = new(app, token: null, log: null);
        using ManualResetEventSlim gone = new();
        relay.WhenGone(gone.Set);
        Task<RelayReply> waiting = Calling(relay);
        Assert.True(await app.Asked.WaitAsync(Patience, TestContext.Current.CancellationToken), "The call was never written to the relay");
        app.Die();
        await Assert.ThrowsAsync<IOException>(() => Within(waiting, "A call waiting as the app's relay went away"));
        Assert.True(gone.Wait(Patience, TestContext.Current.CancellationToken), "The connection never said the relay had gone");
        await Assert.ThrowsAsync<IOException>(() => CallAsync(relay, "A call made after the app's relay went away"));
    }

    private static Task<RelayReply> CallAsync(RelayConnection relay, string what) => Within(Calling(relay), what);

    /// <summary>A call made on a thread of its own, as the engine's are, held by nothing: one that waits for good must not
    /// hold the tests beside it.</summary>
    private static Task<RelayReply> Calling(RelayConnection relay) => BackgroundWork.UnheldThread(() => relay.Call(0x1000, 3, []));

    private static async Task<RelayReply> Within(Task<RelayReply> call, string what)
    {
        try
        {
            return await call.WaitAsync(Patience, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException($"{what} still waits for an answer after {Patience.TotalSeconds:0} s: the debugger's thread that made it would wait for good");
        }
    }

    /// <summary>The app's end of the relay's stream: its hello, then an answer to each call where it <see cref="Answers"/>;
    /// once it dies, its frames end, while what is written to it is still taken, as by adb's forward half closed.</summary>
    private sealed class StandInRelay : Stream
    {
        public const uint ProcessId = 4321;
        public const int Answer = 0x1234;

        private readonly BlockingCollection<byte[]> frames = [];
        private byte[] reading = [];
        private int read;

        public StandInRelay()
        {
            byte[] hello = new byte[21];
            hello[0] = 0x80;
            BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(1), ProcessId);
            frames.Add(Frame(0, hello));
        }

        public bool Answers { get; init; }

        /// <summary>Whether a call has been written.</summary>
        public SemaphoreSlim Asked { get; } = new(0);

        public void Die() => frames.CompleteAdding();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (read == reading.Length)
            {
                if (!frames.TryTake(out byte[]? next, Timeout.Infinite))
                {
                    return 0;
                }
                (reading, read) = (next, 0);
            }
            int given = Math.Min(count, reading.Length - read);
            reading.AsSpan(read, given).CopyTo(buffer.AsSpan(offset));
            read += given;
            return given;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            // A call's header comes in one write and its body in the next: its number is in the header.
            if (count != 8)
            {
                Asked.Release();
                return;
            }
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 4));
            if (Answers && !frames.IsAddingCompleted)
            {
                byte[] answer = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(answer, Answer);
                frames.Add(Frame(id, answer));
            }
        }

        private static byte[] Frame(uint id, byte[] body)
        {
            byte[] frame = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length + 4);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), id);
            body.CopyTo(frame, 8);
            return frame;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                frames.CompleteAdding();
                Asked.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
