using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Passenger.Core;

namespace Passenger.Dap;

/// <summary>One end of a Debug Adapter Protocol conversation over a pair of streams: each message is
/// a <c>Content-Length</c> header and a JSON body, sent with a sequence number and answered by it.
/// Either end can ask (the adapter asks the client to run a program in a terminal), so both sides
/// use the same connection. Responses are matched on the reading thread; requests and events are
/// handed to their handlers in the order they came, on a thread of their own, so a handler waiting
/// on a reply of its own never blocks the read that would bring it.</summary>
public sealed class DapConnection : IAsyncDisposable
{
    /// <summary>The most a message's header is read to before its blank line: the protocol's is one Content-Length line,
    /// and an end that sends more without one is not speaking it.</summary>
    public const int MostHeader = 64 * 1024;

    /// <summary>The longest message body read, 256 MB, far above any real one (Hot Reload's deltas or a large
    /// collection's variables, a few MB): a Content-Length beyond it ends the conversation rather than take the
    /// reader's memory.</summary>
    public const long MostBody = 256L * 1024 * 1024;

    private readonly Stream input;
    private readonly Stream output;
    private readonly Action<string>? log;
    private readonly Lock writeGate = new();

    /// <summary>The message being written, under <see cref="writeGate"/>: its envelope and its body serialized straight into
    /// one buffer kept for the conversation, grown to its longest message, so no message's room is taken from the shared
    /// pool, which a collection under memory load empties.</summary>
    private readonly ArrayBufferWriter<byte> writing = new();
    private Utf8JsonWriter? writer;

    /// <summary>The requests sent and not yet answered, each by its command and what reads its answer's body; every message's
    /// sequence number is counted here.</summary>
    private readonly PendingRequests<Asked, object?> pending = new();
    private readonly Dictionary<string, Func<JsonElement, Task<object?>>> handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Action Then, bool EvenIfFailed)> afterResponse = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action<JsonElement>> eventHandlers = new(StringComparer.Ordinal);
    private readonly Channel<JsonElement> inbox = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource stopping = new();
    private Task? reading;
    private Task? dispatching;

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

    /// <summary>Where every message each connection sends or reads goes while set, with the connection's <see
    /// cref="Name"/> and the message after an arrow for its direction ("-->" sent); called on the connection's
    /// threads.</summary>
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
        // Not held: both serve for as long as the connection lasts.
        dispatching = BackgroundWork.Unheld(DispatchAsync);
        reading = BackgroundWork.Unheld(ReadAsync);
    }

    /// <summary>Answers a request. A handler that throws answers with the exception's message as the
    /// error; <paramref name="then"/> runs once the answer is written (the initialized event must
    /// follow the initialize response), after an error too when <paramref name="evenIfFailed"/>.</summary>
    public void Handle<TArguments, TResponse>(string command, Func<TArguments, Task<TResponse>> handler, Action? then = null, bool evenIfFailed = false)
    {
        handlers[command] = async arguments => await handler(Read<TArguments>(arguments)).ConfigureAwait(false);
        Then(command, then, evenIfFailed);
    }

    /// <summary>A request answered with no body.</summary>
    public void Handle<TArguments>(string command, Func<TArguments, Task> handler, Action? then = null, bool evenIfFailed = false)
    {
        handlers[command] = async arguments =>
        {
            await handler(Read<TArguments>(arguments)).ConfigureAwait(false);
            return null;
        };
        Then(command, then, evenIfFailed);
    }

    public void OnEvent<TBody>(string name, Action<TBody> handler) => eventHandlers[name] = body => handler(Read<TBody>(body));

    /// <summary>Sends a request and waits for its answer, read from the body's bytes as they came; throws <see
    /// cref="DapException"/> when the other end answers with an error, and what the serializer throws when the body does
    /// not read as <typeparamref name="TResponse"/>.</summary>
    public async Task<TResponse> RequestAsync<TResponse>(string command, object? arguments, CancellationToken cancellation = default) =>
        (TResponse)(await Ask(new Asked(command, static body => ReadBody<TResponse>(body)), arguments, cancellation).ConfigureAwait(false))!;

    /// <summary>Sends a request and waits for its answer, whatever its body.</summary>
    public Task RequestAsync(string command, object? arguments, CancellationToken cancellation = default) =>
        Ask(new Asked(command, static _ => null), arguments, cancellation);

    private Task<object?> Ask(Asked asked, object? arguments, CancellationToken cancellation)
    {
        string command = asked.Command;
        int seq;
        Task<object?> answer;
        try
        {
            (seq, answer) = pending.Add(asked);
        }
        catch (DapException closed)
        {
            return Task.FromException<object?>(closed);
        }
        if (cancellation.CanBeCanceled)
        {
            cancellation.Register(() => pending.Forget(seq, answer, cancellation));
        }
        try
        {
            Write(new Envelope(seq, "request"u8, Command: command), "arguments"u8, arguments);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            pending.Fail(seq, request => new DapException(request.Command, "the connection is closed"));
        }
        return answer;
    }

    public void SendEvent(string name, object? body = null) => Write(new Envelope(pending.NextId(), "event"u8, Event: name), "body"u8, body);

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

    /// <summary>A response's body as <typeparamref name="T"/>, read in place; none, or null, reads as the type's empty form.</summary>
    private static T ReadBody<T>(ReadOnlySpan<byte> body) =>
        (body.IsEmpty ? default : JsonSerializer.Deserialize<T>(body, Options)) ?? JsonSerializer.Deserialize<T>("{}"u8, Options)!;

    private void Then(string command, Action? then, bool evenIfFailed)
    {
        if (then is null)
        {
            afterResponse.Remove(command);
        }
        else
        {
            afterResponse[command] = (then, evenIfFailed);
        }
    }

    /// <summary>A request waiting for its answer: its command, and what reads its answer from the body's bytes (empty for none)
    /// while they are still in the read buffer.</summary>
    private readonly record struct Asked(string Command, Func<ReadOnlySpan<byte>, object?> Read);

    /// <summary>A message's fields but its body, in the order they are written.</summary>
    private readonly ref struct Envelope(int seq, ReadOnlySpan<byte> type, int? RequestSeq = null, string? Command = null, string? Event = null,
        bool? Success = null, string? Message = null)
    {
        public int Seq { get; } = seq;
        public ReadOnlySpan<byte> Type { get; } = type;
        public int? RequestSeq { get; } = RequestSeq;
        public string? Command { get; } = Command;
        public string? Event { get; } = Event;
        public bool? Success { get; } = Success;
        public string? Message { get; } = Message;
    }

    /// <summary>Writes one message: its envelope, then its body (or arguments) serialized straight after it into the same
    /// buffer, and its header formatted on the stack. Nothing is sent of a message that does not serialize.</summary>
    private void Write(Envelope envelope, ReadOnlySpan<byte> bodyName, object? body)
    {
        lock (writeGate)
        {
            writing.ResetWrittenCount();
            Utf8JsonWriter json = writer ??= new Utf8JsonWriter(writing, new JsonWriterOptions { Encoder = Options.Encoder });
            json.Reset(writing);
            json.WriteStartObject();
            json.WriteNumber("seq"u8, envelope.Seq);
            json.WriteString("type"u8, envelope.Type);
            if (envelope.RequestSeq is { } requestSeq)
            {
                json.WriteNumber("request_seq"u8, requestSeq);
            }
            if (envelope.Command is { } command)
            {
                json.WriteString("command"u8, command);
            }
            if (envelope.Event is { } name)
            {
                json.WriteString("event"u8, name);
            }
            if (envelope.Success is { } success)
            {
                json.WriteBoolean("success"u8, success);
            }
            if (envelope.Message is { } message)
            {
                json.WriteString("message"u8, message);
            }
            if (body is not null)
            {
                json.WritePropertyName(bodyName);
                JsonSerializer.Serialize(json, body, body.GetType(), Options);
            }
            json.WriteEndObject();
            json.Flush();
            ReadOnlySpan<byte> written = writing.WrittenSpan;
            Span<byte> header = stackalloc byte[ContentLengthHeader.Longest];
            header = header[..ContentLengthHeader.Write(written.Length, header)];
            // Under the gate, so the trace has the messages in the order they went.
            Trace?.Invoke(Name, $"--> {Encoding.UTF8.GetString(written)}");
            output.Write(header);
            output.Write(written);
            output.Flush();
        }
    }

    private async Task ReadAsync()
    {
        ContiguousReader reader = new(input);
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(stopping.Token).ConfigureAwait(false);
                ReadOnlySequence<byte> held = result.Buffer;
                int consumed = ReadMessages(held.First.Span);
                if (result.IsCompleted)
                {
                    return;
                }
                reader.AdvanceTo(held.GetPosition(consumed), held.End);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException)
        {
            // A message that does not read (its JSON, or a field of the wrong kind) ends the conversation as a closed stream does.
            log?.Invoke($"DAP read ended: {error.Message}");
        }
        finally
        {
            reader.Complete();
            inbox.Writer.TryComplete();
            FailPending("the connection closed");
        }
    }

    /// <summary>Reads each whole message at the front of what the reader holds (in one piece) where it lies, and gives the bytes
    /// they took; what is left waits for the rest of its message.</summary>
    private int ReadMessages(ReadOnlySpan<byte> held)
    {
        int consumed = 0;
        while (true)
        {
            ReadOnlySpan<byte> rest = held[consumed..];
            int headerEnd = rest.IndexOf(ContentLengthHeader.End);
            if (headerEnd < 0 ? rest.Length >= MostHeader : headerEnd > MostHeader)
            {
                throw new IOException($"A DAP message's header ran past {MostHeader} bytes without the blank line that ends it");
            }
            if (headerEnd < 0)
            {
                return consumed;
            }
            long length = ContentLengthHeader.Read(rest[..headerEnd]) ?? throw NoLength(rest[..headerEnd]);
            if (length > MostBody)
            {
                throw new IOException($"A DAP message of {length} bytes is over the {MostBody} bytes the connection reads of one");
            }
            int start = headerEnd + ContentLengthHeader.End.Length;
            if (rest.Length - start < length)
            {
                return consumed;
            }
            ReadOnlySpan<byte> message = rest.Slice(start, (int)length);
            Trace?.Invoke(Name, $"<-- {Encoding.UTF8.GetString(message)}");
            Receive(message);
            consumed += start + (int)length;
        }
    }

    /// <summary>What ends a conversation whose header gives no length, quoting the header's start.</summary>
    private static IOException NoLength(ReadOnlySpan<byte> header)
    {
        string shown = Encoding.ASCII.GetString(header[..Math.Min(header.Length, 200)]);
        return new IOException($"A DAP message with no Content-Length: '{(header.Length > 200 ? shown + "…" : shown)}'");
    }

    /// <summary>A whole number a message cannot go without (its seq, an answer's request_seq): missing or of another kind, the
    /// message does not read, as one whose JSON does not.</summary>
    private static int Needed(JsonElement message, string name) => JsonMembers.Number(message, name) ?? throw Missing(name);

    private static FormatException Missing(string name) => new($"A DAP message's {name} is missing or no whole number");

    /// <summary>A message, read where it lies: a response straight into what its request waits for, with no document made of
    /// it; a request or event copied out as an element for its handler.</summary>
    private void Receive(ReadOnlySpan<byte> message)
    {
        Heard heard = Heard.Read(message);
        if (!heard.IsResponse)
        {
            Utf8JsonReader whole = new(message);
            inbox.Writer.TryWrite(JsonElement.ParseValue(ref whole));
            return;
        }
        int seq = heard.RequestSeq ?? throw Missing("request_seq");
        // Read before the request is taken from those waiting, so an answer that does not read fails it with the rest.
        if (heard.Success != true)
        {
            string? command = heard.Command;
            string? why = heard.Message;
            pending.Fail(seq, asked => new DapException(command ?? asked.Command, why ?? "failed"));
        }
        else if (pending.TryGet(seq, out Asked asked))
        {
            // Read here, from the bytes it came in, into what the request waits for: a body that does not read fails it alone.
            Exception? unread = null;
            object? answer = null;
            try
            {
                answer = asked.Read(heard.Body);
            }
            catch (Exception error)
            {
                unread = error;
            }
            if (unread is null)
            {
                pending.Answer(seq, answer);
            }
            else
            {
                pending.Fail(seq, _ => unread);
            }
        }
    }

    /// <summary>What a message's envelope says that its reading turns on, read from its bytes with no document of them: whether
    /// it is a response, and a response's request_seq, success, command, message and body's bytes (none where it has none).
    /// Each is read as <see cref="JsonMembers"/> reads a member, the last of its name, null where of another kind; a message
    /// whose JSON does not read throws.</summary>
    private readonly ref struct Heard(bool isResponse, int? requestSeq, bool? success, string? command, string? message, ReadOnlySpan<byte> body)
    {
        public bool IsResponse { get; } = isResponse;
        public int? RequestSeq { get; } = requestSeq;
        public bool? Success { get; } = success;
        public string? Command { get; } = command;
        public string? Message { get; } = message;
        public ReadOnlySpan<byte> Body { get; } = body;

        public static Heard Read(ReadOnlySpan<byte> message)
        {
            Utf8JsonReader reader = new(message);
            reader.Read();
            bool isResponse = false;
            int? requestSeq = null;
            bool? success = null;
            string? command = null;
            string? said = null;
            ReadOnlySpan<byte> body = default;
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    Part part = PartNamed(ref reader);
                    reader.Read();
                    switch (part)
                    {
                        case Part.Type:
                            isResponse = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("response"u8);
                            break;
                        case Part.RequestSeq:
                            requestSeq = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int seq) ? seq : null;
                            break;
                        case Part.Success:
                            success = reader.TokenType switch { JsonTokenType.True => true, JsonTokenType.False => false, _ => null };
                            break;
                        case Part.Command:
                            command = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                            break;
                        case Part.Message:
                            said = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                            break;
                        case Part.Body:
                        {
                            int start = (int)reader.TokenStartIndex;
                            reader.Skip();
                            body = message[start..(int)reader.BytesConsumed];
                            break;
                        }
                    }
                    reader.Skip();
                }
            }
            else
            {
                // A message that is no object has no members: its value is passed over whole.
                reader.Skip();
            }
            // What follows the message's one value may only be space.
            if (reader.Read())
            {
                throw new JsonException("A DAP message holds more than one JSON value");
            }
            return new Heard(isResponse, requestSeq, success, command, said, body);
        }

        private static Part PartNamed(ref Utf8JsonReader reader) =>
            reader.ValueTextEquals("type"u8) ? Part.Type
            : reader.ValueTextEquals("request_seq"u8) ? Part.RequestSeq
            : reader.ValueTextEquals("success"u8) ? Part.Success
            : reader.ValueTextEquals("command"u8) ? Part.Command
            : reader.ValueTextEquals("message"u8) ? Part.Message
            : reader.ValueTextEquals("body"u8) ? Part.Body
            : Part.Other;

        private enum Part
        {
            Other,
            Type,
            RequestSeq,
            Success,
            Command,
            Message,
            Body,
        }
    }

    private async Task DispatchAsync()
    {
        await foreach (JsonElement message in inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (JsonMembers.Text(message, "type"))
                {
                    case "request":
                        // Started in order and left to finish: the next request may start at a handler's first wait.
                        UnawaitedWork.Observe(AnswerAsync(message));
                        break;
                    case "event":
                        string name = JsonMembers.Text(message, "event") ?? string.Empty;
                        JsonElement body = JsonMembers.Value(message, "body") ?? default;
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
        int seq = Needed(request, "seq");
        string command = JsonMembers.Text(request, "command") ?? string.Empty;
        object? body = null;
        Exception? failed = null;
        try
        {
            if (!handlers.TryGetValue(command, out Func<JsonElement, Task<object?>>? handler))
            {
                throw new DapException(command, $"Request '{command}' is not supported.");
            }
            body = await handler(JsonMembers.Value(request, "arguments") ?? default).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failed = error;
        }
        try
        {
            if (failed is null)
            {
                try
                {
                    Write(new Envelope(pending.NextId(), "response"u8, seq, command, Success: true), "body"u8, body);
                }
                catch (Exception error) when (error is not (IOException or ObjectDisposedException))
                {
                    // A body that does not serialize answers as a failed handler does.
                    failed = error;
                }
            }
            if (failed is not null)
            {
                log?.Invoke($"DAP {command} failed: {failed}");
                Write(new Envelope(pending.NextId(), "response"u8, seq, command, Success: false, Message: failed is DapException dap ? dap.Reason : failed.Message), "body"u8, null);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            return;
        }
        if (afterResponse.TryGetValue(command, out (Action Then, bool EvenIfFailed) after) && (after.EvenIfFailed || failed is null))
        {
            after.Then();
        }
    }

    /// <summary>Fails every request waiting, and each asked from now on, named by its command, since a request failed
    /// this way may be heard of only from the finalizer, with no stack to say whose it was.</summary>
    private void FailPending(string why) => pending.End(asked => new DapException(asked.Command, why));
}

/// <summary>A request the other end answered with an error, or could not answer.</summary>
public sealed class DapException(string command, string reason) : Exception($"{command}: {reason}")
{
    public string Command { get; } = command;

    /// <summary>The other end's words alone, without the command.</summary>
    public string Reason { get; } = reason;
}
