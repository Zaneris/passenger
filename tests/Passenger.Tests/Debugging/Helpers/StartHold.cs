using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using Passenger.Core;

namespace Passenger.Tests.Debugging.Helpers;

/// <summary>A diagnostic port of the test's own that a launched program's runtime connects to and waits at as it starts,
/// before its debugger starts, until the test lets it go: a runtime slow to start, for as long as the test likes.</summary>
public sealed class StartHold : IDisposable
{
    private readonly string address;
    private readonly NamedPipeServerStream? pipe;
    private readonly Socket? listener;
    private Stream? stream;

    public StartHold()
    {
        string name = $"hold-{Guid.NewGuid():N}"[..13];
        if (OperatingSystem.IsWindows())
        {
            address = name;
            pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
        else
        {
            address = Path.Combine(TestRun.Root, name);
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(address));
            listener.Listen(1);
        }
    }

    /// <summary>What sends the program's runtime here, to wait.</summary>
    public IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string> { [RuntimeVariables.DiagnosticPorts] = $"{address},connect,suspend" };

    /// <summary>The process whose runtime has come and waits, once it has, within a minute or the time given.</summary>
    public async Task<int> Held(TimeSpan? within = null)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter((within ?? TimeSpan.FromSeconds(60)).Scaled());
        if (pipe is not null)
        {
            await pipe.WaitForConnectionAsync(limit.Token);
            stream = pipe;
        }
        else
        {
            stream = new NetworkStream(await listener!.AcceptAsync(limit.Token), ownsSocket: true);
        }
        // "ADVR_V1\0", the runtime's cookie, its process's id and two bytes kept for later.
        byte[] advertised = new byte[34];
        await stream.ReadExactlyAsync(advertised, limit.Token);
        return checked((int)BinaryPrimitives.ReadUInt64LittleEndian(advertised.AsSpan(24)));
    }

    public async Task LetGo()
    {
        // The protocol's header (its magic, its size, the Process command set's ResumeRuntime), then the answer's.
        byte[] resume = [.. "DOTNET_IPC_V1\0"u8, 20, 0, 0x04, 0x01, 0, 0];
        await stream!.WriteAsync(resume, TestContext.Current.CancellationToken);
        await stream.ReadExactlyAsync(new byte[24], TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
        stream?.Dispose();
        pipe?.Dispose();
        listener?.Dispose();
        if (listener is not null)
        {
            File.Delete(address);
        }
    }
}
