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

/// <summary>The environment variables the SDK's container push reads a registry's login from, each named once: a pair of
/// name and password, the first set taken, and what names the files it reads.</summary>
[IdentifierHome]
public static class RegistryVariables
{
    public const string PushUser = "DOTNET_CONTAINER_PUSH_REGISTRY_UNAME";

    public const string PushPassword = "DOTNET_CONTAINER_PUSH_REGISTRY_PWORD";

    public const string User = "DOTNET_CONTAINER_REGISTRY_UNAME";

    public const string Password = "DOTNET_CONTAINER_REGISTRY_PWORD";

    public const string SdkUser = "SDK_CONTAINER_REGISTRY_UNAME";

    public const string SdkPassword = "SDK_CONTAINER_REGISTRY_PWORD";

    /// <summary>The one file logins are read from, where it is set.</summary>
    public const string AuthFile = "REGISTRY_AUTH_FILE";

    /// <summary>Where Podman's kept login is, under containers/, in place of ~/.config.</summary>
    public const string ConfigFolder = "XDG_CONFIG_DIR";

    /// <summary>The pairs a push reads, in the order it reads them.</summary>
    public static readonly IReadOnlyList<(string User, string Password)> PushLogins = [(PushUser, PushPassword), (User, Password), (SdkUser, SdkPassword)];
}
