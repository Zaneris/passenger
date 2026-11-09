using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Passenger.UnitTests.Lsp;

/// <summary>Conversations recorded as language servers and test hosts speak them, played through a JsonRpc whose formatter
/// is given over a pipe in memory read in pieces of a given size, each written down as text: what its handlers were handed
/// and answered, and what it wrote of Passenger's own requests and what their callers got back.</summary>
internal static class JsonRpcConversations
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>A semantic tokens answer's numbers, as many as given.</summary>
    public static string Tokens(int count) => "[" + string.Join(',', Enumerable.Range(0, count).Select(i => (i * 7919 % 113).ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";

    /// <summary>The peer's requests and notifications as a server sends them.</summary>
    public static string[] Inbound(int tokens) =>
    [
        """{"jsonrpc":"2.0","method":"window/logMessage","params":{"type":3,"message":"Loaded 2 projects é \u00e9 \"quoted\""}}""",
        """{"jsonrpc":"2.0","\u006dethod":"window/logMessage","params":{"type":4,"message":"named with an escape"}}""",
        """{"jsonrpc":"2.0","id":1,"method":"workspace/configuration","params":{"items":[{"section":"csharp"},{"section":"x","scopeUri":"file:///a"}]}}""",
        """{"jsonrpc":"2.0","method":"element","params":{"nested":{"a":[1,2.5,{"b":null}],"t":true},"s":"x"}}""",
        """{"jsonrpc":"2.0","id":"two","method":"optional"}""",
        """{"jsonrpc":"2.0","id":3,"method":"optional","params":null}""",
        """{"jsonrpc":"2.0","method":"$/progress","params":{"token":"load","value":{"kind":"report","message":"Loading"}}}""",
        """{"jsonrpc":"2.0","method":"$/progress","params":["partial-1",{"items":[{"uri":"file:///a.cs","kind":"full","items":[]}]}]}""",
        """{"jsonrpc":"2.0","id":4,"method":"fails","params":{}}""",
        $$$"""{"jsonrpc":"2.0","id":5,"method":"tokens","params":{"resultId":"7","data":{{{Tokens(tokens)}}}}}""",
        """{"id":6,"jsonrpc":"2.0","params":{"a":1,"b":"two"},"method":"named"}""",
        """{"jsonrpc":"2.0","id":7,"method":"named","params":[3,"four"]}""",
        """{"jsonrpc":"2.0","id":8,"method":"nosuch","params":{}}""",
        """{"jsonrpc":"2.0","id":11,"method":"configurationBad","params":{"items":"not a list"}}""",
        """{"jsonrpc":"2.0","id":9,"method":"slow","params":{"x":1}}""",
        """{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":9}}""",
        """{"jsonrpc":"2.0","id":12,"method":"optional","params":{"after":"cancel"},"traceparent":"00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"}""",
    ];

    /// <summary>How many of <see cref="Inbound"/>'s requests are answered.</summary>
    public const int InboundAnswers = 11;

    public static async Task<string> InboundAsync(IJsonRpcMessageFormatter formatter, int segment, int tokens, CancellationToken cancel)
    {
        (Stream ours, Stream theirs) = FullDuplexStream.CreatePair();
        Target target = new();
        using JsonRpc rpc = new(new HeaderDelimitedMessageHandler(PipeWriter.Create(ours), Reader(ours, segment), formatter));
        rpc.AddLocalRpcTarget(target, new JsonRpcTargetOptions { UseSingleObjectParameterDeserialization = true });
        rpc.StartListening();
        foreach (string message in Inbound(tokens))
        {
            await WriteAsync(theirs, message, cancel);
        }
        List<string> answers = [];
        for (int i = 0; i < InboundAnswers; i++)
        {
            answers.Add(Shown(await ReadAsync(theirs, cancel)));
        }
        answers.Sort(StringComparer.Ordinal);
        StringBuilder said = new();
        lock (target.Heard)
        {
            target.Heard.Sort(StringComparer.Ordinal);
            foreach (string heard in target.Heard)
            {
                said.Append("heard ").AppendLine(heard);
            }
        }
        foreach (string answer in answers)
        {
            said.Append("answered ").AppendLine(answer);
        }
        return said.ToString();
    }

    /// <summary>Passenger's own requests and notifications, each answered as a server answers it.</summary>
    public static async Task<string> OutboundAsync(IJsonRpcMessageFormatter formatter, int segment, int tokens, CancellationToken cancel)
    {
        (Stream ours, Stream theirs) = FullDuplexStream.CreatePair();
        using JsonRpc rpc = new(new HeaderDelimitedMessageHandler(PipeWriter.Create(ours), Reader(ours, segment), formatter));
        rpc.StartListening();
        StringBuilder said = new();

        Task<JsonElement> initialized = rpc.InvokeWithParameterObjectAsync<JsonElement>("initialize", new { processId = 1, rootUri = "file:///x", capabilities = new { }, rootPath = (string?)null }, cancel);
        string asked = await ReadAsync(theirs, cancel);
        said.Append("wrote ").AppendLine(asked);
        await WriteAsync(theirs, $$$$"""{"jsonrpc":"2.0","id":{{{{Id(asked)}}}},"result":{"capabilities":{"semanticTokensProvider":{"legend":{"tokenTypes":["a","b"],"tokenModifiers":[]}}},"serverInfo":{"name":"é"}}}""", cancel);
        said.Append("got ").AppendLine((await initialized).GetRawText());

        Task<TokensResult?> colours = rpc.InvokeWithParameterObjectAsync<TokensResult?>("textDocument/semanticTokens/full", new { textDocument = new { uri = "file:///a b.cs" } }, cancel);
        asked = await ReadAsync(theirs, cancel);
        said.Append("wrote ").AppendLine(asked);
        await WriteAsync(theirs, $$$"""{"jsonrpc":"2.0","id":{{{Id(asked)}}},"result":{"resultId":"9","data":{{{Tokens(tokens)}}}}}""", cancel);
        said.Append("got ").AppendLine(JsonSerializer.Serialize(await colours, Web));

        Task<object?> refused = rpc.InvokeWithParameterObjectAsync<object?>("refused", new Dictionary<string, object?> { ["id"] = new RequestId(7), ["text"] = null, ["list"] = new[] { 1, 2 } }, cancel);
        asked = await ReadAsync(theirs, cancel);
        said.Append("wrote ").AppendLine(asked);
        await WriteAsync(theirs, $$$$"""{"jsonrpc":"2.0","id":{{{{Id(asked)}}}},"error":{"code":-32801,"message":"Content modified","data":{"retry":true,"why":["a",1]}}}""", cancel);
        said.Append("got ").AppendLine(await FailureAsync(refused));

        Task<object?> plain = rpc.InvokeWithParameterObjectAsync<object?>("refusedPlainly", new { }, cancel);
        asked = await ReadAsync(theirs, cancel);
        await WriteAsync(theirs, $$$"""{"jsonrpc":"2.0","id":{{{Id(asked)}}},"error":{"code":-32603,"message":"Internal error"}}""", cancel);
        said.Append("got ").AppendLine(await FailureAsync(plain));

        Task<TokensResult?> mistyped = rpc.InvokeWithParameterObjectAsync<TokensResult?>("mistyped", new { }, cancel);
        asked = await ReadAsync(theirs, cancel);
        await WriteAsync(theirs, $$$"""{"jsonrpc":"2.0","id":{{{Id(asked)}}},"result":"text"}""", cancel);
        said.Append("got ").AppendLine(await FailureAsync(mistyped));

        Task<object?> shutdown = rpc.InvokeWithCancellationAsync<object?>("shutdown", [], cancel);
        asked = await ReadAsync(theirs, cancel);
        said.Append("wrote ").AppendLine(asked);
        await WriteAsync(theirs, $$$"""{"jsonrpc":"2.0","id":{{{Id(asked)}}},"result":null}""", cancel);
        said.Append("got ").AppendLine(JsonSerializer.Serialize(await shutdown, Web));

        using CancellationTokenSource stopping = new();
        Task<object?> stopped = rpc.InvokeWithParameterObjectAsync<object?>("long", new { a = 1 }, stopping.Token);
        asked = await ReadAsync(theirs, cancel);
        said.Append("wrote ").AppendLine(asked);
        stopping.Cancel();
        said.Append("wrote ").AppendLine(await ReadAsync(theirs, cancel));
        await WriteAsync(theirs, $$$"""{"jsonrpc":"2.0","id":{{{Id(asked)}}},"error":{"code":-32800,"message":"Request cancelled"}}""", cancel);
        said.Append("got ").AppendLine(await FailureAsync(stopped));

        await rpc.NotifyWithParameterObjectAsync("textDocument/didChange", new { textDocument = new { uri = "file:///a.cs", version = 2 }, contentChanges = new[] { new { text = "class A { \"é\" }\r\n" } } });
        said.Append("wrote ").AppendLine(await ReadAsync(theirs, cancel));
        await rpc.NotifyAsync("exit");
        said.Append("wrote ").AppendLine(await ReadAsync(theirs, cancel));
        return said.ToString();
    }

    private static async Task<string> FailureAsync(Task answer)
    {
        try
        {
            await answer;
            return "no failure";
        }
        catch (RemoteInvocationException refused)
        {
            return $"{refused.GetType().Name} {refused.ErrorCode} {refused.Message} {JsonSerializer.Serialize(refused.ErrorData, Web)} {JsonSerializer.Serialize(refused.DeserializedErrorData, Web)}";
        }
        catch (Exception failed) when (failed is JsonException or OperationCanceledException)
        {
            return $"{failed.GetType().Name}";
        }
    }

    private static PipeReader Reader(Stream stream, int segment) =>
        PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: segment, minimumReadSize: Math.Min(segment, 64), leaveOpen: true));

    private static string Id(string message)
    {
        using JsonDocument read = JsonDocument.Parse(message);
        return read.RootElement.GetProperty("id").GetRawText();
    }

    /// <summary>An answer as text with the stack traces left out, which name the code that threw.</summary>
    private static string Shown(string message)
    {
        System.Text.Json.Nodes.JsonNode node = System.Text.Json.Nodes.JsonNode.Parse(message)!;
        if (node["error"]?["data"] is System.Text.Json.Nodes.JsonObject data)
        {
            data.Remove("stack");
            if (data["inner"] is System.Text.Json.Nodes.JsonObject inner)
            {
                inner.Remove("stack");
            }
        }
        return node.ToJsonString();
    }

    public static async Task WriteAsync(Stream to, string message, CancellationToken cancel)
    {
        byte[] body = Encoding.UTF8.GetBytes(message);
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await to.WriteAsync(header, cancel);
        // In pieces, as a pipe brings a long message.
        for (int at = 0; at < body.Length; at += 1000)
        {
            await to.WriteAsync(body.AsMemory(at, Math.Min(1000, body.Length - at)), cancel);
            await to.FlushAsync(cancel);
        }
    }

    public static async Task<string> ReadAsync(Stream from, CancellationToken cancel)
    {
        List<byte> header = [];
        byte[] one = new byte[1];
        while (header.Count < 4 || header[^1] != '\n' || header[^2] != '\r' || header[^3] != '\n' || header[^4] != '\r')
        {
            if (await from.ReadAsync(one, cancel) == 0)
            {
                throw new EndOfStreamException();
            }
            header.Add(one[0]);
        }
        string text = Encoding.ASCII.GetString([.. header]);
        int length = int.Parse(text.Split("Content-Length: ")[1].Split('\r')[0], System.Globalization.CultureInfo.InvariantCulture);
        byte[] body = new byte[length];
        await from.ReadExactlyAsync(body, cancel);
        return Encoding.UTF8.GetString(body);
    }

    public sealed record LogParams(int Type, string Message);

    public sealed record ConfigurationItem(string? Section, string? ScopeUri);

    public sealed record ConfigurationParams(IReadOnlyList<ConfigurationItem> Items);

    public sealed record ProgressParams(JsonElement Token, JsonElement Value);

    public sealed record Report(IReadOnlyList<JsonElement> Items);

    public sealed record TokensResult(string? ResultId, int[] Data);

    /// <summary>Handlers as Passenger's are made: one object each, a JsonElement, one optional, two by place, and a
    /// request that waits to be cancelled.</summary>
    public sealed class Target
    {
        public List<string> Heard { get; } = [];

        private void Say(string what, object? value)
        {
            lock (Heard)
            {
                Heard.Add($"{what}: {JsonSerializer.Serialize(value, Web)}");
            }
        }

        [JsonRpcMethod("window/logMessage")]
        public void Log(LogParams message) => Say("log", message);

        [JsonRpcMethod("workspace/configuration")]
        public object?[] Configuration(ConfigurationParams request)
        {
            Say("configuration", request);
            return [new { tabSize = 4 }, null, "é"];
        }

        [JsonRpcMethod("configurationBad")]
        public object?[] ConfigurationBad(ConfigurationParams request) => [];

        [JsonRpcMethod("element")]
        public void Element(JsonElement said) => Say("element", said);

        [JsonRpcMethod("optional")]
        public object? Optional(JsonElement? request = null)
        {
            Say("optional", request);
            return request is { } given ? new { echoed = given } : null;
        }

        [JsonRpcMethod("$/progress")]
        public void Progress(ProgressParams progress) => Say("progress", progress);

        [JsonRpcMethod("$/progress")]
        public void Progress(JsonElement partialToken, Report? partialValue) => Say("partial", new object?[] { partialToken, partialValue });

        [JsonRpcMethod("fails")]
        public object Fails(JsonElement asked) => throw new LocalRpcException("Passenger has no content for it") { ErrorCode = -32603 };

        [JsonRpcMethod("tokens")]
        public int Tokens(TokensResult asked)
        {
            Say("tokens", asked.Data.Length + " " + asked.Data.Sum());
            return asked.Data.Length;
        }

        [JsonRpcMethod("named")]
        public string Named(int a, string b)
        {
            Say("named", $"{a} {b}");
            return $"{a}{b}";
        }

        [JsonRpcMethod("slow")]
        public async Task<string> SlowAsync(JsonElement asked, CancellationToken cancel)
        {
            Say("slow", asked);
            await Task.Delay(Timeout.Infinite, cancel);
            return "never";
        }
    }
}
