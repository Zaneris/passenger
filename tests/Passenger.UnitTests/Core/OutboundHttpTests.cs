using System.Net;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A proxy named in the environment is passed by for this machine, a loopback address, localhost or a name under
/// it, as the proxy would reach its own machine, and taken for every other host.</summary>
public sealed class OutboundHttpTests
{
    private static readonly Uri Dead = new("http://192.0.2.1:3128/");

    [Fact]
    public void A_proxy_is_passed_by_for_this_machine_and_taken_elsewhere() => Cases.All(
        ("A request to this machine passes the proxy by", A_request_to_this_machine_passes_the_proxy_by),
        ("A request elsewhere goes through the proxy", A_request_elsewhere_goes_through_the_proxy));

    private void A_request_to_this_machine_passes_the_proxy_by() => Cases.Each<string>(
    [
        "https://shop.dev.localhost:5001/openapi/v1.json",
        "http://127.0.0.1:9222/json/version",
        "http://[::1]:5000/",
        "ws://localhost:9222/devtools/page/1",
    ], address =>
    {
        Assert.True(OutboundHttp.PassingThisMachineBy(new WebProxy(Dead)).IsBypassed(new Uri(address)),
            $"A request to {address} went to the proxy, which would reach its own machine rather than this one");
    });

    private void A_request_elsewhere_goes_through_the_proxy() => Cases.Each<string>(
    [
        "https://api.nuget.org/v3/index.json",
        "http://localhost.example.com:5000/",
    ], address =>
    {
        IWebProxy proxy = OutboundHttp.PassingThisMachineBy(new WebProxy(Dead));
        Assert.False(proxy.IsBypassed(new Uri(address)), $"A request to {address} passed the proxy by");
        Assert.Equal(Dead, proxy.GetProxy(new Uri(address)));
    });
}
