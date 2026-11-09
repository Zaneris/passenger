using System.Text;
using Passenger.App.Endpoints;
using Passenger.Roslyn.Extension;

namespace Passenger.UnitTests.Endpoints;

/// <summary>A request sent to the running app's address in place of the host its URL names keeps everything after that
/// host: its path, its query and its fragment, whichever comes first; and a route's request added to an .http file begins
/// as one from an app's OpenAPI document does.</summary>
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

    [Theory]
    [InlineData("", "@Shop_HostAddress = http://localhost:5123\n\n")]
    [InlineData("GET http://localhost:5123/ping", "\n###\n@Shop_HostAddress = http://localhost:5123\n\n")]
    [InlineData("@Shop_HostAddress = http://localhost:5123\n\nGET {{Shop_HostAddress}}/ping\n\n###\n", "")]
    public void A_routes_request_begins_in_a_file_as_one_from_OpenAPI_does(string existing, string before)
    {
        WebRoute route = new("/repo/Shop/Shop.csproj", RouteKinds.Minimal, ["GET"], "/items", null, [], "Items", "/repo/Shop/Program.cs", new Place(0, 0), new Place(0, 0), string.Empty);
        StringBuilder preamble = new();
        HttpFile.Preamble(preamble, existing, RouteRequests.HostVariable(route.Project), "http://localhost:5123");
        string written = RouteRequests.Entry(route, null, existing, "http://localhost:5123");
        Assert.True(preamble.ToString() == before && written.StartsWith(before + $"# {RouteKinds.Minimal}: Items\n", StringComparison.Ordinal),
            $"A route's request added to {(existing.Length == 0 ? "an empty file" : $"\"{existing}\"")} began \"{written}\", where one from OpenAPI begins \"{preamble}\": "
            + "the file reads two ways as each adds to it");
    }
}
