using Passenger.App.Endpoints;

namespace Passenger.UnitTests.Endpoints;

/// <summary>A request of an .http file made ready to send: a multipart body sent with its lines ended in CRLF, by which a
/// server finds its parts, where the file ends them as the editor does; any other body sent as written.</summary>
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
    public void A_multipart_body_is_sent_with_its_lines_ended_in_CRLF_and_any_other_as_written()
    {
        const string Parts = "--passenger\nContent-Disposition: form-data; name=\"note\"\n\nhello\n--passenger--\n";
        Assert.Equal("--passenger\r\nContent-Disposition: form-data; name=\"note\"\r\n\r\nhello\r\n--passenger--\r\n",
            Prepared("multipart/form-data; boundary=passenger", Parts).Body);
        Assert.Equal("{\n  \"a\": 1\n}\n", Prepared("application/json", "{\n  \"a\": 1\n}\n").Body);
    }
}
