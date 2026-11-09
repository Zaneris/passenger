using System.Net;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A proxy named in the environment is passed by for this machine, a loopback address, localhost or a name under
/// it, as the proxy would reach its own machine, and taken for every other host.</summary>
public sealed class OutboundHttpTests
{
    private static readonly Uri Dead = new("http://192.0.2.1:3128/");

    [Theory]
    [InlineData("https://shop.dev.localhost:5001/openapi/v1.json")]
    [InlineData("http://127.0.0.1:9222/json/version")]
    [InlineData("http://[::1]:5000/")]
    [InlineData("ws://localhost:9222/devtools/page/1")]
    public void A_request_to_this_machine_passes_the_proxy_by(string address)
    {
        Assert.True(OutboundHttp.PassingThisMachineBy(new WebProxy(Dead)).IsBypassed(new Uri(address)),
            $"A request to {address} went to the proxy, which would reach its own machine rather than this one");
    }

    [Theory]
    [InlineData("https://api.nuget.org/v3/index.json")]
    [InlineData("http://localhost.example.com:5000/")]
    public void A_request_elsewhere_goes_through_the_proxy(string address)
    {
        IWebProxy proxy = OutboundHttp.PassingThisMachineBy(new WebProxy(Dead));
        Assert.False(proxy.IsBypassed(new Uri(address)), $"A request to {address} passed the proxy by");
        Assert.Equal(Dead, proxy.GetProxy(new Uri(address)));
    }
}
