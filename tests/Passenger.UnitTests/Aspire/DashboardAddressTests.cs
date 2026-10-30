using Passenger.App.Aspire;

namespace Passenger.UnitTests.Aspire;

/// <summary>An AppHost's dashboard listening on every interface is called at loopback on its port, so its logs and traces
/// are read from it; one listening at a host of its own is called there.</summary>
public sealed class DashboardAddressTests
{
    [Theory]
    [InlineData("http://0.0.0.0:18888", "http://127.0.0.1:18888/")]
    [InlineData("http://+:18888", "http://127.0.0.1:18888/")]
    [InlineData("http://*:18888/", "http://127.0.0.1:18888/")]
    [InlineData("http://[::]:18888", "http://[::1]:18888/")]
    [InlineData("http://localhost:18888", "http://localhost:18888/")]
    [InlineData("http://127.0.0.1:5000", "http://127.0.0.1:5000/")]
    public void A_dashboard_listening_on_every_interface_is_called_at_loopback_on_its_port(string listening, string called)
    {
        Uri? endpoint = DashboardAddress.FromUrls(listening);
        Assert.Equal(called, endpoint?.AbsoluteUri);
        Assert.True(endpoint!.IsLoopback, $"{listening} is called at {endpoint}, which the telemetry reader refuses as no loopback address");
    }

    [Fact]
    public void The_first_http_address_of_the_list_is_called()
    {
        Assert.Equal("http://127.0.0.1:18889/", DashboardAddress.FromUrls("https://0.0.0.0:18888; http://0.0.0.0:18889")?.AbsoluteUri);
        Assert.Null(DashboardAddress.FromUrls("https://localhost:18888"));
        Assert.Null(DashboardAddress.FromUrls(string.Empty));
    }
}
