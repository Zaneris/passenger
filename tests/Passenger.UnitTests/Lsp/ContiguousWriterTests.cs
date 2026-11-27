using System.IO.Pipelines;
using System.Text;
using Passenger.Lsp;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Passenger.UnitTests.Lsp;

/// <summary>A test host's conversation written through one buffer kept for it: every message comes out as a pipe writes it,
/// a request of thousands of tests takes no array of its length from the shared pool, which a thread other than the one
/// that gave it back misses, nor does a long string a language server is sent, a project's design-time builds.</summary>
public class ContiguousWriterTests
{
    /// <summary>A run's request of 2,000 tests, as a test host is asked to run them: about 300 KB.</summary>
    private static JsonRpcRequest RunRequest() => new()
    {
        RequestId = new RequestId(2),
        Method = "testing/runTests",
        Arguments = new Dictionary<string, object?>
        {
            ["tests"] = Enumerable.Range(0, 2000).Select(i => new Dictionary<string, string> { ["uid"] = $"{i:x64}", ["display-name"] = $"Sums.Tests.RowTests.Adds_up(row: {i}) é" }).ToArray(),
            ["runId"] = "6b0d3c2e-2f9b-4a3c-9d1e-4f5a6b7c8d9e",
        },
    };

    private static JsonRpcMessage[] Conversation() =>
    [
        new JsonRpcRequest { RequestId = new RequestId(1), Method = "initialize", Arguments = new Dictionary<string, object?> { ["processId"] = 7 } },
        RunRequest(),
        new JsonRpcRequest { Method = "exit", Arguments = new Dictionary<string, object?>() },
        new JsonRpcResult { RequestId = new RequestId(9), Result = "done" },
    ];

    [Fact]
    public Task A_conversation_is_written_through_one_buffer_kept_for_it_as_a_pipe_writes_it() => Cases.AllAsync(
        ("a conversation is written as a pipe writes it and a large request rents no buffer of its length", A_conversation_is_written_as_a_pipe_writes_it_and_a_large_request_rents_no_buffer_of_its_length),
        ("a long string is written as StreamJsonRpc's formatter writes it, in parts renting no buffer of its length", A_long_string_is_written_as_StreamJsonRpcs_formatter_writes_it_in_parts_renting_no_buffer_of_its_length),
        ("a writer ended writes no more", A_writer_ended_writes_no_more));

    private async Task A_conversation_is_written_as_a_pipe_writes_it_and_a_large_request_rents_no_buffer_of_its_length()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using MemoryStream piped = new();
        using MemoryStream kept = new();
        (byte[] Written, int LargeRents) before = await WrittenAsync(PipeWriter.Create(piped, new StreamPipeWriterOptions(leaveOpen: true)), piped, Conversation(), new EnvelopeFormatter(JsonRpcConversations.Web), cancel);
        (byte[] Written, int LargeRents) now = await WrittenAsync(new ContiguousWriter(kept), kept, Conversation(), new EnvelopeFormatter(JsonRpcConversations.Web), cancel);

        Assert.True(before.Written.Length > 250_000, $"The conversation wrote {before.Written.Length:N0} bytes, too few for a run's request of 2,000 tests");
        Assert.Equal(Encoding.UTF8.GetString(before.Written), Encoding.UTF8.GetString(now.Written));
        Assert.True(before.LargeRents > 0, "A pipe's writer took no array of the request's length from the shared pool, so this test cannot see one taken");
        Assert.True(now.LargeRents == 0, $"Writing the conversation took {now.LargeRents} arrays of a request's length from the shared pool, which a thread other than the one that gave each back misses, allocating it anew");
    }

    /// <summary>A long string, as a project's design-time builds are a string of JSON a language server is answered with when
    /// it asks for them (about 1.4 million characters, with quotes, a tag, a tab, an accent and an emoji cut where a part
    /// ends), comes out as StreamJsonRpc's own formatter writes it, taking no array of its length from the shared pool:
    /// written whole, a string took one six times its length to be escaped into and grew the message's by as much.</summary>
    private async Task A_long_string_is_written_as_StreamJsonRpcs_formatter_writes_it_in_parts_renting_no_buffer_of_its_length()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string infos = ("[" + string.Join(',', Enumerable.Range(0, 8_000).Select(i =>
            $$"""{"filePath":"/src/Area{{i}}/Project{{i}}.csproj","commandLineArgs":["/reference:<tag> & 'more'.dll","/define:DEBUG{{'\t'}}TRACE é"]}""")) + "]").Insert(1023, "🚀");
        JsonRpcMessage[] answer = [new JsonRpcResult { RequestId = new RequestId(3), Result = new Dictionary<string, object?> { ["infos"] = infos, ["problem"] = null } }];
        using MemoryStream theirs = new();
        using MemoryStream ours = new();
        (byte[] Written, int LargeRents) before = await WrittenAsync(new ContiguousWriter(theirs), theirs, answer, new SystemTextJsonFormatter { JsonSerializerOptions = JsonRpcConversations.Web }, cancel);
        (byte[] Written, int LargeRents) now = await WrittenAsync(new ContiguousWriter(ours), ours, answer, new EnvelopeFormatter(JsonRpcConversations.Web), cancel);

        Assert.True(Encoding.UTF8.GetString(before.Written) == Encoding.UTF8.GetString(now.Written), "A long string came out other than StreamJsonRpc's own formatter writes it, so the server reads other design-time builds");
        Assert.True(before.LargeRents > 0, "StreamJsonRpc's formatter took no array of the string's length from the shared pool, so this test cannot see one taken");
        Assert.True(now.LargeRents == 0, $"Writing a long string took {now.LargeRents} arrays of its length from the shared pool, fresh wherever the writing thread finds none, for every project's builds a server is given");
    }

    private async Task A_writer_ended_writes_no_more()
    {
        ContiguousWriter writer = new(Stream.Null);
        writer.Complete();

        Assert.Throws<ObjectDisposedException>(() => writer.GetSpan(1));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await writer.FlushAsync(TestContext.Current.CancellationToken));
    }

    private static async Task WriteAsync(ContiguousWriter writer, byte[] message, CancellationToken cancel)
    {
        message.CopyTo(writer.GetSpan(message.Length));
        writer.Advance(message.Length);
        await writer.FlushAsync(cancel);
        writer.Complete();
    }

    /// <summary>What messages come to through a writer and formatter, and how many arrays of 128 K elements or more were
    /// rented from the shared pool as they were written.</summary>
    private static async Task<(byte[] Written, int LargeRents)> WrittenAsync(PipeWriter writer, MemoryStream stream, JsonRpcMessage[] messages,
        IJsonRpcMessageFormatter formatter, CancellationToken cancel)
    {
        HeaderDelimitedMessageHandler handler = new(writer, null, formatter);
        using PoolRents rents = new(128 * 1024);
        try
        {
            foreach (JsonRpcMessage message in messages)
            {
                rents.Counting = true;
                await handler.WriteAsync(message, cancel);
                rents.Counting = false;
            }
        }
        finally
        {
            rents.Counting = false;
            await handler.DisposeAsync();
        }
        return (stream.ToArray(), rents.Count);
    }
}
