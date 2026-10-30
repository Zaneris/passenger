namespace Passenger.App.Containers;

/// <summary>The environment variables a container engine's CLI reads the engine it reaches from, and how (Docker's, and
/// Podman's own command's), each named once.</summary>
[IdentifierHome]
public static class EngineVariables
{
    /// <summary>The engine's endpoint, which Testcontainers reads too.</summary>
    public const string DockerHost = "DOCKER_HOST";

    public const string DockerContext = "DOCKER_CONTEXT";

    public const string DockerConfig = "DOCKER_CONFIG";

    public const string DockerTlsVerify = "DOCKER_TLS_VERIFY";

    public const string DockerCertPath = "DOCKER_CERT_PATH";

    public const string DockerTls = "DOCKER_TLS";

    public const string PodmanHost = "CONTAINER_HOST";

    public const string PodmanConnection = "CONTAINER_CONNECTION";

    public const string PodmanConfig = "CONTAINERS_CONF";

    /// <summary>Every one, Docker's first.</summary>
    public static readonly IReadOnlyList<string> All =
        [DockerHost, DockerContext, DockerConfig, DockerTlsVerify, DockerCertPath, DockerTls, PodmanHost, PodmanConnection, PodmanConfig];
}
