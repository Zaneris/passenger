using System.Text.Json;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>The pooled buffer a message is written into before it is sent: every byte written read back in order, however
/// large the message grows it, and nothing of one message left in the next.</summary>
public class PooledBytesTests
{
    [Fact]
    public void Bytes_written_in_pieces_read_back_in_order_as_the_buffer_grows_past_its_usual_size()
    {
        PooledBytes bytes = new();
        byte[] expected = new byte[300_000];
        new Random(5).NextBytes(expected);
        int at = 0;
        Random sizes = new(6);
        while (at < expected.Length)
        {
            int piece = Math.Min(expected.Length - at, sizes.Next(1, 20_000));
            Span<byte> span = sizes.Next(2) == 0 ? bytes.GetSpan(piece) : bytes.GetMemory(piece).Span;
            Assert.True(span.Length >= piece, $"Asked for room for {piece} bytes, the buffer gave {span.Length}");
            expected.AsSpan(at, piece).CopyTo(span);
            bytes.Advance(piece);
            at += piece;
        }
        Assert.True(expected.AsSpan().SequenceEqual(bytes.Written), "The bytes read back are not those written");
    }

    [Fact]
    public void Room_asked_with_no_size_is_at_least_a_byte_even_when_the_buffer_is_full()
    {
        PooledBytes bytes = new();
        int filled = bytes.GetSpan().Length;
        bytes.Advance(filled);
        Assert.NotEqual(0, bytes.GetSpan().Length);
        Assert.Equal(filled, bytes.Written.Length);
    }

    [Fact]
    public void A_cleared_buffer_holds_nothing_of_the_message_before_it_large_or_small()
    {
        PooledBytes bytes = new();
        bytes.GetSpan(1_000_000).Fill(1);
        bytes.Advance(1_000_000);
        bytes.Clear();
        Assert.Equal(0, bytes.Written.Length);
        bytes.GetSpan(3)[..3].Fill(2);
        bytes.Advance(3);
        Assert.Equal([2, 2, 2], bytes.Written.ToArray());
        bytes.Clear();
        Assert.Equal(0, bytes.Written.Length);
    }

    [Fact]
    public void A_large_JSON_message_written_through_it_is_the_JSON_the_serializer_makes()
    {
        Dictionary<string, string> message = Enumerable.Range(0, 20_000).ToDictionary(i => $"key{i}", i => $"value {i} é\U0001F600");
        PooledBytes bytes = new();
        using (Utf8JsonWriter writer = new(bytes))
        {
            JsonSerializer.Serialize(writer, message);
        }
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(message).AsSpan().SequenceEqual(bytes.Written), "The JSON written through the buffer differs from the serializer's");
    }
}
