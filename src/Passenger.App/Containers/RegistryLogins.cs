using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>Whether a push would find a login for a registry, looked for as the SDK looks: the pair of variables it reads
/// first (<see cref="RegistryVariables"/>), then each file it reads, Podman's auth.json before docker's config.json, for the
/// registry's credHelpers entry, a credsStore or the registry's auths entry. A helper answers only as it is asked, so what it
/// lists proves nothing and it is left to answer the SDK. Names alone, never a secret. Passenger only asks; a person logs in
/// with docker login, never through Passenger, and the SDK takes the login itself.</summary>
public static class RegistryLogins
{
    /// <summary>Docker Hub's key in docker's configuration, which docker.io and its other names log in to.</summary>
    private const string DockerHub = "https://index.docker.io/v1/";

    /// <summary>Why a push to a registry would find no login, saying how to make one; null where the SDK would find one, or
    /// for a registry on this machine (localhost), which asks for none. The files are read off the window's thread.</summary>
    public static Task<string?> WhyNotAsync(string registry, CancellationToken cancel)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return BackgroundWork.Run(() => WhyNot(registry, Environment.GetEnvironmentVariable, home, Read), cancel);
    }

    /// <summary>As <see cref="WhyNotAsync"/>, in the variables and folder given, <paramref name="read"/> giving a file's text,
    /// or null for one not there or not read.</summary>
    public static string? WhyNot(string registry, Func<string, string?> variable, string home, Func<string, string?> read)
    {
        string host = HostOf(registry);
        if (IsLocal(host) || Given(variable) is not null)
        {
            return null;
        }
        IReadOnlyList<string> files = ConfigFiles(variable, home);
        return files.Any(file => read(file) is { } text && Answers(text, host)) ? null
            : $"No docker login for {host} was found in {PathNames.Shown(files[^1], null)}: run docker login {host} in a terminal, then publish again";
    }

    /// <summary>How a push to a registry logs in, as Publish says it.</summary>
    public static string With(string registry) => Given(Environment.GetEnvironmentVariable) is { } pair
        ? $"with the login {pair.User} and {pair.Password} give"
        : $"with docker's login for {HostOf(registry)} (docker login)";

    /// <summary>The first pair of variables a push reads a login from whose name and password are both set, its values never
    /// looked at again.</summary>
    private static (string User, string Password)? Given(Func<string, string?> variable) =>
        RegistryVariables.PushLogins.Where(pair => !string.IsNullOrEmpty(variable(pair.User)) && !string.IsNullOrEmpty(variable(pair.Password)))
            .Select(pair => ((string User, string Password)?)pair).FirstOrDefault();

    /// <summary>The files a login is read from, in the SDK's order: <see cref="RegistryVariables.AuthFile"/> alone where it is
    /// set, else Podman's two auth.json and docker's config.json.</summary>
    private static IReadOnlyList<string> ConfigFiles(Func<string, string?> variable, string home)
    {
        if (variable(RegistryVariables.AuthFile) is { Length: > 0 } named)
        {
            return [named];
        }
        string config = variable(RegistryVariables.ConfigFolder) is { Length: > 0 } folder ? folder : Path.Combine(home, ".config");
        string docker = variable(EngineVariables.DockerConfig) is { Length: > 0 } dockers ? dockers : Path.Combine(home, ".docker");
        return [.. variable(SocketFolders.RuntimeVariable) is { Length: > 0 } runtime ? [Path.Combine(runtime, "containers", "auth.json")] : (string[])[],
            Path.Combine(config, "containers", "auth.json"), Path.Combine(docker, "config.json")];
    }

    /// <summary>Whether a file's text gives a host's login, or a helper that is asked for it; one that does not read gives none.</summary>
    private static bool Answers(string text, string host)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(text);
            JsonElement root = read.RootElement;
            return JsonMembers.Text(root, "credsStore") is { Length: > 0 }
                || JsonMembers.Properties(root, "auths").Concat(JsonMembers.Properties(root, "credHelpers")).Any(login => Same(login.Name, host));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Read(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
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
