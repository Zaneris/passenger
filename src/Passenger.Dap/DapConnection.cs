using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Passenger.Dap;

/// <summary>One end of a Debug Adapter Protocol conversation over a pair of streams: each message is
/// a <c>Content-Length</c> header and a JSON body, sent with a sequence number and answered by it.
/// Either end can ask (the adapter asks the client to run a program in a terminal), so both sides
/// use the same connection. Responses are matched on the reading thread; requests and events are
/// handed to their handlers in the order they came, on a thread of their own, so a handler waiting
/// on a reply of its own never blocks the read that would bring it.</summary>
public sealed class DapConnection : IAsyncDisposable
{
    private static readonly byte[] HeaderEnd = "\r\n\r\n"u8.ToArray();

    private readonly Stream input;
    private readonly Stream output;
    private readonly Action<string>? log;
    private readonly Lock writeGate = new();
    private readonly ConcurrentDictionary<int, (string Command, TaskCompletionSource<JsonElement> Answer)> pending = new();
    private readonly Dictionary<string, Func<JsonElement, Task<object?>>> handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action> afterResponse = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action> requestFinished = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action<JsonElement>> eventHandlers = new(StringComparer.Ordinal);
    private readonly Channel<JsonElement> inbox = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource stopping = new();
    private Task? reading;
    private Task? dispatching;
    private int sequence;

    /// <param name="log">What went wrong in the conversation, a line at a time; called on any thread.</param>
    public DapConnection(Stream input, Stream output, Action<string>? log = null)
    {
        this.input = input;
        this.output = output;
        this.log = log;
    }

    /// <summary>The serializer settings of every message: the protocol's camelCase names, and absent
    /// rather than null for what a message leaves out.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Where every message each connection sends or reads goes while set, with the connection's <see cref="Name"/>
    /// and the message after an arrow for its direction ("-->" sent); called on the connection's threads. The IDE's
    /// Trace protocol messages setting, the process's as every window's sessions write the one dap.log; an adapter
    /// never sets it.</summary>
    public static Action<string, string>? Trace { get; set; }

    /// <summary>What a trace calls this connection (the adapter's process).</summary>
    public string Name { get; set; } = "dap";

    /// <summary>Completes when the other end closes its stream, or this end is disposed.</summary>
    public Task Completion => reading ?? Task.CompletedTask;

    /// <summary>An event with no handler of its own: its name and body; raised on any thread.</summary>
    public event Action<string, JsonElement>? EventReceived;

    public void Start()
    {
        if (reading is not null)
        {
            throw new InvalidOperationException("The connection is already started");
        }
        dispatching = Task.Run(DispatchAsync);
        reading = Task.Run(ReadAsync);
    }

    /// <summary>Answers a request. A handler that throws answers with the exception's message as the
    /// error; <paramref name="then"/> follows a successful answer (initialize's event follows its response).
    /// <paramref name="finished"/> follows either answer, or a failed write, to release the handler's hold.</summary>
    public void Handle<TArguments, TResponse>(string command, Func<TArguments, Task<TResponse>> handler, Action? then = null, Action? finished = null)
    {
        handlers[command] = async arguments => await handler(Read<TArguments>(arguments)).ConfigureAwait(false);
        Then(command, then, finished);
    }

    /// <summary>A request answered with no body.</summary>
    public void Handle<TArguments>(string command, Func<TArguments, Task> handler, Action? then = null, Action? finished = null)
    {
        handlers[command] = async arguments =>
        {
            await handler(Read<TArguments>(arguments)).ConfigureAwait(false);
            return null;
        };
        Then(command, then, finished);
    }

    public void OnEvent<TBody>(string name, Action<TBody> handler) => eventHandlers[name] = body => handler(Read<TBody>(body));

    public async Task<TResponse> RequestAsync<TResponse>(string command, object? arguments, CancellationToken cancellation = default)
    {
        JsonElement body = await RequestAsync(command, arguments, cancellation).ConfigureAwait(false);
        return Read<TResponse>(body);
    }

    /// <summary>Sends a request and waits for its answer's body (undefined when it has none);
    /// throws <see cref="DapException"/> when the other end answers with an error.</summary>
    public Task<JsonElement> RequestAsync(string command, object? arguments, CancellationToken cancellation = default)
    {
        int seq = Interlocked.Increment(ref sequence);
        TaskCompletionSource<JsonElement> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[seq] = (command, answer);
        JsonObject message = new()
        {
            ["seq"] = seq,
            ["type"] = "request",
            ["command"] = command,
        };
        if (arguments is not null)
        {
            message["arguments"] = JsonSerializer.SerializeToNode(arguments, arguments.GetType(), Options);
        }
        if (cancellation.CanBeCanceled)
        {
            cancellation.Register(() =>
            {
                if (pending.TryRemove(seq, out (string, TaskCompletionSource<JsonElement> Answer) waiting))
                {
                    waiting.Answer.TrySetCanceled(cancellation);
                }
            });
        }
        try
        {
            Write(message);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            pending.TryRemove(seq, out _);
            answer.TrySetException(new DapException(command, "the connection is closed"));
        }
        return answer.Task;
    }

    public void SendEvent(string name, object? body = null)
    {
        JsonObject message = new()
        {
            ["seq"] = Interlocked.Increment(ref sequence),
            ["type"] = "event",
            ["event"] = name,
        };
        if (body is not null)
        {
            message["body"] = JsonSerializer.SerializeToNode(body, body.GetType(), Options);
        }
        Write(message);
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        inbox.Writer.TryComplete();
        foreach (Task? task in new[] { reading, dispatching })
        {
            if (task is not null)
            {
                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception error) when (error is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException)
                {
                }
            }
        }
        FailPending("the connection is closed");
        stopping.Dispose();
    }

    private static T Read<T>(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            // A request with no arguments, or an answer with no body, reads as the type's empty form.
            return JsonSerializer.Deserialize<T>("{}", Options)!;
        }
        return element.Deserialize<T>(Options) ?? throw new JsonException($"Expected {typeof(T).Name}, not null");
    }

    private void Then(string command, Action? then, Action? finished)
    {
        if (then is null)
        {
            afterResponse.Remove(command);
        }
        else
        {
            afterResponse[command] = then;
        }
        if (finished is null)
        {
            requestFinished.Remove(command);
        }
        else
        {
            requestFinished[command] = finished;
        }
    }

    private void Write(JsonObject message)
    {
        string json = message.ToJsonString(Options);
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n\r\n"));
        lock (writeGate)
        {
            // Under the gate, so the trace has the messages in the order they went.
            Trace?.Invoke(Name, $"--> {json}");
            output.Write(header);
            output.Write(body);
            output.Flush();
        }
    }

    private async Task ReadAsync()
    {
        byte[] buffer = new byte[64 * 1024];
        int filled = 0;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                int headerEnd = buffer.AsSpan(0, filled).IndexOf(HeaderEnd);
                if (headerEnd < 0)
                {
                    if (filled == buffer.Length)
                    {
                        Array.Resize(ref buffer, buffer.Length * 2);
                    }
                    int read = await input.ReadAsync(buffer.AsMemory(filled), stopping.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }
                    filled += read;
                    continue;
                }
                int length = ContentLength(Encoding.ASCII.GetString(buffer, 0, headerEnd));
                int start = headerEnd + HeaderEnd.Length;
                if (buffer.Length < start + length)
                {
                    Array.Resize(ref buffer, start + length);
                }
                while (filled < start + length)
                {
                    int read = await input.ReadAsync(buffer.AsMemory(filled), stopping.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }
                    filled += read;
                }
                Trace?.Invoke(Name, $"<-- {Encoding.UTF8.GetString(buffer, start, length)}");
                // Parsed from a copy: a document reads the memory it is given, and the buffer is reused.
                JsonElement message = JsonDocument.Parse(buffer.AsSpan(start, length).ToArray()).RootElement;
                buffer.AsSpan(start + length, filled - start - length).CopyTo(buffer);
                filled -= start + length;
                Receive(message);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            log?.Invoke($"DAP read ended: {error.Message}");
        }
        finally
        {
            inbox.Writer.TryComplete();
            FailPending("the connection closed");
        }
    }

    private static int ContentLength(string header)
    {
        foreach (string line in header.Split("\r\n"))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line.AsSpan(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int length))
            {
                return length;
            }
        }
        throw new IOException($"A DAP message with no Content-Length: '{header}'");
    }

    private void Receive(JsonElement message)
    {
        if (Text(message, "type") == "response")
        {
            int seq = message.GetProperty("request_seq").GetInt32();
            if (!pending.TryRemove(seq, out (string Command, TaskCompletionSource<JsonElement> Answer) request))
            {
                return;
            }
            if (message.TryGetProperty("success", out JsonElement success) && success.GetBoolean())
            {
                request.Answer.TrySetResult(message.TryGetProperty("body", out JsonElement body) ? body.Clone() : default);
            }
            else
            {
                request.Answer.TrySetException(new DapException(Text(message, "command") ?? request.Command, Text(message, "message") ?? "failed"));
            }
            return;
        }
        inbox.Writer.TryWrite(message);
    }

    private async Task DispatchAsync()
    {
        await foreach (JsonElement message in inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (Text(message, "type"))
                {
                    case "request":
                        // Started here, in order, and left to finish: a handler's first wait is where the
                        // next request may start, as the protocol's own adapters behave.
                        _ = AnswerAsync(message);
                        break;
                    case "event":
                        string name = Text(message, "event") ?? string.Empty;
                        JsonElement body = message.TryGetProperty("body", out JsonElement found) ? found : default;
                        if (eventHandlers.TryGetValue(name, out Action<JsonElement>? handler))
                        {
                            handler(body);
                        }
                        else
                        {
                            EventReceived?.Invoke(name, body);
                        }
                        break;
                    default:
                        log?.Invoke($"DAP message of no known type: {message}");
                        break;
                }
            }
            catch (Exception error)
            {
                log?.Invoke($"DAP handler failed: {error}");
            }
        }
    }

    private async Task AnswerAsync(JsonElement request)
    {
        int seq = request.GetProperty("seq").GetInt32();
        string command = Text(request, "command") ?? string.Empty;
        try
        {
            JsonObject response = new()
            {
                ["seq"] = 0,
                ["type"] = "response",
                ["request_seq"] = seq,
                ["command"] = command,
            };
            try
            {
                if (!handlers.TryGetValue(command, out Func<JsonElement, Task<object?>>? handler))
                {
                    throw new DapException(command, $"Request '{command}' is not supported.");
                }
                object? body = await handler(request.TryGetProperty("arguments", out JsonElement arguments) ? arguments : default).ConfigureAwait(false);
                response["success"] = true;
                if (body is not null)
                {
                    response["body"] = JsonSerializer.SerializeToNode(body, body.GetType(), Options);
                }
            }
            catch (Exception error)
            {
                response["success"] = false;
                response["message"] = error is DapException dap ? dap.Reason : error.Message;
                try
                {
                    log?.Invoke($"DAP {command} failed: {error}");
                }
                catch
                {
                    // A failed diagnostic write must not discard the request's error response.
                }
            }
            response["seq"] = Interlocked.Increment(ref sequence);
            try
            {
                Write(response);
                if ((bool)response["success"]! && afterResponse.TryGetValue(command, out Action? then))
                {
                    then();
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
            }
        }
        finally
        {
            if (requestFinished.TryGetValue(command, out Action? finished))
            {
                finished();
            }
        }
    }

    private void FailPending(string why)
    {
        foreach (int seq in pending.Keys)
        {
            // Named, since a request failed this way may be heard of only from the finalizer, with no stack to say whose it was.
            if (pending.TryRemove(seq, out (string Command, TaskCompletionSource<JsonElement> Answer) request))
            {
                request.Answer.TrySetException(new DapException(request.Command, why));
            }
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>A request the other end answered with an error, or could not answer.</summary>
public sealed class DapException(string command, string reason) : Exception($"{command}: {reason}")
{
    public string Command { get; } = command;

    /// <summary>The other end's words alone, without the command.</summary>
    public string Reason { get; } = reason;
}
