using Passenger.App.Containers;

namespace Passenger.UnitTests.Containers;

/// <summary>A container service's address is filled from where the engine published its port: the scheme, the host (a
/// .dcproj's {ServiceIPAddress} as a launch profile's {ServiceHost}) and the port, its tokens in any letter case; an address
/// with no token is a page as written.</summary>
public sealed class ServiceUrlsTests
{
    [Fact]
    public void A_container_services_address_is_filled_from_where_its_port_was_published_and_one_with_no_token_is_a_page_as_written() => Cases.All(
        ("each token takes the published ports scheme host and port", () => Cases.Each<(string, string)>(
        [
            ("{Scheme}://{ServiceIPAddress}:{ServicePort}/swagger", "https://localhost:32770/swagger"),
            ("{scheme}://{serviceipaddress}:{SERVICEPORT}/", "https://localhost:32770/"),
        ], row => Each_token_takes_the_published_ports_scheme_host_and_port(row.Item1, row.Item2))),
        ("an address with no token is a page as written", An_address_with_no_token_is_a_page_as_written));

    private void Each_token_takes_the_published_ports_scheme_host_and_port(string url, string filled)
    {
        Assert.True(ServiceUrls.HasTokens(url), $"{url} was taken for a page as written");
        Assert.Equal(filled, ServiceUrls.Filled(url, "https", "localhost", "32770"));
    }

    private void An_address_with_no_token_is_a_page_as_written()
    {
        Assert.False(ServiceUrls.HasTokens("http://localhost:5000/health"), "An address with no token waited for a published port");
    }
}
