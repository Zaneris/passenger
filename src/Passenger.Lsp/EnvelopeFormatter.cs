using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace Passenger.Lsp;

/// <summary>JSON-RPC's messages as UTF-8 JSON, read with no document of the whole: the envelope alone (version, id,
/// method, an error's code and message) is read as a message comes, its parameters, result or error data kept as the
/// bytes they came in, which the reader holds until the message's deserialization is complete, and read from there into
/// the type a handler or caller asks for. What Passenger's conversations use is read and written: parameters as one
/// object, by name or by place; results; errors with their data; cancellation's request ids; the trace context's
/// properties; and the conversation's own tracing. Progress objects, streams, pipes, enumerables and remote objects are
/// not marshaled, and a message's top-level properties of its own are neither read nor written.</summary>
internal sealed class EnvelopeFormatter : IJsonRpcMessageTextFormatter, IJsonRpcInstanceContainer, IJsonRpcFormatterTracingCallbacks, IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly JsonSerializerOptions options;
    private readonly JsonReaderOptions readerOptions;

    /// <summary>A message's text for the conversation's tracing, which reads it only as it is told: one for what is read,
    /// one for what is written, which cross on their own threads.</summary>
    private readonly Shown read = new();
    private readonly Shown written = new();

    private JsonRpc? rpc;

    /// <summary>The writer kept between messages; taken while one is written, so a write within a write makes its own.</summary>
    private Utf8JsonWriter? writer;

    /// <summary>A formatter whose parameters, results and error data are read and written with <paramref name="options"/>
    /// (the serializer's defaults where none), request ids as JSON-RPC writes them.</summary>
    public EnvelopeFormatter(JsonSerializerOptions? options = null)
    {
        this.options = new JsonSerializerOptions(options ?? JsonSerializerOptions.Default);
        this.options.Converters.Add(new SystemTextJsonFormatter.RequestIdSTJsonConverter());
        readerOptions = new JsonReaderOptions
        {
            MaxDepth = this.options.MaxDepth,
            AllowTrailingCommas = this.options.AllowTrailingCommas,
            CommentHandling = this.options.ReadCommentHandling,
        };
    }

    public Encoding Encoding
    {
        get => Utf8;
        set => throw new NotSupportedException("Only UTF-8 is read and written");
    }

    JsonRpc IJsonRpcInstanceContainer.Rpc
    {
        set => rpc = value;
    }

    public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer) => Deserialize(contentBuffer, Utf8);

    public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer, Encoding encoding)
    {
        if (encoding is not UTF8Encoding)
        {
            throw new NotSupportedException("Only UTF-8 is read");
        }
        Envelope envelope = Envelope.Read(contentBuffer);
        JsonRpcMessage message = envelope.Method is not null ? new InboundRequest(this, envelope.Parameters, envelope.Named, envelope.Count)
            {
                RequestId = envelope.Id,
                Method = envelope.Method,
                TraceParent = envelope.TraceParent,
                TraceState = envelope.TraceState,
            }
            : envelope.Result is { } result ? new InboundResult(this, result) { RequestId = envelope.Id }
            : envelope.HasError ? new InboundError
            {
                RequestId = envelope.Id,
                Error = new InboundDetail(this, envelope.Data) { Code = envelope.Code, Message = envelope.Message },
            }
            : throw new JsonException("Expected a request, result, or error message.");
        message.Version = envelope.Version;
        if (rpc is IJsonRpcTracingCallbacks tracing)
        {
            read.Show(contentBuffer);
            try
            {
                tracing.OnMessageDeserialized(message, read);
            }
            finally
            {
                read.Hide();
            }
        }
        return message;
    }

    public object GetJsonText(JsonRpcMessage message) => throw new NotSupportedException();

    public void Serialize(IBufferWriter<byte> bufferWriter, JsonRpcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Utf8JsonWriter json = Interlocked.Exchange(ref writer, null) ?? new Utf8JsonWriter(bufferWriter);
        json.Reset(bufferWriter);
        bool whole = false;
        try
        {
            json.WriteStartObject();
            if (message.Version != "1.0")
            {
                json.WriteString("jsonrpc"u8, message.Version);
            }
            switch (message)
            {
                case JsonRpcRequest request:
                    WriteId(json, request.RequestId);
                    json.WriteString("method"u8, request.Method);
                    WriteArguments(json, request);
                    if (request.TraceParent is { } parent)
                    {
                        json.WriteString("traceparent"u8, parent);
                    }
                    if (request.TraceState is { } state)
                    {
                        json.WriteString("tracestate"u8, state);
                    }
                    break;
                case JsonRpcResult result:
                    WriteId(json, result.RequestId);
                    json.WritePropertyName("result"u8);
                    WriteData(json, result.Result, result.ResultDeclaredType);
                    break;
                case JsonRpcError error:
                    WriteId(json, error.RequestId);
                    WriteError(json, error);
                    break;
                default:
                    throw new ArgumentException($"Unknown message type: {message.GetType().Name}", nameof(message));
            }
            json.WriteEndObject();
            json.Flush();
            json.Reset(Nowhere.Instance);
            whole = true;
        }
        catch (Exception failed)
        {
            throw new JsonException($"The message could not be written: {failed.Message}", failed);
        }
        finally
        {
            if (!whole || Interlocked.CompareExchange(ref writer, json, null) is not null)
            {
                json.Dispose();
            }
        }
    }

    void IJsonRpcFormatterTracingCallbacks.OnSerializationComplete(JsonRpcMessage message, ReadOnlySequence<byte> encodedMessage)
    {
        if (rpc is IJsonRpcTracingCallbacks tracing)
        {
            written.Show(encodedMessage);
            try
            {
                tracing.OnMessageSerialized(message, written);
            }
            finally
            {
                written.Hide();
            }
        }
    }

    public void Dispose() => Interlocked.Exchange(ref writer, null)?.Dispose();

    /// <summary>A value kept as its bytes read as <paramref name="type"/>.</summary>
    private object? Read(ReadOnlySequence<byte> value, Type type)
    {
        Utf8JsonReader reader = new(value, readerOptions);
        return JsonSerializer.Deserialize(ref reader, type, options);
    }

    private T? Read<T>(ReadOnlySequence<byte> value)
    {
        Utf8JsonReader reader = new(value, readerOptions);
        return JsonSerializer.Deserialize<T>(ref reader, options);
    }

    private static void WriteId(Utf8JsonWriter json, RequestId id)
    {
        if (id.IsEmpty)
        {
            return;
        }
        json.WritePropertyName("id"u8);
        if (id.Number is { } number)
        {
            json.WriteNumberValue(number);
        }
        else if (id.String is { } text)
        {
            json.WriteStringValue(text);
        }
        else
        {
            json.WriteNullValue();
        }
    }

    private void WriteArguments(Utf8JsonWriter json, JsonRpcRequest request)
    {
        if (request.ArgumentsList is { } list)
        {
            json.WriteStartArray("params"u8);
            for (int i = 0; i < list.Count; i++)
            {
                WriteData(json, list[i], request.ArgumentListDeclaredTypes?[i]);
            }
            json.WriteEndArray();
        }
        else if (request.NamedArguments is { } named)
        {
            json.WriteStartObject("params"u8);
            foreach ((string name, object? value) in named)
            {
                json.WritePropertyName(name);
                WriteData(json, value, request.NamedArgumentDeclaredTypes?[name]);
            }
            json.WriteEndObject();
        }
        else if (request.Arguments is { } arguments)
        {
            json.WritePropertyName("params"u8);
            WriteData(json, arguments, null);
        }
    }

    private void WriteError(Utf8JsonWriter json, JsonRpcError error)
    {
        JsonRpcError.ErrorDetail detail = error.Error ?? throw new ArgumentException("Error property must be set.", nameof(error));
        json.WriteStartObject("error"u8);
        json.WriteNumber("code"u8, (int)detail.Code);
        json.WriteString("message"u8, detail.Message);
        if (detail.Data is { } data)
        {
            json.WritePropertyName("data"u8);
            WriteData(json, data, null);
        }
        json.WriteEndObject();
    }

    private void WriteData(Utf8JsonWriter json, object? value, Type? declared)
    {
        if (declared is not null && value is not null)
        {
            JsonSerializer.Serialize(json, value, declared, options);
        }
        else
        {
            JsonSerializer.Serialize(json, value, options);
        }
    }

    /// <summary>What a message says of itself, read in one pass that checks the whole is JSON: its kind's properties, and
    /// where its parameters, result or error data lie in the message, with how many parameters it gives.</summary>
    private ref struct Envelope
    {
        public string Version;
        public RequestId Id;
        public string? Method;
        public ReadOnlySequence<byte>? Parameters;
        public bool Named;
        public int Count;
        public string? TraceParent;
        public string? TraceState;
        public ReadOnlySequence<byte>? Result;
        public bool HasError;
        public JsonRpcErrorCode Code;
        public string? Message;
        public ReadOnlySequence<byte>? Data;

        public static Envelope Read(ReadOnlySequence<byte> content)
        {
            Utf8JsonReader reader = new(content);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("Expected a JSON object at the root of the message.");
            }
            Envelope envelope = new() { Version = "1.0", Id = RequestId.NotSpecified };
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("jsonrpc"u8))
                {
                    reader.Read();
                    envelope.Version = reader.ValueTextEquals("2.0"u8) ? "2.0" : reader.GetString() ?? throw new JsonException("Unexpected null value for jsonrpc property.");
                }
                else if (reader.ValueTextEquals("id"u8))
                {
                    reader.Read();
                    envelope.Id = reader.TokenType switch
                    {
                        JsonTokenType.Number => new RequestId(reader.GetInt64()),
                        JsonTokenType.String => new RequestId(reader.GetString()),
                        JsonTokenType.Null => RequestId.Null,
                        _ => throw new JsonException($"Unexpected token type for id property: {reader.TokenType}"),
                    };
                }
                else if (reader.ValueTextEquals("method"u8))
                {
                    reader.Read();
                    envelope.Method = reader.GetString();
                }
                else if (reader.ValueTextEquals("params"u8))
                {
                    envelope.Parameters = Arguments(ref reader, content, out envelope.Named, out envelope.Count);
                }
                else if (reader.ValueTextEquals("result"u8))
                {
                    envelope.Result = Value(ref reader, content);
                }
                else if (reader.ValueTextEquals("error"u8))
                {
                    envelope.ReadError(ref reader, content);
                }
                else if (reader.ValueTextEquals("traceparent"u8))
                {
                    reader.Read();
                    envelope.TraceParent = reader.GetString();
                }
                else if (reader.ValueTextEquals("tracestate"u8))
                {
                    reader.Read();
                    envelope.TraceState = reader.GetString();
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
            if (reader.Read())
            {
                throw new JsonException("Expected the message to end after its object.");
            }
            return envelope;
        }

        private void ReadError(ref Utf8JsonReader reader, ReadOnlySequence<byte> content)
        {
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("Expected an error to be an object.");
            }
            bool coded = false;
            bool said = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("code"u8))
                {
                    reader.Read();
                    Code = (JsonRpcErrorCode)reader.GetInt64();
                    coded = true;
                }
                else if (reader.ValueTextEquals("message"u8))
                {
                    reader.Read();
                    Message = reader.GetString();
                    said = true;
                }
                else if (reader.ValueTextEquals("data"u8))
                {
                    Data = Value(ref reader, content);
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
            if (!coded || !said)
            {
                throw new JsonException("Expected an error to have a code and a message.");
            }
            HasError = true;
        }

        /// <summary>The next value, passed over, as the part of the message it is.</summary>
        private static ReadOnlySequence<byte> Value(ref Utf8JsonReader reader, ReadOnlySequence<byte> content)
        {
            reader.Read();
            long start = reader.TokenStartIndex;
            reader.Skip();
            return content.Slice(start, reader.BytesConsumed - start);
        }

        /// <summary>The parameters, passed over, as the part of the message they are, with whether they are named and how
        /// many there are; none for null, which stands for none.</summary>
        private static ReadOnlySequence<byte>? Arguments(ref Utf8JsonReader reader, ReadOnlySequence<byte> content, out bool named, out int count)
        {
            reader.Read();
            named = reader.TokenType == JsonTokenType.StartObject;
            count = 0;
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }
            if (reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
            {
                throw new JsonException($"Expected params to be an object or an array, not {reader.TokenType}.");
            }
            long start = reader.TokenStartIndex;
            while (reader.Read() && reader.TokenType is not (JsonTokenType.EndObject or JsonTokenType.EndArray))
            {
                if (named)
                {
                    reader.Read();
                }
                reader.Skip();
                count++;
            }
            return content.Slice(start, reader.BytesConsumed - start);
        }
    }

    /// <summary>A request or notification read, its parameters kept as their bytes until its deserialization is complete.</summary>
    private sealed class InboundRequest(EnvelopeFormatter formatter, ReadOnlySequence<byte>? given, bool named, int count)
        : JsonRpcRequest, IJsonRpcMessageBufferManager
    {
        private ReadOnlySequence<byte>? kept = given;

        public override int ArgumentCount => count;

        public override bool ArgumentsAreNamed => kept is not null && named;

        public override IEnumerable<string>? ArgumentNames => kept is { } arguments && named ? Names(arguments) : null;

        public override ArgumentMatchResult TryGetTypedArguments(ReadOnlySpan<ParameterInfo> parameters, Span<object?> typedArguments) =>
            TryGetTypedArguments(parameters, default, typedArguments);

        public override ArgumentMatchResult TryGetTypedArguments(ReadOnlySpan<ParameterInfo> parameters, ReadOnlySpan<string?> parameterNames, Span<object?> typedArguments) =>
            AsOneObject(parameters, typedArguments) ? ArgumentMatchResult.Success : base.TryGetTypedArguments(parameters, parameterNames, typedArguments);

        public override ArgumentMatchResult TryGetTypedArguments(ReadOnlySpan<ParameterInfo> parameters, ReadOnlySpan<string?> parameterNames, Span<object?> typedArguments,
            bool allowFlexibleNamedArgumentMatching) =>
            AsOneObject(parameters, typedArguments) ? ArgumentMatchResult.Success
                : base.TryGetTypedArguments(parameters, parameterNames, typedArguments, allowFlexibleNamedArgumentMatching);

        public override bool TryGetArgumentByNameOrIndex(string? name, int position, Type? typeHint, out object? value)
        {
            value = null;
            if (kept is not { } arguments || Find(arguments, name, position) is not { } found)
            {
                return false;
            }
            try
            {
                value = formatter.Read(found, typeHint ?? typeof(object));
            }
            catch (Exception failed)
            {
                formatter.rpc?.TraceSource.TraceEvent(TraceEventType.Warning, (int)JsonRpc.TraceEvents.MethodArgumentDeserializationFailure,
                    "Argument {0} (position {1}) of {2} could not be read as {3}: {4}", name, position, Method, typeHint, failed);
                throw new RpcArgumentDeserializationException(name, position, typeHint, failed);
            }
            return true;
        }

        void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message) => kept = null;

        /// <summary>The parameters read whole as a method's one parameter, where the method takes them so and they could be
        /// its: named, or at most one by place (none where the parameter has a default), as JsonRpc matches a request's shape.</summary>
        private bool AsOneObject(ReadOnlySpan<ParameterInfo> takes, Span<object?> typedArguments)
        {
            if (takes.Length != 1 || kept is not { } arguments || Method is not { } method
                || !(named || count == 1 || count == 0 && takes[0].HasDefaultValue)
                || formatter.rpc?.GetJsonRpcMethodAttribute(method, takes) is not { UseSingleObjectParameterDeserialization: true })
            {
                return false;
            }
            typedArguments[0] = formatter.Read(arguments, takes[0].ParameterType);
            return true;
        }

        /// <summary>A parameter by its name or place, the last of a name given twice.</summary>
        private ReadOnlySequence<byte>? Find(ReadOnlySequence<byte> arguments, string? name, int position)
        {
            Utf8JsonReader reader = new(arguments, formatter.readerOptions);
            reader.Read();
            ReadOnlySequence<byte>? found = null;
            int at = 0;
            while (reader.Read() && reader.TokenType is not (JsonTokenType.EndObject or JsonTokenType.EndArray))
            {
                bool wanted = named ? name is not null && reader.ValueTextEquals(name) : at == position;
                if (named)
                {
                    reader.Read();
                }
                long start = reader.TokenStartIndex;
                reader.Skip();
                if (wanted)
                {
                    found = arguments.Slice(start, reader.BytesConsumed - start);
                    if (!named)
                    {
                        break;
                    }
                }
                at++;
            }
            return found;
        }

        private List<string> Names(ReadOnlySequence<byte> arguments)
        {
            Utf8JsonReader reader = new(arguments, formatter.readerOptions);
            reader.Read();
            List<string> names = [];
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                names.Add(reader.GetString()!);
                reader.Read();
                reader.Skip();
            }
            return names;
        }
    }

    /// <summary>A result read, kept as its bytes until the type its caller waits for is known.</summary>
    private sealed class InboundResult(EnvelopeFormatter formatter, ReadOnlySequence<byte> given) : JsonRpcResult, IJsonRpcMessageBufferManager
    {
        private ReadOnlySequence<byte>? kept = given;
        private Exception? unread;

        public override T GetResult<T>()
        {
            if (unread is not null)
            {
                ExceptionDispatchInfo.Throw(unread);
            }
            return kept is { } result ? formatter.Read<T>(result)! : (T)Result!;
        }

        protected override void SetExpectedResultType(Type resultType)
        {
            if (kept is not { } result)
            {
                throw new InvalidOperationException("Result is no longer available or has already been deserialized.");
            }
            try
            {
                Result = formatter.Read(result, resultType);
                kept = null;
            }
            catch (Exception failed)
            {
                unread = new JsonException($"The result could not be read as {resultType.Name}: {failed.GetType().Name}: {failed.Message}", failed);
            }
        }

        void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message) => kept = null;
    }

    private sealed class InboundError : JsonRpcError, IJsonRpcMessageBufferManager
    {
        void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message) => (Error as InboundDetail)?.LetGo();
    }

    /// <summary>An error's detail read, its data kept as its bytes until the type it is read as is known; data that does not
    /// read as that type is read as any value, else none.</summary>
    private sealed class InboundDetail(EnvelopeFormatter formatter, ReadOnlySequence<byte>? given) : JsonRpcError.ErrorDetail
    {
        private ReadOnlySequence<byte>? kept = given;

        public override object? GetData(Type dataType)
        {
            ArgumentNullException.ThrowIfNull(dataType);
            if (kept is not { } data)
            {
                return Data;
            }
            try
            {
                return formatter.Read(data, dataType);
            }
            catch (JsonException)
            {
                try
                {
                    return formatter.Read(data, typeof(object));
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }

        protected override void SetExpectedDataType(Type dataType)
        {
            Data = GetData(dataType);
            kept = null;
        }

        public void LetGo() => kept = null;
    }

    /// <summary>A message's bytes as text, for tracing that reads them only while they are shown.</summary>
    private sealed class Shown
    {
        private ReadOnlySequence<byte>? shown;

        public void Show(ReadOnlySequence<byte> message) => shown = message;

        public void Hide() => shown = null;

        public override string ToString() => shown is { } message ? Utf8.GetString(message) : "(a message no longer held)";
    }

    /// <summary>Where the kept writer points between messages, so it holds no buffer of a message written.</summary>
    private sealed class Nowhere : IBufferWriter<byte>
    {
        public static readonly Nowhere Instance = new();

        public void Advance(int count) => throw new InvalidOperationException("Nothing is written between messages");

        public Memory<byte> GetMemory(int sizeHint = 0) => throw new InvalidOperationException("Nothing is written between messages");

        public Span<byte> GetSpan(int sizeHint = 0) => throw new InvalidOperationException("Nothing is written between messages");
    }
}
