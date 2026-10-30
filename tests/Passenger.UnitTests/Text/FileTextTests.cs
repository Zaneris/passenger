using System.Text;
using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>A file's bytes read by the byte-order mark they start with, and the bytes a save writes of what was read.</summary>
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
}
