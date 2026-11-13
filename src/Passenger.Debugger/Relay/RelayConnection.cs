using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;

namespace Passenger.Debugger.Relay;

/// <summary>One argument of a call through the relay: a number, bytes the callee reads, room it writes, both, or room
/// for interface pointers it hands back (each with its object's identity).</summary>
internal readonly record struct RelayArg(RelayArgKind Kind, ulong Value, byte[]? Bytes, int Size)
{
    public static RelayArg Number(ulong value) => new(RelayArgKind.Number, value, null, 0);
    public static RelayArg Out(int size) => new(RelayArgKind.Out, 0, null, size);
    public static RelayArg In(byte[] bytes) => new(RelayArgKind.In, 0, bytes, bytes.Length);
    public static RelayArg InOut(byte[] bytes) => new(RelayArgKind.InOut, 0, bytes, bytes.Length);
    public static RelayArg Objects(int count) => new(RelayArgKind.Objects, 0, null, count);
}

internal enum RelayArgKind : byte
{
    Number = 0,
    Out = 1,
    In = 2,
    InOut = 3,
    Objects = 4,
}

/// <summary>An interface pointer in the app, and the pointer to its IUnknown, which is its identity.</summary>
internal readonly record struct RemotePointer(ulong Pointer, ulong Identity);

/// <summary>A call's answer: its HRESULT, and what came back for each argument that brings something back, in order.</summary>
internal sealed record RelayReply(int Hr, IReadOnlyList<byte[]> Outs, IReadOnlyList<RemotePointer[]> Objects);

/// <summary>A managed callback the runtime made in the app: which callback interface (1 to 4), its method's place, and
/// its arguments as the relay sent them.</summary>
internal sealed record RelayEvent(int Interface, int Slot, byte[] Body, int Offset);

/// <summary>What the stand-ins need of the relay: calls answered in turn, and releases of the app's objects, which need no
/// answer.</summary>
internal interface IRelayLink
{
    ulong Callback { get; }

    BlockingCollection<RelayEvent> Events { get; }

    RelayReply Call(ulong obj, uint slot, IReadOnlyList<RelayArg> args);

    /// <summary>Lets go of one reference to an object in the app, sent with the next call or once enough wait.</summary>
    void Release(ulong pointer);

    /// <summary>The round trips and the time waited for their answers since the last tally.</summary>
    (int Trips, double WaitedMs) TakeTally();
}

/// <summary>The stream to Passenger's relay inside an app: calls, each answered in turn, and the runtime's callbacks,
/// which come whenever the app makes them and are handed on in order. Releases go unanswered, in batches written just
/// before a call, so letting go costs no round trip.</summary>
internal sealed class RelayConnection : IDisposable, IRelayLink
{
    private const int ReleasesSentAlone = 256;

    /// <summary>The longest frame the relay sends: a call's answer brings back no more than the call made room for.</summary>
    private const int LongestFrame = 64 << 20;
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly Lock _writing = new();
    private readonly ConcurrentDictionary<uint, Pending> _pending = new();
    private readonly BlockingCollection<RelayEvent> _events = new();
    private readonly Action<string>? _log;
    private int _nextId;
    private long _waited;
    private int _answered;
    private int _tallied;
    private readonly List<ulong> _releases = [];
    private long _talliedWait;
    private volatile bool _ended;

    public uint ProcessId { get; }
    public ulong CorDebug { get; }
    public ulong Callback { get; }

    /// <summary>The runtime's callbacks, as they come; done once the connection ends.</summary>
    public BlockingCollection<RelayEvent> Events => _events;

    private RelayConnection(Socket socket, string? token, Action<string>? log)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        _log = log;
        if (token is not null)
        {
            // The relay says nothing to a client that does not first give the token the app's environment gave it.
            byte[] said = [4, .. System.Text.Encoding.ASCII.GetBytes(token)];
            byte[] header = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)said.Length + 4);
            _stream.Write(header);
            _stream.Write(said);
        }
        byte[] hello = ReadFrame(out uint id) ?? throw new IOException("The relay closed before it said hello");
        if (id != 0 || hello[0] != 0x80)
        {
            throw new IOException("The relay's first word was not its hello");
        }
        ProcessId = BinaryPrimitives.ReadUInt32LittleEndian(hello.AsSpan(1));
        CorDebug = BinaryPrimitives.ReadUInt64LittleEndian(hello.AsSpan(5));
        Callback = BinaryPrimitives.ReadUInt64LittleEndian(hello.AsSpan(13));
        Thread reader = new(ReadAll) { IsBackground = true, Name = "Passenger relay reader" };
        reader.Start();
    }

    /// <summary>Connects to a relay at host:port (adb forwards a local port to the app's socket), giving it
    /// <paramref name="token"/>, waiting up to <paramref name="within"/> for the app to start listening.</summary>
    public static RelayConnection Connect(string address, string? token, TimeSpan within, Action<string>? log)
    {
        int colon = address.LastIndexOf(':');
        string host = colon > 0 ? address[..colon] : "127.0.0.1";
        int port = int.Parse(address[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        Deadline deadline = new(within);
        while (true)
        {
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                socket.Connect(host, port);
                return new RelayConnection(socket, token, log);
            }
            catch (Exception notYet) when (notYet is SocketException or IOException && !deadline.Passed)
            {
                // adb accepts the forward before the app listens, then closes it: the app is still starting.
                socket.Dispose();
                Thread.Sleep(200);
            }
        }
    }

    public RelayReply Call(ulong obj, uint slot, IReadOnlyList<RelayArg> args)
    {
        uint id = (uint)Interlocked.Increment(ref _nextId);
        if (id % 500 == 0)
        {
            _log?.Invoke($"Relay calls so far: {id}, waiting {Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _waited)).TotalMilliseconds / Math.Max(1, _answered):F2} ms for each answer");
        }
        using MemoryStream body = new();
        Span<byte> number = stackalloc byte[8];
        body.WriteByte(1);
        BinaryPrimitives.WriteUInt64LittleEndian(number, obj);
        body.Write(number);
        BinaryPrimitives.WriteUInt32LittleEndian(number, slot);
        body.Write(number[..4]);
        body.WriteByte((byte)args.Count);
        foreach (RelayArg arg in args)
        {
            body.WriteByte((byte)arg.Kind);
            if (arg.Kind == RelayArgKind.Number)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(number, arg.Value);
                body.Write(number);
                continue;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(number, (uint)arg.Size);
            body.Write(number[..4]);
            if (arg.Bytes is { } bytes && arg.Kind is RelayArgKind.In or RelayArgKind.InOut)
            {
                body.Write(bytes);
            }
        }
        byte[] answer = Ask(id, body.ToArray());
        int hr = BinaryPrimitives.ReadInt32LittleEndian(answer);
        int at = 4;
        List<byte[]> outs = [];
        List<RemotePointer[]> objects = [];
        // A call the relay refused unmade comes back as its HRESULT alone: its rooms as they were made, empty.
        bool unmade = answer.Length == 4;
        foreach (RelayArg arg in args)
        {
            if (unmade)
            {
                if (arg.Kind is RelayArgKind.Out or RelayArgKind.InOut)
                {
                    outs.Add(new byte[arg.Size]);
                }
                else if (arg.Kind == RelayArgKind.Objects)
                {
                    objects.Add(new RemotePointer[arg.Size]);
                }
            }
            else if (arg.Kind is RelayArgKind.Out or RelayArgKind.InOut)
            {
                int size = BinaryPrimitives.ReadInt32LittleEndian(answer.AsSpan(at));
                outs.Add(answer.AsSpan(at + 4, size).ToArray());
                at += 4 + size;
            }
            else if (arg.Kind == RelayArgKind.Objects)
            {
                int count = BinaryPrimitives.ReadInt32LittleEndian(answer.AsSpan(at));
                at += 4;
                RemotePointer[] pointers = new RemotePointer[count];
                for (int i = 0; i < count; i++, at += 16)
                {
                    pointers[i] = new(BinaryPrimitives.ReadUInt64LittleEndian(answer.AsSpan(at)), BinaryPrimitives.ReadUInt64LittleEndian(answer.AsSpan(at + 8)));
                }
                objects.Add(pointers);
            }
        }
        return new RelayReply(hr, outs, objects);
    }

    /// <summary>Lets the app's runtime go on with its start: the debugger has taken hold of it.</summary>
    public void Go()
    {
        uint id = (uint)Interlocked.Increment(ref _nextId);
        Ask(id, [2]);
    }

    public void Release(ulong pointer)
    {
        if (pointer == 0 || _ended)
        {
            return;
        }
        lock (_writing)
        {
            _releases.Add(pointer);
            if (_releases.Count >= ReleasesSentAlone)
            {
                WriteReleases();
            }
        }
    }

    /// <summary>Every release waiting, as one frame the relay answers nothing to: called under the write lock.</summary>
    private void WriteReleases()
    {
        if (_releases.Count == 0)
        {
            return;
        }
        byte[] frame = new byte[8 + 1 + 4 + _releases.Count * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)frame.Length - 4);
        frame[8] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(9), (uint)_releases.Count);
        for (int i = 0; i < _releases.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(13 + i * 8), _releases[i]);
        }
        _releases.Clear();
        try
        {
            _stream.Write(frame);
        }
        catch (Exception gone) when (gone is IOException or SocketException or ObjectDisposedException)
        {
            _log?.Invoke($"Releases could not reach the relay: {gone.Message}");
        }
    }

    private byte[] Ask(uint id, byte[] body)
    {
        Pending pending = new();
        _pending[id] = pending;
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length + 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), id);
        lock (_writing)
        {
            WriteReleases();
            _stream.Write(header);
            _stream.Write(body);
        }
        long asked = Stopwatch.GetTimestamp();
        pending.Answered.Wait();
        Interlocked.Add(ref _waited, Stopwatch.GetTimestamp() - asked);
        Interlocked.Increment(ref _answered);
        _pending.TryRemove(id, out _);
        return pending.Answer ?? throw new IOException("The app's relay went away");
    }

    private void ReadAll()
    {
        try
        {
            while (ReadFrame(out uint id) is { } frame)
            {
                if (id == 0 && frame.Length > 3 && frame[0] == 0x81)
                {
                    _events.Add(new RelayEvent(frame[1], frame[2], frame, 4));
                }
                else if (_pending.TryGetValue(id, out Pending? pending))
                {
                    pending.Answer = frame;
                    pending.Answered.Set();
                }
            }
        }
        catch (Exception gone) when (gone is IOException or SocketException or ObjectDisposedException)
        {
            _log?.Invoke($"The relay's stream ended: {gone.Message}");
        }
        Action? told;
        lock (_writing)
        {
            _ended = true;
            told = _gone;
        }
        _events.CompleteAdding();
        foreach (Pending pending in _pending.Values)
        {
            pending.Answered.Set();
        }
        told?.Invoke();
    }

    private Action? _gone;

    /// <summary>Tells <paramref name="told"/> once the stream has ended (the app gone with it), at once where it has already.</summary>
    public void WhenGone(Action told)
    {
        bool already;
        lock (_writing)
        {
            _gone = told;
            already = _ended;
        }
        if (already)
        {
            told();
        }
    }

    private byte[]? ReadFrame(out uint id)
    {
        id = 0;
        byte[] header = new byte[8];
        if (!Fill(header))
        {
            return null;
        }
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (length is < 5 or > LongestFrame)
        {
            throw new IOException($"The relay sent a frame of {length} bytes, which no frame of its is");
        }
        byte[] body = new byte[length - 4];
        return Fill(body) ? body : null;
    }

    private bool Fill(byte[] buffer)
    {
        int at = 0;
        while (at < buffer.Length)
        {
            int read = _stream.Read(buffer, at, buffer.Length - at);
            if (read == 0)
            {
                return false;
            }
            at += read;
        }
        return true;
    }

    public bool Ended => _ended;

    public (int Trips, double WaitedMs) TakeTally()
    {
        int answered = Volatile.Read(ref _answered);
        long waited = Interlocked.Read(ref _waited);
        int trips = answered - Interlocked.Exchange(ref _tallied, answered);
        long since = waited - Interlocked.Exchange(ref _talliedWait, waited);
        return (trips, Stopwatch.GetElapsedTime(0, since).TotalMilliseconds);
    }

    public void Dispose()
    {
        _socket.Dispose();
        _stream.Dispose();
    }

    private sealed class Pending
    {
        public ManualResetEventSlim Answered { get; } = new();
        public byte[]? Answer { get; set; }
    }

    /// <summary>A limit on the wall clock for a connect that waits for an app to start listening.</summary>
    private readonly struct Deadline(TimeSpan within)
    {
        private readonly long _end = Environment.TickCount64 + (long)within.TotalMilliseconds;
        public bool Passed => Environment.TickCount64 > _end;
    }
}
