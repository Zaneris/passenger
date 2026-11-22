using System.Text;
using Passenger.Core;
using Passenger.Lsp;

namespace Passenger.UnitTests.Lsp;

/// <summary>A language server's messages as the protocol trace sees them cross: each passed through unchanged and traced
/// whole once its last byte has, however the pipe cuts it, and counted; a message longer than the connection reads traced
/// by its size as its header crosses, the frames still followed; only what is no frame ending the trace of its stream.</summary>
public class TracedStreamTests
{
    private const int Server = 4321;

    [Fact]
    public Task A_servers_messages_pass_unchanged_and_are_traced_whole_or_by_their_size_however_the_pipe_cuts_them() => Cases.AllAsync(
        ("messages read in any pieces pass unchanged and are traced whole and counted", () => Cases.EachAsync<int>(
        [
            1,
            100_000,
        ], Messages_read_in_any_pieces_pass_unchanged_and_are_traced_whole_and_counted)),
        ("a message over the most the connection reads is traced by its size and the frames still followed", A_message_over_the_most_the_connection_reads_is_traced_by_its_size_and_the_frames_still_followed),
        ("a frame with no length ends the trace of its stream and its bytes still pass", A_frame_with_no_length_ends_the_trace_of_its_stream_and_its_bytes_still_pass));

    private async Task Messages_read_in_any_pieces_pass_unchanged_and_are_traced_whole_and_counted(int chunk)
    {
        byte[] sent = Concat(Framed("""{"id":1}"""), Framed("""{"method":"café ☕"}"""), Framed(string.Empty));
        (byte[] read, List<string> traced, TracedStream stream) = await ReadAllAsync(sent, chunk);
        Assert.Equal(sent, read);
        Assert.Equal(["<-- {\"id\":1}", "<-- {\"method\":\"café ☕\"}", "<-- "], traced);
        Assert.Equal(3, stream.Crossed);
    }

    /// <summary>A length past an int's, which the connection never reads: the trace says the message's size as its header
    /// crosses and goes on following the frames, where it once took the length for none and traced nothing more.</summary>
    private async Task A_message_over_the_most_the_connection_reads_is_traced_by_its_size_and_the_frames_still_followed()
    {
        byte[] sent = Concat(Encoding.ASCII.GetBytes("Content-Length: 3000000000\r\n\r\n"), Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","result":["""));
        (byte[] read, List<string> traced, TracedStream stream) = await ReadAllAsync(sent, 5);
        Assert.Equal(sent, read);
        Assert.Equal(["<-- a message of 3,000,000,000 bytes, over the 2,147,483,647 the connection reads of one"], traced);
        Assert.Equal(0, stream.Crossed);
    }

    private async Task A_frame_with_no_length_ends_the_trace_of_its_stream_and_its_bytes_still_pass()
    {
        byte[] sent = Concat(Framed("{}"), Encoding.ASCII.GetBytes("Content-Type: text/plain\r\n\r\n{}"), Framed("""{"after":true}"""));
        (byte[] read, List<string> traced, TracedStream stream) = await ReadAllAsync(sent, 3);
        Assert.Equal(sent, read);
        Assert.Equal(["<-- {}"], traced);
        Assert.Equal(long.MaxValue, stream.Crossed);
    }

    /// <summary>Everything a server's output gives read through the trace in reads of at most <paramref name="chunk"/>
    /// bytes, with what the trace was handed.</summary>
    private static async Task<(byte[] Read, List<string> Traced, TracedStream Stream)> ReadAllAsync(byte[] sent, int chunk)
    {
        List<string> traced = [];
        TracedStream stream = new(new ChunkedStream(sent, chunk), Server, "<--", () => (server, message) =>
        {
            Assert.Equal(Server, server);
            traced.Add(message);
        });
        MemoryStream read = new();
        await stream.CopyToAsync(read, 4_096, TestContext.Current.CancellationToken);
        return (read.ToArray(), traced, stream);
    }

    private static byte[] Framed(string body) => Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];
}
