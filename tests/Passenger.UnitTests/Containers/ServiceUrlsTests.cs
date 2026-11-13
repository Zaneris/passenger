using Passenger.App.Containers;

namespace Passenger.UnitTests.Containers;

/// <summary>A container service's address is filled from where the engine published its port: the scheme, the host (a
/// .dcproj's {ServiceIPAddress} as a launch profile's {ServiceHost}) and the port, its tokens in any letter case; an address
/// with no token is a page as written.</summary>
public sealed class ServiceUrlsTests
{
    [Theory]
    [InlineData("{Scheme}://{ServiceIPAddress}:{ServicePort}/swagger", "https://localhost:32770/swagger")]
    [InlineData("{Scheme}://{ServiceHost}:{ServicePort}", "https://localhost:32770")]
    [InlineData("{scheme}://{serviceipaddress}:{SERVICEPORT}/", "https://localhost:32770/")]
    public void Each_token_takes_the_published_ports_scheme_host_and_port(string url, string filled)
    {
        Assert.True(ServiceUrls.HasTokens(url), $"{url} was taken for a page as written");
        Assert.Equal(filled, ServiceUrls.Filled(url, "https", "localhost", "32770"));
    }

    [Fact]
    public void An_address_with_no_token_is_a_page_as_written()
    {
        Assert.False(ServiceUrls.HasTokens("http://localhost:5000/health"), "An address with no token waited for a published port");
    }
}
