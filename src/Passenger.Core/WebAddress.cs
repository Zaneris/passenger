namespace Passenger.Core;

/// <summary>An address something else's answer names (a release's JSON, a package feed's advisory), opened only as a web
/// page.</summary>
public static class WebAddress
{
    /// <summary>An absolute http or https URL, or null.</summary>
    public static Uri? Of(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri : null;
}
