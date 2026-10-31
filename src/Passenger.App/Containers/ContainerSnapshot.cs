using System.Globalization;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>A port a container publishes: on the host's port, from the container's, by its protocol. A TCP one is a link
/// to its port on the engine's machine: this one's (<c>http://localhost:port</c>) for an engine on a socket here, the
/// remote one's for an engine reached over TCP or SSH (<see cref="LinkOn"/>).</summary>
public sealed record ContainerPort(string HostPort, string Inside, string Protocol)
{
    /// <summary>The link on the machine of the engine at an endpoint (the context's <c>DOCKER_HOST</c>): a <c>tcp://</c>,
    /// <c>ssh://</c> or <c>http(s)://</c> host is another machine, anything else (a socket, a pipe, none) this one.</summary>
    public Uri? LinkOn(string? endpoint)
    {
        if (Protocol != "tcp" || !int.TryParse(HostPort, NumberStyles.None, CultureInfo.InvariantCulture, out int port))
        {
            return null;
        }
        string host = endpoint is not null && Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? engine) && engine.Scheme is "tcp" or "ssh" or "http" or "https"
            && engine.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6 && engine.Host.Length > 0
            ? engine.Host : "localhost";
        return Uri.TryCreate($"http://{host}:{port}/", UriKind.Absolute, out Uri? link) ? link : null;
    }

    public override string ToString() => $"{HostPort}→{Inside}{(Protocol == "tcp" ? string.Empty : $"/{Protocol}")}";

    /// <summary>The ports a row of <c>docker ps --format json</c> publishes: Docker's words, or Podman's own list of them
    /// (<c>host_port</c>, <c>container_port</c>, <c>protocol</c>).</summary>
    public static IReadOnlyList<ContainerPort> Read(JsonElement row)
    {
        if (JsonMembers.Scalar(row, "Ports") is { } said)
        {
            return Read(said);
        }
        List<ContainerPort> ports = [];
        foreach (JsonElement published in JsonMembers.Items(row, "Ports"))
        {
            if (JsonMembers.Scalar(published, "host_port") is { } host and not "0" && JsonMembers.Scalar(published, "container_port") is { } inside
                && new ContainerPort(host, inside, JsonMembers.Scalar(published, "protocol") ?? "tcp") is var port && !ports.Contains(port))
            {
                ports.Add(port);
            }
        }
        return ports;
    }

    /// <summary>The ports in <c>docker ps</c>'s words ("0.0.0.0:8080->80/tcp, [::]:8080->80/tcp, 443/tcp"): those published,
    /// each once whichever addresses it is on.</summary>
    public static IReadOnlyList<ContainerPort> Read(string? said)
    {
        List<ContainerPort> ports = [];
        foreach (string part in (said ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int arrow = part.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0)
            {
                continue;
            }
            string host = part[..arrow];
            string inside = part[(arrow + 2)..];
            string[] protocol = inside.Split('/');
            ContainerPort port = new(host[(host.LastIndexOf(':') + 1)..], protocol[0], protocol.Length > 1 ? protocol[1] : "tcp");
            if (!ports.Contains(port))
            {
                ports.Add(port);
            }
        }
        return ports;
    }
}

/// <summary>A container as <c>docker ps -a</c> lists it: its id, name, image, state (running, exited, paused, created,
/// restarting, dead), status words, health (healthy, unhealthy, starting, or none), ports and labels, and the Compose
/// project and service it belongs to, if any.</summary>
public sealed record ContainerInfo(string Id, string Name, string Image, string State, string Status, string? Health, IReadOnlyList<ContainerPort> Ports,
    IReadOnlyDictionary<string, string> Labels)
{
    public string? Project => Labels.GetValueOrDefault(ComposeLabels.Project);

    public string? Service => Labels.GetValueOrDefault(ComposeLabels.Service);

    /// <summary>The engine holding it where that is not the window's: an AppHost's orchestrator's own runtime (Podman's
    /// beside Docker's), which every command on it goes to; null for the window's own.</summary>
    public ContainerEngine? Engine { get; init; }

    /// <summary>Started by Testcontainers (its <c>org.testcontainers</c> label), for a test.</summary>
    public bool IsTestcontainers => Labels.ContainsKey("org.testcontainers");

    public bool IsRunning => State is "running" or "restarting";

    public bool IsPaused => State == "paused";

    /// <summary>The code it exited with, as its status says it ("Exited (1) 2 minutes ago"); null while it has none.</summary>
    public int? ExitCode => Status.StartsWith("Exited (", StringComparison.Ordinal) && Status.IndexOf(')', 8) is var end and > 8
        && int.TryParse(Status.AsSpan(8, end - 8), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int code) ? code : null;

    /// <summary>Whether it failed: started again and again after each crash (restarting), dead, or exited with a code other
    /// than a stop's (0, and SIGTERM's 143 and SIGKILL's 137, the codes a person's Stop ends a container with).</summary>
    public bool IsFailed => State is "restarting" or "dead" || State == "exited" && ExitCode is { } code && code is not (0 or 137 or 143);

    internal static ContainerInfo Read(JsonElement row)
    {
        string status = JsonMembers.Scalar(row, "Status") ?? string.Empty;
        string? health = JsonMembers.Scalar(row, "HealthStatus") is { } said && said != "none" ? said
            : status.Contains("(healthy)", StringComparison.Ordinal) ? "healthy"
            : status.Contains("(unhealthy)", StringComparison.Ordinal) ? "unhealthy"
            : status.Contains("(health: starting)", StringComparison.Ordinal) || status.Contains("(starting)", StringComparison.Ordinal) ? "starting" : null;
        return new ContainerInfo(IdOf(row) ?? string.Empty, First(row, "Names") ?? string.Empty,
            JsonMembers.Scalar(row, "Image") ?? string.Empty, (JsonMembers.Scalar(row, "State") ?? string.Empty).ToLowerInvariant(), status, health,
            ContainerPort.Read(row), ReadLabels(row));
    }

    /// <summary>A row's id: Docker's CLI writes ID, Podman's own Id (an image's, a container's) or id (a network's).</summary>
    internal static string? IdOf(JsonElement row) => JsonMembers.Scalar(row, "ID") ?? JsonMembers.Scalar(row, "Id") ?? JsonMembers.Scalar(row, "id");

    /// <summary>The first of a row's names: Docker's CLI joins them with commas, Podman's own lists them.</summary>
    internal static string? First(JsonElement row, string name) =>
        JsonMembers.Texts(row, name) is [var first, ..] ? first : JsonMembers.Scalar(row, name)?.Split(',')[0];

    /// <summary>A row's labels: Docker's CLI joins them ("a=1,b=2"), Podman's own gives an object (Labels, or a network's labels).</summary>
    internal static IReadOnlyDictionary<string, string> ReadLabels(JsonElement row)
    {
        if ((JsonMembers.Object(row, "Labels") ?? JsonMembers.Object(row, "labels")) is { } labels)
        {
            return labels.EnumerateObject().ToDictionary(l => l.Name, l => l.Value.ToString(), StringComparer.Ordinal);
        }
        return ReadLabels(JsonMembers.Scalar(row, "Labels"));
    }

    /// <summary>Labels as the CLI's table joins them, "a=1,b=2": a part with no '=' belongs to the value before it (Compose's
    /// depends_on lists services with commas).</summary>
    internal static IReadOnlyDictionary<string, string> ReadLabels(string? joined)
    {
        Dictionary<string, string> labels = new(StringComparer.Ordinal);
        string? lastKey = null;
        foreach (string part in (joined ?? string.Empty).Split(','))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && !part[..equals].Contains(':', StringComparison.Ordinal))
            {
                lastKey = part[..equals];
                labels[lastKey] = part[(equals + 1)..];
            }
            else if (lastKey is not null)
            {
                labels[lastKey] += "," + part;
            }
        }
        return labels;
    }
}

/// <summary>Compose's own labels on what it makes.</summary>
public static class ComposeLabels
{
    public const string Project = "com.docker.compose.project";
    public const string Service = "com.docker.compose.service";
    public const string ConfigFiles = "com.docker.compose.project.config_files";
    public const string WorkingDir = "com.docker.compose.project.working_dir";
}

/// <summary>A Compose project: its name, its status as <c>docker compose ls</c> words it ("running(2)", "exited(1)"), its
/// files and folder, and its services, each with its containers (a stopped project's too, while they exist).</summary>
public sealed record ComposeProject(string Name, string Status, IReadOnlyList<string> ConfigFiles, string? WorkingDir,
    IReadOnlyList<(string Service, IReadOnlyList<ContainerInfo> Containers)> Services)
{
    public IEnumerable<ContainerInfo> Containers => Services.SelectMany(s => s.Containers);

    public bool IsRunning => Containers.Any(c => c.IsRunning);

    /// <summary>The words that make <c>docker compose</c> act on this project: its name, and its files and folder where
    /// they are known (to pull, scale or start what it has not made yet).</summary>
    public IReadOnlyList<string> Arguments
    {
        get
        {
            List<string> words = ["compose", "-p", Name];
            foreach (string file in ConfigFiles.Where(File.Exists))
            {
                words.AddRange(["-f", file]);
            }
            if (WorkingDir is { } folder && Directory.Exists(folder))
            {
                words.AddRange(["--project-directory", folder]);
            }
            return words;
        }
    }
}

/// <summary>An image, a volume or a network, as the CLI lists it: its kind, id, name and the words after it.</summary>
public sealed record EngineObject(string Kind, string Id, string Name, string Detail, IReadOnlyDictionary<string, string> Labels);

/// <summary>What the engine holds, read in one go: its Compose projects (with their services and containers), every other
/// container, and its images, volumes and networks.</summary>
public sealed record ContainerSnapshot(IReadOnlyList<ComposeProject> Projects, IReadOnlyList<ContainerInfo> Containers, IReadOnlyList<EngineObject> Images,
    IReadOnlyList<EngineObject> Volumes, IReadOnlyList<EngineObject> Networks)
{
    public static ContainerSnapshot Empty { get; } = new([], [], [], [], []);

    /// <summary>Every container, those of projects first.</summary>
    public IEnumerable<ContainerInfo> AllContainers => Projects.SelectMany(p => p.Containers).Concat(Containers);

    /// <summary>Reads it all, each list asked of the CLI at once; a <see cref="ContainerCommandException"/> when one fails,
    /// but for Compose's list of projects, which only words their statuses (an engine with no Compose v2 has none: the
    /// projects are its containers' labels, their statuses their states). <paramref name="containersOnly"/> reads the
    /// containers and projects alone (a container named by an agent), its images, volumes and networks none.</summary>
    public static async Task<ContainerSnapshot> ReadAsync(ContainerEngine engine, CancellationToken cancel = default, bool containersOnly = false)
    {
        Task<IReadOnlyList<JsonElement>> containers = engine.JsonLinesAsync(["ps", "-a", "--format", "json"], cancel);
        Task<IReadOnlyList<JsonElement>> projects = ProjectsAsync();
        Task<IReadOnlyList<JsonElement>> images = containersOnly ? Task.FromResult<IReadOnlyList<JsonElement>>([]) : engine.JsonLinesAsync(["images", "--format", "json"], cancel);
        Task<IReadOnlyList<JsonElement>> volumes = containersOnly ? Task.FromResult<IReadOnlyList<JsonElement>>([]) : engine.JsonLinesAsync(["volume", "ls", "--format", "json"], cancel);
        Task<IReadOnlyList<JsonElement>> networks = containersOnly ? Task.FromResult<IReadOnlyList<JsonElement>>([]) : engine.JsonLinesAsync(["network", "ls", "--format", "json"], cancel);
        await Task.WhenAll(containers, projects, images, volumes, networks);
        List<ContainerInfo> all = [.. (await containers).Select(ContainerInfo.Read).OrderBy(c => c.Name, StringComparer.Ordinal)];
        if (engine.Known is { IsPodman: true, PodmanCli: false })
        {
            all = await InspectedAsync(engine, all, cancel);
        }
        Dictionary<string, string> statuses = (await projects).Where(p => JsonMembers.Scalar(p, "Name") is not null)
            .ToDictionary(p => JsonMembers.Scalar(p, "Name")!, p => JsonMembers.Scalar(p, "Status") ?? string.Empty, StringComparer.Ordinal);
        List<ComposeProject> composed = [];
        foreach (IGrouping<string, ContainerInfo> project in all.Where(c => c.Project is not null).GroupBy(c => c.Project!).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            ContainerInfo first = project.First();
            string[] files = first.Labels.GetValueOrDefault(ComposeLabels.ConfigFiles)?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
            List<(string, IReadOnlyList<ContainerInfo>)> services = [.. project.GroupBy(c => c.Service ?? c.Name).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (g.Key, (IReadOnlyList<ContainerInfo>)[.. g]))];
            composed.Add(new ComposeProject(project.Key, statuses.GetValueOrDefault(project.Key) ?? Status(project), files,
                first.Labels.GetValueOrDefault(ComposeLabels.WorkingDir), services));
        }
        return new ContainerSnapshot(composed, [.. all.Where(c => c.Project is null)],
            [.. (await images).Select(i => new EngineObject("image", ContainerInfo.IdOf(i) ?? string.Empty, ImageName(i), ImageDetail(i), new Dictionary<string, string>()))],
            [.. (await volumes).Select(v => new EngineObject("volume", JsonMembers.Scalar(v, "Name") ?? string.Empty, JsonMembers.Scalar(v, "Name") ?? string.Empty,
                JsonMembers.Scalar(v, "Driver") ?? string.Empty, ContainerInfo.ReadLabels(v)))
                .OrderBy(v => v.Labels.ContainsKey("com.docker.volume.anonymous")).ThenBy(v => v.Name, StringComparer.Ordinal)],
            [.. (await networks).Select(n => new EngineObject("network", ContainerInfo.IdOf(n) ?? string.Empty, JsonMembers.Scalar(n, "Name") ?? JsonMembers.Scalar(n, "name") ?? string.Empty,
                JsonMembers.Scalar(n, "Driver") ?? JsonMembers.Scalar(n, "driver") ?? string.Empty, ContainerInfo.ReadLabels(n)))
                .OrderBy(n => n.Name, StringComparer.Ordinal)]);

        static string Status(IEnumerable<ContainerInfo> containers) =>
            string.Join(", ", containers.GroupBy(c => c.State).Select(g => $"{g.Key}({g.Count()})"));

        async Task<IReadOnlyList<JsonElement>> ProjectsAsync()
        {
            try
            {
                return await engine.JsonLinesAsync(["compose", "ls", "-a", "--format", "json"], cancel);
            }
            catch (Exception unread) when (unread is ContainerCommandException or JsonException)
            {
                return [];
            }
        }
    }

    /// <summary>The running containers' health and published ports, which Podman's Docker API leaves out of its list (no
    /// health in the status words, no address on a port, so Docker's CLI shows none published): asked of inspect, all at
    /// once (one removed meanwhile leaves the others answered).</summary>
    private static async Task<List<ContainerInfo>> InspectedAsync(ContainerEngine engine, List<ContainerInfo> all, CancellationToken cancel)
    {
        string[] running = [.. all.Where(c => c.IsRunning).Select(c => c.Id)];
        if (running.Length == 0)
        {
            return all;
        }
        Core.ChildResult said = await engine.RunAsync(["inspect", "--type", "container", "--format",
            "{{.Id}}\t{{if .State.Health}}{{.State.Health.Status}}{{end}}\t{{json .NetworkSettings.Ports}}", .. running], cancel: cancel);
        Dictionary<string, (string Health, IReadOnlyList<ContainerPort> Ports)> read = new(StringComparer.Ordinal);
        foreach (string line in said.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Split('\t', 3) is [{ } id, { } health, { } ports])
            {
                read[id] = (health, PortsOf(ports));
            }
        }
        // The list gives a short id, inspect the whole.
        return [.. all.Select(c => read.Where(r => c.Id.Length > 0 && r.Key.StartsWith(c.Id, StringComparison.Ordinal)).Select(r => r.Value).FirstOrDefault() is { Ports: not null } found
            ? c with { Health = c.Health ?? (found.Health.Length > 0 ? found.Health : null), Ports = c.Ports.Count > 0 ? c.Ports : found.Ports }
            : c)];

        // NetworkSettings.Ports: each port inside, its bindings on the host (null where it is not published).
        static IReadOnlyList<ContainerPort> PortsOf(string json)
        {
            List<ContainerPort> ports = [];
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return ports;
                }
                foreach (JsonProperty inside in document.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array))
                {
                    string[] port = inside.Name.Split('/');
                    foreach (JsonElement binding in inside.Value.EnumerateArray())
                    {
                        if (JsonMembers.Scalar(binding, "HostPort") is { Length: > 0 } host
                            && new ContainerPort(host, port[0], port.Length > 1 ? port[1] : "tcp") is var published && !ports.Contains(published))
                        {
                            ports.Add(published);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Not what inspect writes: none read.
            }
            return ports;
        }
    }

    private static string ImageName(JsonElement image)
    {
        // Podman's own command names an image in full ("docker.io/library/busybox:1.37"), with no repository and tag apart.
        if (JsonMembers.Scalar(image, "Repository") is null && ContainerInfo.First(image, "Names") is { } named)
        {
            return named;
        }
        string repository = JsonMembers.Scalar(image, "Repository") ?? "<none>";
        string tag = JsonMembers.Scalar(image, "Tag") ?? "<none>";
        return repository == "<none>" ? ContainerInfo.IdOf(image) ?? repository : tag == "<none>" ? repository : $"{repository}:{tag}";
    }

    /// <summary>An image's size and age as Docker's CLI words them; Podman's own gives the size in bytes and no age.</summary>
    private static string ImageDetail(JsonElement image)
    {
        string? size = JsonMembers.Scalar(image, "Size");
        if (image.TryGetProperty("Size", out JsonElement bytes) && bytes.ValueKind == JsonValueKind.Number && bytes.TryGetInt64(out long count))
        {
            size = count >= 1_000_000_000 ? string.Create(CultureInfo.InvariantCulture, $"{count / 1e9:0.##}GB")
                : count >= 1_000_000 ? string.Create(CultureInfo.InvariantCulture, $"{count / 1e6:0.##}MB")
                : string.Create(CultureInfo.InvariantCulture, $"{count / 1e3:0.##}kB");
        }
        return JsonMembers.Scalar(image, "CreatedSince") is { } since ? $"{size}, {since}" : size ?? string.Empty;
    }
}
