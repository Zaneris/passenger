using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Passenger.Dap;

namespace Passenger.UnitTests.Dap;

/// <summary>The Debug Adapter Protocol's messages as they cross between the IDE and its debugger: a Content-Length header and
/// a JSON body, written whole and read whole whatever pieces the pipe cuts them into, and what is no message ending the
/// conversation rather than hanging it.</summary>
public class FramingTests
{
    private const string Said = "said";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_message_sent_is_its_body_length_in_bytes_in_its_header_then_its_JSON_numbered_in_order()
    {
        MemoryStream output = new();
        DapConnection connection = new(new ChunkedStream([], 1), output);
        connection.SendEvent(Said, new Words("café ☕ \U0001F600"));
        connection.SendEvent(Said, new Words("again"));
        await connection.DisposeAsync();
        List<JsonElement> sent = Messages(output.ToArray());
        Assert.Equal(2, sent.Count);
        Assert.Equal([1, 2], sent.Select(m => m.GetProperty("seq").GetInt32()));
        Assert.All(sent, m => Assert.Equal("event", m.GetProperty("type").GetString()));
        Assert.Equal(["café ☕ \U0001F600", "again"], sent.Select(m => m.GetProperty("body").GetProperty("text").GetString()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(100_000)]
    public async Task Messages_cut_into_any_pieces_arrive_whole_and_in_order(int chunk)
    {
        byte[] input = Concat(
            Framed(Event("one")),
            Encoding.UTF8.GetBytes($"Content-Type: application/vscode-jsonrpc; charset=utf-8\r\ncontent-length:  {Encoding.UTF8.GetByteCount(Event("twö"))} \r\n\r\n{Event("twö")}"),
            Framed(Event("three \U0001F600")));
        Assert.Equal(["one", "twö", "three \U0001F600"], await HeardAsync(new ChunkedStream(input, chunk)));
    }

    [Fact]
    public async Task A_message_many_times_the_read_buffer_arrives_whole_and_the_next_after_it()
    {
        string large = string.Concat(Enumerable.Repeat("0123456789abcdef", 40_000));
        byte[] input = Concat(Framed(Event(large)), Framed(Event("small")));
        Assert.Equal([large, "small"], await HeardAsync(new ChunkedStream(input, 8_192)));
    }

    [Fact]
    public async Task A_response_answers_its_request_by_number_and_an_error_fails_it_with_the_other_ends_words()
    {
        byte[] input = Concat(
            Framed("""{"seq":1,"type":"response","request_seq":2,"command":"second","success":true,"body":{"text":"two"}}"""),
            Framed("""{"seq":2,"type":"response","request_seq":1,"command":"first","success":false,"message":"no such thing"}"""));
        MemoryStream output = new();
        DapConnection connection = new(new ChunkedStream(input, 5), output);
        Task first = connection.RequestAsync("first", null, TestContext.Current.CancellationToken);
        Task<Words> second = connection.RequestAsync<Words>("second", new Words("asked"), TestContext.Current.CancellationToken);
        connection.Start();
        Assert.Equal(new Words("two"), await second.WaitAsync(Patience, TestContext.Current.CancellationToken));
        DapException failed = await Assert.ThrowsAsync<DapException>(() => first.WaitAsync(Patience, TestContext.Current.CancellationToken));
        Assert.Equal("first", failed.Command);
        Assert.Equal("no such thing", failed.Reason);
        await connection.DisposeAsync();
        List<JsonElement> asked = Messages(output.ToArray());
        Assert.Equal(["first", "second"], asked.Select(m => m.GetProperty("command").GetString()));
        Assert.Equal("asked", asked[1].GetProperty("arguments").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("""{"type":"response","request_seq":1,"success":"true"}""")]
    [InlineData("""{"type":"response","request_seq":1,"success":null}""")]
    [InlineData("""{"type":"response","request_seq":2147483648,"success":true}""")]
    [InlineData("{")]
    public async Task A_response_that_does_not_read_ends_the_conversation_and_fails_the_request_waiting_naming_it(string response)
    {
        DapConnection connection = new(new ChunkedStream(Framed(response), 7), new MemoryStream());
        Task pending = connection.RequestAsync("waiting", null, TestContext.Current.CancellationToken);
        connection.Start();
        DapException failed = await Assert.ThrowsAsync<DapException>(() => pending.WaitAsync(Patience, TestContext.Current.CancellationToken));
        Assert.Equal("waiting", failed.Command);
        await connection.Completion.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await connection.DisposeAsync();
    }

    /// <summary>A header that never ends, a length past the most a message may be or past any number, or no length at all: the
    /// reader ends the conversation saying why, never taking its memory for it or waiting on.</summary>
    [Theory]
    [InlineData("header", "ran past 65536 bytes")]
    [InlineData("Content-Length: 268435457\r\n\r\n", "over the 268435456 bytes")]
    [InlineData("Content-Length: 99999999999999999999\r\n\r\n", "over the 268435456 bytes")]
    [InlineData("Content-Type: text/plain\r\n\r\n{}", "no Content-Length")]
    [InlineData("Content-Length: 12\r\n\r\n{\"cut\":", null)]
    public async Task What_is_no_message_ends_the_conversation_saying_why_and_fails_the_request_waiting(string sent, string? logged)
    {
        string text = sent == "header" ? string.Concat(Enumerable.Repeat($"X-Filler: {new string('x', 1000)}\r\n", 80)) : sent;
        ConcurrentQueue<string> log = new();
        DapConnection connection = new(new ChunkedStream(Encoding.ASCII.GetBytes(text), 4_096), new MemoryStream(), log.Enqueue);
        Task pending = connection.RequestAsync("waiting", null, TestContext.Current.CancellationToken);
        connection.Start();
        await connection.Completion.WaitAsync(Patience, TestContext.Current.CancellationToken);
        DapException failed = await Assert.ThrowsAsync<DapException>(() => pending.WaitAsync(Patience, TestContext.Current.CancellationToken));
        Assert.Equal("the connection closed", failed.Reason);
        await connection.DisposeAsync();
        if (logged is null)
        {
            Assert.Empty(log);
        }
        else
        {
            Assert.True(log.Any(l => l.Contains("DAP read ended", StringComparison.Ordinal) && l.Contains(logged, StringComparison.Ordinal)),
                $"The connection's log does not say '{logged}': {string.Join(" | ", log)}");
        }
    }

    [Fact]
    public async Task A_request_with_no_handler_is_refused_naming_it_and_the_next_is_still_answered()
    {
        byte[] input = Concat(
            Framed("""{"seq":7,"type":"request","command":"nothing"}"""),
            Framed("""{"seq":8,"type":"request","command":"echo","arguments":{"text":"hi"}}"""));
        MemoryStream output = new();
        DapConnection connection = new(new ChunkedStream(input, 3), output);
        connection.Handle<Words, Words>("echo", asked => Task.FromResult(new Words(asked.Text + "!")));
        connection.Start();
        await connection.Completion.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await connection.DisposeAsync();
        List<JsonElement> answers = Messages(output.ToArray());
        Assert.Equal([7, 8], answers.Select(m => m.GetProperty("request_seq").GetInt32()));
        Assert.False(answers[0].GetProperty("success").GetBoolean());
        Assert.Equal("Request 'nothing' is not supported.", answers[0].GetProperty("message").GetString());
        Assert.True(answers[1].GetProperty("success").GetBoolean());
        Assert.Equal("hi!", answers[1].GetProperty("body").GetProperty("text").GetString());
    }

    private static async Task<List<string>> HeardAsync(Stream input)
    {
        ConcurrentQueue<string> heard = new();
        DapConnection connection = new(input, new MemoryStream());
        connection.OnEvent<Words>(Said, said => heard.Enqueue(said.Text));
        connection.Start();
        await connection.Completion.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await connection.DisposeAsync();
        return [.. heard];
    }

    private static string Event(string text) => JsonSerializer.Serialize(new { seq = 1, type = "event", @event = Said, body = new Words(text) }, DapConnection.Options);

    private static byte[] Framed(string body) => Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    /// <summary>The messages in bytes written to the other end, each header checked to be the one Content-Length of its body.</summary>
    private static List<JsonElement> Messages(byte[] written)
    {
        List<JsonElement> messages = [];
        int at = 0;
        while (at < written.Length)
        {
            int end = written.AsSpan(at).IndexOf("\r\n\r\n"u8);
            Assert.True(end > 0, "A message written has no header");
            string header = Encoding.ASCII.GetString(written, at, end);
            Assert.StartsWith("Content-Length: ", header, StringComparison.Ordinal);
            int length = int.Parse(header["Content-Length: ".Length..], System.Globalization.CultureInfo.InvariantCulture);
            at += end + 4;
            Assert.True(at + length <= written.Length, $"A header says {length} bytes, but {written.Length - at} follow it");
            messages.Add(JsonDocument.Parse(written.AsMemory(at, length)).RootElement.Clone());
            at += length;
        }
        return messages;
    }

    private sealed record Words(string Text);
}
