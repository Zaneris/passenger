using System.Net.Http.Headers;

namespace Passenger.Core;

/// <summary>Every request Passenger itself makes of a host (its updates, JSON schemas, Source Link, package feeds): nothing
/// in it but the request, what its caller adds and a User-Agent naming Passenger and its version; no cookie an earlier
/// answer set and no trace header from an activity that happens to be current; and an answer read whole held to the most
/// its client takes.</summary>
public static class OutboundHttp
{
    /// <summary>A client, to keep for the process, whose requests give up after <paramref name="timeout"/> (infinite where
    /// the caller holds its own) and whose answers read whole may be <paramref name="most"/> bytes at most; with
    /// <paramref name="anyCertificate"/>, one that takes a server's certificate unchecked, as a package feed's configuration
    /// may ask.</summary>
    public static HttpClient Client(TimeSpan timeout, long most, bool anyCertificate = false)
    {
        SocketsHttpHandler handler = new()
        {
            UseCookies = false,
            ActivityHeadersPropagator = null,
            // A host whose address changes is reached at its new one by a client kept for the process.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
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
}
