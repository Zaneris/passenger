using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A peer's lines of JSON read from the UTF-8 bytes they came in: each message whole however the stream cuts it,
/// an empty line or null passed over, one that does not read handed on as its text, and a large message (a picture of a
/// window) costing what it is read as, never its text made a string first.</summary>
public partial class JsonLinesTests
{
    [Fact]
    public async Task Messages_are_read_whole_from_the_pieces_a_stream_cuts_them_into()
    {
        byte[] said = Encoding.UTF8.GetBytes("{\"name\":\"café\",\"bytes\":\"AQID\"}\n\nnull\r\n{\"name\":\"two\"}\r\nnot json\n{\"name\":\"last\"}");
        List<string> unread = [];
        List<Said> read = [];
        await foreach (Said message in JsonLines.ReadAsync(new ChunkedStream(said, 3), SaidJson.Default.Said, (line, _) => unread.Add(line),
            TestContext.Current.CancellationToken))
        {
            read.Add(message);
        }
        Assert.Equal(["café", "two", "last"], read.Select(m => m.Name));
        Assert.Equal([1, 2, 3], read[0].Bytes ?? []);
        Assert.Equal(["not json"], unread);
    }

    /// <summary>The picture's line fits the reader's own buffer, so what the second message costs is the message alone,
    /// whatever the pool keeps; its length a multiple of 3, so its base 64 decodes to an array of its own size.</summary>
    [Fact]
    public async Task A_message_costs_what_it_is_read_as_and_never_its_text()
    {
        byte[] picture = new byte[39_999];
        new Random(1).NextBytes(picture);
        byte[] line = [.. JsonSerializer.SerializeToUtf8Bytes(new Said("window", picture), SaidJson.Default.Said), (byte)'\n'];
        long[] after = new long[2];
        Said[] read = new Said[2];
        int count = 0;
        await foreach (Said message in JsonLines.ReadAsync(new ChunkedStream([.. line, .. line], 16 * 1024), SaidJson.Default.Said, (_, _) => { },
            TestContext.Current.CancellationToken))
        {
            after[count] = GC.GetAllocatedBytesForCurrentThread();
            read[count++] = message;
        }

        Assert.True(read.All(m => picture.AsSpan().SequenceEqual(m.Bytes)), "A picture read back other than it was written");
        long allocated = after[1] - after[0];
        Assert.True(allocated < picture.Length + 8 * 1024,
            $"A message of {line.Length:N0} bytes holding a {picture.Length:N0}-byte picture allocated {allocated:N0} bytes, so each picture of a window costs its text again");
    }

    public sealed record Said(string Name, byte[]? Bytes = null);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(Said))]
    internal sealed partial class SaidJson : JsonSerializerContext;
}
