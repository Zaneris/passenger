using System.Net;
using System.Net.Sockets;
using System.Text;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>A connection to a Godot editor's IDE messaging server on a loopback port (<see cref="GodotIdeMessaging"/>):
/// handshaken as it opens, its answers matched to its requests in the order asked, as the server answers one request at a
/// time, and a request the server makes of it answered as one it does not serve. Raises nothing; any thread.</summary>
public sealed class GodotIdeClient : IAsyncDisposable
{
    private readonly TcpClient tcp;
    private readonly NetworkStream stream;
    private readonly StreamReader reader;
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly Lock gate = new();
    private readonly Queue<TaskCompletionSource<GodotIdeMessage?>> waiting = new();
    private readonly Task reading;
    private bool ended;

    private GodotIdeClient(TcpClient tcp, NetworkStream stream, StreamReader reader)
    {
        this.tcp = tcp;
        this.stream = stream;
        this.reader = reader;
        // Unheld: it reads for as long as the editor stays open, which no wait for the IDE to be quiet may wait out.
        reading = BackgroundWork.Unheld(ReadAsync);
    }

    /// <summary>The port it is connected on.</summary>
    public int Port { get; private init; }

    /// <summary>Whether the server is still there to ask.</summary>
    public bool Open
    {
        get
        {
            lock (gate)
            {
                return !ended;
            }
        }
    }

    /// <summary>A connection to the server on a port, its handshake taken within a limit; null where nothing listens there,
    /// or what listens is no Godot editor this client may talk to.</summary>
    public static async Task<GodotIdeClient?> ConnectAsync(int port, TimeSpan limit, CancellationToken cancel)
    {
        TcpClient tcp = new();
        try
        {
            using CancellationTokenSource within = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            within.CancelAfter(limit);
            await tcp.ConnectAsync(IPAddress.Loopback, port, within.Token);
            NetworkStream stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(GodotIdeMessaging.ClientHandshake + "\n"), within.Token);
            // Its writer starts with UTF-8's mark, which the reader passes over.
            StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            if (await reader.ReadLineAsync(within.Token) is { } line && GodotIdeMessaging.IsServerHandshake(line))
            {
                return new GodotIdeClient(tcp, stream, reader) { Port = port };
            }
        }
        catch (Exception unconnected) when (unconnected is SocketException or IOException or OperationCanceledException)
        {
            // Nothing that answers as Godot's editor listens there.
        }
        tcp.Dispose();
        return null;
    }

    /// <summary>The server's answer to a request; null once the connection has gone, before or while it waits.</summary>
    public async Task<GodotIdeMessage?> AskAsync(string id, string body, CancellationToken cancel)
    {
        TaskCompletionSource<GodotIdeMessage?> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await writing.WaitAsync(cancel);
        try
        {
            lock (gate)
            {
                if (ended)
                {
                    return null;
                }
                waiting.Enqueue(answer);
            }
            await stream.WriteAsync(Encoding.UTF8.GetBytes(GodotIdeMessaging.Written(new GodotIdeMessage(true, id, GodotIdeMessaging.Ok, body))), cancel);
        }
        catch (Exception unwritten) when (unwritten is IOException or SocketException or ObjectDisposedException)
        {
            End();
        }
        finally
        {
            writing.Release();
        }
        // Left in the queue when the wait is given up, so the late answer is matched to it and dropped.
        return await answer.Task.WaitAsync(cancel);
    }

    private async Task ReadAsync()
    {
        GodotIdeMessaging.Reader messages = new();
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (messages.Read(line) is not { } message)
                {
                    continue;
                }
                if (message.Request)
                {
                    await writing.WaitAsync();
                    try
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(GodotIdeMessaging.Written(new GodotIdeMessage(false, message.Id, GodotIdeMessaging.NotSupported, "null"))));
                    }
                    finally
                    {
                        writing.Release();
                    }
                    continue;
                }
                TaskCompletionSource<GodotIdeMessage?>? asked = null;
                lock (gate)
                {
                    waiting.TryDequeue(out asked);
                }
                asked?.TrySetResult(message);
            }
        }
        catch (Exception gone) when (gone is IOException or SocketException or ObjectDisposedException)
        {
            // The editor has closed, or the connection was let go of.
        }
        End();
    }

    /// <summary>The connection ended: every request still waiting answered with none.</summary>
    private void End()
    {
        List<TaskCompletionSource<GodotIdeMessage?>> left;
        lock (gate)
        {
            ended = true;
            left = [.. waiting];
            waiting.Clear();
        }
        foreach (TaskCompletionSource<GodotIdeMessage?> asked in left)
        {
            asked.TrySetResult(null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        End();
        tcp.Dispose();
        await reading;
    }
}
