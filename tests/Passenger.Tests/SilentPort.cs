using System.IO.Pipes;
using System.Net.Sockets;

namespace Passenger.Tests;

/// <summary>A stand-in in front of a program's diagnostic port, at the address the diagnostics library takes first for its
/// process (a router's, as <see cref="StopRefusingPort"/> takes it), that takes every connection and answers none, as a
/// runtime whose diagnostics are held up does. On macOS and Linux it is taken only over a port of the program's own made
/// before it. Disposed, it stops listening and lets each connection go: on the UI thread with the window's loop running.</summary>
public sealed class SilentPort : IDisposable
{
    private readonly string address;
    private readonly Socket? listener;
    private readonly CancellationTokenSource end = new();
    private readonly Task accepting;
    private readonly List<IDisposable> taken = [];

    public SilentPort(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            address = $"dotnet-diagnostic-dsrouter-{processId}";
            accepting = Waiting.Apart(() => AcceptPipesAsync());
            return;
        }
        address = Path.Combine(StopRefusingPort.SocketFolder(processId), $"dotnet-diagnostic-dsrouter-{processId}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-socket");
        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(address));
        listener.Listen();
        accepting = Waiting.Apart(() => AcceptSocketsAsync(listener));
    }

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

    public void Dispose()
    {
        end.Cancel();
        listener?.Dispose();
        if (listener is not null)
        {
            File.Delete(address);
        }
        Ui.Pumped(() => accepting.IsCompleted, TimeSpan.FromSeconds(10), scaled: false);
        end.Dispose();
        lock (taken)
        {
            foreach (IDisposable connection in taken)
            {
                connection.Dispose();
            }
        }
    }

    private void Take(IDisposable connection)
    {
        lock (taken)
        {
            taken.Add(connection);
        }
    }

    private async Task AcceptSocketsAsync(Socket listening)
    {
        try
        {
            while (true)
            {
                Take(await listening.AcceptAsync(end.Token));
            }
        }
        catch (Exception stopped) when (stopped is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Disposed.
        }
    }

    private async Task AcceptPipesAsync()
    {
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
                Take(pipe);
            }
        }
        catch (Exception stopped) when (stopped is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Disposed.
        }
    }
}
