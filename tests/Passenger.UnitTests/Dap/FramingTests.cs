using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Passenger.Dap;

namespace Passenger.UnitTests.Dap;

/// <summary>The Debug Adapter Protocol's messages as they cross between the IDE and its debugger: a Content-Length header and
/// a JSON body, written whole and read whole whatever pieces the pipe cuts them into, and what is no message ending the
/// conversation rather than hanging it. Each test waits for what it checks with no limit of its own: a conversation that
/// hangs holds its test, which the run's own limit on a test names.</summary>
public class FramingTests
{
    private const string Said = "said";

    [Fact]
    public Task A_message_is_written_whole_and_read_whole_whatever_pieces_the_pipe_cuts_it_into() => Cases.AllAsync(
        ("a message sent is its body length in bytes in its header then its JSON numbered in order", async () =>
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
        }),
        ("a message many times the read buffer arrives whole and the next after it", async () =>
        {
            string large = string.Concat(Enumerable.Repeat("0123456789abcdef", 40_000));
            byte[] input = Concat(Framed(Event(large)), Framed(Event("small")));
            Assert.Equal([large, "small"], await HeardAsync(new ChunkedStream(input, 8_192)));
        }),
        ("a response answers its request by number and an error fails it with the other end's words", async () =>
        {
            byte[] input = Concat(
                Framed("""{"seq":1,"type":"response","request_seq":2,"command":"second","success":true,"body":{"text":"two"}}"""),
                Framed("""{"seq":2,"type":"response","request_seq":1,"command":"first","success":false,"message":"no such thing"}"""));
            MemoryStream output = new();
            DapConnection connection = new(new ChunkedStream(input, 5), output);
            Task first = connection.RequestAsync("first", null, TestContext.Current.CancellationToken);
            Task<Words> second = connection.RequestAsync<Words>("second", new Words("asked"), TestContext.Current.CancellationToken);
            connection.Start();
            Assert.Equal(new Words("two"), await second.WaitAsync(TestContext.Current.CancellationToken));
            DapException failed = await Assert.ThrowsAsync<DapException>(() => first.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Equal("first", failed.Command);
            Assert.Equal("no such thing", failed.Reason);
            await connection.DisposeAsync();
            List<JsonElement> asked = Messages(output.ToArray());
            Assert.Equal(["first", "second"], asked.Select(m => m.GetProperty("command").GetString()));
            Assert.Equal("asked", asked[1].GetProperty("arguments").GetProperty("text").GetString());
        }),
        ("a request with no handler is refused naming it and the next is still answered", async () =>
        {
            byte[] input = Concat(
                Framed("""{"seq":7,"type":"request","command":"nothing"}"""),
                Framed("""{"seq":8,"type":"request","command":"echo","arguments":{"text":"hi"}}"""));
            MemoryStream output = new();
            DapConnection connection = new(new ChunkedStream(input, 3), output);
            connection.Handle<Words, Words>("echo", asked => Task.FromResult(new Words(asked.Text + "!")));
            connection.Start();
            await connection.Completion.WaitAsync(TestContext.Current.CancellationToken);
            await connection.DisposeAsync();
            List<JsonElement> answers = Messages(output.ToArray());
            Assert.Equal([7, 8], answers.Select(m => m.GetProperty("request_seq").GetInt32()));
            Assert.False(answers[0].GetProperty("success").GetBoolean());
            Assert.Equal("Request 'nothing' is not supported.", answers[0].GetProperty("message").GetString());
            Assert.True(answers[1].GetProperty("success").GetBoolean());
            Assert.Equal("hi!", answers[1].GetProperty("body").GetProperty("text").GetString());
        }),
        ("a large answer is written and read with no large array from the shared pool", async () =>
        {
            CancellationToken cancel = TestContext.Current.CancellationToken;
            VariablesResponse sent = new([.. Enumerable.Range(0, 2000).Select(i => new DapVariable
            {
                Name = $"[{i}]", Value = $"\"customer-{i:D6}@example.com\"", Type = "string", EvaluateName = $"emails[{i}]",
            })]);
            byte[] asked = Framed($$$"""{"seq":1,"type":"request","command":"{{{DapCommands.Variables}}}","arguments":{"variablesReference":7}}""");
            MemoryStream written = new();
            VariablesResponse read;
            using PoolRents rents = new(64 * 1024);
            rents.Counting = true;
            try
            {
                DapConnection adapter = new(new ChunkedStream(asked, 4_096), written);
                TaskCompletionSource answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                adapter.Handle<VariablesArguments, VariablesResponse>(DapCommands.Variables, _ => Task.FromResult(sent), () => answered.TrySetResult());
                adapter.Start();
                await answered.Task.WaitAsync(cancel);
                await adapter.DisposeAsync();
                DapConnection ide = new(new ChunkedStream(written.ToArray(), 16_384), new MemoryStream());
                Task<VariablesResponse> answer = ide.RequestAsync<VariablesResponse>(DapCommands.Variables, new VariablesArguments(7), cancel);
                ide.Start();
                read = await answer.WaitAsync(cancel);
                await ide.DisposeAsync();
            }
            finally
            {
                rents.Counting = false;
            }

            Assert.True(written.Length > 200_000, $"The answer came to {written.Length:N0} bytes, too few to need a large buffer");
            Assert.Equivalent(sent.Variables, read.Variables, strict: true);
            Assert.True(rents.Count == 0, $"Writing and reading a {written.Length:N0}-byte answer took {rents.Count} arrays of 64 KB or more from the shared pool, which a collection under memory load empties, so that each answer would allocate its room anew");
        }),
        ("messages cut into any pieces arrive whole and in order", () => Cases.EachAsync([1, 3, 100_000], CutInto, chunk => $"pieces of {chunk} bytes")),
        ("a response is read by its members as a document of it reads them", () => Cases.EachAsync(
        [
            ("""{"type":"event","type":"response","request_seq":1,"success":true,"body":{"text":"first"},"body":{"text":"last"}}""", "last"),
            ("""{"\u0074ype":"\u0072esponse","request_seq":1,"success":true,"body":{"text":"caf\u00e9"}}""", "café"),
            ("""{"type":"response","request_seq":1,"success":true,"body":null}""", (string?)null),
        ], row => ReadByMembers(row.Item1, row.Item2))));

    /// <summary>A header that never ends, a length past the most a message may be or past any number, or no length at all,
    /// and a response that does not read: the reader ends the conversation, failing the request waiting, never taking its
    /// memory for it or waiting on.</summary>
    [Fact]
    public Task What_is_no_message_ends_the_conversation_saying_why_and_fails_the_request_waiting() => Cases.AllAsync(
        ("a response that does not read ends the conversation and fails the request waiting, naming it", () => Cases.EachAsync(
        [
            """{"type":"response","request_seq":1,"success":"true"}""",
            """{"type":"response","request_seq":1,"success":null}""",
            """{"type":"response","request_seq":2147483648,"success":true}""",
            "{",
        ], Unread)),
        ("a header that never ends, a length past the most or none, or a body cut short ends the conversation saying why", () => Cases.EachAsync(
        [
            ("header", (string?)"ran past 65,536 bytes"),
            ("Content-Length: 268435457\r\n\r\n", "over the 268,435,456 bytes"),
            ("Content-Length: 99999999999999999999\r\n\r\n", "over the 268,435,456 bytes"),
            ("Content-Type: text/plain\r\n\r\n{}", "no Content-Length"),
            ("Content-Length: 12\r\n\r\n{\"cut\":", null),
        ], row => NoMessage(row.Item1, row.Item2))));

    private static async Task CutInto(int chunk)
    {
        byte[] input = Concat(
            Framed(Event("one")),
            Encoding.UTF8.GetBytes($"Content-Type: application/vscode-jsonrpc; charset=utf-8\r\ncontent-length:  {Encoding.UTF8.GetByteCount(Event("twö"))} \r\n\r\n{Event("twö")}"),
            Framed(Event("three \U0001F600")));
        Assert.Equal(["one", "twö", "three \U0001F600"], await HeardAsync(new ChunkedStream(input, chunk)));
    }

    private static async Task Unread(string response)
    {
        DapConnection connection = new(new ChunkedStream(Framed(response), 7), new MemoryStream());
        Task pending = connection.RequestAsync("waiting", null, TestContext.Current.CancellationToken);
        connection.Start();
        DapException failed = await Assert.ThrowsAsync<DapException>(() => pending.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal("waiting", failed.Command);
        await connection.Completion.WaitAsync(TestContext.Current.CancellationToken);
        await connection.DisposeAsync();
    }

    /// <summary>A response read straight from its bytes reads its members as a document of it would: by their names unescaped,
    /// the last of a name, and a body of null as none.</summary>
    private static async Task ReadByMembers(string response, string? text)
    {
        DapConnection connection = new(new ChunkedStream(Framed(response), 7), new MemoryStream());
        Task<Words> answer = connection.RequestAsync<Words>("asked", null, TestContext.Current.CancellationToken);
        connection.Start();
        Assert.Equal(text, (await answer.WaitAsync(TestContext.Current.CancellationToken)).Text);
        await connection.DisposeAsync();
    }

    /// <summary>A header that never ends, a length past the most a message may be or past any number, or no length at all: the
    /// reader ends the conversation saying why, never taking its memory for it or waiting on.</summary>
    private static async Task NoMessage(string sent, string? logged)
    {
        string text = sent == "header" ? string.Concat(Enumerable.Repeat($"X-Filler: {new string('x', 1000)}\r\n", 80)) : sent;
        ConcurrentQueue<string> log = new();
        DapConnection connection = new(new ChunkedStream(Encoding.ASCII.GetBytes(text), 4_096), new MemoryStream(), log.Enqueue);
        Task pending = connection.RequestAsync("waiting", null, TestContext.Current.CancellationToken);
        connection.Start();
        await connection.Completion.WaitAsync(TestContext.Current.CancellationToken);
        DapException failed = await Assert.ThrowsAsync<DapException>(() => pending.WaitAsync(TestContext.Current.CancellationToken));
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

    private static async Task<List<string>> HeardAsync(Stream input)
    {
        ConcurrentQueue<string> heard = new();
        DapConnection connection = new(input, new MemoryStream());
        connection.OnEvent<Words>(Said, said => heard.Enqueue(said.Text));
        connection.Start();
        await connection.Completion.WaitAsync(TestContext.Current.CancellationToken);
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
