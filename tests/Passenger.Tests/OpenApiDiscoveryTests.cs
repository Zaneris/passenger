using Passenger.App.Endpoints;

namespace Passenger.Tests;

/// <summary>An app's OpenAPI document is asked of that app alone: a path written to name another host and a redirect to
/// another origin reach nothing there, and a redirect within the app is followed; an app at a name under localhost is this
/// machine's. Two stand-ins on this machine's loopback are the app and what lies beyond it, each its own origin by its
/// port.</summary>
[Collection(Beside.Name)]
public sealed class OpenApiDiscoveryTests
{
    private const string Document = """{ "openapi": "3.1.1", "info": { "title": "Shop", "version": "1" }, "paths": {} }""";

    private static Task<OpenApiDocument?> Asked(StandInHttp app, params string[] mapped) =>
        OpenApiSources.FromAppAsync(new Uri(app.Root), mapped, TestContext.Current.CancellationToken);

    private static StandInHttp Beyond() => new(exchange => exchange.ReplyAsync(200, "application/json", Document));

    [Fact]
    public async Task A_path_written_to_name_another_host_asks_that_host_nothing()
    {
        await using StandInHttp beyond = Beyond();
        await using StandInHttp app = new(exchange => exchange.ReplyAsync(404, null, string.Empty));
        OpenApiDocument? read = await Asked(app, $"//127.0.0.1:{beyond.Port}/spec.json");
        Assert.Null(read);
        Assert.True(beyond.Connections == 0, "A path the app's code maps sent Passenger's request to another host, where it promises to ask only the local app");
    }

    [Fact]
    public async Task A_redirect_to_another_origin_is_not_followed_and_reaches_nothing_there()
    {
        await using StandInHttp beyond = Beyond();
        await using StandInHttp app = new(exchange => exchange.ReplyAsync(302, null, string.Empty, $"Location: {beyond.Root}/spec.json\r\n"));
        OpenApiDocument? read = await Asked(app);
        Assert.Null(read);
        Assert.True(beyond.Connections == 0, "An app's redirect took Passenger's request to another server, where it promises to ask only the local app");
    }

    [Fact]
    public async Task An_app_at_a_name_under_localhost_has_its_document_read()
    {
        await using StandInHttp app = new(exchange => exchange.ReplyAsync(200, "application/json", Document));
        OpenApiDocument? read = await OpenApiSources.FromAppAsync(new Uri($"http://shop.dev.localhost:{app.Port}"), [], TestContext.Current.CancellationToken);
        Assert.True(read?.Title == "Shop",
            "An app at shop.dev.localhost, as dotnet new webapi --localhost-tld makes one, was taken for another machine's and never asked for its document");
    }

    [Fact]
    public async Task A_redirect_within_the_app_is_followed_to_its_document()
    {
        await using StandInHttp app = new(exchange => exchange.Path == "/moved.json"
            ? exchange.ReplyAsync(200, "application/json", Document)
            : exchange.ReplyAsync(302, null, string.Empty, "Location: /moved.json\r\n"));
        OpenApiDocument? read = await Asked(app);
        Assert.True(read?.Title == "Shop", "An app whose document is one redirect away within the app gave none");
    }
}
