using System.Text;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>The header before each message the IDE and its language servers and debugger send each other: the body's length
/// read from it as the other end wrote it, however its lines are spaced or cased, a length past what a reader takes read as
/// itself so the reader refuses it by its size, and the header written for a body read back as that body's length.</summary>
public class ContentLengthHeaderTests
{
    [Theory]
    [InlineData("Content-Length: 42", 42L)]
    [InlineData("content-LENGTH:7", 7L)]
    [InlineData("  Content-Length  :   19  ", 19L)]
    [InlineData("Content-Type: application/vscode-jsonrpc; charset=utf-8\r\nContent-Length: 5\r\n\r\n", 5L)]
    [InlineData("Content-Length: 0\r\n", 0L)]
    [InlineData("Content-Length: 3000000000", 3_000_000_000L)]
    [InlineData("Content-Length: 9223372036854775807", long.MaxValue)]
    [InlineData("Content-Length: 99999999999999999999", long.MaxValue)]
    [InlineData("Content-Length: 3\r\nContent-Length: 4", 3L)]
    [InlineData("Content-Length: three\r\nContent-Length: 4", 4L)]
    public void A_headers_length_is_its_first_line_of_digits_alone_as_a_long(string header, long length) =>
        Assert.Equal(length, ContentLengthHeader.Read(Encoding.ASCII.GetBytes(header)));

    [Theory]
    [InlineData("")]
    [InlineData("Content-Type: text/plain\r\n\r\n")]
    [InlineData("Content-Length:")]
    [InlineData("Content-Length: twelve")]
    [InlineData("Content-Length: -1")]
    [InlineData("Content-Length: +5")]
    [InlineData("Content-Length: 1 2")]
    [InlineData("Content-Length 12")]
    [InlineData("X-Content-Length: 12")]
    public void A_header_with_no_line_of_digits_alone_for_its_length_gives_none(string header) =>
        Assert.Null(ContentLengthHeader.Read(Encoding.ASCII.GetBytes(header)));

    [Theory]
    [InlineData(0L)]
    [InlineData(42L)]
    [InlineData(2_147_483_648L)]
    [InlineData(long.MaxValue)]
    public void The_header_written_for_a_body_says_its_length_ends_in_a_blank_line_and_reads_back_as_it(long length)
    {
        Span<byte> header = stackalloc byte[ContentLengthHeader.Longest];
        int written = ContentLengthHeader.Write(length, header);
        Assert.Equal($"Content-Length: {length}\r\n\r\n", Encoding.ASCII.GetString(header[..written]));
        Assert.True(header[..written].EndsWith(ContentLengthHeader.End), "The header written does not end in its blank line");
        Assert.Equal(length, ContentLengthHeader.Read(header[..written]));
    }
}
