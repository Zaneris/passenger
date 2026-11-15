namespace Passenger.Core;

/// <summary>A stream read whole but never past the most its reader takes, however long it says it is or runs on: a file, a
/// zip's entry or an answer read a chunk at a time, given up the moment it would hold more.</summary>
public static class Streams
{
    private const int Chunk = 81920;

    /// <summary>Its bytes to its end, or null where it holds more than <paramref name="most"/>.</summary>
    public static byte[]? Bounded(Stream stream, long most)
    {
        using MemoryStream read = new();
        byte[] chunk = new byte[Chunk];
        int got;
        while ((got = stream.Read(chunk)) > 0)
        {
            if (read.Length + got > most)
            {
                return null;
            }
            read.Write(chunk, 0, got);
        }
        return read.ToArray();
    }

    /// <summary>As <see cref="Bounded"/>, read without holding a thread while it waits.</summary>
    public static async Task<byte[]?> BoundedAsync(Stream stream, long most, CancellationToken cancel)
    {
        using MemoryStream read = new();
        byte[] chunk = new byte[Chunk];
        int got;
        while ((got = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false)) > 0)
        {
            if (read.Length + got > most)
            {
                return null;
            }
            read.Write(chunk, 0, got);
        }
        return read.ToArray();
    }
}
