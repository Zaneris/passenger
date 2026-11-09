using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Endpoints;

/// <summary>Where an app's OpenAPI document is read, and nowhere else: from the app itself on this machine's loopback
/// (MapOpenApi's <c>/openapi/v1.json</c>, Swashbuckle's and NSwag's <c>/swagger/v1/swagger.json</c>, or the path its code
/// gives MapOpenApi), each address and redirect held to that app (<see cref="OpenApiAddresses"/>), or from the file
/// its build writes (Microsoft.Extensions.ApiDescription.Server's, in its <c>OpenApiDocumentsDirectory</c>). An app's
/// documents are kept a moment by its address, so a run of tests asks it once. Any thread.</summary>
public static partial class OpenApiSources
{
    /// <summary>The paths an app serves its document at by default: MapOpenApi's, then Swashbuckle's and NSwag's.</summary>
    public static readonly IReadOnlyList<string> Defaults = ["/openapi/v1.json", "/swagger/v1/swagger.json"];

    /// <summary>How long a document read from an app stands before it is asked again.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(10);

    private static readonly HttpClient Client = OutboundHttp.Client(TimeSpan.FromSeconds(10), 16 * 1024 * 1024, followsRedirects: false);

    /// <summary>The documents read of late, by origin and paths: an app's port changes from run to run, and a document kept
    /// holds its JSON whole, several times the size of its text.</summary>
    private static readonly FreshReads<OpenApiDocument?> Read = new(Fresh, StringComparer.OrdinalIgnoreCase);

    /// <summary>The document an app at <paramref name="address"/> serves (its own path <paramref name="mapped"/> first, then the
    /// defaults); null where none answers with one, or the address is not on this machine, which is never asked.</summary>
    public static Task<OpenApiDocument?> FromAppAsync(Uri address, IReadOnlyList<string> mapped, CancellationToken cancel)
    {
        if (!WebAddress.OnThisMachine(address) || address.Scheme is not ("http" or "https"))
        {
            return Task.FromResult<OpenApiDocument?>(null);
        }
        string origin = address.GetLeftPart(UriPartial.Authority);
        string key = $"{origin}\n{string.Join('\n', mapped)}";
        long now = Stopwatch.GetTimestamp();
        if (Read.TryGet(key, now, out Task<OpenApiDocument?>? known))
        {
            return known.WaitAsync(cancel);
        }
        Task<OpenApiDocument?> reading = AskAsync(new Uri(origin), [.. mapped.Concat(Defaults).Distinct(StringComparer.OrdinalIgnoreCase)]);
        Read.Add(key, reading, now);
        return reading.WaitAsync(cancel);
    }

    private static async Task<OpenApiDocument?> AskAsync(Uri origin, IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            try
            {
                if (OpenApiAddresses.At(origin, path) is { } address && await ReadAsync(address).ConfigureAwait(false) is { } document)
                {
                    return document;
                }
            }
            catch (Exception unanswered) when (unanswered is HttpRequestException or TaskCanceledException or IOException or UriFormatException)
            {
                // The app is not up, or serves nothing there: the next path, else none.
            }
        }
        return null;
    }

    /// <summary>The document at an address of an app, a redirect followed only while it stays with the app
    /// (<see cref="OpenApiAddresses.Stays"/>); null for any other answer.</summary>
    private static async Task<OpenApiDocument?> ReadAsync(Uri address)
    {
        for (int redirects = 0; redirects <= EndpointClient.MostRedirects; redirects++)
        {
            using HttpRequestMessage request = OutboundHttp.Request(HttpMethod.Get, address);
            using HttpResponseMessage answer = await Client.SendAsync(request).ConfigureAwait(false);
            if (EndpointClient.Redirect(answer, address) is not { } next)
            {
                return answer.IsSuccessStatusCode ? OpenApiDocument.Parse(await answer.Content.ReadAsStringAsync().ConfigureAwait(false)) : null;
            }
            if (!OpenApiAddresses.Stays(address, next))
            {
                return null;
            }
            address = next;
        }
        return null;
    }

    /// <summary>The document a project's build wrote (Microsoft.Extensions.ApiDescription.Server's <c>GetDocument</c>):
    /// <c>{project}.json</c> in its <c>OpenApiDocumentsDirectory</c>, else the first <c>{project}_*.json</c> there; null where
    /// its build writes none.</summary>
    public static async Task<OpenApiDocument?> FromBuildAsync(ProjectModel project, WorkspaceModel? workspace, CancellationToken cancel)
    {
        IReadOnlyDictionary<string, string> properties = await LaunchPlan.EvaluateAsync(project, LaunchPlan.FrameworkOf(string.Empty, project),
            [MSBuildNames.OpenApiDocumentsDirectory, MSBuildNames.BaseIntermediateOutputPath], workspace, cancel);
        string? folder = properties.GetValueOrDefault(MSBuildNames.OpenApiDocumentsDirectory) ?? properties.GetValueOrDefault(MSBuildNames.BaseIntermediateOutputPath);
        if (folder is null)
        {
            return null;
        }
        string where = Path.GetFullPath(Path.Combine(project.Folder, folder.Replace('\\', Path.DirectorySeparatorChar)));
        string name = Path.GetFileNameWithoutExtension(project.Path);
        return await BackgroundWork.Run(() =>
        {
            if (!Directory.Exists(where))
            {
                return null;
            }
            foreach (string file in Directory.EnumerateFiles(where, $"{name}*.json").OrderBy(f => Path.GetFileNameWithoutExtension(f).Length).ThenBy(f => f, StringComparer.Ordinal))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                if ((stem == name || stem.StartsWith(name + "_", StringComparison.Ordinal)) && OpenApiDocument.Parse(File.ReadAllText(file)) is { } document)
                {
                    return document;
                }
            }
            return (OpenApiDocument?)null;
        }, cancel);
    }

    /// <summary>The paths a project's code gives MapOpenApi (<c>MapOpenApi("/docs/{documentName}.json")</c>), its default
    /// document's name in place; read from the text of its C# files.</summary>
    public static IReadOnlyList<string> MappedIn(IEnumerable<string> texts) =>
        [.. texts.SelectMany(text => Mapped().Matches(text)).Select(m => m.Groups[1].Value.Replace("{documentName}", "v1", StringComparison.Ordinal))
            .Select(path => path.StartsWith('/') ? path : "/" + path).Distinct(StringComparer.OrdinalIgnoreCase)];

    [GeneratedRegex(@"\bMapOpenApi\s*\(\s*(?:pattern\s*:\s*)?""([^""]+)""")]
    private static partial Regex Mapped();

    /// <summary>The document named for an app (<see cref="OpenApiAddresses.Named"/>) asked of it: a path at the address its
    /// request went to, a URL as named; null where it answers with none.</summary>
    public static Task<OpenApiDocument?> FromNamedAsync(string named, Uri address, CancellationToken cancel) =>
        named.StartsWith('/') ? FromAppAsync(address, [named], cancel)
        : Uri.TryCreate(named, UriKind.Absolute, out Uri? url) ? FromAppAsync(url, [url.PathAndQuery], cancel) : Task.FromResult<OpenApiDocument?>(null);

    /// <summary>Whether a project's code has its app describe itself (MapOpenApi, Swashbuckle's UseSwagger, NSwag's
    /// UseOpenApi), so its document is worth asking it for.</summary>
    public static bool Describes(IEnumerable<string> texts) => texts.Any(text => Describing().IsMatch(text));

    [GeneratedRegex(@"\b(?:MapOpenApi|UseSwagger|UseOpenApi|MapSwagger)\s*\(")]
    private static partial Regex Describing();

    /// <summary>Where a response breaks the schema a document gives it: none where the document has no operation for it or
    /// no JSON schema for its status, or its body is no JSON.</summary>
    public static IReadOnlyList<SchemaBreak> Check(OpenApiDocument document, string method, string url, EndpointResponse response)
    {
        if (response.Error is not null || response.Cut || response.Body.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out Uri? sent)
            || !response.Headers.Any(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && h.Value.Contains("json", StringComparison.OrdinalIgnoreCase))
            || document.OperationFor(method, sent.AbsolutePath)?.ResponseSchema(response.Status) is not { } schema)
        {
            return [];
        }
        try
        {
            using JsonDocument body = JsonDocument.Parse(response.Body);
            return OpenApiSchemas.Check(document, schema, body.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
