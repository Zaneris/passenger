using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A stand-in in front of a program's diagnostic port, at the address the diagnostics library takes first for its
/// process (a router's, as <see cref="StopRefusingPort"/> takes it), that takes every connection and answers none, as a
/// runtime whose diagnostics are held up does. Any process of the machine may reach it, as a pipe's name on Windows and the
/// temporary folder elsewhere are shared, so it keeps each connection's time, the process that made it and the request it
/// sent (<see cref="Account"/>). On macOS and Linux it is taken only over a port of the program's own made before it.
/// Disposed, it stops listening and lets each connection go: on the UI thread with the window's loop running.</summary>
public sealed partial class SilentPort : IDisposable
{
    /// <summary>A request's header: the protocol's magic, the message's whole size, its command set and command, two bytes unused.</summary>
    private const int HeaderSize = 20;

    private static readonly byte[] Magic = "DOTNET_IPC_V1\0"u8.ToArray();

    private const byte EventPipe = 0x02;
    private const byte StopTracing = 0x01;

    private readonly string address;
    private readonly Socket? listener;
    private readonly CancellationTokenSource end = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Task accepting;
    private readonly List<Connection> taken = [];
    private readonly List<Task> hearing = [];

    public SilentPort(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            address = $"dotnet-diagnostic-dsrouter-{processId}";
            // Its first pipe is there before the constructor returns, as a socket is, for a client looking for the port at once.
            NamedPipeServerStream first = Pipe();
            accepting = Waiting.Apart(() => AcceptPipesAsync(first));
            return;
        }
        address = Path.Combine(StopRefusingPort.SocketFolder(processId), $"dotnet-diagnostic-dsrouter-{processId}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-socket");
        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(address));
        listener.Listen();
        accepting = Waiting.Apart(() => AcceptSocketsAsync(listener));
    }

    /// <summary>How long it has listened, the clock its connections' times are on.</summary>
    public TimeSpan Now => clock.Elapsed;

    /// <summary>Its clock now as its account says a time: seconds, to a tenth.</summary>
    public string Time => Seconds(Now);

    /// <summary>The connections it has taken.</summary>
    public int Taken
    {
        get
        {
            lock (taken)
            {
                return taken.Count;
            }
        }
    }

    /// <summary>The connections it has taken from this test's own process, or from one the system did not name.</summary>
    public int TakenHere
    {
        get
        {
            lock (taken)
            {
                return taken.Count(Here);
            }
        }
    }

    /// <summary>What those connections asked, in the order they came, each once it has been read.</summary>
    public IReadOnlyList<string> AskedHere
    {
        get
        {
            lock (taken)
            {
                return [.. taken.Where(Here).Select(connection => connection.Asked).OfType<string>()];
            }
        }
    }

    /// <summary>Whether a process other than this test's made any of its connections.</summary>
    public bool TakenFromElsewhere
    {
        get
        {
            lock (taken)
            {
                return taken.Any(connection => connection.From is int from && from != Environment.ProcessId);
            }
        }
    }

    /// <summary>Each connection in the order it came: when, from which process, and what it asked.</summary>
    public string Account()
    {
        lock (taken)
        {
            return taken.Count == 0 ? "none" : string.Join("; ", taken.Select((connection, at) => $"{at + 1}) {connection}"));
        }
    }

    public void Dispose()
    {
        end.Cancel();
        listener?.Dispose();
        if (listener is not null)
        {
            File.Delete(address);
        }
        Ui.Pumped(() => accepting.IsCompleted, TimeSpan.FromSeconds(10), scaled: false);
        Task[] reads;
        lock (taken)
        {
            foreach (Connection connection in taken)
            {
                connection.Stream.Dispose();
            }
            reads = [.. hearing];
        }
        Ui.Pumped(() => reads.All(read => read.IsCompleted), TimeSpan.FromSeconds(10), scaled: false);
        end.Dispose();
    }

    private static bool Here(Connection connection) => connection.From is null || connection.From == Environment.ProcessId;

    private void Take(Stream stream, int? from)
    {
        // Named as it comes, while the process that made it may still run.
        Connection connection = new(stream, Now, from, from is int id ? Named(id) : null);
        lock (taken)
        {
            taken.Add(connection);
            hearing.Add(Waiting.Apart(() => HearAsync(connection)));
        }
    }

    private async Task AcceptSocketsAsync(Socket listening)
    {
        try
        {
            while (true)
            {
                Socket client = await listening.AcceptAsync(end.Token);
                Take(new NetworkStream(client, ownsSocket: true), PeerOf(client));
            }
        }
        catch (Exception stopped) when (stopped is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Disposed.
        }
    }

    private async Task AcceptPipesAsync(NamedPipeServerStream first)
    {
        NamedPipeServerStream? pipe = first;
        try
        {
            while (true)
            {
                pipe ??= Pipe();
                await pipe.WaitForConnectionAsync(end.Token);
                Take(pipe, GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint client) ? (int)client : null);
                pipe = null;
            }
        }
        catch (Exception stopped) when (stopped is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Disposed.
        }
        finally
        {
            if (pipe is not null)
            {
                await pipe.DisposeAsync();
            }
        }
    }

    private NamedPipeServerStream Pipe() =>
        new(address, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    /// <summary>Reads the request a connection sent, answering nothing; one closed or let go of before it was whole says so.</summary>
    private async Task HearAsync(Connection connection)
    {
        byte[] header = new byte[HeaderSize];
        try
        {
            await connection.Stream.ReadExactlyAsync(header, end.Token);
            if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                connection.Asked = $"no request of the diagnostics protocol ({Convert.ToHexString(header)})";
                return;
            }
            byte[] payload = new byte[Math.Max(0, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(Magic.Length)) - HeaderSize)];
            await connection.Stream.ReadExactlyAsync(payload, end.Token);
            connection.Asked = Request(header[Magic.Length + 2], header[Magic.Length + 3], payload);
        }
        catch (EndOfStreamException)
        {
            connection.Asked = "closed before its request was whole";
        }
        catch (Exception unread) when (unread is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // The stand-in let go of it first: what it sent stays unread.
        }
    }

    /// <summary>A request in the protocol's words: a session's start with its providers and their arguments, a session's
    /// stop with its id, else its command set and command.</summary>
    private static string Request(byte set, byte command, byte[] payload) => (set, command) switch
    {
        (EventPipe, StopTracing) when payload.Length >= sizeof(ulong) => $"a stop of EventPipe session {BinaryPrimitives.ReadUInt64LittleEndian(payload)}",
        (EventPipe, >= 0x03 and <= 0x07) => $"an EventPipe session's start (CollectTracing{command - 1}) of {Providers(command, payload)}",
        (0x01, _) => $"a dump (command {command})",
        (0x03, _) => $"a profiler's attach (command {command})",
        (0x04, < 9) => $"the process command {ProcessCommands[command]}",
        _ => $"command {command} of command set {set}, with {payload.Length} bytes",
    };

    private static readonly string[] ProcessCommands =
        ["GetProcessInfo", "ResumeRuntime", "GetProcessEnvironment", "SetEnvironmentVariable", "GetProcessInfo2", "EnablePerfMap", "DisablePerfMap", "ApplyStartupHook", "GetProcessInfo3"];

    /// <summary>A session start's providers, each with its arguments: after the buffer's size and format, each version's
    /// rundown and stack flags (a session's kind first from CollectTracing5, which gives each provider an event filter).</summary>
    private static string Providers(byte command, byte[] payload)
    {
        using BinaryReader reader = new(new MemoryStream(payload));
        try
        {
            reader.BaseStream.Position = command switch
            {
                0x03 => 9,
                0x04 => 10,
                0x05 => 17,
                _ => 21,
            };
            uint count = reader.ReadUInt32();
            List<string> providers = [];
            for (uint at = 0; at < count && at < 16; at++)
            {
                reader.BaseStream.Position += sizeof(ulong) + sizeof(uint);
                string name = Text(reader);
                string arguments = Text(reader);
                if (command >= 0x06)
                {
                    reader.BaseStream.Position += sizeof(bool);
                    uint events = reader.ReadUInt32();
                    reader.BaseStream.Position += sizeof(uint) * (long)events;
                }
                providers.Add(arguments.Length == 0 ? name : $"{name} ({arguments})");
            }
            return string.Join(", ", providers);
        }
        catch (EndOfStreamException)
        {
            return $"providers that do not read ({payload.Length} bytes)";
        }
    }

    /// <summary>A string as the protocol writes one: its length in UTF-16 units with the NUL that ends it, then those units.</summary>
    private static string Text(BinaryReader reader)
    {
        uint units = reader.ReadUInt32();
        byte[] text = reader.ReadBytes((int)Math.Min(units * 2L, reader.BaseStream.Length));
        return Encoding.Unicode.GetString(text).TrimEnd('\0');
    }

    private static string Seconds(TimeSpan time) => $"{time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";

    /// <summary>A process by its id and command line, or as this test's own.</summary>
    private static string Named(int id) => id == Environment.ProcessId ? "this test's process"
        : DotnetProcesses.Describe(id) is { } process ? $"process {id.ToString(CultureInfo.InvariantCulture)} ({process.CommandLine})"
        : $"process {id.ToString(CultureInfo.InvariantCulture)}, gone as it was looked at";

    /// <summary>The process at a socket's other end: Linux's peer credentials, macOS's peer process id; null where the system
    /// will not say.</summary>
    private static int? PeerOf(Socket socket)
    {
        Span<byte> value = stackalloc byte[12];
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return socket.GetRawSocketOption(SolSocket, SoPeerCred, value) >= sizeof(int) ? MemoryMarshal.Read<int>(value) : null;
            }
            if (OperatingSystem.IsMacOS())
            {
                return socket.GetRawSocketOption(SolLocal, LocalPeerPid, value[..sizeof(int)]) == sizeof(int) ? MemoryMarshal.Read<int>(value) : null;
            }
        }
        catch (SocketException)
        {
            // Not told.
        }
        return null;
    }

    // Linux's SOL_SOCKET and SO_PEERCRED (a ucred, its pid first); macOS's SOL_LOCAL and LOCAL_PEERPID.
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;
    private const int SolLocal = 0;
    private const int LocalPeerPid = 2;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    /// <summary>A connection taken: when it came on the port's clock, the process that made it (null where the system did
    /// not say), and the request it sent once read.</summary>
    private sealed class Connection(Stream stream, TimeSpan at, int? from, string? who)
    {
        private volatile string? asked;

        public Stream Stream { get; } = stream;
        public int? From { get; } = from;

        /// <summary>What it asked, set once read on the thread that read it.</summary>
        public string? Asked
        {
            get => asked;
            set => asked = value;
        }

        public override string ToString() =>
            $"at {Seconds(at)} from {who ?? "a process the system did not name"}: {Asked ?? "nothing read"}";
    }
}
