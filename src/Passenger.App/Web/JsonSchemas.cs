using Passenger.App.Settings;
using Passenger.Core;

namespace Passenger.App.Web;

/// <summary>The JSON schemas the JSON and YAML servers check and complete files by. The well-known files of a .NET and
/// Node.js project (package.json, tsconfig.json, jsconfig.json, appsettings.json, launchSettings.json and a file-based
/// program's .run.json, global.json) take SchemaStore's, and compose files the Compose specification's, carried under
/// <c>web/schemas/</c> with those they refer to and never downloaded. A schema a file names itself (its <c>$schema</c>) is
/// downloaded while Settings allow it, over HTTP or HTTPS, kept for the process, or read from a file on this machine's own
/// disks (<see cref="FoundPaths.OfFileAddress"/>), never another machine's or a share's; <c>PASSENGER_SCHEMAS</c> names
/// another place to ask for them. The JSON server asks here for every schema, a file's too. Called on any thread.</summary>
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
        ([Run.LaunchProfile.SettingsFile, "*.run.json"], "https://json.schemastore.org/launchsettings.json"),
        (["global.json"], "https://json.schemastore.org/global.json"),
    ];

    /// <summary>A schema is some kilobytes, the largest SchemaStore lists some megabytes.</summary>
    private static readonly HttpClient Http = OutboundHttp.Client(TimeSpan.FromSeconds(15), most: 64 * 1024 * 1024);

    /// <summary>Each schema downloaded, or under way, by its URI: one that failed is downloaded again by the next ask.</summary>
    private readonly SharedWork<string, string> downloaded = new(schema => schema.IsCompletedSuccessfully, StringComparer.Ordinal);

    /// <summary>Where the carried schemas are, beside the app.</summary>
    public static string Folder => Path.Combine(WebLanguages.Carried, "schemas");

    /// <summary>The Compose specification's schema, carried, as the YAML server reads it from disk.</summary>
    public static string ComposeUri => new Uri(Path.Combine(Folder, "compose-spec.json")).AbsoluteUri;

    /// <summary>The globs compose files take the Compose specification's schema by (<see cref="Containers.ComposeFiles.SchemaGlobs"/>).</summary>
    public static readonly string[] ComposeFiles = [.. Containers.ComposeFiles.SchemaGlobs];

    /// <summary>The associations as the JSON server's <c>json/schemaAssociations</c> takes them.</summary>
    public static object Notification => Associations.Select(a => new { fileMatch = a.FileMatch, uri = a.Uri }).ToArray();

    /// <summary>A schema's text by its URI: a carried one's, a file's on this machine's own disks, else one downloaded when
    /// Settings allow it; a failure says why, which the JSON server shows where the file names the schema.</summary>
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
        if (asked.IsFile)
        {
            return FoundPaths.OfFileAddress(asked) is { } file ? await ReadAsync(file, cancel)
                : throw new InvalidOperationException($"Passenger reads a schema's file only from this machine's own disks, never another machine's or a share's ({uri})");
        }
        if (asked.Scheme != Uri.UriSchemeHttps && asked.Scheme != Uri.UriSchemeHttp)
        {
            throw new InvalidOperationException($"Passenger reads schemas from http and https URLs, not {uri}");
        }
        if (!download())
        {
            throw new InvalidOperationException($"{uri} is not downloaded: {SettingsDialog.PlaceOfPage(SettingsDialog.SchemasPage)} does not allow it");
        }
        Task<string> fetching = downloaded.Take(uri, () => DownloadAsync(From(asked)));
        try
        {
            return await fetching.WaitAsync(cancel);
        }
        catch (Exception error) when (fetching.IsCompleted && error is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException($"{uri} could not be downloaded: {error.Message}", error);
        }
    }

    /// <summary>A schema's file on this machine, its failure to read said.</summary>
    private static async Task<string> ReadAsync(string path, CancellationToken cancel)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancel);
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{path} could not be read: {unread.Message}", unread);
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

    /// <summary>The place <c>PASSENGER_SCHEMAS</c> names to ask for schemas at in place of their own hosts; null for none
    /// (<see cref="EnvironmentValues.Address(string, string)"/>).</summary>
    public static Uri? Place => EnvironmentValues.Address(Variable, "schemas are asked for at");

    /// <summary>Where a schema is asked for: its own URL, or at <see cref="Place"/>, its path kept.</summary>
    private static Uri From(Uri schema) => Place is { } place ? new Uri(place, schema.PathAndQuery) : schema;

    private static async Task<string> DownloadAsync(Uri uri)
    {
        using HttpRequestMessage request = OutboundHttp.Request(HttpMethod.Get, uri);
        using HttpResponseMessage response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
