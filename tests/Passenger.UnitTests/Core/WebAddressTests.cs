using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>An address on this machine is a loopback one, localhost or a name under it (an app made with
/// <c>--localhost-tld</c> serves at one), never a name that merely begins with localhost; a service's endpoint on this
/// machine names nothing but where it is; and a listen address on every interface is called at localhost, the rest as
/// written.</summary>
public sealed class WebAddressTests
{
    [Fact]
    public void An_address_is_on_this_machine_only_as_a_loopback_localhost_or_a_name_under_it() => Cases.All(
        ("A loopback address localhost or a name under it is on this machine", A_loopback_address_localhost_or_a_name_under_it_is_on_this_machine),
        ("A name that only looks local is not on this machine", A_name_that_only_looks_local_is_not_on_this_machine),
        ("A program names its own machine on every interface or its own loopback", A_program_names_its_own_machine_on_every_interface_or_its_own_loopback),
        ("A relative address is on no machine", A_relative_address_is_on_no_machine));

    private void A_loopback_address_localhost_or_a_name_under_it_is_on_this_machine() => Cases.Each<string>(
    [
        "https://myapi.dev.localhost:5001/",
        "http://a.dev.localhost/openapi/v1.json?full=1",
        "http://a.localhost:5000",
        "http://LOCALHOST:5000",
        "http://127.0.0.1:5000",
        "http://127.0.0.2:5000",
        "http://[::1]:5000",
    ], address =>
    {
        Assert.True(WebAddress.OnThisMachine(new Uri(address)), $"{address} was taken for another machine's, so its app's document, page refresh or service would never be reached");
    });

    private void A_name_that_only_looks_local_is_not_on_this_machine() => Cases.Each<string>(
    [
        "http://localhost.example.com:5000",
        "http://evil-localhost:5000",
        "http://192.0.2.10:5000",
        "http://example.com/a.localhost",
    ], address =>
    {
        Assert.False(WebAddress.OnThisMachine(new Uri(address)), $"{address} was taken for this machine's, so what Passenger keeps to this machine would go to it");
    });

    private void A_program_names_its_own_machine_on_every_interface_or_its_own_loopback() => Cases.Each<(string address, bool own)>(
    [
        ("http://0.0.0.0:8080", true),
        ("http://[::]:8080/swagger", true),
        ("https://localhost:8443", true),
        ("http://127.0.0.1:5000", true),
        ("http://[::1]:5000", true),
        ("http://192.0.2.10:8080", false),
        ("https://learn.microsoft.com/aspnet", false),
        ("ftp://0.0.0.0:21", false),
    ], row =>
    {
        (string address, bool own) = row;
        Assert.True(WebAddress.OnItsOwnMachine(new Uri(address)) == own,
            $"{address} was {(own ? "taken for another machine's" : "taken for the machine of the program that wrote it")}, so a container's console would link it {(own ? "as it is, at this machine's port" : "to its container's published port")}");
    });

    private void A_relative_address_is_on_no_machine()
    {
        Assert.False(WebAddress.OnThisMachine(new Uri("/openapi/v1.json", UriKind.Relative)));
    }

    [Fact]
    public void A_download_is_secure_over_https_or_to_this_machine_alone_and_a_host_on_every_interface_is_called_at_its_loopback() => Cases.All(
        ("A local endpoint is http or https on this machine with nothing but where it is", A_local_endpoint_is_http_or_https_on_this_machine_with_nothing_but_where_it_is),
        ("A download is secure over https or over http to this machine alone", A_download_is_secure_over_https_or_over_http_to_this_machine_alone),
        ("A host on every interface is called at localhost and the rest as written", A_host_on_every_interface_is_called_at_localhost_and_the_rest_as_written),
        ("By family a host on every interface is called at its familys loopback", By_family_a_host_on_every_interface_is_called_at_its_familys_loopback));

    private void A_local_endpoint_is_http_or_https_on_this_machine_with_nothing_but_where_it_is() => Cases.Each<(string address, bool local)>(
    [
        ("https://a.dev.localhost:7001", true),
        ("http://127.0.0.1:7001/", true),
        ("ftp://localhost:7001", false),
        ("https://user@localhost:7001", false),
        ("https://localhost:7001/?key=1", false),
        ("https://localhost:7001/#top", false),
        ("https://192.0.2.10:7001", false),
    ], row =>
    {
        (string address, bool local) = row;
        Assert.True(WebAddress.Local(new Uri(address)) == local, $"{address} was {(local ? "refused" : "taken")} as a service's endpoint on this machine");
    });

    private void A_download_is_secure_over_https_or_over_http_to_this_machine_alone() => Cases.Each<(string address, bool secure)>(
    [
        ("https://example.com/sdk.tar.gz", true),
        ("http://127.0.0.1:5000/sdk.tar.gz", true),
        ("http://feed.dev.localhost:5000/sdk.tar.gz", true),
        ("http://example.com/sdk.tar.gz", false),
        ("ftp://localhost/sdk.tar.gz", false),
    ], row =>
    {
        (string address, bool secure) = row;
        Assert.True(WebAddress.Secure(new Uri(address)) == secure, $"{address} was taken as {(secure ? "insecure" : "secure")}");
    });

    private void A_host_on_every_interface_is_called_at_localhost_and_the_rest_as_written() => Cases.Each<(string listened, string called)>(
    [
        ("http://*:5000", "http://localhost:5000"),
        ("http://+:5000/", "http://localhost:5000/"),
        ("https://0.0.0.0:7001/swagger", "https://localhost:7001/swagger"),
        ("http://[::]:5000", "http://localhost:5000"),
        ("http://[0:0:0:0:0:0:0:0]:5000", "http://localhost:5000"),
        ("http://*", "http://localhost"),
        ("http://localhost:5000", "http://localhost:5000"),
        ("http://127.0.0.1:0", "http://127.0.0.1:0"),
        ("http://[::1]:5000", "http://[::1]:5000"),
        ("http://myapi.dev.localhost:5000", "http://myapi.dev.localhost:5000"),
        ("swagger", "swagger"),
        ("swagger?from=http://*:5000", "swagger?from=http://*:5000"),
        ("http://", "http://"),
    ], row =>
    {
        (string listened, string called) = row;
        Assert.True(WebAddress.Callable(listened) == called, $"{listened} was called at {WebAddress.Callable(listened)}, not {called}");
    });

    private void By_family_a_host_on_every_interface_is_called_at_its_familys_loopback() => Cases.Each<(string listened, string called)>(
    [
        ("http://*:5000", "http://127.0.0.1:5000"),
        ("http://+:5000/", "http://127.0.0.1:5000/"),
        ("http://0.0.0.0:5000", "http://127.0.0.1:5000"),
        ("http://[::]:5000", "http://[::1]:5000"),
        ("http://localhost:5000", "http://localhost:5000"),
    ], row =>
    {
        (string listened, string called) = row;
        Assert.True(WebAddress.Callable(listened, byFamily: true) == called,
            $"{listened} was called at {WebAddress.Callable(listened, byFamily: true)}, not {called}, so a call to it may try the other family first");
    });
}
