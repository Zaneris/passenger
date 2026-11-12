using System.Buffers;
using System.Text;
using System.Text.Json;
using Passenger.Lsp;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace Passenger.UnitTests.Lsp;

/// <summary>Passenger's JSON-RPC conversations read with the envelope alone parsed: every recorded request, notification,
/// answer, error, cancellation and progress handed to handlers and callers, and every message written, exactly as
/// StreamJsonRpc's own System.Text.Json formatter does, however the pipe cuts the messages; and a large answer read with
/// no buffer from the shared pool and nothing beyond what it is read as.</summary>
public class EnvelopeFormatterTests
{
    private static readonly JsonSerializerOptions Web = JsonRpcConversations.Web;

    /// <summary>A pipe's pieces: smaller than most messages, a socket's, and larger than any.</summary>
    public static TheoryData<int> PipePieces => new() { 100, 4096, 1 << 20 };

    [Theory]
    [MemberData(nameof(PipePieces))]
    public async Task A_peers_requests_and_notifications_reach_their_handlers_and_are_answered_as_before(int piece)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string before = await JsonRpcConversations.InboundAsync(new SystemTextJsonFormatter { JsonSerializerOptions = Web }, piece, 5_000, cancel);
        string now = await JsonRpcConversations.InboundAsync(new EnvelopeFormatter(Web), piece, 5_000, cancel);

        Assert.Equal(before, now);
        Assert.Contains("heard log: {\"type\":4,\"message\":\"named with an escape\"}", now);
        Assert.Contains("heard partial: [\"partial-1\",", now);
        Assert.Contains("heard tokens: \"5000 ", now);
        Assert.Contains("answered {\"jsonrpc\":\"2.0\",\"id\":9,\"error\":{\"code\":-32800,", now);
    }

    [Theory]
    [MemberData(nameof(PipePieces))]
    public async Task Passengers_own_requests_are_written_and_their_answers_and_errors_read_as_before(int piece)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string before = await JsonRpcConversations.OutboundAsync(new SystemTextJsonFormatter { JsonSerializerOptions = Web }, piece, 5_000, cancel);
        string now = await JsonRpcConversations.OutboundAsync(new EnvelopeFormatter(Web), piece, 5_000, cancel);

        Assert.Equal(before, now);
        Assert.Contains("got RemoteInvocationException -32801 Content modified", now);
        Assert.Contains("wrote {\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":", now);
    }

    /// <summary>Each kind of message cut into pieces of a byte, so every name, number and string crosses one: read as it is
    /// read whole.</summary>
    [Fact]
    public void A_message_cut_into_pieces_of_a_byte_reads_as_it_does_whole()
    {
        string[] messages =
        [
            """{"jsonrpc":"2.0","id":"a\"b","method":"textDocument/didOpen","params":{"text":"é ☕ \ud83d\ude00"},"tracestate":"x=1"}""",
            """{"jsonrpc":"2.0","id":12345678901,"result":[1,-2.5e3,"three",null,{"four":[]}]}""",
            """{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Parse error","data":{"why":"é"}}}""",
            """{"jsonrpc":"1.5","method":"exit"}""",
            """{"method":"exit","id":3}""",
        ];
        foreach (string message in messages)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            Assert.Equal(Said(new ReadOnlySequence<byte>(bytes)), Said(Pieces(bytes, 1)));
        }
        Assert.Equal("request a\"b textDocument/didOpen 2.0 {\"text\":\"é ☕ \\ud83d\\ude00\"} 1", Said(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(messages[0]))));
        Assert.Equal("result 12345678901 2.0 [1,-2.5e3,\"three\",null,{\"four\":[]}]", Said(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(messages[1]))));
        Assert.Equal("error  2.0 ParseError Parse error {\"why\":\"é\"}", Said(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(messages[2]))));
        Assert.Equal("request  exit 1.5 0", Said(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(messages[3]))));
        Assert.Equal("request 3 exit 1.0 0", Said(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(messages[4]))));
    }

    [Fact]
    public void What_is_no_message_is_refused_as_JSON_that_does_not_read()
    {
        string[] refused =
        [
            "[1]",
            """{"jsonrpc":"2.0","id":1}""",
            """{"jsonrpc":"2.0","method":"exit"} {}""",
            """{"jsonrpc":"2.0","method":"exit","params":"one"}""",
            """{"jsonrpc":"2.0","id":1,"error":{"message":"no code"}}""",
            """{"jsonrpc":"2.0","method":"exit","params":{"a":}}""",
        ];
        foreach (string message in refused)
        {
            Assert.ThrowsAny<JsonException>(() => new EnvelopeFormatter(Web).Deserialize(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(message))));
        }
    }

    /// <summary>A semantic tokens answer of 250,000 numbers, about 750 KB: read whole, it rents nothing from the shared pool,
    /// which a busy conversation's messages take from one another, and allocates no more than the answer read alone.</summary>
    [Fact]
    public void A_large_answer_rents_no_buffer_and_costs_what_it_is_read_as()
    {
        string answer = $$$"""{"resultId":"9","data":{{{JsonRpcConversations.Tokens(250_000)}}}}""";
        ReadOnlySequence<byte> message = new(Encoding.UTF8.GetBytes($$$"""{"jsonrpc":"2.0","id":7,"result":{{{answer}}}}"""));
        byte[] alone = Encoding.UTF8.GetBytes(answer);
        EnvelopeFormatter formatter = new(Web);
        Answered(formatter, message);
        JsonSerializer.Deserialize<JsonRpcConversations.TokensResult>(alone, Web);

        long before = GC.GetAllocatedBytesForCurrentThread();
        JsonSerializer.Deserialize<JsonRpcConversations.TokensResult>(alone, Web);
        long readAlone = GC.GetAllocatedBytesForCurrentThread() - before;
        int rented;
        long read;
        using (PoolRents rents = new())
        {
            rents.Counting = true;
            before = GC.GetAllocatedBytesForCurrentThread();
            JsonRpcConversations.TokensResult? tokens = Answered(formatter, message);
            read = GC.GetAllocatedBytesForCurrentThread() - before;
            rents.Counting = false;
            rented = rents.Count;
            Assert.Equal(250_000, tokens?.Data.Length);
        }

        Assert.True(rented == 0, $"Reading a {message.Length:N0}-byte answer rented {rented} buffers from the shared pool, which a busy server's answers take from one another, so that each allocates its own");
        Assert.True(read <= readAlone + 8 * 1024, $"Reading a {message.Length:N0}-byte answer allocated {read:N0} bytes, past the {readAlone:N0} its result alone costs");
    }

    /// <summary>A message as text: its kind, id, method, version and what it carries, as the conversation would read it.</summary>
    private static string Said(ReadOnlySequence<byte> bytes)
    {
        JsonRpcMessage message = new EnvelopeFormatter(Web).Deserialize(bytes);
        string said = message switch
        {
            JsonRpcRequest request => $"request {request.RequestId.String ?? request.RequestId.Number?.ToString(System.Globalization.CultureInfo.InvariantCulture)} {request.Method} {request.Version} "
                + (request.ArgumentCount > 0 && request.TryGetArgumentByNameOrIndex("text", 0, typeof(JsonElement), out object? text)
                    ? $"{{\"text\":{((JsonElement)text!).GetRawText()}}} {request.ArgumentCount}" : request.ArgumentCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            JsonRpcResult result => $"result {result.RequestId.Number} {result.Version} {result.GetResult<JsonElement>().GetRawText()}",
            JsonRpcError error => $"error {error.RequestId.Number} {error.Version} {error.Error?.Code} {error.Error?.Message} {((JsonElement)error.Error!.GetData(typeof(JsonElement))!).GetRawText()}",
            _ => message.GetType().Name,
        };
        ((IJsonRpcMessageBufferManager)message).DeserializationComplete(message);
        return said;
    }

    private static JsonRpcConversations.TokensResult? Answered(EnvelopeFormatter formatter, ReadOnlySequence<byte> message)
    {
        JsonRpcResult result = (JsonRpcResult)formatter.Deserialize(message);
        JsonRpcConversations.TokensResult? read = result.GetResult<JsonRpcConversations.TokensResult?>();
        ((IJsonRpcMessageBufferManager)result).DeserializationComplete(result);
        return read;
    }

    /// <summary>The bytes as a pipe holds them, in pieces of a size.</summary>
    private static ReadOnlySequence<byte> Pieces(byte[] bytes, int size)
    {
        Piece first = new(bytes.AsMemory(0, Math.Min(size, bytes.Length)), 0);
        Piece last = first;
        for (int at = size; at < bytes.Length; at += size)
        {
            last = last.Then(bytes.AsMemory(at, Math.Min(size, bytes.Length - at)));
        }
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Piece : ReadOnlySequenceSegment<byte>
    {
        public Piece(ReadOnlyMemory<byte> memory, long index)
        {
            Memory = memory;
            RunningIndex = index;
        }

        public Piece Then(ReadOnlyMemory<byte> memory)
        {
            Piece next = new(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
