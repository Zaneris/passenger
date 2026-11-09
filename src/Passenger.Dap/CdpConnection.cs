using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Passenger.Core;

namespace Passenger.Dap;

/// <summary>One end of a Chrome DevTools Protocol conversation over a WebSocket: each message one text message of JSON, a
/// command numbered and answered by its number with its result or an error, an event by its method's name. The answers are
/// matched on the reading thread; the events are handed out in the order they came, on a thread of their own, so a handler
/// waiting on an answer of its own never blocks the read that brings it.</summary>
public sealed class CdpConnection : IAsyncDisposable
{
    /// <summary>The longest message read, far above any real one (a page's script source, a large object's properties): a
    /// longer one ends the conversation rather than take the reader's memory.</summary>
    public const int MostMessage = 64 * 1024 * 1024;

    private readonly ClientWebSocket socket;
    private readonly Action<string>? log;
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly PendingRequests<string, JsonElement> pending = new();
    private readonly Channel<(string Method, JsonElement Parameters)> inbox =
        Channel.CreateUnbounded<(string, JsonElement)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource stopping = new();
    private Task? reading;
    private Task? dispatching;

    private CdpConnection(ClientWebSocket socket, Action<string>? log)
    {
        this.socket = socket;
        this.log = log;
    }

    /// <summary>What the protocol's traces call it.</summary>
    public string Name { get; init; } = "cdp";

    /// <summary>Completes when the other end closes the socket, or this end is disposed.</summary>
    public Task Completion => reading ?? Task.CompletedTask;

    /// <summary>What takes each event, its method and parameters: on the connection's own thread, one at a time in order,
    /// the next once the last is done.</summary>
    public Func<string, JsonElement, Task>? Events { get; set; }

    /// <summary>A conversation opened with the WebSocket at <paramref name="address"/>, reading from its first message.</summary>
    /// <param name="log">What went wrong in the conversation, a line at a time; called on any thread.</param>
    public static async Task<CdpConnection> ConnectAsync(Uri address, Action<string>? log, CancellationToken cancel)
    {
        ClientWebSocket socket = new();
        socket.Options.Proxy = OutboundHttp.Proxy;
        try
        {
            await socket.ConnectAsync(address, cancel).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        CdpConnection connection = new(socket, log) { Name = address.Authority };
        // Not held: both serve for as long as the conversation lasts.
        connection.dispatching = BackgroundWork.Unheld(connection.DispatchAsync);
        connection.reading = BackgroundWork.Unheld(connection.ReadAsync);
        return connection;
    }

    /// <summary>Sends a command and waits for its result; throws <see cref="CdpException"/> when the other end answers with an
    /// error, or the conversation ends first.</summary>
    public async Task<JsonElement> SendAsync(string method, object? parameters = null, CancellationToken cancel = default)
    {
        int id;
        Task<JsonElement> answer;
        try
        {
            (id, answer) = pending.Add(method);
        }
        catch (CdpException closed)
        {
            throw new CdpException(method, closed.Reason);
        }
        using CancellationTokenRegistration given = cancel.CanBeCanceled ? cancel.Register(() => pending.Forget(id, answer, cancel)) : default;
        ArrayBufferWriter<byte> message = new();
        using (Utf8JsonWriter json = new(message, new JsonWriterOptions { Encoder = DapConnection.Options.Encoder }))
        {
            json.WriteStartObject();
            json.WriteNumber("id"u8, id);
            json.WriteString("method"u8, method);
            json.WritePropertyName("params"u8);
            if (parameters is null)
            {
                json.WriteStartObject();
                json.WriteEndObject();
            }
            else
            {
                JsonSerializer.Serialize(json, parameters, parameters.GetType(), DapConnection.Options);
            }
            json.WriteEndObject();
        }
        DapConnection.Trace?.Invoke(Name, $"--> {Encoding.UTF8.GetString(message.WrittenSpan)}");
        await writing.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(message.WrittenMemory, WebSocketMessageType.Text, endOfMessage: true, stopping.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancel.IsCancellationRequested && error is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            pending.Fail(id, asked => new CdpException(asked, "the connection is closed"));
        }
        finally
        {
            writing.Release();
        }
        return await answer.WaitAsync(cancel).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        inbox.Writer.TryComplete();
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using CancellationTokenSource closing = new(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The other end went first.
        }
        foreach (Task? task in new[] { reading, dispatching })
        {
            if (task is not null)
            {
                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception error) when (error is TimeoutException or OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
                {
                }
            }
        }
        pending.End(asked => new CdpException(asked, "the connection is closed"));
        socket.Dispose();
        stopping.Dispose();
    }

    private async Task ReadAsync()
    {
        using PooledBytes message = new();
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                if (message.Written.Length >= MostMessage)
                {
                    throw new IOException($"A DevTools message ran past the {MostMessage} bytes the connection reads of one");
                }
                ValueWebSocketReceiveResult read = await socket.ReceiveAsync(message.GetMemory(), stopping.Token).ConfigureAwait(false);
                if (read.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                message.Advance(read.Count);
                if (!read.EndOfMessage)
                {
                    continue;
                }
                Receive(message.WrittenMemory);
                // A large message's array goes back to the pool, the next read into one of the usual size.
                message.Clear();
            }
        }
        catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException or JsonException)
        {
            if (!stopping.IsCancellationRequested)
            {
                log?.Invoke($"The DevTools connection ended: {error.Message}");
            }
        }
        finally
        {
            pending.End(asked => new CdpException(asked, "the connection is closed"));
            inbox.Writer.TryComplete();
        }
    }

    /// <summary>One message read: an answer completes its command, an event goes to the inbox, copied out of the buffer.</summary>
    private void Receive(ReadOnlyMemory<byte> bytes)
    {
        DapConnection.Trace?.Invoke(Name, $"<-- {Encoding.UTF8.GetString(bytes.Span)}");
        using JsonDocument message = JsonDocument.Parse(bytes);
        JsonElement root = message.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        if (JsonMembers.Number(root, "id") is int id)
        {
            if (JsonMembers.Object(root, "error") is { } error)
            {
                string said = JsonMembers.Text(error, "message") ?? "refused";
                pending.Fail(id, asked => new CdpException(asked, JsonMembers.Text(error, "data") is { Length: > 0 } data ? $"{said}: {data}" : said));
            }
            else
            {
                pending.Answer(id, JsonMembers.Object(root, "result")?.Clone() ?? default);
            }
            return;
        }
        if (JsonMembers.Text(root, "method") is { } method)
        {
            inbox.Writer.TryWrite((method, JsonMembers.Object(root, "params")?.Clone() ?? default));
        }
    }

    private async Task DispatchAsync()
    {
        await foreach ((string method, JsonElement parameters) in inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (Events is not { } take)
            {
                continue;
            }
            try
            {
                await take(method, parameters).ConfigureAwait(false);
            }
            catch (Exception error) when (error is CdpException or DapException or InvalidOperationException or KeyNotFoundException or FormatException or JsonException)
            {
                log?.Invoke($"A DevTools event ({method}) was not taken: {error.Message}");
            }
        }
    }
}

/// <summary>A DevTools command the other end refused, or that the conversation's end left unanswered.</summary>
public sealed class CdpException(string method, string reason) : Exception($"{method}: {reason}")
{
    public string Method { get; } = method;

    /// <summary>The other end's words alone, without the method.</summary>
    public string Reason { get; } = reason;
}
