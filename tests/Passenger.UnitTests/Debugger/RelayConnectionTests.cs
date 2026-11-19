using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Passenger.Core;
using Passenger.Debugger.Relay;

namespace Passenger.UnitTests.Debugger;

/// <summary>The debugger's end of the relay in an app on a device: a call answered by the frame that names its number, its
/// rooms and objects as asked for, and a relay answering more ending its session; once the app has gone, every call
/// failing, the one waiting and each made after it, as an app that died mid-call leaves a forward that still takes what is
/// written; a relay reached at a Unix socket's path (a Mac Catalyst app's, in its container) as at a port. Each test waits
/// for what it checks, however long a busy machine keeps the threads that bring it waiting: a call that waits for good
/// holds its test, which the run's own limit on a test names.</summary>
public class RelayConnectionTests
{
    /// <summary>How long a connection waits for the app's relay to listen, which the stand-ins here do before it asks.</summary>
    private static readonly TimeSpan Listening = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_call_is_answered_by_the_frame_naming_its_number()
    {
        StandInRelay app = new() { Answers = true };
        using RelayConnection relay = new(app, token: null, log: null);
        Assert.True(relay.ProcessId == StandInRelay.ProcessId, $"The relay's hello named process {relay.ProcessId}, not {StandInRelay.ProcessId}");
        RelayReply reply = await CallAsync(relay);
        Assert.True(reply.Hr == StandInRelay.Answer, $"A call came back with 0x{reply.Hr:X8}, not the 0x{StandInRelay.Answer:X8} the relay answered it with");
    }

    [Fact]
    public async Task A_room_answered_at_the_size_asked_for_comes_back_whole_with_its_objects()
    {
        StandInRelay app = new() { Reply = Answer(room: 8, objects: 2) };
        using RelayConnection relay = new(app, token: null, log: null);
        RelayReply reply = await Calling(relay, [RelayArg.Out(8), RelayArg.Objects(2)]).WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(reply.Outs[0].AsSpan().SequenceEqual(Enumerable.Range(1, 8).Select(i => (byte)i).ToArray())
            && reply.Objects[0].SequenceEqual([new RemotePointer(0x100, 0x101), new RemotePointer(0x200, 0x201)]),
            $"The room came back as {Convert.ToHexString(reply.Outs[0])} with {reply.Objects[0].Length} objects, not as the relay answered it");
    }

    /// <summary>A room of another size than asked for, or more objects than room was made for, would be written past the
    /// engine's own: the call fails and the session ends, saying why.</summary>
    [Theory]
    [InlineData(64, 2)]
    [InlineData(4, 2)]
    [InlineData(8, 3)]
    public async Task A_relay_answering_a_room_of_another_size_or_more_objects_than_asked_for_fails_the_call_and_ends_its_session(int room, int objects)
    {
        StandInRelay app = new() { Reply = Answer(room, objects) };
        List<string> said = [];
        using RelayConnection relay = new(app, token: null, log: line =>
        {
            lock (said)
            {
                said.Add(line);
            }
        });
        using ManualResetEventSlim gone = new();
        relay.WhenGone(gone.Set);
        await Assert.ThrowsAsync<IOException>(() => Calling(relay, [RelayArg.Out(8), RelayArg.Objects(2)]).WaitAsync(TestContext.Current.CancellationToken));
        gone.Wait(TestContext.Current.CancellationToken);
        lock (said)
        {
            Assert.True(said.Any(line => line.Contains("broken", StringComparison.Ordinal)), $"The session ended without saying the relay broke: {string.Join(" | ", said)}");
        }
    }

    [Fact]
    public async Task Once_the_apps_relay_goes_away_the_call_waiting_fails_and_so_does_every_call_after_it()
    {
        StandInRelay app = new();
        using RelayConnection relay = new(app, token: null, log: null);
        using ManualResetEventSlim gone = new();
        relay.WhenGone(gone.Set);
        Task<RelayReply> waiting = Calling(relay);
        await app.Asked.WaitAsync(TestContext.Current.CancellationToken);
        app.Die();
        await Assert.ThrowsAsync<IOException>(() => waiting.WaitAsync(TestContext.Current.CancellationToken));
        gone.Wait(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => CallAsync(relay));
    }

    [Fact]
    public async Task A_relay_listening_at_a_socket_path_is_reached_there_given_its_token_and_heard_saying_hello()
    {
        string folder = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-relay-").FullName);
        try
        {
            string path = Path.Combine(folder, "r");
            using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            Task<byte[]> heard = BackgroundWork.UnheldThread(() => Hello(listener));
            using RelayConnection relay = await BackgroundWork.UnheldThread(() => RelayConnection.Connect(path, "abc", Listening, log: null))
                .WaitAsync(TestContext.Current.CancellationToken);
            byte[] token = await heard.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(token.AsSpan().SequenceEqual((byte[])[8, 0, 0, 0, 0, 0, 0, 0, 4, (byte)'a', (byte)'b', (byte)'c']),
                $"The relay at {path} was first told {Convert.ToHexString(token)}, not the session's token, so it would turn the debugger away");
            Assert.True(relay.ProcessId == StandInRelay.ProcessId, $"The relay at {path} was heard naming process {relay.ProcessId}, not {StandInRelay.ProcessId}");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_relays_address_is_a_unix_socket_for_a_path_and_the_loopbacks_port_for_host_and_port()
    {
        Assert.Equal(new UnixDomainSocketEndPoint("/Users/a/Library/Containers/com.x/Data/tmp/passenger/r"),
            RelayConnection.EndPointOf("/Users/a/Library/Containers/com.x/Data/tmp/passenger/r"));
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 5599), RelayConnection.EndPointOf("127.0.0.1:5599"));
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 5600), RelayConnection.EndPointOf(":5600"));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(new UnixDomainSocketEndPoint(@"C:\Temp\passenger\r"), RelayConnection.EndPointOf(@"C:\Temp\passenger\r"));
        }
    }

    /// <summary>The app's end of a relay at a socket: one client taken, its first frame read whole and the relay's hello
    /// written; the frame read.</summary>
    private static byte[] Hello(Socket listener)
    {
        using Socket client = listener.Accept();
        byte[] said = new byte[12];
        for (int at = 0; at < said.Length;)
        {
            int read = client.Receive(said, at, said.Length - at, SocketFlags.None);
            if (read == 0)
            {
                break;
            }
            at += read;
        }
        byte[] hello = new byte[8 + 21];
        BinaryPrimitives.WriteUInt32LittleEndian(hello, 4 + 21);
        hello[8] = 0x80;
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(9), StandInRelay.ProcessId);
        client.Send(hello);
        return said;
    }

    private static Task<RelayReply> CallAsync(RelayConnection relay) => Calling(relay).WaitAsync(TestContext.Current.CancellationToken);

    /// <summary>A call made on a thread of its own, as the engine's are, held by nothing: one that waits for good must not
    /// hold the tests beside it.</summary>
    private static Task<RelayReply> Calling(RelayConnection relay, RelayArg[]? args = null) => BackgroundWork.UnheldThread(() => relay.Call(0x1000, 3, args ?? []));

    /// <summary>A call's answer as the relay writes one: S_OK, a room of <paramref name="room"/> bytes (1, 2, …), and
    /// <paramref name="objects"/> objects, each its pointer and identity.</summary>
    private static byte[] Answer(int room, int objects)
    {
        byte[] answer = new byte[4 + 4 + room + 4 + objects * 16];
        BinaryPrimitives.WriteInt32LittleEndian(answer.AsSpan(4), room);
        for (int i = 0; i < room; i++)
        {
            answer[8 + i] = (byte)(i + 1);
        }
        int at = 8 + room;
        BinaryPrimitives.WriteInt32LittleEndian(answer.AsSpan(at), objects);
        for (int i = 0; i < objects; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(answer.AsSpan(at + 4 + i * 16), (ulong)(0x100 * (i + 1)));
            BinaryPrimitives.WriteUInt64LittleEndian(answer.AsSpan(at + 12 + i * 16), (ulong)(0x100 * (i + 1) + 1));
        }
        return answer;
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

        /// <summary>What each call is answered with, in place of <see cref="Answer"/> alone; null for that.</summary>
        public byte[]? Reply { get; init; }

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
            if ((Answers || Reply is not null) && !frames.IsAddingCompleted)
            {
                byte[] answer = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(answer, Answer);
                frames.Add(Frame(id, Reply ?? answer));
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
