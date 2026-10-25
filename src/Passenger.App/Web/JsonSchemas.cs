using System.Collections.Concurrent;
using Passenger.Core;

namespace Passenger.App.Web;

/// <summary>The JSON schemas the JSON and YAML servers check and complete files by. The well-known files of a .NET and
/// Node.js project (package.json, tsconfig.json, jsconfig.json, appsettings.json, launchSettings.json, global.json)
/// take SchemaStore's, and compose files the Compose specification's, carried under <c>web/schemas/</c> with those they
/// refer to and never downloaded. A schema a file names itself (its <c>$schema</c>) is downloaded while Settings allow
/// it, over HTTP or HTTPS, kept for the process; <c>PASSENGER_SCHEMAS</c> names another place to ask for them. Called
/// on any thread.</summary>
public sealed class JsonSchemas(Func<bool> download)
{
    [IdentifierHome]
    public const string Variable = "PASSENGER_SCHEMAS";

    /// <summary>SchemaStore's hosts, whose schemas Passenger carries by their file's name.</summary>
    private static readonly string[] SchemaStore = ["json.schemastore.org", "www.schemastore.org", "schemastore.org"];

    /// <summary>The files each carried schema is for, each name matched anywhere in the folder.</summary>
    public static readonly IReadOnlyList<(string[] FileMatch, string Uri)> Associations =
    [
        (["package.json"], "https://json.schemastore.org/package.json"),
        (["tsconfig.json", "tsconfig.*.json"], "https://json.schemastore.org/tsconfig.json"),
        (["jsconfig.json", "jsconfig.*.json"], "https://json.schemastore.org/jsconfig.json"),
        (["appsettings.json", "appsettings.*.json"], "https://json.schemastore.org/appsettings.json"),
        ([Run.LaunchProfile.SettingsFile], "https://json.schemastore.org/launchsettings.json"),
        (["global.json"], "https://json.schemastore.org/global.json"),
    ];

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseCookies = false,
        ActivityHeadersPropagator = null,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly ConcurrentDictionary<string, Task<string>> downloaded = new(StringComparer.Ordinal);

    /// <summary>Where the carried schemas are, beside the app.</summary>
    public static string Folder => Path.Combine(WebLanguages.Carried, "schemas");

    /// <summary>The Compose specification's schema, carried, as the YAML server reads it from disk.</summary>
    public static string ComposeUri => new Uri(Path.Combine(Folder, "compose-spec.json")).AbsoluteUri;

    /// <summary>The names Docker Compose looks for (compose.yaml first) and the overrides and variants named after
    /// them, matched anywhere in the folder.</summary>
    public static readonly string[] ComposeFiles =
    [
        "compose.yaml", "compose.yml", "compose.*.yaml", "compose.*.yml",
        "docker-compose.yaml", "docker-compose.yml", "docker-compose.*.yaml", "docker-compose.*.yml",
    ];

    /// <summary>The associations as the JSON server's <c>json/schemaAssociations</c> takes them.</summary>
    public static object Notification => Associations.Select(a => new { fileMatch = a.FileMatch, uri = a.Uri }).ToArray();

    /// <summary>A schema's text by its URI: a carried one's, else one downloaded when Settings allow it; a failure says
    /// why, which the JSON server shows where the file names the schema.</summary>
    public async Task<string> ContentAsync(string uri, CancellationToken cancel)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? asked))
        {
            throw new InvalidOperationException($"'{uri}' is not a schema's URL");
        }
        if (Carried(asked) is { } path)
        {
            return await File.ReadAllTextAsync(path, cancel);
        }
        if (asked.Scheme != Uri.UriSchemeHttps && asked.Scheme != Uri.UriSchemeHttp)
        {
            throw new InvalidOperationException($"Passenger reads schemas from http and https URLs, not {uri}");
        }
        if (!download())
        {
            throw new InvalidOperationException($"{uri} is not downloaded: Settings | Languages & Frameworks | Schemas and DTDs | Remote JSON Schemas does not allow it");
        }
        Task<string> fetching = downloaded.GetOrAdd(uri, _ => DownloadAsync(From(asked)));
        try
        {
            return await fetching.WaitAsync(cancel);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            // Asked again next time: a network that comes back finds it.
            downloaded.TryRemove(new KeyValuePair<string, Task<string>>(uri, fetching));
            throw new InvalidOperationException($"{uri} could not be downloaded: {error.Message}", error);
        }
    }

    /// <summary>The carried schema a URI names: SchemaStore's by its file's name (its <c>$id</c> leaves out ".json").</summary>
    private static string? Carried(Uri uri)
    {
        if (!SchemaStore.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }
        string name = Path.GetFileName(uri.AbsolutePath);
        if (name.Length == 0)
        {
            return null;
        }
        string path = Path.Combine(Folder, name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : name + ".json");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Where a schema is asked for: its own URL, or under the place <c>PASSENGER_SCHEMAS</c> names, its path kept.</summary>
    private static Uri From(Uri schema)
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } named)
        {
            return schema;
        }
        if (!Uri.TryCreate(named, UriKind.Absolute, out Uri? place) || (place.Scheme != Uri.UriSchemeHttp && place.Scheme != Uri.UriSchemeHttps))
        {
            throw new FormatException($"{Variable} takes the http or https URL schemas are asked for at, not '{named}'");
        }
        return new Uri(place, schema.PathAndQuery);
    }

    /// <summary>Checks <c>PASSENGER_SCHEMAS</c> as the IDE starts, a malformed one being fatal.</summary>
    public static void CheckVariable() => From(new Uri("https://json.schemastore.org/"));

    private static async Task<string> DownloadAsync(Uri uri)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd($"Passenger/{Product.Version}");
        using HttpResponseMessage response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
