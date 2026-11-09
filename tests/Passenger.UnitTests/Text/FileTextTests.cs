using System.Text;
using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>A file's bytes read by the byte-order mark they start with, and the bytes a save writes of what was read; a
/// file on disk read into one array of its length, whole or on after its first bytes.</summary>
public sealed class FileTextTests
{
    private const string Code = "class Café\r\n{\r\n    // 🚀 ü\r\n}\r\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_UTF_32_file_with_its_mark_reads_as_its_text_and_saves_back_byte_for_byte(bool bigEndian)
    {
        byte[] bytes = [.. new UTF32Encoding(bigEndian, byteOrderMark: true).GetPreamble(), .. new UTF32Encoding(bigEndian, byteOrderMark: false).GetBytes(Code)];

        FileText? read = FileText.FromBytes(bytes);

        Assert.True(read is not null, "A UTF-32 file was taken for binary, shown as bytes and never editable");
        Assert.True(read.Text == Code, $"A UTF-32 file read as {read.EncodingName} showed '{read.Text}', its characters wrong");
        Assert.Equal(bigEndian ? "UTF-32 BE" : "UTF-32 LE", read.EncodingName);
        Assert.True(FileText.Encoded(read.Text, read.Encoding, read.HasBom, read.CrBreaks).AsSpan().SequenceEqual(bytes), $"Saving a UTF-32 file read as {read.EncodingName} wrote other bytes, corrupting it");
    }

    [Fact]
    public void A_UTF_16_file_with_its_mark_still_reads_as_UTF_16()
    {
        byte[] bytes = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Code)];

        FileText? read = FileText.FromBytes(bytes);

        Assert.True(read is not null, "A UTF-16 LE file was taken for binary");
        Assert.True(read.Text == Code, $"A UTF-16 LE file read as {read.EncodingName} showed '{read.Text}', not its text");
        Assert.Equal("UTF-16 LE", read.EncodingName);
        Assert.True(FileText.Encoded(read.Text, read.Encoding, read.HasBom, read.CrBreaks).AsSpan().SequenceEqual(bytes), "Saving a UTF-16 LE file wrote other bytes");
    }

    /// <summary>A large file and an empty one, read as an open file is: each into one array of its length, so a file opened,
    /// changed on disk or saved costs its bytes once rather than the copies a growing buffer makes.</summary>
    [Fact]
    public void A_file_is_read_into_one_array_of_its_length()
    {
        string folder = Directory.CreateTempSubdirectory("passenger-unit-").FullName;
        try
        {
            byte[] large = [.. Enumerable.Range(0, 2_000_000).Select(i => (byte)(i % 64 == 63 ? '\n' : 'a' + i % 26))];
            string path = Path.Join(folder, "Large.cs");
            string empty = Path.Join(folder, "Empty.cs");
            File.WriteAllBytes(path, large);
            File.WriteAllBytes(empty, []);
            Assert.Empty(FileText.Read(empty));
            FileText.Read(path);

            long before = GC.GetAllocatedBytesForCurrentThread();
            byte[] read = FileText.Read(path);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(read.AsSpan().SequenceEqual(large), "A file read back other than it was written");
            Assert.True(allocated <= large.Length + 4 * 1024,
                $"Reading a {large.Length:N0}-byte file allocated {allocated:N0} bytes, so every file opened or changed on disk costs its size again and again");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The rest of a file after its first bytes were probed, as Local History reads what it keeps: in one array of
    /// the file's length, the probed bytes first; and from a stream of no length, as it comes.</summary>
    [Fact]
    public void A_file_read_on_after_its_probe_is_read_into_one_array_of_its_length()
    {
        byte[] file = [.. Enumerable.Range(0, 300_000).Select(i => (byte)('a' + i % 26))];
        using MemoryStream warm = new(file);
        warm.ReadAtLeast(new byte[FileText.BinaryProbe], FileText.BinaryProbe);
        FileText.ReadRest(warm, file.AsSpan(0, FileText.BinaryProbe));

        using MemoryStream stream = new(file);
        byte[] probe = new byte[FileText.BinaryProbe];
        stream.ReadAtLeast(probe, probe.Length);
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] read = FileText.ReadRest(stream, probe);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(read.AsSpan().SequenceEqual(file), "A file read on after its probe came back other than it is");
        Assert.True(allocated <= file.Length + 1024,
            $"Reading a {file.Length:N0}-byte file on after its probe allocated {allocated:N0} bytes, so each file Local History keeps costs its size again and again");
        Assert.Equal(file, FileText.ReadRest(new Unsized(file.AsSpan(4).ToArray()), file.AsSpan(0, 4)));
    }

    /// <summary>A stream that says nothing of its length, as a pipe's does.</summary>
    private sealed class Unsized(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
