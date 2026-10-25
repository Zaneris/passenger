namespace Passenger.App.Web;

/// <summary>Whether and where the Dockerfile server asks for an image's tags as it completes <c>FROM image:</c>: Docker
/// Hub only while Settings' Complete image tags from Docker Hub is on. The server runs with Passenger's preload
/// (<c>web/passenger/docker-hub.cjs</c>), told through <c>PASSENGER_DOCKER_HUB</c> to ask nothing, or to ask another
/// place: a window's stand-in, else the one the IDE's own <c>PASSENGER_DOCKER_HUB</c> names.</summary>
public static class DockerHubTags
{
    [IdentifierHome]
    public const string Variable = "PASSENGER_DOCKER_HUB";

    /// <summary>The preload, carried beside the web language servers.</summary>
    public static string Preload => Path.Combine(WebLanguages.Carried, "passenger", "docker-hub.cjs");

    /// <summary>What the server's <c>PASSENGER_DOCKER_HUB</c> says: off while the setting is (<paramref name="asks"/>
    /// false), else the place standing in for Docker Hub, else nothing (Docker Hub itself).</summary>
    public static string? Told(bool asks, Uri? standIn) => !asks ? "off" : (standIn ?? Named())?.AbsoluteUri;

    /// <summary>Checks <c>PASSENGER_DOCKER_HUB</c> as the IDE starts, a malformed one being fatal.</summary>
    public static void CheckVariable() => Named();

    /// <summary>The place the IDE's <c>PASSENGER_DOCKER_HUB</c> names; null when it names none.</summary>
    private static Uri? Named()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } named)
        {
            return null;
        }
        if (!Uri.TryCreate(named, UriKind.Absolute, out Uri? place) || (place.Scheme != Uri.UriSchemeHttp && place.Scheme != Uri.UriSchemeHttps))
        {
            throw new FormatException($"{Variable} takes the http or https URL Docker Hub's requests for image tags go to instead, not '{named}'");
        }
        return place;
    }
}
