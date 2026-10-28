using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Passenger.App.Mcp;

/// <summary>An agent's relay's connection to its agent: one JSON-RPC message a line on standard input and output, held
/// so it can be handed whole to the relay that takes this process's place (<see cref="Freeze"/>). Each request is kept,
/// as its line, until it is answered or the agent cancels it; on macOS and Linux input is read only when it waits, a
/// line at a time under the same gate as every write, so once frozen nothing more is read or written and each request
/// is either answered here or carried over.</summary>
internal sealed partial class RelayTransport : ITransport
{
    private static readonly JsonTypeInfo Messages = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage));

    private readonly Channel<JsonRpcMessage> received = Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly Stream output;
    private readonly Lock gate = new();

    /// <summary>The requests not answered yet, in the order they came, each as its line and the tool it calls.</summary>
    private readonly OrderedDictionary<RequestId, Unanswered> unanswered = [];

    /// <summary>Requests this relay sent its own server for the agent, whose answers the agent already had.</summary>
    private readonly HashSet<RequestId> own = [];

    /// <summary>Subscriptions the relay before this one acknowledged, whose acknowledgement the agent already had.</summary>
    private readonly HashSet<RequestId> acknowledged = [];

    /// <summary>What has been read of a line not yet ended.</summary>
    private byte[] begun = [];

    private JsonObject? initialize;
    private bool frozen;

    /// <summary>A line of the agent's carried over from the relay before, or one it sent its own server for the agent,
    /// answered for the first time here: the session is as the agent had it.</summary>
    public event Action? Resumed;

    public RelayTransport(Stream input, Stream output, RelaySession? session)
    {
        this.output = output;
        if (session is not null)
        {
            if (session.Initialize is { } asked)
            {
                string id = $"passenger-relay-{Guid.NewGuid():N}";
                own.Add(new RequestId(id));
                Received(Encoding.UTF8.GetBytes(new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = RequestMethods.Initialize, ["params"] = asked.DeepClone(),
                }.ToJsonString()));
                Received(Encoding.UTF8.GetBytes(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = NotificationMethods.InitializedNotification }.ToJsonString()));
            }
            foreach (string line in session.Carried)
            {
                Received(Encoding.UTF8.GetBytes(line));
            }
            foreach ((RequestId id, Unanswered request) in unanswered)
            {
                if (request.Method == RequestMethods.SubscriptionsListen)
                {
                    acknowledged.Add(id);
                }
            }
            begun = Convert.FromBase64String(session.Begun);
        }
        Thread reader = new(() => Read(input)) { IsBackground = true, Name = "Passenger relay input" };
        reader.Start();
    }

    public string? SessionId => null;

    public ChannelReader<JsonRpcMessage> MessageReader => received.Reader;

    /// <summary>Writes a message whole, unless the transport is frozen, when nothing is written; an answer the agent had
    /// already (<see cref="own"/>, a repeated acknowledgement) is dropped.</summary>
    public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        byte[] line = [.. JsonSerializer.SerializeToUtf8Bytes(message, Messages), (byte)'\n'];
        bool resumed = false;
        lock (gate)
        {
            if (frozen)
            {
                return Task.CompletedTask;
            }
            if (message is JsonRpcResponse or JsonRpcError && message is JsonRpcMessageWithId { Id: var answered } && own.Remove(answered))
            {
                unanswered.Remove(answered);
                resumed = true;
            }
            else if (message is JsonRpcNotification { Method: NotificationMethods.SubscriptionsAcknowledgedNotification } ack && SubscriptionOf(ack) is { } subscription
                && acknowledged.Remove(subscription))
            {
                resumed = true;
            }
            else
            {
                try
                {
                    output.Write(line);
                    output.Flush();
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    throw new IOException("Failed to send message.", error);
                }
                if (message is JsonRpcResponse or JsonRpcError && message is JsonRpcMessageWithId { Id: var id })
                {
                    unanswered.Remove(id);
                }
            }
        }
        if (resumed)
        {
            Resumed?.Invoke();
        }
        return Task.CompletedTask;
    }

    /// <summary>Notes that a call is going on to a window, unless the transport is frozen, when it must not: the relay
    /// after this one makes it instead.</summary>
    public bool Forwarding(RequestId id)
    {
        lock (gate)
        {
            if (frozen)
            {
                return false;
            }
            if (unanswered.TryGetValue(id, out Unanswered? request))
            {
                unanswered[id] = request with { Forwarded = true };
            }
            return true;
        }
    }

    /// <summary>Stops reading and writing for good and gives what the next relay takes on: the agent's initialize (its
    /// protocol version the one agreed, <paramref name="protocol"/>), each request not answered as its line, and the
    /// line begun. A call a window was already making is answered first with <paramref name="cutShort"/>'s words, since
    /// the window it went to goes on without it.</summary>
    public RelaySession Freeze(string? protocol, string tools, Func<string, string> cutShort)
    {
        lock (gate)
        {
            List<string> carried = [];
            foreach ((RequestId id, Unanswered request) in unanswered)
            {
                if (!request.Forwarded)
                {
                    carried.Add(request.Line);
                    continue;
                }
                JsonRpcResponse refused = new()
                {
                    Id = id,
                    Result = JsonSerializer.SerializeToNode(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = cutShort(request.Tool ?? "the call") }] },
                        McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult))),
                };
                try
                {
                    output.Write([.. JsonSerializer.SerializeToUtf8Bytes(refused, Messages), (byte)'\n']);
                    output.Flush();
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    // The agent went, with nobody to tell.
                }
            }
            frozen = true;
            JsonObject? asked = initialize?.DeepClone().AsObject();
            if (asked is not null && protocol is not null)
            {
                asked["protocolVersion"] = protocol;
            }
            return new RelaySession(asked, tools, carried, Convert.ToBase64String(begun));
        }
    }

    public ValueTask DisposeAsync()
    {
        received.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>Reads the agent's lines until its input ends, or, frozen, until the next relay is to read them. On Windows,
    /// where no relay takes another's place, a plain read.</summary>
    private void Read(Stream input)
    {
        byte[] chunk = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int count;
                if (OperatingSystem.IsWindows())
                {
                    count = input.Read(chunk);
                    lock (gate)
                    {
                        Take(chunk.AsSpan(0, count));
                    }
                }
                else if (!Waiting())
                {
                    continue;
                }
                else
                {
                    lock (gate)
                    {
                        if (frozen)
                        {
                            return;
                        }
                        count = ReadInput(chunk);
                        if (count < 0)
                        {
                            continue;
                        }
                        Take(chunk.AsSpan(0, count));
                    }
                }
                if (count == 0)
                {
                    received.Writer.TryComplete();
                    return;
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            received.Writer.TryComplete();
        }
    }

    /// <summary>What was read, split into lines, each ended line taken in.</summary>
    private void Take(ReadOnlySpan<byte> read)
    {
        byte[] joined = [.. begun, .. read];
        int start = 0;
        for (int end = Array.IndexOf(joined, (byte)'\n'); end >= 0; end = Array.IndexOf(joined, (byte)'\n', start))
        {
            Received(joined.AsSpan(start, end - start));
            start = end + 1;
        }
        begun = joined[start..];
    }

    /// <summary>One line of the agent's: a request kept until answered, a cancellation striking its request off, and each
    /// passed to the server; a line that is no message is answered as the protocol says, when it has an id.</summary>
    private void Received(ReadOnlySpan<byte> line)
    {
        string text = Encoding.UTF8.GetString(line).TrimEnd('\r');
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        JsonRpcMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(text, Messages) as JsonRpcMessage;
        }
        catch (JsonException)
        {
            message = null;
            if (IdOf(text) is { } id && !frozen)
            {
                JsonRpcError unread = new() { Id = id, Error = new JsonRpcErrorDetail { Code = (int)McpErrorCode.ParseError, Message = "Failed to parse the JSON-RPC request." } };
                try
                {
                    output.Write([.. JsonSerializer.SerializeToUtf8Bytes(unread, Messages), (byte)'\n']);
                    output.Flush();
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    // The agent went, with nobody to tell.
                }
            }
        }
        switch (message)
        {
            case null:
                return;
            case JsonRpcRequest request:
                unanswered[request.Id] = new Unanswered(text, request.Method, (request.Params as JsonObject)?["name"] is JsonValue name && name.TryGetValue(out string? tool) ? tool : null, false);
                if (request.Method == RequestMethods.Initialize && request.Params is JsonObject asked)
                {
                    initialize = asked.DeepClone().AsObject();
                }
                break;
            case JsonRpcNotification { Method: NotificationMethods.CancelledNotification, Params: JsonObject cancelled } when cancelled["requestId"] is { } given:
                if (IdOf(given) is { } gone)
                {
                    unanswered.Remove(gone);
                }
                break;
        }
        received.Writer.TryWrite(message);
    }

    private static RequestId? IdOf(string line)
    {
        try
        {
            return JsonNode.Parse(line) is JsonObject read && read["id"] is { } id ? IdOf(id) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static RequestId? IdOf(JsonNode id) => id.GetValueKind() switch
    {
        JsonValueKind.String => new RequestId(id.GetValue<string>()),
        JsonValueKind.Number when id.AsValue().TryGetValue(out long number) => new RequestId(number),
        _ => null,
    };

    private static RequestId? SubscriptionOf(JsonRpcNotification notification) =>
        (notification.Params as JsonObject)?["_meta"] is JsonObject meta && meta[MetaKeys.SubscriptionId] is { } id ? IdOf(id) : null;

    /// <summary>Whether the agent's input has something to read, or has ended, within a quarter of a second: by select,
    /// since macOS's poll never says a named pipe's writer has gone. An input that cannot be waited on has ended.</summary>
    private static unsafe bool Waiting()
    {
        // An fd_set of standard input alone (bit 0), as both systems lay one out.
        byte* readable = stackalloc byte[128];
        new Span<byte>(readable, 128).Clear();
        readable[0] = 1;
        TimeLimit limit = new() { Seconds = 0, Microseconds = 250_000 };
        int ready = Select(1, readable, null, null, &limit);
        return ready >= 0 || Marshal.GetLastPInvokeError() == Interrupted
            ? ready > 0
            : throw new IOException($"Standard input cannot be waited on: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
    }

    /// <summary>Reads what waits on standard input, or -1 when the read was interrupted.</summary>
    private static unsafe int ReadInput(byte[] chunk)
    {
        fixed (byte* bytes = chunk)
        {
            nint count = ReadDescriptor(0, bytes, (nuint)chunk.Length);
            if (count >= 0)
            {
                return (int)count;
            }
            int error = Marshal.GetLastPInvokeError();
            return error == Interrupted || error == TryAgain ? -1 : throw new IOException($"Standard input could not be read: {Marshal.GetPInvokeErrorMessage(error)}");
        }
    }

    /// <summary>EINTR, as macOS and Linux number it.</summary>
    private const int Interrupted = 4;

    /// <summary>EAGAIN: 35 on macOS, 11 on Linux.</summary>
    private static readonly int TryAgain = OperatingSystem.IsMacOS() ? 35 : 11;

    /// <summary>A struct timeval: its microseconds an int on macOS, in the low half of the long here, as both are little-endian.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TimeLimit
    {
        public long Seconds;
        public long Microseconds;
    }

    [LibraryImport("libc", EntryPoint = "select", SetLastError = true)]
    private static unsafe partial int Select(int count, byte* readable, byte* writable, byte* failed, TimeLimit* limit);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static unsafe partial nint ReadDescriptor(int descriptor, byte* buffer, nuint count);

    /// <summary>A request of the agent's not answered yet: its line as it came, its method, the tool it calls, and whether
    /// a window is making it.</summary>
    private sealed record Unanswered(string Line, string Method, string? Tool, bool Forwarded);
}

/// <summary>What a relay hands the one that takes its process's place: the agent's initialize, if it made one (its protocol
/// version the one agreed), the tools it was listed (<see cref="McpProxy.ToolsListed"/>), each request not answered yet as
/// its line, and what the agent had begun of its next line, in base64.</summary>
internal sealed record RelaySession(JsonObject? Initialize, string Tools, IReadOnlyList<string> Carried, string Begun)
{
    public byte[] Written() => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["initialize"] = Initialize?.DeepClone(),
        ["tools"] = Tools,
        ["carried"] = new JsonArray([.. Carried.Select(line => (JsonNode?)JsonValue.Create(line))]),
        ["begun"] = Begun,
    }.ToJsonString());

    /// <summary>A session as <see cref="Written"/> wrote it; a <see cref="FormatException"/> for anything else.</summary>
    public static RelaySession Read(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is JsonObject read && read["tools"]?.GetValue<string>() is { } tools && read["carried"] is JsonArray carried
                && read["begun"]?.GetValue<string>() is { } begun)
            {
                return new RelaySession((read["initialize"] as JsonObject)?.DeepClone().AsObject(), tools,
                    [.. carried.Select(line => line?.GetValue<string>() ?? throw new FormatException("a carried line is null"))], begun);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new FormatException($"it is no relay's session: {error.Message}", error);
        }
        throw new FormatException("it is no relay's session");
    }
}
