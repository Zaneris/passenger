using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>An address on this machine is a loopback one, localhost or a name under it (an app made with
/// <c>--localhost-tld</c> serves at one), never a name that merely begins with localhost; a service's endpoint on this
/// machine names nothing but where it is; and a listen address on every interface is called at localhost, the rest as
/// written.</summary>
public sealed class WebAddressTests
{
    [Theory]
    [InlineData("https://myapi.dev.localhost:5001/")]
    [InlineData("http://a.dev.localhost/openapi/v1.json?full=1")]
    [InlineData("http://a.localhost:5000")]
    [InlineData("http://LOCALHOST:5000")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://127.0.0.2:5000")]
    [InlineData("http://[::1]:5000")]
    public void A_loopback_address_localhost_or_a_name_under_it_is_on_this_machine(string address)
    {
        Assert.True(WebAddress.OnThisMachine(new Uri(address)), $"{address} was taken for another machine's, so its app's document, page refresh or service would never be reached");
    }

    [Theory]
    [InlineData("http://localhost.example.com:5000")]
    [InlineData("http://evil-localhost:5000")]
    [InlineData("http://192.0.2.10:5000")]
    [InlineData("http://example.com/a.localhost")]
    public void A_name_that_only_looks_local_is_not_on_this_machine(string address)
    {
        Assert.False(WebAddress.OnThisMachine(new Uri(address)), $"{address} was taken for this machine's, so what Passenger keeps to this machine would go to it");
    }

    [Fact]
    public void A_relative_address_is_on_no_machine()
    {
        Assert.False(WebAddress.OnThisMachine(new Uri("/openapi/v1.json", UriKind.Relative)));
    }

    [Theory]
    [InlineData("https://a.dev.localhost:7001", true)]
    [InlineData("http://127.0.0.1:7001/", true)]
    [InlineData("ftp://localhost:7001", false)]
    [InlineData("https://user@localhost:7001", false)]
    [InlineData("https://localhost:7001/?key=1", false)]
    [InlineData("https://localhost:7001/#top", false)]
    [InlineData("https://192.0.2.10:7001", false)]
    public void A_local_endpoint_is_http_or_https_on_this_machine_with_nothing_but_where_it_is(string address, bool local)
    {
        Assert.True(WebAddress.Local(new Uri(address)) == local, $"{address} was {(local ? "refused" : "taken")} as a service's endpoint on this machine");
    }

    [Theory]
    [InlineData("https://example.com/sdk.tar.gz", true)]
    [InlineData("http://127.0.0.1:5000/sdk.tar.gz", true)]
    [InlineData("http://feed.dev.localhost:5000/sdk.tar.gz", true)]
    [InlineData("http://example.com/sdk.tar.gz", false)]
    [InlineData("ftp://localhost/sdk.tar.gz", false)]
    public void A_download_is_secure_over_https_or_over_http_to_this_machine_alone(string address, bool secure)
    {
        Assert.True(WebAddress.Secure(new Uri(address)) == secure, $"{address} was taken as {(secure ? "insecure" : "secure")}");
    }

    [Theory]
    [InlineData("http://*:5000", "http://localhost:5000")]
    [InlineData("http://+:5000/", "http://localhost:5000/")]
    [InlineData("https://0.0.0.0:7001/swagger", "https://localhost:7001/swagger")]
    [InlineData("http://[::]:5000", "http://localhost:5000")]
    [InlineData("http://[0:0:0:0:0:0:0:0]:5000", "http://localhost:5000")]
    [InlineData("http://*", "http://localhost")]
    [InlineData("http://localhost:5000", "http://localhost:5000")]
    [InlineData("http://127.0.0.1:0", "http://127.0.0.1:0")]
    [InlineData("http://[::1]:5000", "http://[::1]:5000")]
    [InlineData("http://myapi.dev.localhost:5000", "http://myapi.dev.localhost:5000")]
    [InlineData("swagger", "swagger")]
    [InlineData("swagger?from=http://*:5000", "swagger?from=http://*:5000")]
    [InlineData("http://", "http://")]
    public void A_host_on_every_interface_is_called_at_localhost_and_the_rest_as_written(string listened, string called)
    {
        Assert.True(WebAddress.Callable(listened) == called, $"{listened} was called at {WebAddress.Callable(listened)}, not {called}");
    }

    [Theory]
    [InlineData("http://*:5000", "http://127.0.0.1:5000")]
    [InlineData("http://+:5000/", "http://127.0.0.1:5000/")]
    [InlineData("http://0.0.0.0:5000", "http://127.0.0.1:5000")]
    [InlineData("http://[::]:5000", "http://[::1]:5000")]
    [InlineData("http://localhost:5000", "http://localhost:5000")]
    public void By_family_a_host_on_every_interface_is_called_at_its_familys_loopback(string listened, string called)
    {
        Assert.True(WebAddress.Callable(listened, byFamily: true) == called,
            $"{listened} was called at {WebAddress.Callable(listened, byFamily: true)}, not {called}, so a call to it may try the other family first");
    }
}
