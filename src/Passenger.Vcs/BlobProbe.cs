using System.Buffers;
using System.Globalization;
using System.Text;
using Passenger.Text;

namespace Passenger.Vcs;

/// <summary>Whether blobs are binary, read from git cat-file --batch's answer for them as it comes: of each blob only its
/// first <see cref="FileText.BinaryProbe"/> bytes are kept and looked at, the rest read past, so a large asset costs a
/// probe's bytes rather than its size.</summary>
internal static class BlobProbe
{
    /// <summary>How much of git's answer is read at a time, which bounds a header line ("&lt;object&gt; blob
    /// &lt;size&gt;") far past a hash and a size.</summary>
    private const int ReadSize = 64 * 1024;

    /// <summary>Each blob's answer in turn, as <paramref name="looksBinary"/> finds its first bytes, a missing object (a
    /// submodule's commit) not binary; from an answer that is none (a header not a blob's, the output cut short) on,
    /// the blobs have no answer.</summary>
    public static async Task<Dictionary<string, bool>> ReadAsync(Stream output, IReadOnlyList<string> blobs, Func<ReadOnlySpan<byte>, bool> looksBinary)
    {
        Dictionary<string, bool> found = new(StringComparer.Ordinal);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadSize);
        byte[] kept = ArrayPool<byte>.Shared.Rent(FileText.BinaryProbe);
        try
        {
            BatchReader reader = new(output, buffer);
            foreach (string blob in blobs)
            {
                string[] header = await reader.LineAsync() is { } line ? line.Split(' ') : [];
                if (header is [_, "missing"])
                {
                    found[blob] = false;
                    continue;
                }
                if (header is not [_, "blob", { } length] || !long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out long size))
                {
                    break;
                }
                int probe = (int)Math.Min(size, FileText.BinaryProbe);
                if (!await reader.TakeAsync(kept.AsMemory(0, probe)))
                {
                    break;
                }
                bool binary = looksBinary(kept.AsSpan(0, probe));
                // The rest of its bytes and the line break after them.
                if (!await reader.SkipAsync(size - probe + 1))
                {
                    break;
                }
                found[blob] = binary;
            }
            return found;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(kept);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>A stream's bytes read through one buffer: a line, so many bytes copied out, or so many passed over.</summary>
    private sealed class BatchReader(Stream stream, byte[] buffer)
    {
        private int start;
        private int end;

        private int Unread => end - start;

        /// <summary>The next line in ASCII, its line feed left out; null where the output ends first or the line outruns
        /// the buffer.</summary>
        public async Task<string?> LineAsync()
        {
            int seen = 0;
            while (true)
            {
                int feed = buffer.AsSpan(start + seen, Unread - seen).IndexOf((byte)'\n');
                if (feed >= 0)
                {
                    string line = Encoding.ASCII.GetString(buffer, start, seen + feed);
                    start += seen + feed + 1;
                    return line;
                }
                seen = Unread;
                if (!await FillAsync())
                {
                    return null;
                }
            }
        }

        /// <summary>Fills <paramref name="into"/> with the next bytes; false where the output ends first.</summary>
        public async Task<bool> TakeAsync(Memory<byte> into)
        {
            int taken = 0;
            while (taken < into.Length)
            {
                if (Unread == 0 && !await FillAsync())
                {
                    return false;
                }
                int count = Math.Min(Unread, into.Length - taken);
                buffer.AsMemory(start, count).CopyTo(into[taken..]);
                start += count;
                taken += count;
            }
            return true;
        }

        /// <summary>Passes over the next bytes, keeping none; false where the output ends first.</summary>
        public async Task<bool> SkipAsync(long count)
        {
            while (count > 0)
            {
                if (Unread == 0 && !await FillAsync())
                {
                    return false;
                }
                int passed = (int)Math.Min(Unread, count);
                start += passed;
                count -= passed;
            }
            return true;
        }

        /// <summary>Reads more after the bytes unread, moved to the buffer's start first; false at the output's end or
        /// with the buffer full.</summary>
        private async Task<bool> FillAsync()
        {
            if (start > 0)
            {
                buffer.AsSpan(start, Unread).CopyTo(buffer);
                end -= start;
                start = 0;
            }
            if (end == buffer.Length)
            {
                return false;
            }
            int read = await stream.ReadAsync(buffer.AsMemory(end));
            end += read;
            return read > 0;
        }
    }
}
