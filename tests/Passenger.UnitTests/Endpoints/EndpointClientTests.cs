using Passenger.App.Endpoints;

namespace Passenger.UnitTests.Endpoints;

/// <summary>A request of an .http file made ready to send: a multipart body sent with its framing's lines ended in CRLF, by
/// which a server finds its parts, where the file ends them as the editor does, each part's own lines as written; any other
/// body sent as written.</summary>
public sealed class EndpointClientTests
{
    private static EndpointCall Prepared(string contentType, string body)
    {
        KeyValuePair<string, string>[] headers = [new("Content-Type", contentType)];
        EndpointRequest request = new(Path.Combine(Path.GetTempPath(), "requests.http"), 1, string.Empty, "POST", "http://localhost:5123/upload", headers, body, []);
        (EndpointCall? call, string? problem) = EndpointClient.Prepare(request, request.Method, request.Url, headers, body, new Dictionary<string, string>(), null);
        Assert.True(call is not null, $"The request was not made ready: {problem}");
        return call;
    }

    [Fact]
    public void A_multipart_bodys_framing_is_sent_with_its_lines_ended_in_CRLF_and_any_other_body_as_written()
    {
        const string Parts = "--passenger\nContent-Disposition: form-data; name=\"note\"\n\nhello\n--passenger--\n";
        Assert.Equal("--passenger\r\nContent-Disposition: form-data; name=\"note\"\r\n\r\nhello\r\n--passenger--\r\n",
            Prepared("multipart/form-data; boundary=passenger", Parts).Body);
        Assert.Equal("{\n  \"a\": 1\n}\n", Prepared("application/json", "{\n  \"a\": 1\n}\n").Body);
    }

    /// <summary>What a part holds goes as written whichever way its lines end, a blank line, a line that reads as a header
    /// and one a boundary only begins among them; only the line end before the next delimiter is the delimiter's.</summary>
    [Fact]
    public void A_parts_lines_go_as_written_between_its_headers_and_the_next_delimiter()
    {
        const string Parts = "--passenger\nContent-Disposition: form-data; name=\"file\"; filename=\"a.txt\"\nContent-Type: text/plain\n\n"
            + "a\nb\r\n\nName: not a header\n--passengers\n\n--passenger\nContent-Disposition: form-data; name=\"note\"\n\nc\n--passenger--";
        Assert.Equal("--passenger\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.txt\"\r\nContent-Type: text/plain\r\n\r\n"
            + "a\nb\r\n\nName: not a header\n--passengers\n\r\n--passenger\r\nContent-Disposition: form-data; name=\"note\"\r\n\r\nc\r\n--passenger--",
            Prepared("multipart/form-data; boundary=passenger", Parts).Body);
    }

    [Fact]
    public void A_boundary_is_read_however_its_content_type_writes_it_and_a_body_framed_already_stays_so()
    {
        const string Framed = "--a b\r\nContent-Disposition: form-data; name=\"x\"\r\n\r\none\ntwo\r\n--a b--\r\n";
        Assert.Equal(Framed, Prepared("MULTIPART/form-data; charset=utf-8; BOUNDARY=\"a b\"", Framed).Body);
        Assert.Equal(Framed, Prepared("multipart/form-data; boundary=\"a b\"", Framed.Replace("--a b\r\n", "--a b\n", StringComparison.Ordinal)).Body);
    }

    [Fact]
    public void A_multipart_type_naming_no_boundary_leaves_its_body_as_written()
    {
        const string Body = "--passenger\nName: value\n\na\n--passenger--\n";
        Assert.Equal(Body, Prepared("multipart/form-data", Body).Body);
        Assert.Equal(Body, Prepared("text/plain; boundary=passenger", Body).Body);
    }

    /// <summary>The text before the first delimiter and after the last, which no reader takes for a part, stays as written,
    /// and a part that is itself multipart is framed by its own boundary, its parts' lines kept as any part's.</summary>
    [Fact]
    public void A_nested_multipart_part_is_framed_by_its_own_boundary_and_text_outside_the_parts_goes_as_written()
    {
        const string Parts = "before\nit\n--outer\nContent-Type: multipart/mixed; boundary=inner\n\n--inner\nContent-Type: text/plain\n\na\nb\n--inner--\n--outer--\nafter\nit\n";
        Assert.Equal("before\nit\r\n--outer\r\nContent-Type: multipart/mixed; boundary=inner\r\n\r\n--inner\r\nContent-Type: text/plain\r\n\r\na\nb\r\n--inner--\r\n--outer--\r\nafter\nit\n",
            Prepared("multipart/mixed; boundary=outer", Parts).Body);
    }
}
