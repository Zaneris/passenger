using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Passenger.App.Discord;

/// <summary>One connection to the Discord app's local IPC: the handshake, then commands once Discord answers READY.
/// Every frame Discord sends is read as it comes, so its side never fills. No UI: its callbacks come on any
/// thread.</summary>
internal sealed class DiscordConnection : IAsyncDisposable
{
    private const int Handshake = 0;
    private const int Frame = 1;
    private const int Close = 2;
    private const int Ping = 3;
    private const int Pong = 4;

    /// <summary>Far beyond READY, the largest frame Discord sends here; a length past it is not Discord's.</summary>
    private const int LargestFrame = 1024 * 1024;

    private readonly Stream stream;
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly CancellationTokenSource ending = new();
    private readonly TaskCompletionSource<string?> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<string> said;
    private readonly Task reading;

    private DiscordConnection(Stream stream, string where, Action<string> said)
    {
        this.stream = stream;
        this.said = said;
        Where = where;
        reading = ReadAsync();
    }

    /// <summary>The socket's path or the pipe's name.</summary>
    public string Where { get; }

    /// <summary>Ends when Discord closes the connection or it fails, saying why.</summary>
    public Task<string> Lost => lost.Task;

    /// <summary>Connects to the first endpoint that takes the handshake for the application, or says why none did: no
    /// Discord is running, or the one running refused or never answered. <paramref name="said"/> hears what Discord
    /// reports wrong afterwards.</summary>
    public static async Task<(DiscordConnection? Connection, string Why)> OpenAsync(DiscordIpc ipc, string application, Action<string> said, CancellationToken cancel)
    {
        string why = $"not running (nothing at {string.Join(", ", ipc.Places.Select(p => p + "-0"))})";
        foreach (string endpoint in Present(ipc))
        {
            Stream? stream = await ConnectAsync(endpoint, ipc.Timeout, cancel).ConfigureAwait(false);
            if (stream is null)
            {
                continue;
            }
            DiscordConnection connection = new(stream, endpoint, said);
            string? refused = "closed before Discord answered";
            try
            {
                JsonObject hello = new() { ["v"] = 1, ["client_id"] = application };
                await connection.SendAsync(Handshake, hello, cancel).ConfigureAwait(false);
                refused = await connection.ready.Task.WaitAsync(ipc.Timeout, cancel).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                refused = FormattableString.Invariant($"{endpoint} did not answer the handshake in {ipc.Timeout.TotalSeconds:0.#} s");
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
            {
                refused = $"the connection to {endpoint} failed: {error.Message}";
            }
            finally
            {
                // Still set when cancelled, so every connection that failed is let go of here.
                if (refused is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
            if (refused is null)
            {
                return (connection, string.Empty);
            }
            why = refused;
        }
        return (null, why);
    }

    /// <summary>A command, with a nonce of its own for Discord's answer.</summary>
    public Task CommandAsync(string command, JsonObject arguments, CancellationToken cancel) =>
        SendAsync(Frame, new JsonObject { ["cmd"] = command, ["args"] = arguments, ["nonce"] = Guid.NewGuid().ToString() }, cancel);

    public async ValueTask DisposeAsync()
    {
        if (ending.IsCancellationRequested)
        {
            return;
        }
        ending.Cancel();
        await stream.DisposeAsync().ConfigureAwait(false);
        await reading.ConfigureAwait(false);
    }

    private async Task SendAsync(int op, JsonObject payload, CancellationToken cancel)
    {
        byte[] json = Encoding.UTF8.GetBytes(payload.ToJsonString());
        byte[] frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
        json.CopyTo(frame, 8);
        await writing.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, cancel).ConfigureAwait(false);
            await stream.FlushAsync(cancel).ConfigureAwait(false);
        }
        finally
        {
            writing.Release();
        }
    }

    /// <summary>Reads every frame Discord sends until it closes, answering its pings; ends saying why.</summary>
    private async Task ReadAsync()
    {
        string why;
        byte[] head = new byte[8];
        try
        {
            while (true)
            {
                await stream.ReadExactlyAsync(head, ending.Token).ConfigureAwait(false);
                int op = BinaryPrimitives.ReadInt32LittleEndian(head);
                int length = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
                if (length is < 0 or > LargestFrame)
                {
                    why = $"{Where} sent a frame of {length} bytes, which Discord never sends";
                    break;
                }
                byte[] body = new byte[length];
                await stream.ReadExactlyAsync(body, ending.Token).ConfigureAwait(false);
                if (op == Close)
                {
                    why = $"{Where} closed the connection: {Message(body) ?? "no reason given"}";
                    break;
                }
                if (op == Ping)
                {
                    await SendAsync(Pong, Parse(body) ?? [], ending.Token).ConfigureAwait(false);
                }
                else if (op == Frame)
                {
                    Answered(body);
                }
            }
        }
        catch (Exception) when (ending.IsCancellationRequested)
        {
            why = $"Passenger closed the connection to {Where}";
        }
        catch (EndOfStreamException)
        {
            why = $"{Where} closed the connection";
        }
        catch (IOException error)
        {
            why = $"the connection to {Where} failed: {error.Message}";
        }
        catch (Exception error)
        {
            // A fault of the IDE's own, in what reads Discord's answers: reported, and the connection lost.
            IdeErrors.Report(error);
            why = $"the connection to {Where} failed: {error.Message}";
        }
        ready.TrySetResult(why);
        lost.TrySetResult(why);
    }

    /// <summary>READY lets the commands begin; an error Discord answers with is said.</summary>
    private void Answered(byte[] body)
    {
        JsonObject? frame = Parse(body);
        string? evt = frame?["evt"]?.GetValueKind() == JsonValueKind.String ? (string?)frame["evt"] : null;
        if (evt == "READY")
        {
            ready.TrySetResult(null);
        }
        else if (evt == "ERROR")
        {
            said($"{Where} refused {(string?)frame?["cmd"] ?? "a command"}: {Message(body) ?? "no reason given"}");
        }
    }

    /// <summary>The message of a close or an error: its own, or its data's.</summary>
    private static string? Message(byte[] body)
    {
        JsonObject? frame = Parse(body);
        JsonNode? message = frame?["message"] ?? frame?["data"]?["message"];
        JsonNode? code = frame?["code"] ?? frame?["data"]?["code"];
        return message?.GetValueKind() == JsonValueKind.String ? $"{(string?)message}{(code is null ? string.Empty : $" ({code.ToJsonString()})")}" : null;
    }

    /// <summary>A frame's JSON object, or null for anything else: what Discord sends is read, never trusted to parse.</summary>
    private static JsonObject? Parse(byte[] body)
    {
        try
        {
            return body.Length == 0 ? null : JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The endpoints there is something at: a socket file, or a pipe of that name among the system's (an attempt
    /// to open one that is not there would wait out the timeout).</summary>
    private static IEnumerable<string> Present(DiscordIpc ipc)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ipc.Endpoints().Where(File.Exists);
        }
        HashSet<string> pipes;
        try
        {
            pipes = [.. Directory.EnumerateFiles(@"\\.\pipe\").Select(Path.GetFileName).OfType<string>()];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ipc.Endpoints();
        }
        return ipc.Endpoints().Where(name => pipes.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<Stream?> ConnectAsync(string endpoint, TimeSpan limit, CancellationToken cancel)
    {
        if (OperatingSystem.IsWindows())
        {
            NamedPipeClientStream pipe = new(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(limit, cancel).ConfigureAwait(false);
                return pipe;
            }
            catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return null;
            }
        }
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cancel).AsTask().WaitAsync(limit, cancel).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception error) when (error is SocketException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            socket.Dispose();
            return null;
        }
    }
}
