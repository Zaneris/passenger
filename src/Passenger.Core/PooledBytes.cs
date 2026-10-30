using System.Buffers;

namespace Passenger.Core;

/// <summary>A message's bytes as they are written, or a stream's as they are read, in an array from the shared pool, grown
/// by renting a larger one; a large message's is given back for one of the usual size before the next is written, and the
/// array goes back to the pool once disposed. Framework-free, since it is linked into the project host.</summary>
public sealed class PooledBytes : IBufferWriter<byte>, IDisposable
{
    private const int Usual = 64 * 1024;
    private byte[] array = ArrayPool<byte>.Shared.Rent(Usual);
    private int count;

    public ReadOnlySpan<byte> Written => array.AsSpan(0, count);

    public void Advance(int written) => count += written;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return array.AsMemory(count);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return array.AsSpan(count);
    }

    /// <summary>Writes what a stream holds, read to its end, after what is written.</summary>
    public async Task ReadToEndAsync(Stream stream)
    {
        int read;
        while ((read = await stream.ReadAsync(GetMemory())) > 0)
        {
            Advance(read);
        }
    }

    public void Clear()
    {
        count = 0;
        if (array.Length > Usual)
        {
            ArrayPool<byte>.Shared.Return(array);
            array = ArrayPool<byte>.Shared.Rent(Usual);
        }
    }

    /// <summary>Gives the array back to the pool, what was written with it; written again, it rents another.</summary>
    public void Dispose()
    {
        if (array.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(array);
        }
        array = [];
        count = 0;
    }

    private void Ensure(int sizeHint)
    {
        int needed = count + Math.Max(sizeHint, 1);
        if (needed > array.Length)
        {
            byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, array.Length * 2));
            array.AsSpan(0, count).CopyTo(larger);
            if (array.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(array);
            }
            array = larger;
        }
    }
}
