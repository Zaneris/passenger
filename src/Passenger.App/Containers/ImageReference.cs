namespace Passenger.App.Containers;

/// <summary>An image as its reference names it, <c>[registry/]path[:tag][@digest]</c>, read from its text alone: the
/// registry, where its first part names a host (a '.' or ':' in it, or localhost), else none, Docker Hub's; its path in that
/// registry; and its repository, the image as written without its tag or digest.</summary>
internal sealed record ImageReference(string Image, string? Registry, string Path, string Repository)
{
    /// <summary>Docker Hub's name, as docker keys its logins; an image naming no registry is pulled from it.</summary>
    public const string DockerHub = "docker.io";

    /// <summary>Where Docker Hub's API answers, under none of the names a person gives it.</summary>
    public const string DockerHubApi = "registry-1.docker.io";

    public static ImageReference Parse(string image)
    {
        string named = image.Split('@')[0];
        int colon = named.LastIndexOf(':');
        string repository = colon > named.LastIndexOf('/') ? named[..colon] : named;
        int slash = repository.IndexOf('/', StringComparison.Ordinal);
        string first = slash > 0 ? repository[..slash] : string.Empty;
        string? registry = first.Contains('.') || first.Contains(':') || first == "localhost" ? first : null;
        return new ImageReference(image, registry, registry is null ? repository : repository[(slash + 1)..], repository);
    }

    /// <summary>Whether a registry's host is Docker Hub, by any of the names it answers to, in any letter case.</summary>
    public static bool IsDockerHub(string host) =>
        host.Equals(DockerHub, StringComparison.OrdinalIgnoreCase) || host.Equals("index.docker.io", StringComparison.OrdinalIgnoreCase)
        || host.Equals(DockerHubApi, StringComparison.OrdinalIgnoreCase) || host.Equals("registry.hub.docker.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Its page on its registry: Docker Hub's for an image there, else the registry's own; null where a variable
    /// names any of it (<c>${REGISTRY}/app</c>), which only Compose or a build knows.</summary>
    public Uri? Page
    {
        get
        {
            if (Image.Contains('$', StringComparison.Ordinal) || Path.Length == 0)
            {
                return null;
            }
            if (Registry is null || IsDockerHub(Registry))
            {
                return new Uri(Path.StartsWith("library/", StringComparison.Ordinal) ? $"https://hub.docker.com/_/{Path["library/".Length..]}"
                    : Path.Contains('/') ? $"https://hub.docker.com/r/{Path}" : $"https://hub.docker.com/_/{Path}");
            }
            return Registry switch
            {
                "mcr.microsoft.com" => new Uri($"https://mcr.microsoft.com/artifact/mar/{Path}"),
                "ghcr.io" when Path.Contains('/') => new Uri($"https://github.com/{Path}/pkgs/container/{Path[(Path.LastIndexOf('/') + 1)..]}"),
                "quay.io" => new Uri($"https://quay.io/repository/{Path}"),
                _ => Uri.TryCreate($"https://{Registry}/", UriKind.Absolute, out Uri? host) ? host : null,
            };
        }
    }
}
