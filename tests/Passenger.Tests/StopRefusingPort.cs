using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;

namespace Passenger.Tests;

/// <summary>A stand-in in front of a program's diagnostic port, at the address Passenger's client takes first for its
/// process (a router's: a socket beside the program's own, <see cref="Passenger.Core.DiagnosticPorts.RouterOf"/>, a named
/// pipe on Windows). Each connection made to it is passed
/// through to the program's own port, but an EventPipe session's stop, though passed on, so the program ends the session
/// and closes its stream, is answered with an error, as a runtime that refuses the stop answers. Disposed, it stops
/// listening and ends what it passes through: on the UI thread with the window's loop running.</summary>
public sealed class StopRefusingPort : IDisposable
{
    /// <summary>A message's header: the protocol's magic, the message's whole size, its command set and command, two bytes unused.</summary>
    private const int HeaderSize = 20;

    private static readonly byte[] Magic = "DOTNET_IPC_V1\0"u8.ToArray();

    private const byte EventPipe = 0x02;
    private const byte StopTracing = 0x01;

    /// <summary>The server's command set, and its answer that a command failed, with its HRESULT (E_FAIL here).</summary>
    private const byte Server = 0xFF;
    private const byte Error = 0xFF;
    private const uint Failed = 0x80004005;

    private readonly int processId;
    private readonly string address;
    private readonly Socket? listener;
    private readonly CancellationTokenSource end = new();
    private readonly Task accepting;
    private int refused;
    private int open;

    public StopRefusingPort(int processId)
    {
        this.processId = processId;
        if (OperatingSystem.IsWindows())
        {
            address = $"dotnet-diagnostic-dsrouter-{processId}";
            accepting = Waiting.Apart(() => AcceptPipesAsync());
            return;
        }
        address = Passenger.Core.DiagnosticPorts.RouterOf(processId) ?? throw new InvalidOperationException($"Process {processId} could not be read");
        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(address));
        listener.Listen();
        accepting = Waiting.Apart(() => AcceptSocketsAsync(listener));
    }

    /// <summary>The stops it has answered with an error.</summary>
    public int Refused => Volatile.Read(ref refused);

    /// <summary>The connections it passes through still open.</summary>
    public int Open => Volatile.Read(ref open);

    public void Dispose()
    {
        end.Cancel();
        listener?.Dispose();
        if (listener is not null)
        {
            File.Delete(address);
        }
        bool stopped = Ui.Pumped(() => accepting.IsCompleted, TimeSpan.FromSeconds(10), scaled: false);
        end.Dispose();
        if (stopped && accepting.IsCompleted)
        {
            // A connection passed through that failed fails the test.
            accepting.GetAwaiter().GetResult();
        }
    }

    private async Task AcceptSocketsAsync(Socket listening)
    {
        List<Task> passing = [];
        try
        {
            while (true)
            {
                Socket client = await listening.AcceptAsync(end.Token);
                NetworkStream stream = new(client, ownsSocket: true);
                passing.Add(Waiting.Apart(() => PassAsync(stream)));
            }
        }
        catch (Exception stopped) when (stopped is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Disposed.
        }
        await Task.WhenAll(passing);
    }

    private async Task AcceptPipesAsync()
    {
        List<Task> passing = [];
        try
        {
            while (true)
            {
                NamedPipeServerStream pipe = new(address, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                try
                {
                    await pipe.WaitForConnectionAsync(end.Token);
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }
                passing.Add(Waiting.Apart(() => PassAsync(pipe)));
            }
        }
        catch (Exception stopped) when (stopped is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Disposed.
        }
        await Task.WhenAll(passing);
    }

    /// <summary>One connection's first message passed to the program, then everything either way until either side closes;
    /// a stop's answer taken from the program and an error given in its place.</summary>
    private async Task PassAsync(Stream client)
    {
        Interlocked.Increment(ref open);
        try
        {
            await using (client)
            {
                byte[] header = new byte[HeaderSize];
                if (!await ReadAsync(client, header))
                {
                    return;
                }
                byte[] payload = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(Magic.Length)) - HeaderSize];
                if (!await ReadAsync(client, payload))
                {
                    return;
                }
                await using Stream program = await ProgramPortAsync(processId, end.Token);
                await program.WriteAsync(header, end.Token);
                await program.WriteAsync(payload, end.Token);
                await program.FlushAsync(end.Token);
                if (header[Magic.Length + 2] == EventPipe && header[Magic.Length + 3] == StopTracing)
                {
                    byte[] answer = new byte[HeaderSize];
                    if (await ReadAsync(program, answer))
                    {
                        await ReadAsync(program, new byte[BinaryPrimitives.ReadUInt16LittleEndian(answer.AsSpan(Magic.Length)) - HeaderSize]);
                    }
                    Interlocked.Increment(ref refused);
                    await client.WriteAsync(Refusal(), end.Token);
                    await client.FlushAsync(end.Token);
                    return;
                }
                await BothWaysAsync(client, program, end.Token);
            }
        }
        catch (Exception gone) when (gone is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // The client, the program or the stand-in went first.
        }
        finally
        {
            Interlocked.Decrement(ref open);
        }
    }

    /// <summary>A connection to process <paramref name="processId"/>'s own port.</summary>
    internal static async Task<Stream> ProgramPortAsync(int processId, CancellationToken cancel)
    {
        if (OperatingSystem.IsWindows())
        {
            NamedPipeClientStream pipe = new(".", $"dotnet-diagnostic-{processId}", PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(10), cancel);
            return pipe;
        }
        string own = Passenger.Core.RuntimeFiles.Of(processId)?.Socket ?? throw new InvalidOperationException($"Process {processId} has gone");
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(own), cancel);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>Everything passed either way between a client and the program's port until either side closes, whose end
    /// closes the other.</summary>
    internal static async Task BothWaysAsync(Stream client, Stream program, CancellationToken cancel)
    {
        Task toProgram = client.CopyToAsync(program, cancel);
        Task toClient = program.CopyToAsync(client, cancel);
        await Task.WhenAny(toProgram, toClient);
        await client.DisposeAsync();
        await program.DisposeAsync();
        try
        {
            await Task.WhenAll(toProgram, toClient);
        }
        catch (Exception closed) when (closed is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // The copy the other's end closed under its read.
        }
    }

    /// <summary>An error answered for the command: the server's Error, its payload the HRESULT.</summary>
    private static byte[] Refusal()
    {
        byte[] answer = new byte[HeaderSize + sizeof(uint)];
        Magic.CopyTo(answer, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(answer.AsSpan(Magic.Length), (ushort)answer.Length);
        answer[Magic.Length + 2] = Server;
        answer[Magic.Length + 3] = Error;
        BinaryPrimitives.WriteUInt32LittleEndian(answer.AsSpan(HeaderSize), Failed);
        return answer;
    }

    /// <summary>Fills the buffer from the stream; false when it ends first.</summary>
    private async Task<bool> ReadAsync(Stream stream, byte[] buffer)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, end.Token);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }
}
