namespace Passenger.App.Aspire;

/// <summary>Where an AppHost's dashboard is called, from the addresses it listens at (ASPNETCORE_URLS): an address on every
/// interface (<c>0.0.0.0</c>, <c>[::]</c>, <c>+</c> or <c>*</c>) is one to listen at, never to call, so it is called at
/// loopback on its port.</summary>
public static class DashboardAddress
{
    /// <summary>The first HTTP address of a list of them (';' between), as one to call; null where none is HTTP.</summary>
    public static Uri? FromUrls(string urls) => urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Called).FirstOrDefault(endpoint => endpoint?.Scheme == Uri.UriSchemeHttp);

    /// <summary>An address listened at as one to call: its host on every interface made loopback (IPv6's for IPv6's), the
    /// rest as it was; null for one that is no address.</summary>
    public static Uri? Called(string url)
    {
        int begins = url.IndexOf("://", StringComparison.Ordinal) + 3;
        if (begins < 3 || begins >= url.Length)
        {
            return null;
        }
        int ends = url[begins] == '[' ? url.IndexOf(']', begins) + 1 : url.IndexOfAny([':', '/'], begins);
        ends = ends <= 0 ? url.Length : ends;
        string host = url[begins..ends];
        string called = host switch
        {
            "0.0.0.0" or "+" or "*" => "127.0.0.1",
            "[::]" or "[0:0:0:0:0:0:0:0]" => "[::1]",
            _ => host,
        };
        return Uri.TryCreate(url[..begins] + called + url[ends..], UriKind.Absolute, out Uri? endpoint) ? endpoint : null;
    }
}
