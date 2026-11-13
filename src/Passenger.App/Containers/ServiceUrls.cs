namespace Passenger.App.Containers;

/// <summary>A container service's address as a configuration writes it, in Visual Studio's tokens: <c>{Scheme}</c>,
/// <c>{ServiceHost}</c> (a .dcproj's DockerServiceUrl names it <c>{ServiceIPAddress}</c>) and <c>{ServicePort}</c>, in any
/// letter case.</summary>
public static class ServiceUrls
{
    /// <summary>Whether an address has a token to fill, so names no page until the service's port is published.</summary>
    public static bool HasTokens(string url) => url.Contains('{', StringComparison.Ordinal);

    /// <summary>An address with its tokens filled: the scheme the service serves, the host its published port is reached on
    /// and that port.</summary>
    public static string Filled(string url, string scheme, string host, string port) =>
        url.Replace("{Scheme}", scheme, StringComparison.OrdinalIgnoreCase)
            .Replace("{ServiceHost}", host, StringComparison.OrdinalIgnoreCase)
            .Replace("{ServiceIPAddress}", host, StringComparison.OrdinalIgnoreCase)
            .Replace("{ServicePort}", port, StringComparison.OrdinalIgnoreCase);
}
