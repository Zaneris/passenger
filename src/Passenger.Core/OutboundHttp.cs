using System.Net;
using System.Net.Http.Headers;

namespace Passenger.Core;

/// <summary>Every request Passenger itself makes of a host (its updates, JSON schemas, Source Link, package feeds): nothing
/// in it but the request, what its caller adds and a User-Agent naming Passenger and its version; no cookie an earlier
/// answer set and no trace header from an activity that happens to be current; an answer read whole held to the most
/// its client takes; and a request to this machine never through a proxy the environment names (<see cref="Proxy"/>).</summary>
public static class OutboundHttp
{
    /// <summary>The proxy a request Passenger makes goes through: where the environment names one (<c>http_proxy</c>,
    /// <c>https_proxy</c> or <c>all_proxy</c>, in either case), which .NET asks for every host <c>no_proxy</c> leaves out,
    /// loopback and <c>*.localhost</c> too, that one with this machine passed by (<see cref="PassingThisMachineBy"/>), as a
    /// proxy on another machine would reach its own; else the system's whole, so its failover between proxies holds.</summary>
    public static IWebProxy Proxy { get; } = NamedInEnvironment() ? PassingThisMachineBy(HttpClient.DefaultProxy) : HttpClient.DefaultProxy;

    /// <summary>A proxy that passes this machine by (<see cref="WebAddress.OnThisMachine"/>), and is <paramref name="proxy"/>
    /// for every other host.</summary>
    public static IWebProxy PassingThisMachineBy(IWebProxy proxy) => new ThisMachineDirect(proxy);

    /// <summary>A client, to keep for the process, whose requests give up after <paramref name="timeout"/> (infinite where
    /// the caller holds its own) and whose answers read whole may be <paramref name="most"/> bytes at most; with
    /// <paramref name="anyCertificate"/>, one that takes a server's certificate unchecked, as a package feed's configuration
    /// may ask; without <paramref name="followsRedirects"/>, one handing a redirect to its caller to judge.</summary>
    public static HttpClient Client(TimeSpan timeout, long most, bool anyCertificate = false, bool followsRedirects = true)
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = followsRedirects,
            UseCookies = false,
            ActivityHeadersPropagator = null,
            // A host whose address changes is reached at its new one by a client kept for the process.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            Proxy = Proxy,
        };
        if (anyCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        return new HttpClient(handler) { Timeout = timeout, MaxResponseContentBufferSize = most };
    }

    /// <summary>A request carrying Passenger's User-Agent (some hosts, GitHub's API among them, refuse one without).</summary>
    public static HttpRequestMessage Request(HttpMethod method, Uri uri)
    {
        HttpRequestMessage request = new(method, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Passenger", Product.Version));
        return request;
    }

    /// <summary>Whether a variable .NET reads a proxy from is set, in lower case or upper.</summary>
    private static bool NamedInEnvironment() => ((string[])["http_proxy", "https_proxy", "all_proxy"])
        .Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name.ToUpperInvariant())));

    private sealed class ThisMachineDirect(IWebProxy proxy) : IWebProxy
    {
        private ICredentials? credentials;

        public ICredentials? Credentials
        {
            get => credentials ?? proxy.Credentials;
            set => credentials = value;
        }

        public Uri? GetProxy(Uri destination) => proxy.GetProxy(destination);

        public bool IsBypassed(Uri host) => WebAddress.OnThisMachine(host) || proxy.IsBypassed(host);
    }
}
