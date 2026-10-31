using System.Globalization;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>A service's port as Compose's model has it: the container's port, the host's when published by number, its
/// protocol, and the rest of Compose's long syntax as written (the host's address it is published on, its mode, name and
/// application protocol), which a port Passenger pins keeps.</summary>
public sealed record ComposePort(int Target, string? Published, string Protocol, string? HostIp = null, string? Mode = null, string? Name = null, string? AppProtocol = null);

/// <summary>A service of a Compose application as <c>docker compose config --format json</c> says it: its build (context,
/// Dockerfile, target, args) or image, ports, environment's names, whether it has a <c>develop.watch</c>, and its
/// <c>depends_on</c>.</summary>
public sealed record ComposeService(string Name, string? Context, string? Dockerfile, string? Target, IReadOnlyDictionary<string, string> BuildArguments, string? Image,
    IReadOnlyList<ComposePort> Ports, IReadOnlyDictionary<string, string> Environment, bool Watches, IReadOnlyList<string> DependsOn)
{
    /// <summary>The networks it joins by name (Compose's model names <c>default</c> for one that names none); none when its
    /// <c>network_mode</c> puts it elsewhere.</summary>
    public IReadOnlyList<string> Networks { get; init; } = [];

    /// <summary>The Dockerfile's full path, for a service built from a folder here (not a repository, not an inline one).</summary>
    public string? DockerfilePath => Context is { } context && Directory.Exists(context) ? Path.GetFullPath(Path.Combine(context, Dockerfile ?? "Dockerfile")) : null;
}

/// <summary>A Compose application as Compose's own model says it, with its files, env files and profiles: its project's
/// name, its services and its networks (each with whether it is external). Passenger never merges compose files itself:
/// this is the engine's CLI's answer.</summary>
public sealed record ComposeModel(string Name, IReadOnlyList<ComposeService> Services, IReadOnlyDictionary<string, bool> Networks)
{
    public ComposeService? this[string service] => Services.FirstOrDefault(s => s.Name == service);

    /// <summary>Asks Compose for the model of the files (the words <c>compose -p … -f … --env-file … --profile …</c>):
    /// its JSON read, or a <see cref="ContainerCommandException"/> with what Compose said of the files.</summary>
    public static async Task<ComposeModel> ReadAsync(ContainerEngine engine, IReadOnlyList<string> compose, string folder, CancellationToken cancel = default)
    {
        Core.ChildResult result = await engine.RunAsync([.. compose, "config", "--format", "json"], ContainerEngine.AskLimit, cancel, folder);
        if (!result.Succeeded)
        {
            throw new ContainerCommandException(ContainerEngine.Said(result, folder));
        }
        using JsonDocument document = JsonDocument.Parse(result.Output);
        return Read(document.RootElement);
    }

    public static ComposeModel Read(JsonElement root)
    {
        List<ComposeService> services = [];
        if (root.TryGetProperty("services", out JsonElement all) && all.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty service in all.EnumerateObject())
            {
                JsonElement value = service.Value;
                (string? context, string? dockerfile, string? target, Dictionary<string, string> args) = (null, null, null, []);
                if (value.TryGetProperty("build", out JsonElement build) && build.ValueKind == JsonValueKind.Object)
                {
                    context = JsonMembers.Scalar(build, "context");
                    dockerfile = JsonMembers.Scalar(build, "dockerfile");
                    target = JsonMembers.Scalar(build, "target");
                    args = Map(build, "args");
                }
                List<ComposePort> ports = [];
                if (value.TryGetProperty("ports", out JsonElement listed) && listed.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement port in listed.EnumerateArray())
                    {
                        if (port.TryGetProperty("target", out JsonElement inside) && inside.TryGetInt32(out int number))
                        {
                            ports.Add(new ComposePort(number, JsonMembers.Scalar(port, "published"), JsonMembers.Scalar(port, "protocol") ?? "tcp",
                                JsonMembers.Scalar(port, "host_ip"), JsonMembers.Scalar(port, "mode"), JsonMembers.Scalar(port, "name"), JsonMembers.Scalar(port, "app_protocol")));
                        }
                    }
                }
                bool watches = value.TryGetProperty("develop", out JsonElement develop) && develop.ValueKind == JsonValueKind.Object
                    && develop.TryGetProperty("watch", out JsonElement watch) && watch.ValueKind == JsonValueKind.Array && watch.GetArrayLength() > 0;
                List<string> depends = value.TryGetProperty("depends_on", out JsonElement on) && on.ValueKind == JsonValueKind.Object
                    ? [.. on.EnumerateObject().Select(d => d.Name)] : [];
                List<string> joined = value.TryGetProperty("networks", out JsonElement nets) && nets.ValueKind == JsonValueKind.Object
                    ? [.. nets.EnumerateObject().Select(n => n.Name)] : [];
                services.Add(new ComposeService(service.Name, context, dockerfile, target, args, JsonMembers.Scalar(value, "image"), ports, Map(value, "environment"), watches, depends)
                {
                    Networks = joined,
                });
            }
        }
        Dictionary<string, bool> networks = [];
        if (root.TryGetProperty("networks", out JsonElement declared) && declared.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty network in declared.EnumerateObject())
            {
                networks[network.Name] = network.Value.ValueKind == JsonValueKind.Object && network.Value.TryGetProperty("external", out JsonElement external)
                    && external.ValueKind == JsonValueKind.True;
            }
        }
        return new ComposeModel(JsonMembers.Scalar(root, "name") ?? string.Empty, services, networks);

        static Dictionary<string, string> Map(JsonElement element, string name)
        {
            Dictionary<string, string> map = new(StringComparer.Ordinal);
            if (element.TryGetProperty(name, out JsonElement found) && found.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty entry in found.EnumerateObject())
                {
                    map[entry.Name] = entry.Value.ValueKind switch
                    {
                        JsonValueKind.String => entry.Value.GetString()!,
                        JsonValueKind.Null => string.Empty,
                        _ => entry.Value.GetRawText(),
                    };
                }
            }
            return map;
        }
    }

    /// <summary>A published port's number on the host, as Compose's model gives it ("8080", or "8080-8081" for a range,
    /// taken at its first).</summary>
    public static int? HostPort(string? published) =>
        published is { Length: > 0 } && int.TryParse(published.Split('-')[0], NumberStyles.None, CultureInfo.InvariantCulture, out int port) ? port : null;
}
