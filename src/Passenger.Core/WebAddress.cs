using System.Net;
using System.Net.Sockets;

namespace Passenger.Core;

/// <summary>What Passenger takes a web address to be: one something else's answer names, opened only as a web page; one on
/// this machine, a loopback address or a name under <c>localhost</c> (<c>myapi.dev.localhost</c>, as
/// <c>dotnet new webapi --localhost-tld</c> makes one), which RFC 6761 keeps to loopback and .NET and browsers resolve so;
/// and a listen address as one to call.</summary>
public static class WebAddress
{
    /// <summary>An absolute http or https URL, or null.</summary>
    public static Uri? Of(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri : null;

    /// <summary>Whether an absolute address is on this machine: its host loopback, <c>localhost</c> or a name under it; never
    /// a name that only begins with it (<c>localhost.example.com</c>).</summary>
    public static bool OnThisMachine(Uri address) =>
        address.IsAbsoluteUri && (address.IsLoopback || address.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether an address is an http or https one on this machine with nothing but where it is: no user info,
    /// query or fragment, as a service's endpoint is given.</summary>
    public static bool Local(Uri address) => OnThisMachine(address) && address.Scheme is ("http" or "https")
        && address.UserInfo.Length == 0 && address.Query.Length == 0 && address.Fragment.Length == 0;

    /// <summary>Whether what comes from an address comes unread and unchanged on the way: HTTPS, or http to this machine
    /// alone.</summary>
    public static bool Secure(Uri url) => url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && OnThisMachine(url));

    /// <summary>An address listened at (<c>ASPNETCORE_URLS</c>, an <c>applicationUrl</c>) as one to call: a host on every
    /// interface (<c>*</c>, <c>+</c>, <c>0.0.0.0</c>, <c>[::]</c>), which no client can name, as <c>localhost</c>, the name a
    /// development certificate is made for; by family, for a call no certificate names, as that family's loopback
    /// (<c>127.0.0.1</c>, <c>[::1]</c>), never tried in the other family first; the rest as written.</summary>
    public static string Callable(string listened, bool byFamily = false)
    {
        int begins = listened.IndexOf("://", StringComparison.Ordinal) + 3;
        if (begins < 3 || begins >= listened.Length || !Uri.CheckSchemeName(listened[..(begins - 3)]))
        {
            return listened;
        }
        int ends = listened[begins] == '[' ? listened.IndexOf(']', begins) + 1 : listened.IndexOfAny([':', '/', '?', '#'], begins);
        ends = ends <= 0 ? listened.Length : ends;
        string host = listened[begins..ends];
        string? called = host switch
        {
            "*" or "+" or "0.0.0.0" => byFamily ? "127.0.0.1" : "localhost",
            _ when IsIPv6Any(host) => byFamily ? "[::1]" : "localhost",
            _ => null,
        };
        return called is null ? listened : string.Concat(listened.AsSpan(0, begins), called, listened.AsSpan(ends));
    }

    /// <summary>A loopback port no listener holds now, the system's choice. Another program may take it before whoever it
    /// is given to binds it.</summary>
    public static int FreeLoopbackPort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static bool IsIPv6Any(string host) =>
        host.Length > 2 && host[0] == '[' && host[^1] == ']' && IPAddress.TryParse(host[1..^1], out IPAddress? address) && address.Equals(IPAddress.IPv6Any);
}
