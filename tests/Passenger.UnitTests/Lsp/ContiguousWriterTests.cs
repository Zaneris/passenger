using System.IO.Pipelines;
using System.Text;
using Passenger.Lsp;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Passenger.UnitTests.Lsp;

/// <summary>A test host's conversation written through one buffer kept for it: every message comes out as a pipe writes it,
/// a request of thousands of tests takes no array of its length from the shared pool, which a thread other than the one
/// that gave it back misses, and a later conversation writes into the buffer the first one grew.</summary>
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
    public async Task A_conversation_is_written_as_a_pipe_writes_it_and_a_large_request_rents_no_buffer_of_its_length()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using MemoryStream piped = new();
        using MemoryStream kept = new();
        (byte[] Written, int LargeRents) before = await WrittenAsync(PipeWriter.Create(piped, new StreamPipeWriterOptions(leaveOpen: true)), piped, cancel);
        (byte[] Written, int LargeRents) now = await WrittenAsync(new ContiguousWriter(kept), kept, cancel);

        Assert.True(before.Written.Length > 250_000, $"The conversation wrote {before.Written.Length:N0} bytes, too few for a run's request of 2,000 tests");
        Assert.Equal(Encoding.UTF8.GetString(before.Written), Encoding.UTF8.GetString(now.Written));
        Assert.True(before.LargeRents > 0, "A pipe's writer took no array of the request's length from the shared pool, so this test cannot see one taken");
        Assert.True(now.LargeRents == 0, $"Writing the conversation took {now.LargeRents} arrays of a request's length from the shared pool, which a thread other than the one that gave each back misses, allocating it anew");
    }

    [Fact]
    public async Task A_later_conversation_writes_its_request_into_the_buffer_the_first_one_grew()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        byte[] request = new byte[300_000];
        await WriteAsync(new ContiguousWriter(Stream.Null), request, cancel);

        long before = GC.GetAllocatedBytesForCurrentThread();
        await WriteAsync(new ContiguousWriter(Stream.Null), request, cancel);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 1024, $"A second conversation's {request.Length:N0}-byte request allocated {allocated:N0} bytes: each run's request would grow a buffer anew");
    }

    [Fact]
    public async Task A_writer_ended_writes_no_more()
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

    /// <summary>What a conversation's messages come to through a writer, and how many arrays of 128 KB or more were rented
    /// from the shared pool as they were written.</summary>
    private static async Task<(byte[] Written, int LargeRents)> WrittenAsync(PipeWriter writer, MemoryStream stream, CancellationToken cancel)
    {
        HeaderDelimitedMessageHandler handler = new(writer, null, new EnvelopeFormatter(JsonRpcConversations.Web));
        using PoolRents rents = new(128 * 1024);
        try
        {
            foreach (JsonRpcMessage message in Conversation())
            {
                PoolRents.Counting = true;
                await handler.WriteAsync(message, cancel);
                PoolRents.Counting = false;
            }
        }
        finally
        {
            PoolRents.Counting = false;
            await handler.DisposeAsync();
        }
        return (stream.ToArray(), rents.Count);
    }
}
