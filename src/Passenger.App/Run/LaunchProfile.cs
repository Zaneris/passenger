using System.Text;
using System.Text.Json;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>A launch profile from a project's <c>Properties/launchSettings.json</c> or a file-based program's
/// <c>.run.json</c>: what it launches (its <c>commandName</c>), with what arguments, environment and working directory, and
/// the browser it opens. Passenger launches the two kinds that run a program (<see cref="ProjectCommand"/> and
/// <see cref="ExecutableCommand"/>), the two that run the project in a container (<c>Docker</c>, <c>SdkContainer</c>, with
/// Visual Studio's container properties: its ports, whether it serves HTTPS, its <c>docker run</c> words and env files),
/// and a Docker Compose project's (<see cref="DockerComposeCommand"/>, with its services' actions); every other kind is
/// read too, and says why it does not run here.</summary>
public sealed record LaunchProfile(
    string Name,
    string? Command,
    string? ExecutablePath,
    string? CommandLineArgs,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    string? ApplicationUrl,
    bool LaunchBrowser,
    string? LaunchUrl)
{
    /// <summary>A container profile's host port for the container's HTTP port (<c>httpPort</c>), or null for the engine's choice.</summary>
    public int? HttpPort { get; init; }

    /// <summary>A container profile's host port for its HTTPS port (<c>sslPort</c>).</summary>
    public int? SslPort { get; init; }

    /// <summary>Whether the container serves HTTPS (<c>useSSL</c>), which takes the development certificate.</summary>
    public bool UseSsl { get; init; }

    /// <summary>Whether every port the image exposes is published (<c>publishAllPorts</c>).</summary>
    public bool PublishAllPorts { get; init; }

    /// <summary>The container's <c>docker run</c> words (<c>DockerfileRunArguments</c> or <c>containerRunArguments</c>).</summary>
    public string? RunArguments { get; init; }

    /// <summary>Which of the two names the profile gives its <c>docker run</c> words under, or null when it gives none.</summary>
    public string? RunArgumentsKey { get; init; }

    /// <summary>The names a profile gives its container's <c>docker run</c> words under, Visual Studio's Docker one first.</summary>
    public static readonly IReadOnlyList<string> RunArgumentsKeys = ["DockerfileRunArguments", "containerRunArguments"];

    /// <summary>The env files the container reads (<c>containerRunEnvironmentFiles</c>, separated by ';').</summary>
    public string? EnvironmentFiles { get; init; }

    /// <summary>A Docker Compose profile's start for each service it names (<c>serviceActions</c>, its own then its
    /// <c>composeProfile</c>'s): <c>StartDebugging</c>, <c>DoNotStart</c> or <c>StartWithoutDebugging</c>.</summary>
    public IReadOnlyDictionary<string, string> ServiceActions { get; init; } = new Dictionary<string, string>();

    /// <summary>The Compose profiles a Docker Compose profile turns on (its <c>composeProfile</c>'s <c>includes</c>).</summary>
    public IReadOnlyList<string> ComposeProfiles { get; init; } = [];

    /// <summary>What a Docker Compose profile does once its services start (<c>composeLaunchAction</c>), as <c>LaunchBrowser</c>.</summary>
    public string? ComposeLaunchAction { get; init; }

    /// <summary>The service a Docker Compose profile launches (<c>composeLaunchServiceName</c>).</summary>
    public string? ComposeLaunchService { get; init; }

    /// <summary>The page a Docker Compose profile opens (<c>composeLaunchUrl</c>).</summary>
    public string? ComposeLaunchUrl { get; init; }

    /// <summary>Whether it runs the project in a container: a <c>Docker</c> (its Dockerfile) or <c>SdkContainer</c>
    /// (the SDK's own container) profile.</summary>
    public bool RunsInContainer => Is(DockerCommand) || Is(SdkContainerCommand);

    public const string DockerCommand = "Docker";

    public const string SdkContainerCommand = "SdkContainer";

    /// <summary>A Docker Compose project's (.dcproj) services, as its configurations run them.</summary>
    public const string DockerComposeCommand = "DockerCompose";

    /// <summary>The project's own program, built.</summary>
    public const string ProjectCommand = "Project";

    /// <summary>Another program, named by <c>executablePath</c>.</summary>
    public const string ExecutableCommand = "Executable";

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The file a project's launch profiles are in, under its folder's Properties.</summary>
    public const string SettingsFile = "launchSettings.json";

    public static string SettingsPath(ProjectModel project) => SettingsIn(project.Folder);

    /// <summary>The launch profiles' file of a project in this folder.</summary>
    public static string SettingsIn(string folder) => Path.Combine(folder, "Properties", SettingsFile);

    /// <summary>Where a file-based program's profiles are, as <c>dotnet run</c> reads them: its folder's
    /// <c>Properties/launchSettings.json</c>, else the <c>.run.json</c> of its name beside it.</summary>
    public static string FileSettingsPath(string file)
    {
        string settings = SettingsIn(Path.GetDirectoryName(file)!);
        return File.Exists(settings) ? settings : Path.ChangeExtension(file, ".run.json");
    }

    /// <summary>Null for a profile Passenger launches, else why it does not, for each command known.</summary>
    public string? Unsupported => Command switch
    {
        null or "" => "its profile names no commandName",
        _ when Is(ProjectCommand) || Is(ExecutableCommand) || RunsInContainer => null,
        _ when Is("IISExpress") => "an IIS Express profile runs under IIS Express on Windows, which Passenger does not start",
        _ when Is("IIS") => "an IIS profile runs under IIS on Windows, which Passenger does not drive",
        _ when Is("MsixPackage") => "an MSIX package profile runs a deployed Windows package, which Passenger does not deploy",
        _ when Is("DebugRoslynComponent") => "a Roslyn component profile runs the compiler over another project, which Passenger does not",
        _ when Is(DockerComposeCommand) => "a Docker Compose profile belongs to a Docker Compose project (.dcproj), whose configurations run it",
        _ when Is("WSL2") => "a WSL 2 profile runs in Linux under Windows, which Passenger does not start",
        _ => $"Passenger does not know the {Command} command",
    };

    /// <summary>Whether this profile runs the project's own program.</summary>
    public bool RunsProject => Is(ProjectCommand);

    public bool Is(string command) => string.Equals(Command, command, StringComparison.OrdinalIgnoreCase);

    /// <summary>The project's profiles of every kind in the file's order; none when it has no settings or they do not read.</summary>
    public static IReadOnlyList<LaunchProfile> Read(ProjectModel project) => Read(SettingsPath(project));

    /// <summary>A settings file's profiles of every kind in its order (a launchSettings.json, a .run.json); none when it is
    /// not there or does not read.</summary>
    public static IReadOnlyList<LaunchProfile> Read(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The profiles a settings file's text gives, in its order; none when it does not read. A value that is not a
    /// string is taken as its JSON, as <c>dotnet run</c> takes it.</summary>
    public static IReadOnlyList<LaunchProfile> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        try
        {
            using JsonDocument settings = JsonDocument.Parse(text, Lenient);
            if (!settings.RootElement.TryGetProperty("profiles", out JsonElement profiles) || profiles.ValueKind != JsonValueKind.Object)
            {
                return [];
            }
            List<LaunchProfile> found = [];
            foreach (JsonProperty profile in profiles.EnumerateObject())
            {
                JsonElement value = profile.Value;
                if (value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                JsonElement? compose = value.TryGetProperty("composeProfile", out JsonElement inProfile) && inProfile.ValueKind == JsonValueKind.Object ? inProfile : null;
                Dictionary<string, string> actions = [];
                foreach (JsonElement holder in compose is { } composeProfile ? (JsonElement[])[value, composeProfile] : [value])
                {
                    Values(holder, "serviceActions", actions);
                }
                bool browser = value.TryGetProperty("launchBrowser", out JsonElement launch) && launch.ValueKind == JsonValueKind.True;
                found.Add(new LaunchProfile(profile.Name, Text(value, "commandName"), Text(value, "executablePath"), Text(value, "commandLineArgs"),
                    Text(value, "workingDirectory"), Values(value, "environmentVariables", []), Text(value, "applicationUrl"), browser, Text(value, "launchUrl"))
                {
                    HttpPort = Number(value, "httpPort"),
                    SslPort = Number(value, "sslPort"),
                    UseSsl = value.TryGetProperty("useSSL", out JsonElement ssl) && ssl.ValueKind == JsonValueKind.True,
                    PublishAllPorts = value.TryGetProperty("publishAllPorts", out JsonElement all) && all.ValueKind == JsonValueKind.True,
                    RunArguments = Text(value, RunArgumentsKeys[0]) ?? Text(value, RunArgumentsKeys[1]),
                    RunArgumentsKey = RunArgumentsKeys.FirstOrDefault(k => Text(value, k) is not null),
                    EnvironmentFiles = Text(value, "containerRunEnvironmentFiles"),
                    ServiceActions = actions,
                    ComposeProfiles = compose is { } c && c.TryGetProperty("includes", out JsonElement includes) && includes.ValueKind == JsonValueKind.Array
                        ? [.. includes.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!)] : [],
                    ComposeLaunchAction = Text(value, "composeLaunchAction"),
                    ComposeLaunchService = Text(value, "composeLaunchServiceName"),
                    ComposeLaunchUrl = Text(value, "composeLaunchUrl"),
                });
            }
            return found;
        }
        catch (JsonException)
        {
            return [];
        }

        static string? Text(JsonElement value, string key) =>
            value.TryGetProperty(key, out JsonElement found) && found.ValueKind == JsonValueKind.String ? found.GetString() : null;

        static Dictionary<string, string> Values(JsonElement value, string key, Dictionary<string, string> into)
        {
            if (value.TryGetProperty(key, out JsonElement values) && values.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty named in values.EnumerateObject())
                {
                    into[named.Name] = named.Value.ValueKind == JsonValueKind.String ? named.Value.GetString()! : named.Value.GetRawText();
                }
            }
            return into;
        }

        static int? Number(JsonElement value, string key)
        {
            if (!value.TryGetProperty(key, out JsonElement found))
            {
                return null;
            }
            int port = 0;
            bool read = found.ValueKind == JsonValueKind.Number ? found.TryGetInt32(out port)
                : found.ValueKind == JsonValueKind.String && int.TryParse(found.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port);
            return read && port is > 0 and < 65536 ? port : null;
        }
    }

    /// <summary>The ports the profiles listen on over HTTP (their <c>applicationUrl</c>s), in the order they first come.</summary>
    public static IReadOnlyList<int> HttpPorts(IEnumerable<LaunchProfile> profiles)
    {
        List<int> ports = [];
        foreach (string url in profiles.SelectMany(p => (p.ApplicationUrl ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (Uri.TryCreate(url.Replace("*", "localhost", StringComparison.Ordinal).Replace("+", "localhost", StringComparison.Ordinal), UriKind.Absolute, out Uri? address)
                && address.Scheme == Uri.UriSchemeHttp && !ports.Contains(address.Port))
            {
                ports.Add(address.Port);
            }
        }
        return ports;
    }

    /// <summary>The environment a run of the profile gets: its variables, with its application URL as ASP.NET Core's
    /// <c>ASPNETCORE_URLS</c> unless it sets that itself, as <c>dotnet run</c> passes it.</summary>
    public Dictionary<string, string> Environment()
    {
        Dictionary<string, string> environment = new(EnvironmentVariables);
        if (RunsProject && !string.IsNullOrWhiteSpace(ApplicationUrl) && !environment.ContainsKey("ASPNETCORE_URLS"))
        {
            environment["ASPNETCORE_URLS"] = ApplicationUrl;
        }
        return environment;
    }

    /// <summary>Where the browser opens when the profile asks for one: its launch URL, relative to its first application
    /// URL unless absolute; empty with neither.</summary>
    public string BrowserUrl()
    {
        string? root = ApplicationUrl?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(LaunchUrl))
        {
            return root ?? string.Empty;
        }
        if (Uri.TryCreate(LaunchUrl, UriKind.Absolute, out Uri? absolute) && absolute.Scheme is "http" or "https")
        {
            return absolute.ToString();
        }
        return root is not null && Uri.TryCreate(root.TrimEnd('/') + "/", UriKind.Absolute, out Uri? baseUri) && Uri.TryCreate(baseUri, LaunchUrl, out Uri? joined)
            ? joined.ToString()
            : LaunchUrl;
    }
}

/// <summary>A command line as one string, split the way .NET splits one into a program's arguments on
/// every platform: spaces separate, double quotes group, and backslashes escape only the quotes they
/// precede.</summary>
public static class CommandLine
{
    public static IReadOnlyList<string> Split(string? line)
    {
        List<string> arguments = [];
        if (string.IsNullOrWhiteSpace(line))
        {
            return arguments;
        }
        StringBuilder current = new();
        bool quoted = false;
        bool started = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\')
            {
                int run = 0;
                while (i < line.Length && line[i] == '\\')
                {
                    run++;
                    i++;
                }
                if (i < line.Length && line[i] == '"')
                {
                    current.Append('\\', run / 2);
                    if (run % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                }
                else
                {
                    current.Append('\\', run);
                    i--;
                }
                started = true;
            }
            else if (c == '"')
            {
                // Two quotes inside a quoted run are one literal quote.
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }
        if (started)
        {
            arguments.Add(current.ToString());
        }
        return arguments;
    }
}
