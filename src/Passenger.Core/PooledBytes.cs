using System.Buffers;

namespace Passenger.Core;

/// <summary>A message's bytes as they are written, in an array from the shared pool, grown by renting a larger one; a large
/// message's is given back for one of the usual size before the next is written. Framework-free, since it is linked into
/// the project host.</summary>
public sealed class PooledBytes : IBufferWriter<byte>
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

    public void Clear()
    {
        count = 0;
        if (array.Length > Usual)
        {
            ArrayPool<byte>.Shared.Return(array);
            array = ArrayPool<byte>.Shared.Rent(Usual);
        }
    }

    private void Ensure(int sizeHint)
    {
        int needed = count + Math.Max(sizeHint, 1);
        if (needed > array.Length)
        {
            byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, array.Length * 2));
            array.AsSpan(0, count).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(array);
            array = larger;
        }
    }
}
