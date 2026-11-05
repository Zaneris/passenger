namespace Passenger.App.Endpoints;

/// <summary>What keeps the asking for an OpenAPI document on the app it is of: where a path of the app's lies, whether a
/// redirect stays with the app, and what the person may name.</summary>
public static class OpenApiAddresses
{
    /// <summary>Stands for an app's origin where a path is judged before any app is known.</summary>
    private static readonly Uri AnyApp = new("http://localhost");

    /// <summary>Where a path of an app's lies at its origin; null where what is written leaves that origin, as
    /// <c>//host/spec.json</c>, a reference to another host, does.</summary>
    public static Uri? At(Uri origin, string path) =>
        Uri.TryCreate(origin, path, out Uri? address) && SameOrigin(origin, address) ? address : null;

    /// <summary>Whether a redirect from an app's address stays with that app: to the same origin, or on the same host from
    /// HTTP to HTTPS, the app's own move to its secure address, which has a port of its own.</summary>
    public static bool Stays(Uri from, Uri to) =>
        SameOrigin(from, to) || from.Scheme == Uri.UriSchemeHttp && to.Scheme == Uri.UriSchemeHttps && string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>A document as the person names it, or why it is none: a path the app serves it at (<c>/docs/v1.json</c>), or
    /// an http or https URL on this machine's loopback; never another host, which is never asked.</summary>
    public static (string? Document, string? Why) Named(string? text)
    {
        string given = (text ?? string.Empty).Trim();
        if (given.StartsWith('/') && At(AnyApp, given) is not null)
        {
            return (given, null);
        }
        if (!Uri.TryCreate(given, UriKind.Absolute, out Uri? address) || address.Scheme is not ("http" or "https"))
        {
            return (null, "Name the path the app serves its OpenAPI document at (/openapi/v1.json), or its URL on this machine");
        }
        return address.IsLoopback ? (address.ToString(), null) : (null, $"{address.Host} is not this machine: Passenger asks for a document only of the app on this machine");
    }

    private static bool SameOrigin(Uri one, Uri other) =>
        one.Scheme == other.Scheme && one.Port == other.Port && string.Equals(one.Host, other.Host, StringComparison.OrdinalIgnoreCase);
}
