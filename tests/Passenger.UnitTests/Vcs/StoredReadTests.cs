using System.Text;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>A revision's file read as git writes it, in pieces, beside the file on disk: a binary file's bytes counted and
/// compared through two buffers however large it is, never held, and two text files read no further than their probes.</summary>
public class StoredReadTests
{
    private const int Large = 20 * 1024 * 1024;

    [Fact]
    public async Task A_large_binary_file_is_compared_and_counted_through_buffers_never_held_whole()
    {
        byte[] stored = Binary(Large, seed: 1);
        byte[] changed = (byte[])stored.Clone();
        changed[^1] ^= 1;
        byte[] grown = [.. stored, 7];
        await Measured(Piecewise(stored), Piecewise(stored), StoredRead.CompareAsync);

        (BinaryChange? same, long sameBytes) = await Measured(Piecewise(stored), new MemoryStream(stored), StoredRead.CompareAsync);
        (BinaryChange? last, long lastBytes) = await Measured(Piecewise(stored), Piecewise(changed), StoredRead.CompareAsync);
        (BinaryChange? longer, long longerBytes) = await Measured(Piecewise(stored), Piecewise(grown), StoredRead.CompareAsync);

        Assert.Equal(new BinaryChange(Large, Large, true), same);
        Assert.Equal(new BinaryChange(Large, Large, false), last);
        Assert.Equal(new BinaryChange(Large, Large + 1, false), longer);
        long most = Math.Max(sameBytes, Math.Max(lastBytes, longerBytes));
        Assert.True(most < 256 * 1024, $"Comparing two {Large}-byte binary files allocated {most} bytes, so a large asset's diff costs its size");
    }

    [Fact]
    public async Task A_binary_file_new_or_deleted_is_counted_and_two_text_files_are_read_no_further_than_their_probes()
    {
        byte[] asset = Binary(100_000, seed: 2);
        Assert.Equal(new BinaryChange(null, asset.Length, false), await StoredRead.CompareAsync(null, Piecewise(asset)));
        Assert.Equal(new BinaryChange(asset.Length, null, false), await StoredRead.CompareAsync(Piecewise(asset), null));
        Assert.Equal(new BinaryChange(asset.Length, 5, false), await StoredRead.CompareAsync(Piecewise(asset), new MemoryStream("text\n"u8.ToArray())));

        byte[] text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("a line of text\n", 10_000)));
        ChunkedStream before = Piecewise(text);
        ChunkedStream after = Piecewise([.. text, (byte)'!']);
        Assert.Null(await StoredRead.CompareAsync(before, after));
        Assert.True(before.Position <= FileText.BinaryProbe && after.Position <= FileText.BinaryProbe,
            $"Two text files were read to {before.Position} and {after.Position} bytes to find neither binary, past the probe's {FileText.BinaryProbe}");
        Assert.Null(await StoredRead.CompareAsync(null, Piecewise(text)));
    }

    [Fact]
    public async Task A_revisions_text_is_read_whole_and_a_binary_files_bytes_only_counted()
    {
        byte[] text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("café au lait\n", 30_000)));
        StoredBytes read = await StoredRead.ReadAsync(Piecewise(text));
        Assert.True(read.Text is { } bytes && bytes.AsSpan().SequenceEqual(text), $"The text read back as {read.Text?.Length} bytes of {text.Length}");
        byte[] wide = [0xFF, 0xFE, (byte)'a', 0, (byte)'b', 0];
        Assert.Equal(wide, (await StoredRead.ReadAsync(Piecewise(wide))).Text);

        byte[] asset = Binary(Large, seed: 3);
        await Measured(Piecewise(asset), null, (stored, _) => StoredRead.ReadAsync(stored));
        (StoredBytes binary, long allocated) = await Measured(Piecewise(asset), null, (stored, _) => StoredRead.ReadAsync(stored));
        Assert.Equal(new StoredBytes(null, Large), binary);
        Assert.True(allocated < 128 * 1024, $"Reading a {Large}-byte binary file allocated {allocated} bytes, so a large asset's version costs its size");
    }

    /// <summary>Bytes with a NUL in their first, each run the same for its seed.</summary>
    private static byte[] Binary(int length, int seed)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        bytes[10] = 0;
        return bytes;
    }

    /// <summary>Bytes as a pipe gives them, in uneven pieces.</summary>
    private static ChunkedStream Piecewise(byte[] bytes) => new(bytes, read => 1 + (read * 7919 % 50_000));

    /// <summary>A read's answer and what this thread allocated for it: the streams answer at once, so it runs here whole.</summary>
    private static async Task<(T Answer, long Allocated)> Measured<T>(Stream stored, Stream? disk, Func<Stream, Stream?, Task<T>> read)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Task<T> work = read(stored, disk);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(work.IsCompleted, "The read waited on streams that answer at once");
        return (await work, allocated);
    }
}
