namespace Passenger.App.Containers;

/// <summary>Where a container's paths are the workspace's files, both ways: each folder an image's build copied from its
/// context (a Dockerfile's <c>COPY . .</c> into its WORKDIR, a project's folder copied on its own) and a deterministic
/// build's <c>/_/</c>, the source root, which a Dockerfile's build makes the context. A stack frame or a compiler message
/// from an image's program names <c>/src/Web/Program.cs</c>, which <see cref="ToHost"/> makes the workspace's
/// <c>src/Web/Program.cs</c>; <see cref="ToContainer"/> goes back, for a debugger at the container's edge. Immutable and
/// thread-safe: what is read from the Dockerfile once, used by any console and by the build's problems.</summary>
public sealed class ContainerPaths
{
    /// <summary>The container's folders and the host's, longest container folder first, so the deepest copy wins.</summary>
    private readonly (string Container, string Host)[] pairs;

    public ContainerPaths(IEnumerable<(string Container, string Host)> pairs)
    {
        this.pairs = [.. pairs
            .Select(p => (Folder(p.Container.Replace('\\', '/')), Path.TrimEndingDirectorySeparator(p.Host)))
            .Where(p => p.Item1.Length > 1)
            .DistinctBy(p => p.Item1, StringComparer.Ordinal)
            .OrderByDescending(p => p.Item1.Length)];
    }

    public static ContainerPaths None { get; } = new([]);

    /// <summary>The map's pairs, container folder first, for a reader to show or pass on.</summary>
    public IReadOnlyList<(string Container, string Host)> Pairs => pairs;

    public bool IsEmpty => pairs.Length == 0;

    /// <summary>The map of an image built from a Dockerfile with a context: every folder its stages copy from the context
    /// (a file copied names its own folder's place), and <c>/_/</c> as the context's root.</summary>
    public static ContainerPaths For(DockerfileStages dockerfile, string context)
    {
        List<(string, string)> found = [("/_/", context)];
        foreach (DockerfileStage stage in dockerfile.Stages)
        {
            foreach ((string source, string target) in stage.Copies)
            {
                string from = FromContext(source.Replace('\\', '/'));
                bool folder = source is "." or "./" || source.EndsWith('/') || Directory.Exists(Path.Combine(context, from));
                if (folder)
                {
                    found.Add((target, from.Length == 0 ? context : Path.Combine(context, from)));
                }
                else if (Path.GetDirectoryName(from) is { } parent && target.EndsWith('/'))
                {
                    // COPY ["src/Web/Web.csproj", "src/Web/"]: that folder of the context is that folder of the image.
                    found.Add((target, parent.Length == 0 ? context : Path.Combine(context, parent)));
                }
            }
        }
        return new ContainerPaths(found);
    }

    /// <summary>A COPY source as a path from the context: its leading <c>./</c> and <c>/</c> taken off, the context itself
    /// empty, a name beginning with a dot (<c>.config/</c>) kept whole.</summary>
    private static string FromContext(string source)
    {
        while (true)
        {
            if (source.StartsWith("./", StringComparison.Ordinal))
            {
                source = source[2..];
            }
            else if (source.StartsWith('/'))
            {
                source = source[1..];
            }
            else
            {
                return source == "." ? string.Empty : source;
            }
        }
    }

    /// <summary>This map with another's pairs: a Compose application's images together.</summary>
    public ContainerPaths With(ContainerPaths other) => new([.. pairs, .. other.pairs]);

    /// <summary>The workspace's file for a container's path, or null for a path no copy covers, and for one that would lead
    /// out of the folder it maps to (a <c>..</c>, a <c>//</c>, a rooted rest): a container's words never name a file
    /// elsewhere on this machine.</summary>
    public string? ToHost(string containerPath)
    {
        string path = containerPath.Replace('\\', '/');
        foreach ((string container, string host) in pairs)
        {
            if (!path.StartsWith(container, StringComparison.Ordinal))
            {
                continue;
            }
            string rest = path[container.Length..];
            if (rest.StartsWith('/') || rest.Split('/').Contains("..") || rest.Contains('\0', StringComparison.Ordinal)
                || (OperatingSystem.IsWindows() && rest.Contains(':', StringComparison.Ordinal)))
            {
                return null;
            }
            string folder = Path.GetFullPath(host);
            string file = Path.GetFullPath(Path.Combine(folder, rest.Replace('/', Path.DirectorySeparatorChar)));
            return Core.PathNames.Within(file, folder) ? Path.Combine(host, rest.Replace('/', Path.DirectorySeparatorChar)) : null;
        }
        return null;
    }

    /// <summary>The container's path for a workspace file, or null for a file no copy covers.</summary>
    public string? ToContainer(string hostPath)
    {
        foreach ((string container, string host) in pairs.Where(p => p.Container != "/_/").OrderByDescending(p => p.Host.Length))
        {
            if (Core.PathNames.IsUnder(hostPath, host) || Core.PathNames.Same(hostPath, host))
            {
                string rest = Path.GetRelativePath(host, hostPath).Replace('\\', '/');
                return rest == "." ? container.TrimEnd('/') : container + rest;
            }
        }
        return null;
    }

    private static string Folder(string path) => path.EndsWith('/') ? path : path + "/";
}
