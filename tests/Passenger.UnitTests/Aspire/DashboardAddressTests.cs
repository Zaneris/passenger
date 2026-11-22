using System.Text.Json;
using Passenger.App.Aspire;

namespace Passenger.UnitTests.Aspire;

/// <summary>An AppHost's dashboard listening on every interface is called at loopback on its port, so its logs and traces
/// are read from it; one listening at a host of its own is called there. One Passenger adds is placed by an orchestrator
/// that places it, and called where the dashboard's resource says it was placed.</summary>
public sealed class DashboardAddressTests
{
    [Fact]
    public void A_dashboard_is_called_at_loopback_where_it_listens_or_where_its_resource_says_it_was_placed_and_nowhere_else() => Cases.All(
        ("a dashboard listening on every interface is called at its familys loopback on its port", () => Cases.Each<(string, string)>(
        [
            ("http://0.0.0.0:18888", "http://127.0.0.1:18888/"),
            ("http://+:18888", "http://127.0.0.1:18888/"),
            ("http://[::]:18888", "http://[::1]:18888/"),
            ("http://localhost:18888", "http://localhost:18888/"),
        ], row => A_dashboard_listening_on_every_interface_is_called_at_its_familys_loopback_on_its_port(row.Item1, row.Item2))),
        ("the first http address of the list is called", The_first_http_address_of_the_list_is_called),
        ("the dashboard is called at the origin its resource says its http address was placed", () => Cases.Each<(string, string)>(
        [
            ("http://127.0.0.1:52411/login?t=0123abcd", "http://127.0.0.1:52411/"),
            ("http://shop.dev.localhost:52411/", "http://shop.dev.localhost:52411/"),
        ], row => The_dashboard_is_called_at_the_origin_its_resource_says_its_http_address_was_placed(row.Item1, row.Item2))),
        ("nothing but the dashboards own http address on loopback is taken for where it was placed", Nothing_but_the_dashboards_own_http_address_on_loopback_is_taken_for_where_it_was_placed),
        ("an orchestrator places the added address from Aspire 9 4", () => Cases.Each<(string?, bool)>(
        [
            ("9.4.0", true),
            ("9.4.0-preview.1.25378.8", true),
            (null, true),
            ("9.3.2", false),
        ], row => An_orchestrator_places_the_added_address_from_Aspire_9_4(row.Item1, row.Item2))));

    private void A_dashboard_listening_on_every_interface_is_called_at_its_familys_loopback_on_its_port(string listening, string called)
    {
        Uri? endpoint = DashboardAddress.FromUrls(listening);
        Assert.Equal(called, endpoint?.AbsoluteUri);
        Assert.True(endpoint!.IsLoopback, $"{listening} is called at {endpoint}, which the telemetry reader refuses as no loopback address");
    }

    private void The_first_http_address_of_the_list_is_called()
    {
        Assert.Equal("http://127.0.0.1:18889/", DashboardAddress.FromUrls("https://0.0.0.0:18888; http://0.0.0.0:18889")?.AbsoluteUri);
        Assert.Null(DashboardAddress.FromUrls("https://localhost:18888"));
        Assert.Null(DashboardAddress.FromUrls(string.Empty));
    }

    private void The_dashboard_is_called_at_the_origin_its_resource_says_its_http_address_was_placed(string url, string called)
    {
        JsonElement[] resources =
        [
            Resource("apiservice", ("http", "http://127.0.0.1:5400")),
            Resource(DashboardAddress.Resource, ("https", "https://localhost:17037/login?t=0123abcd"), ("otlp-http", "http://127.0.0.1:19065"), ("http", url)),
        ];
        Assert.True(DashboardAddress.FromResources(resources)?.AbsoluteUri == called,
            $"The dashboard placed at {url} was called at {DashboardAddress.FromResources(resources)}, so its logs and traces would be asked of another program");
    }

    private void Nothing_but_the_dashboards_own_http_address_on_loopback_is_taken_for_where_it_was_placed()
    {
        (string Words, JsonElement[] Resources)[] cases =
        [
            ("no resource yet", []),
            ("its resource with no address yet", [Resource(DashboardAddress.Resource)]),
            ("another resource's http address", [Resource("apiservice", ("http", "http://127.0.0.1:5400"))]),
            ("its HTTPS address alone", [Resource(DashboardAddress.Resource, ("https", "https://localhost:17037"))]),
            ("its OTLP address", [Resource(DashboardAddress.Resource, ("otlp-http", "http://127.0.0.1:19065"))]),
            ("port 0, the address as handed", [Resource(DashboardAddress.Resource, ("http", DashboardAddress.Unplaced))]),
            ("an address off this machine", [Resource(DashboardAddress.Resource, ("http", "http://192.0.2.10:52411"))]),
            ("no URL", [Resource(DashboardAddress.Resource, ("http", "not a url"))]),
        ];
        foreach ((string words, JsonElement[] resources) in cases)
        {
            Assert.True(DashboardAddress.FromResources(resources) is null,
                $"{words} was taken for where the dashboard was placed ({DashboardAddress.FromResources(resources)}), so its key would go to a program that is not it");
        }
    }

    private void An_orchestrator_places_the_added_address_from_Aspire_9_4(string? hostingVersion, bool placed)
    {
        Assert.True(DashboardAddress.PlacedIn(hostingVersion) == placed, placed
            ? $"An AppHost of Aspire {hostingVersion ?? "unknown"} is handed a port chosen and let go, which another program can take before its dashboard binds it"
            : $"An AppHost of Aspire {hostingVersion} is handed port 0, which its dashboard binds itself, saying the port nowhere Passenger reads");
    }

    /// <summary>A resource as the resource stream's snapshot writes it: its names, its URLs by endpoint name, hidden.</summary>
    private static JsonElement Resource(string displayName, params (string Name, string Url)[] urls) => JsonSerializer.SerializeToElement(new
    {
        name = displayName + "-abcdefgh",
        displayName,
        urls = urls.Select(url => new { name = url.Name, displayName = (string?)null, url = url.Url, isInternal = false, isInactive = false }),
        isHidden = displayName == DashboardAddress.Resource,
    });
}
