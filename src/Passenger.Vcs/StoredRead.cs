using System.Buffers;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Vcs;

/// <summary>A file's bytes read as they come (git's output, a file on disk) through buffers of the pool's: the first
/// <see cref="FileText.BinaryProbe"/> bytes say whether they are binary (<see cref="FileText.LooksBinary"/>), and a
/// binary file's are then only counted and compared, never held whole, so a large asset costs a probe and a buffer rather
/// than its size.</summary>
internal static class StoredRead
{
    /// <summary>How much of each side is read at a time once its probe is.</summary>
    private const int ReadSize = 64 * 1024;

    /// <summary>The bytes whole where they are text, else only how many there are.</summary>
    public static async Task<StoredBytes> ReadAsync(Stream stored)
    {
        using PooledBytes bytes = new();
        int probed = await FillAsync(stored, bytes.GetMemory(FileText.BinaryProbe)[..FileText.BinaryProbe]);
        bytes.Advance(probed);
        if (FileText.LooksBinary(bytes.Written))
        {
            return new StoredBytes(null, probed + await RestAsync(stored, bytes.GetMemory()));
        }
        await bytes.ReadToEndAsync(stored);
        return new StoredBytes(bytes.Written.ToArray(), bytes.Written.Length);
    }

    /// <summary>How <paramref name="after"/> differs from <paramref name="before"/> where either is binary, each null where
    /// there is no such file; null where neither is, each read no further than its probe.</summary>
    public static async Task<BinaryChange?> CompareAsync(Stream? before, Stream? after)
    {
        byte[] left = ArrayPool<byte>.Shared.Rent(ReadSize);
        byte[] right = ArrayPool<byte>.Shared.Rent(ReadSize);
        try
        {
            int was = before is null ? 0 : await FillAsync(before, left.AsMemory(0, FileText.BinaryProbe));
            int now = after is null ? 0 : await FillAsync(after, right.AsMemory(0, FileText.BinaryProbe));
            if (!(before is not null && FileText.LooksBinary(left.AsSpan(0, was))) && !(after is not null && FileText.LooksBinary(right.AsSpan(0, now))))
            {
                return null;
            }
            long beforeSize = was;
            long afterSize = now;
            bool same = before is not null && after is not null && left.AsSpan(0, was).SequenceEqual(right.AsSpan(0, now));
            bool more = was == FileText.BinaryProbe;
            // Both read in whole buffers, so the same bytes come in the same pieces until one side ends.
            while (same && more)
            {
                was = await FillAsync(before!, left.AsMemory(0, ReadSize));
                now = await FillAsync(after!, right.AsMemory(0, ReadSize));
                beforeSize += was;
                afterSize += now;
                same = left.AsSpan(0, was).SequenceEqual(right.AsSpan(0, now));
                more = was == ReadSize;
            }
            beforeSize += before is null ? 0 : await RestAsync(before, left);
            afterSize += after is null ? 0 : await RestAsync(after, right);
            return new BinaryChange(before is null ? null : beforeSize, after is null ? null : afterSize, same);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(right);
            ArrayPool<byte>.Shared.Return(left);
        }
    }

    /// <summary>Reads into <paramref name="into"/> until it is full or the stream ends, giving how much it read.</summary>
    private static async Task<int> FillAsync(Stream stream, Memory<byte> into)
    {
        int filled = 0;
        int read;
        while (filled < into.Length && (read = await stream.ReadAsync(into[filled..])) > 0)
        {
            filled += read;
        }
        return filled;
    }

    /// <summary>How many bytes are left, passed over through <paramref name="buffer"/>, or told by a stream that knows.</summary>
    private static async Task<long> RestAsync(Stream stream, Memory<byte> buffer)
    {
        if (stream.CanSeek)
        {
            long left = stream.Length - stream.Position;
            stream.Position = stream.Length;
            return left;
        }
        long count = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            count += read;
        }
        return count;
    }
}

/// <summary>A file's bytes as a revision has them, checked out: all of them where they are text, else only how many a
/// binary file has (<see cref="Text"/> null), never kept.</summary>
public sealed record StoredBytes(byte[]? Text, long Length)
{
    public bool IsBinary => Text is null;

    /// <summary>Bytes read whole, kept only where they are text.</summary>
    public static StoredBytes Of(byte[] bytes) => new(FileText.LooksBinary(bytes) ? null : bytes, bytes.Length);
}
