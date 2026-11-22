using System.Text;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>The header before each message the IDE and its language servers and debugger send each other: the body's length
/// read from it as the other end wrote it, however its lines are spaced or cased, a length past what a reader takes read as
/// itself so the reader refuses it by its size, and the header written for a body read back as that body's length.</summary>
public class ContentLengthHeaderTests
{
    [Fact]
    public void A_messages_length_is_read_from_its_header_as_the_other_end_wrote_it_and_written_back_as_read() => Cases.All(
        ("A headers length is its first line of digits alone as a long", A_headers_length_is_its_first_line_of_digits_alone_as_a_long),
        ("A header with no line of digits alone for its length gives none", A_header_with_no_line_of_digits_alone_for_its_length_gives_none),
        ("The header written for a body says its length ends in a blank line and reads back as it", The_header_written_for_a_body_says_its_length_ends_in_a_blank_line_and_reads_back_as_it));

    private void A_headers_length_is_its_first_line_of_digits_alone_as_a_long() => Cases.Each<(string header, long length)>(
    [
        ("Content-Length: 42", 42L),
        ("content-LENGTH:7", 7L),
        ("  Content-Length  :   19  ", 19L),
        ("Content-Type: application/vscode-jsonrpc; charset=utf-8\r\nContent-Length: 5\r\n\r\n", 5L),
        ("Content-Length: 0\r\n", 0L),
        ("Content-Length: 3000000000", 3_000_000_000L),
        ("Content-Length: 9223372036854775807", long.MaxValue),
        ("Content-Length: 99999999999999999999", long.MaxValue),
        ("Content-Length: 3\r\nContent-Length: 4", 3L),
        ("Content-Length: three\r\nContent-Length: 4", 4L),
    ], row =>
    {
        (string header, long length) = row;
        
                Assert.Equal(length, ContentLengthHeader.Read(Encoding.ASCII.GetBytes(header)));
    });

    private void A_header_with_no_line_of_digits_alone_for_its_length_gives_none() => Cases.Each<string>(
    [
        "",
        "Content-Type: text/plain\r\n\r\n",
        "Content-Length:",
        "Content-Length: twelve",
        "Content-Length: -1",
        "Content-Length: +5",
        "Content-Length: 1 2",
        "Content-Length 12",
        "X-Content-Length: 12",
    ], header =>
    {
        
                Assert.Null(ContentLengthHeader.Read(Encoding.ASCII.GetBytes(header)));
    });

    private void The_header_written_for_a_body_says_its_length_ends_in_a_blank_line_and_reads_back_as_it() => Cases.Each<long>(
    [
        0L,
        42L,
        2_147_483_648L,
        long.MaxValue,
    ], length =>
    {
        Span<byte> header = stackalloc byte[ContentLengthHeader.Longest];
        int written = ContentLengthHeader.Write(length, header);
        Assert.Equal($"Content-Length: {length}\r\n\r\n", Encoding.ASCII.GetString(header[..written]));
        Assert.True(header[..written].EndsWith(ContentLengthHeader.End), "The header written does not end in its blank line");
        Assert.Equal(length, ContentLengthHeader.Read(header[..written]));
    });
}
