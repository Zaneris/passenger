using Passenger.App.Endpoints;

namespace Passenger.UnitTests.Endpoints;

/// <summary>A request sent to the running app's address in place of the host its URL names keeps everything after that
/// host: its path, its query and its fragment, whichever comes first.</summary>
public sealed class RouteRequestsTests
{
    [Theory]
    [InlineData("http://example.com?key=123", "http://localhost:5000?key=123")]
    [InlineData("{{host}}?key=123", "http://localhost:5000?key=123")]
    [InlineData("http://example.com#top", "http://localhost:5000#top")]
    [InlineData("https://example.com:8443?next=/items/1", "http://localhost:5000?next=/items/1")]
    [InlineData("http://example.com/items?key=123#top", "http://localhost:5000/items?key=123#top")]
    [InlineData("http://example.com", "http://localhost:5000")]
    [InlineData("{{host}}/items", "http://localhost:5000/items")]
    [InlineData("/items", "http://localhost:5000/items")]
    public void A_request_sent_to_another_address_keeps_what_follows_its_host(string url, string sent)
    {
        string written = RouteRequests.WithHost(url, "http://localhost:5000/");
        Assert.True(written == sent, $"{url} was sent to the running app as {written}, not {sent}: what follows its host was not kept");
    }
}
