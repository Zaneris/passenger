using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>Whether docker's own configuration holds a login for a registry, as its CLI and the SDK's push read it: the
/// config.json of DOCKER_CONFIG (else ~/.docker), its auths and credHelpers, and the servers its credsStore's helper lists,
/// names alone, never a secret. Passenger only asks; a person logs in with docker login, never through Passenger, and the
/// SDK takes the login from docker's configuration itself.</summary>
public static class RegistryLogins
{
    /// <summary>Docker Hub's key in docker's configuration, which docker.io and its other names log in to.</summary>
    private const string DockerHub = "https://index.docker.io/v1/";

    private static readonly TimeSpan ListLimit = TimeSpan.FromSeconds(10);

    /// <summary>Why a push to a registry would find no login, saying how to make one; null where docker's configuration has
    /// one, or for a registry on this machine (localhost), which asks for none.</summary>
    public static async Task<string?> WhyNotAsync(string registry, string? cli, CancellationToken cancel)
    {
        string host = HostOf(registry);
        if (IsLocal(host))
        {
            return null;
        }
        string folder = Environment.GetEnvironmentVariable(EngineVariables.DockerConfig) is { Length: > 0 } named ? named
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker");
        string config = Path.Combine(folder, "config.json");
        string refused = $"No docker login for {host} was found in {PathNames.Shown(config, null)}: run docker login {host} in a terminal, then publish again";
        JsonElement root;
        try
        {
            using JsonDocument read = JsonDocument.Parse(await File.ReadAllTextAsync(config, cancel));
            root = read.RootElement.Clone();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return refused;
        }
        if (JsonMembers.Properties(root, "auths").Concat(JsonMembers.Properties(root, "credHelpers")).Any(login => Same(login.Name, host)))
        {
            return null;
        }
        if (JsonMembers.Text(root, "credsStore") is { Length: > 0 } store && await ListedAsync(store, cli, cancel) is { } listed && listed.Any(server => Same(server, host)))
        {
            return null;
        }
        return refused;
    }

    /// <summary>The servers a credential helper holds logins for (its list, names and users alone); null where it is not
    /// found or does not answer.</summary>
    private static async Task<IReadOnlyList<string>?> ListedAsync(string store, string? cli, CancellationToken cancel)
    {
        string name = $"docker-credential-{store}";
        string? helper = Programs.OnPath(name)
            ?? (cli is not null && Path.GetDirectoryName(cli) is { } beside && Directory.EnumerateFiles(beside, name + "*").FirstOrDefault() is { } found ? found : null);
        if (helper is null)
        {
            return null;
        }
        try
        {
            ChildResult listed = await ChildProcesses.RunAsync(ChildProcesses.Command(helper, ["list"]), limit: ListLimit, cancel: cancel);
            if (!listed.Succeeded)
            {
                return null;
            }
            using JsonDocument servers = JsonDocument.Parse(listed.Output);
            return servers.RootElement.ValueKind == JsonValueKind.Object ? [.. servers.RootElement.EnumerateObject().Select(server => server.Name)] : null;
        }
        catch (Exception error) when (error is JsonException or TimeoutException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A registry's host as docker keys its logins: Docker Hub's names as one, a scheme and path left out.</summary>
    public static string HostOf(string registry)
    {
        string host = registry.Trim();
        int scheme = host.IndexOf("://", StringComparison.Ordinal);
        host = scheme >= 0 ? host[(scheme + 3)..] : host;
        host = host.Split('/')[0].ToLowerInvariant();
        return host is "docker.io" or "index.docker.io" or "registry-1.docker.io" or "registry.hub.docker.com" ? "docker.io" : host;
    }

    private static bool Same(string key, string host) => key == DockerHub ? host == "docker.io" : HostOf(key) == host;

    /// <summary>Whether a registry is this machine's, which docker reaches with no login.</summary>
    private static bool IsLocal(string host)
    {
        string name = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
        return name is "localhost" or "127.0.0.1" or "[::1]" || name.EndsWith(".localhost", StringComparison.Ordinal);
    }
}
