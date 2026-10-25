using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Passenger.Core;

namespace Passenger.Packages;

/// <summary>A package as a search lists it: its id, its newest version, what it says it is, who made it, how much it is
/// used, and the feeds that have it.</summary>
public sealed record PackageSummary(string Id, string Version, string Description, string Authors, long Downloads)
{
    public IReadOnlyList<string> Feeds { get; init; } = [];
}

/// <summary>Where packages are found: a v3 feed (nuget.org's protocol), a v2 feed (OData), or a folder of .nupkg files.</summary>
public interface IPackageFeed
{
    string Name { get; }

    /// <summary>The packages a query finds, the most used first.</summary>
    Task<IReadOnlyList<PackageSummary>> SearchAsync(string query, bool prerelease, CancellationToken cancel);

    /// <summary>A package's versions that can be installed, newest first: none its owner unlisted.</summary>
    Task<IReadOnlyList<string>> VersionsAsync(string id, bool prerelease, CancellationToken cancel);

    /// <summary>What a version of a package depends on, as its own metadata says, by target framework; null when the feed
    /// has no such version, or cannot say.</summary>
    Task<PackageDependencies?> DependenciesAsync(string id, string version, CancellationToken cancel) => Task.FromResult<PackageDependencies?>(null);
}

/// <summary>A package version's dependencies as its nuspec groups them: by target framework (null for the group that
/// names none), each a package id and the range of its versions.</summary>
public sealed record PackageDependencies(IReadOnlyList<DependencyGroup> Groups)
{
    /// <summary>The range a framework's group gives a package, the first group naming it when none is the framework's
    /// (a package's groups are near-alike across frameworks); null when no group depends on it.</summary>
    public string? RangeOf(string id, string? framework = null)
    {
        DependencyGroup? own = framework is null ? null : Groups.FirstOrDefault(g => string.Equals(g.Framework, framework, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Short(g.Framework), framework, StringComparison.OrdinalIgnoreCase));
        return (own is null ? Groups : [own, .. Groups]).SelectMany(g => g.Packages)
            .FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Range;
    }

    /// <summary>A nuspec's dependencies element read: its groups, or its flat list as one group of no framework.</summary>
    public static PackageDependencies FromNuspec(XElement? dependencies)
    {
        if (dependencies is null)
        {
            return new PackageDependencies([]);
        }
        static (string Id, string Range) Dependency(XElement element) => ((string?)element.Attribute("id") ?? string.Empty, (string?)element.Attribute("version") ?? string.Empty);
        List<XElement> groups = [.. dependencies.Elements().Where(e => e.Name.LocalName == "group")];
        return groups.Count == 0
            ? new PackageDependencies([new DependencyGroup(null, [.. dependencies.Elements().Where(e => e.Name.LocalName == "dependency").Select(Dependency)])])
            : new PackageDependencies([.. groups.Select(g => new DependencyGroup((string?)g.Attribute("targetFramework"),
                [.. g.Elements().Where(e => e.Name.LocalName == "dependency").Select(Dependency)]))]);
    }

    /// <summary>A framework as a project names it: net10.0 for .NETCoreApp10.0, .NETCoreApp,Version=v10.0 or net10.0.</summary>
    private static string? Short(string? framework)
    {
        const string Core = ".NETCoreApp";
        if (framework is null || !framework.StartsWith(Core, StringComparison.OrdinalIgnoreCase))
        {
            return framework;
        }
        string version = framework[Core.Length..].Replace(",Version=v", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return $"net{version}";
    }
}

/// <summary>A target framework's dependencies in a package's metadata.</summary>
public sealed record DependencyGroup(string? Framework, IReadOnlyList<(string Id, string Range)> Packages);

/// <summary>What a search of several feeds found: the packages, each once with the feeds that have it; the feeds that
/// did not answer, and why; and how many packages the package source mapping left out.</summary>
public sealed record FeedSearch(IReadOnlyList<PackageSummary> Packages, IReadOnlyList<(string Feed, string Problem)> Failed, int Unmapped);

/// <summary>The feeds the NuGet window searches at once: All Feeds, or the one chosen. Each is asked in parallel; a
/// package found in several is listed once, at its newest version; one the package source mapping keeps from a feed is
/// not offered from it, since restore would never take it from there.</summary>
public sealed class FeedSet(string name, IReadOnlyList<IPackageFeed> feeds, Func<string, string, bool> allows) : IPackageFeed
{
    /// <summary>The chooser's every-feed choice.</summary>
    public const string All = "All Feeds";

    public string Name => name;

    public IReadOnlyList<IPackageFeed> Feeds => feeds;

    public async Task<IReadOnlyList<PackageSummary>> SearchAsync(string query, bool prerelease, CancellationToken cancel)
    {
        FeedSearch found = await SearchEachAsync(query, prerelease, cancel);
        return found.Packages.Count == 0 && found.Failed.Count > 0 && found.Failed.Count == feeds.Count
            ? throw new HttpRequestException(string.Join("; ", found.Failed.Select(f => $"{f.Feed}: {f.Problem}")))
            : found.Packages;
    }

    public async Task<FeedSearch> SearchEachAsync(string query, bool prerelease, CancellationToken cancel)
    {
        (IPackageFeed Feed, IReadOnlyList<PackageSummary>? Found, string? Problem)[] answers = await Task.WhenAll(feeds.Select(async feed =>
        {
            try
            {
                return (feed, await feed.SearchAsync(query, prerelease, cancel), (string?)null);
            }
            catch (Exception failed) when (!cancel.IsCancellationRequested && IsFeedFailure(failed))
            {
                return (feed, (IReadOnlyList<PackageSummary>?)null, failed.Message);
            }
        }));
        cancel.ThrowIfCancellationRequested();
        Dictionary<string, PackageSummary> merged = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = [];
        int unmapped = 0;
        foreach ((IPackageFeed feed, IReadOnlyList<PackageSummary>? found, _) in answers)
        {
            foreach (PackageSummary hit in found ?? [])
            {
                if (!allows(feed.Name, hit.Id))
                {
                    unmapped++;
                    continue;
                }
                if (!merged.TryGetValue(hit.Id, out PackageSummary? known))
                {
                    merged[hit.Id] = hit with { Feeds = [feed.Name] };
                    order.Add(hit.Id);
                    continue;
                }
                PackageSummary newer = VersionOrder.Instance.Compare(hit.Version, known.Version) > 0 ? hit : known;
                merged[hit.Id] = newer with { Downloads = Math.Max(hit.Downloads, known.Downloads), Feeds = [.. known.Feeds, feed.Name] };
            }
        }
        // By how well the id matches before downloads, which a private feed never counts, so its packages do not sink.
        string wanted = query.Trim();
        int Match(string id) => id.Equals(wanted, StringComparison.OrdinalIgnoreCase) ? 0
            : id.StartsWith(wanted, StringComparison.OrdinalIgnoreCase) ? 1
            : id.Contains(wanted, StringComparison.OrdinalIgnoreCase) ? 2
            : 3;
        IReadOnlyList<PackageSummary> packages = [.. order.Select(id => merged[id])
            .OrderBy(p => feeds.Count > 1 ? Match(p.Id) : 0)
            .ThenByDescending(p => feeds.Count > 1 ? p.Downloads : 0)];
        return new FeedSearch(packages, [.. answers.Where(a => a.Problem is not null).Select(a => (a.Feed.Name, a.Problem!))], unmapped);
    }

    public async Task<IReadOnlyList<string>> VersionsAsync(string id, bool prerelease, CancellationToken cancel)
    {
        IPackageFeed[] asked = [.. feeds.Where(f => allows(f.Name, id))];
        (IReadOnlyList<string>? Found, Exception? Failed)[] answers = await Task.WhenAll(asked.Select(async feed =>
        {
            try
            {
                return (await feed.VersionsAsync(id, prerelease, cancel), (Exception?)null);
            }
            catch (Exception failed) when (!cancel.IsCancellationRequested && asked.Length > 1 && IsFeedFailure(failed))
            {
                return ((IReadOnlyList<string>?)null, failed);
            }
        }));
        if (answers.Length > 0 && answers.All(a => a.Failed is not null))
        {
            throw answers[0].Failed!;
        }
        return [.. answers.SelectMany(a => a.Found ?? []).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(v => v, VersionOrder.Instance)];
    }

    /// <summary>A version's dependencies from the first feed the package source mapping allows that has it.</summary>
    public async Task<PackageDependencies?> DependenciesAsync(string id, string version, CancellationToken cancel)
    {
        foreach (IPackageFeed feed in feeds.Where(f => allows(f.Name, id)))
        {
            try
            {
                if (await feed.DependenciesAsync(id, version, cancel) is { } found)
                {
                    return found;
                }
            }
            catch (Exception failed) when (!cancel.IsCancellationRequested && IsFeedFailure(failed))
            {
                // A feed that does not answer: the next is asked.
            }
        }
        return null;
    }

    /// <summary>What a feed throws when it cannot answer: it is down, refuses, says what is no feed, or is no folder.</summary>
    public static bool IsFeedFailure(Exception failed) =>
        failed is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException or System.Xml.XmlException
            or IOException or UnauthorizedAccessException or UriFormatException or NotSupportedException;
}

/// <summary>A feed's HTTP: its requests, with its credentials sent to its own host alone (those of its nuget.config, else
/// what a credential provider gives when it answers 401 without any), the certificate check its configuration may turn
/// off, and HTTP refused unless the feed allows insecure connections. Credentials are never logged.</summary>
public sealed class FeedClient
{
    private static readonly HttpClient Shared = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(30) };

    private readonly HttpClient http;
    private readonly bool configured;
    private FeedCredentials? credentials;

    public FeedClient(string name, Uri index, HttpClient? http = null, FeedCredentials? credentials = null, bool allowInsecure = false, bool skipCertificateCheck = false)
    {
        Name = name;
        Index = index;
        this.credentials = credentials;
        configured = credentials is not null;
        AllowInsecure = allowInsecure;
        this.http = http ?? (skipCertificateCheck
            ? new HttpClient(new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } }) { Timeout = Shared.Timeout }
            : Shared);
    }

    public string Name { get; }

    /// <summary>The feed's own URL: a v3 feed's service index, a v2 feed's root.</summary>
    public Uri Index { get; }

    public bool AllowInsecure { get; }

    /// <summary>Where the window asks a credential provider for what the feed's nuget.config does not give.</summary>
    public CredentialProviders Providers { get; init; } = CredentialProviders.Default;

    /// <summary>A GET of one of the feed's URLs, answered (whatever its status) once credentials have been tried.</summary>
    public async Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken cancel)
    {
        if (url.Scheme == Uri.UriSchemeHttp && !AllowInsecure)
        {
            throw new HttpRequestException($"{Name} is reached over HTTP, which NuGet uses only with allowInsecureConnections set for it");
        }
        bool own = string.Equals(url.GetLeftPart(UriPartial.Authority), Index.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
        FeedCredentials? sent = own ? Volatile.Read(ref credentials) : null;
        HttpResponseMessage response = await SendAsync(url, sent, cancel);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }
        response.Dispose();
        // A URL off the feed's host comes from the feed's own answers, which may name any host: never one to give credentials.
        if (!own)
        {
            throw new HttpRequestException($"{url.Authority}, where {Name} sent Passenger, asks for credentials (401 Unauthorized); {Name}'s go only to {Index.Authority}",
                null, HttpStatusCode.Unauthorized);
        }
        FeedCredentials? retry = Volatile.Read(ref credentials);
        if (retry is null || retry == sent)
        {
            if (configured)
            {
                throw new HttpRequestException($"{Name} refused the credentials its nuget.config gives (401 Unauthorized)", null, HttpStatusCode.Unauthorized);
            }
            retry = await Providers.GetAsync(Index, isRetry: sent is not null, cancel)
                ?? throw new HttpRequestException($"{Name} needs credentials (401 Unauthorized): none in its nuget.config, and no credential provider gave any", null, HttpStatusCode.Unauthorized);
            Volatile.Write(ref credentials, retry);
        }
        response = await SendAsync(url, retry, cancel);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            throw new HttpRequestException($"{Name} refused the credentials given for it (401 Unauthorized)", null, HttpStatusCode.Unauthorized);
        }
        return response;
    }

    /// <summary>A JSON answer, or null where there is none (a package the feed does not have).</summary>
    public async Task<T?> GetJsonAsync<T>(Uri url, JsonSerializerOptions options, CancellationToken cancel) where T : class
    {
        using HttpResponseMessage response = await GetAsync(url, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        await using Stream body = await Body(response, cancel);
        return await JsonSerializer.DeserializeAsync<T>(body, options, cancel);
    }

    /// <summary>An XML answer, or null where there is none.</summary>
    public async Task<XDocument?> GetXmlAsync(Uri url, CancellationToken cancel)
    {
        using HttpResponseMessage response = await GetAsync(url, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        await using Stream body = await Body(response, cancel);
        return await XDocument.LoadAsync(body, LoadOptions.None, cancel);
    }

    /// <summary>The body, unpacked when it came gzipped whatever was asked for (nuget.org's SemVer 2 registrations).</summary>
    private static async Task<Stream> Body(HttpResponseMessage response, CancellationToken cancel)
    {
        Stream body = await response.Content.ReadAsStreamAsync(cancel);
        return response.Content.Headers.ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase) ? new GZipStream(body, CompressionMode.Decompress) : body;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, FeedCredentials? with, CancellationToken cancel)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Passenger", Product.Version));
        if (with is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", with.Basic());
        }
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
    }
}

/// <summary>A feed through NuGet's v3 protocol, by what its service index names: the search, the registrations (whether
/// each version is listed), the flat container (for a feed without registrations) and the known vulnerabilities.</summary>
public sealed class V3Feed(FeedClient client) : IPackageFeed
{
    /// <summary>nuget.org's service index.</summary>
    public static readonly Uri NuGetOrg = new("https://api.nuget.org/v3/index.json");

    private (Uri? Search, Uri? Registrations, Uri? Flat, Uri? Vulnerabilities)? services;

    public string Name => client.Name;

    public async Task<IReadOnlyList<PackageSummary>> SearchAsync(string query, bool prerelease, CancellationToken cancel)
    {
        Uri search = (await ServicesAsync(cancel)).Search ?? throw new InvalidOperationException($"{Name} offers no search");
        Uri url = new($"{search}?q={Uri.EscapeDataString(query)}&take=30&prerelease={(prerelease ? "true" : "false")}&semVerLevel=2.0.0");
        SearchResponse? found = await client.GetJsonAsync<SearchResponse>(url, Json, cancel);
        return [.. (found?.Data ?? []).Select(p => new PackageSummary(p.Id, p.Version, p.Description ?? string.Empty, Authors(p.Authors), p.TotalDownloads ?? 0) { Feeds = [Name] })];
    }

    public async Task<IReadOnlyList<string>> VersionsAsync(string id, bool prerelease, CancellationToken cancel)
    {
        (_, Uri? registrations, Uri? flat, _) = await ServicesAsync(cancel);
        IEnumerable<string> versions;
        if (registrations is not null)
        {
            if (await client.GetJsonAsync<Registration>(new Uri(registrations, $"{id.ToLowerInvariant()}/index.json"), Json, cancel) is not { } registration)
            {
                return [];
            }
            // A package with many versions has its pages by reference, each fetched on its own.
            RegistrationPage[] pages = await Task.WhenAll((registration.Items ?? []).Select(async page =>
                page.Items is null ? await client.GetJsonAsync<RegistrationPage>(new Uri(page.Id), Json, cancel) ?? page : page));
            versions = pages.SelectMany(p => p.Items ?? []).Select(leaf => leaf.CatalogEntry).Where(entry => entry.Listed != false).Select(entry => entry.Version);
        }
        else if (flat is not null)
        {
            versions = (await client.GetJsonAsync<FlatVersions>(new Uri(flat, $"{id.ToLowerInvariant()}/index.json"), Json, cancel))?.Versions ?? [];
        }
        else
        {
            throw new InvalidOperationException($"{Name} offers neither registrations nor a flat container");
        }
        return [.. versions.Where(v => prerelease || !VersionOrder.IsPrerelease(v)).OrderByDescending(v => v, VersionOrder.Instance)];
    }

    /// <summary>A version's dependencies from its nuspec in the flat container, else from its registration's catalog entry.</summary>
    public async Task<PackageDependencies?> DependenciesAsync(string id, string version, CancellationToken cancel)
    {
        (_, Uri? registrations, Uri? flat, _) = await ServicesAsync(cancel);
        string lower = id.ToLowerInvariant();
        string at = version.ToLowerInvariant();
        if (flat is not null && await client.GetXmlAsync(new Uri(flat, $"{lower}/{at}/{lower}.nuspec"), cancel) is { Root: { } nuspec })
        {
            return PackageDependencies.FromNuspec(nuspec.Descendants().FirstOrDefault(e => e.Name.LocalName == "dependencies"));
        }
        if (registrations is null || await client.GetJsonAsync<JsonDocument>(new Uri(registrations, $"{lower}/{at}.json"), Json, cancel) is not { } leaf)
        {
            return null;
        }
        using (leaf)
        {
            if (!leaf.RootElement.TryGetProperty("catalogEntry", out JsonElement entry))
            {
                return null;
            }
            // The leaf names its catalog entry by its URL; a feed may give it whole.
            if (entry.ValueKind == JsonValueKind.String)
            {
                using JsonDocument? fetched = await client.GetJsonAsync<JsonDocument>(new Uri(entry.GetString()!), Json, cancel);
                return fetched is null ? null : Groups(fetched.RootElement);
            }
            return entry.ValueKind == JsonValueKind.Object ? Groups(entry) : null;
        }

        static PackageDependencies Groups(JsonElement entry)
        {
            List<DependencyGroup> groups = [];
            foreach (JsonElement group in entry.TryGetProperty("dependencyGroups", out JsonElement all) && all.ValueKind == JsonValueKind.Array ? all.EnumerateArray() : [])
            {
                string? framework = group.TryGetProperty("targetFramework", out JsonElement named) ? named.GetString() : null;
                List<(string, string)> packages = [];
                foreach (JsonElement dependency in group.TryGetProperty("dependencies", out JsonElement listed) && listed.ValueKind == JsonValueKind.Array ? listed.EnumerateArray() : [])
                {
                    packages.Add((dependency.TryGetProperty("id", out JsonElement depId) ? depId.GetString() ?? string.Empty : string.Empty,
                        dependency.TryGetProperty("range", out JsonElement range) ? range.GetString() ?? string.Empty : string.Empty));
                }
                groups.Add(new DependencyGroup(framework, packages));
            }
            return new PackageDependencies(groups);
        }
    }

    /// <summary>The advisories the feed's VulnerabilityInfo resource gives, by package; none from a feed without the
    /// resource.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Advisory>>> VulnerabilitiesAsync(CancellationToken cancel)
    {
        if ((await ServicesAsync(cancel)).Vulnerabilities is not { } index)
        {
            return new Dictionary<string, IReadOnlyList<Advisory>>();
        }
        IReadOnlyList<VulnerabilityData.IndexPage> pages = await client.GetJsonAsync<List<VulnerabilityData.IndexPage>>(index, VulnerabilityData.Json, cancel) ?? [];
        IReadOnlyDictionary<string, IReadOnlyList<Advisory>>[] read = await Task.WhenAll(pages.Select(async page =>
            VulnerabilityData.Page(await client.GetJsonAsync<Dictionary<string, List<VulnerabilityData.Entry>>>(new Uri(index, page.Id), VulnerabilityData.Json, cancel))));
        Dictionary<string, IReadOnlyList<Advisory>> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, IReadOnlyList<Advisory> advisories) in read.SelectMany(p => p))
        {
            merged[id] = merged.TryGetValue(id, out IReadOnlyList<Advisory>? known) ? [.. known.Concat(advisories).Distinct()] : advisories;
        }
        return merged;
    }

    private static string Authors(JsonElement? authors) => authors switch
    {
        { ValueKind: JsonValueKind.Array } list => string.Join(", ", list.EnumerateArray().Select(a => a.GetString())),
        { ValueKind: JsonValueKind.String } one => one.GetString() ?? string.Empty,
        _ => string.Empty,
    };

    private async Task<(Uri? Search, Uri? Registrations, Uri? Flat, Uri? Vulnerabilities)> ServicesAsync(CancellationToken cancel)
    {
        if (services is { } known)
        {
            return known;
        }
        ServiceIndex index = await client.GetJsonAsync<ServiceIndex>(client.Index, Json, cancel)
            ?? throw new InvalidOperationException($"{Name} has no service index at {client.Index}");
        string? Find(params string[] types) => types.Select(type => index.Resources?.FirstOrDefault(r => r.Type == type)?.Id).FirstOrDefault(id => id is not null);
        Uri? Folder(string? found) => found is null ? null : new Uri(found.EndsWith('/') ? found : found + "/");
        // 3.6.0 is the hive with packages of SemVer 2 versions, which the earlier ones leave out.
        services = (
            Find("SearchQueryService/3.5.0", "SearchQueryService/3.0.0-rc", "SearchQueryService/3.0.0-beta", "SearchQueryService") is { } search ? new Uri(search) : null,
            Folder(Find("RegistrationsBaseUrl/3.6.0", "RegistrationsBaseUrl/3.4.0", "RegistrationsBaseUrl/3.0.0-rc", "RegistrationsBaseUrl/3.0.0-beta", "RegistrationsBaseUrl")),
            Folder(Find("PackageBaseAddress/3.0.0")),
            Find("VulnerabilityInfo/6.7.0") is { } vulnerabilities ? new Uri(vulnerabilities) : null);
        return services.Value;
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record ServiceIndex(IReadOnlyList<ServiceResource>? Resources);

    private sealed record ServiceResource([property: JsonPropertyName("@id")] string Id, [property: JsonPropertyName("@type")] string Type);

    private sealed record SearchResponse(IReadOnlyList<SearchHit>? Data);

    /// <summary>A search's package; its authors a list on nuget.org and a string on some private feeds.</summary>
    private sealed record SearchHit(string Id, string Version, string? Description, JsonElement? Authors, long? TotalDownloads);

    private sealed record Registration(IReadOnlyList<RegistrationPage>? Items);

    private sealed record RegistrationPage([property: JsonPropertyName("@id")] string Id, IReadOnlyList<RegistrationLeaf>? Items);

    private sealed record RegistrationLeaf(CatalogEntry CatalogEntry);

    /// <summary>A version as its registration has it; one with no listed flag is listed.</summary>
    private sealed record CatalogEntry(string Version, bool? Listed);

    private sealed record FlatVersions(IReadOnlyList<string>? Versions);
}

/// <summary>A feed through NuGet's v2 protocol (OData), page by page; a version published in 1900 or not listed is
/// unlisted.</summary>
public sealed class V2Feed(FeedClient client) : IPackageFeed
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Data = "http://schemas.microsoft.com/ado/2007/08/dataservices";
    private static readonly XNamespace Meta = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";

    /// <summary>More pages than a package's versions fill, so a server that pages forever is not followed forever.</summary>
    private const int MostPages = 50;

    public string Name => client.Name;

    private string Root => client.Index.AbsoluteUri.TrimEnd('/');

    public async Task<IReadOnlyList<PackageSummary>> SearchAsync(string query, bool prerelease, CancellationToken cancel)
    {
        string filter = prerelease ? "IsAbsoluteLatestVersion" : "IsLatestVersion";
        Uri url = new($"{Root}/Search()?$filter={filter}&searchTerm={Uri.EscapeDataString(Literal(query))}&targetFramework=''&includePrerelease={(prerelease ? "true" : "false")}&$skip=0&$top=30&semVerLevel=2.0.0");
        XDocument? found = await client.GetXmlAsync(url, cancel);
        return [.. Entries(found).Select(e => new PackageSummary(e.Id, e.Version, e.Description, e.Authors, e.Downloads) { Feeds = [Name] })];
    }

    public async Task<IReadOnlyList<string>> VersionsAsync(string id, bool prerelease, CancellationToken cancel)
    {
        List<string> versions = [];
        Uri? next = new($"{Root}/FindPackagesById()?id={Uri.EscapeDataString(Literal(id))}&semVerLevel=2.0.0");
        for (int page = 0; next is not null && page < MostPages; page++)
        {
            XDocument? found = await client.GetXmlAsync(next, cancel);
            versions.AddRange(Entries(found).Where(e => e.Listed && (prerelease || !VersionOrder.IsPrerelease(e.Version))).Select(e => e.Version));
            next = found?.Root?.Elements(Atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") == "next")?.Attribute("href") is { } href ? new Uri(client.Index, href.Value) : null;
        }
        return [.. versions.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(v => v, VersionOrder.Instance)];
    }

    /// <summary>A version's dependencies from its entry's Dependencies field: "id:range:framework|…".</summary>
    public async Task<PackageDependencies?> DependenciesAsync(string id, string version, CancellationToken cancel)
    {
        Uri url = new($"{Root}/Packages(Id={Uri.EscapeDataString(Literal(id))},Version={Uri.EscapeDataString(Literal(version))})");
        XDocument? found = await client.GetXmlAsync(url, cancel);
        XElement? entry = found?.Root?.Name == Atom + "entry" ? found.Root : found?.Root?.Elements(Atom + "entry").FirstOrDefault();
        if (entry?.Element(Meta + "properties") is not { } properties)
        {
            return null;
        }
        string listed = properties.Element(Data + "Dependencies")?.Value ?? string.Empty;
        return new PackageDependencies([.. listed.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Split(':'))
            .GroupBy(d => d.Length > 2 && d[2].Length > 0 ? d[2] : null)
            .Select(g => new DependencyGroup(g.Key, [.. g.Where(d => d[0].Length > 0).Select(d => (d[0], d.Length > 1 ? d[1] : string.Empty))]))]);
    }

    /// <summary>An OData string literal: in quotes, a quote in it doubled.</summary>
    private static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static IEnumerable<(string Id, string Version, string Description, string Authors, long Downloads, bool Listed)> Entries(XDocument? feed)
    {
        foreach (XElement entry in feed?.Root?.Elements(Atom + "entry") ?? [])
        {
            XElement? properties = entry.Element(Meta + "properties");
            string? Field(string name) => properties?.Element(Data + name)?.Value;
            string? id = Field("Id") ?? entry.Element(Atom + "title")?.Value;
            string? version = Field("NormalizedVersion") ?? Field("Version");
            if (id is null || version is null)
            {
                continue;
            }
            bool listed = !string.Equals(Field("Listed"), "false", StringComparison.OrdinalIgnoreCase) && Field("Published")?.StartsWith("1900", StringComparison.Ordinal) != true;
            yield return (id, version, Field("Description") ?? entry.Element(Atom + "summary")?.Value ?? string.Empty,
                entry.Element(Atom + "author")?.Element(Atom + "name")?.Value ?? Field("Authors") ?? string.Empty,
                long.TryParse(Field("DownloadCount"), System.Globalization.CultureInfo.InvariantCulture, out long downloads) ? downloads : 0, listed);
        }
    }
}

/// <summary>A folder of .nupkg files, the kind a nuget.config can name as a source, flat or in NuGet's id/version/
/// layout: each package's id, version, description and authors read from its .nuspec.</summary>
public sealed class FolderFeed(string folder, string? name = null) : IPackageFeed
{
    public string Name => name ?? folder;

    public Task<IReadOnlyList<PackageSummary>> SearchAsync(string query, bool prerelease, CancellationToken cancel) => BackgroundWork.Run(() =>
        (IReadOnlyList<PackageSummary>)[.. Read(prerelease)
            .Where(p => query.Trim().Length == 0 || p.Id.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => p.Version, VersionOrder.Instance).First() with { Feeds = [Name] })], cancel);

    public Task<IReadOnlyList<string>> VersionsAsync(string id, bool prerelease, CancellationToken cancel) => BackgroundWork.Run(() =>
        (IReadOnlyList<string>)[.. Read(prerelease).Where(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Select(p => p.Version)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(v => v, VersionOrder.Instance)], cancel);

    public Task<PackageDependencies?> DependenciesAsync(string id, string version, CancellationToken cancel) => BackgroundWork.Run(() =>
    {
        foreach (XElement metadata in Nuspecs())
        {
            string Field(string field) => metadata.Elements().FirstOrDefault(e => e.Name.LocalName == field)?.Value ?? string.Empty;
            if (Field("id").Equals(id, StringComparison.OrdinalIgnoreCase) && VersionOrder.Instance.Compare(Field("version"), version) == 0)
            {
                return PackageDependencies.FromNuspec(metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "dependencies"));
            }
        }
        return (PackageDependencies?)null;
    }, cancel);

    /// <summary>Each package's nuspec metadata, the flat layout's then NuGet's own (id/version/id.version.nupkg); a file
    /// that is no package passed over.</summary>
    private IEnumerable<XElement> Nuspecs()
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"{Name} is no folder: {folder} is not there");
        }
        IEnumerable<string> files = Directory.EnumerateFiles(folder, "*.nupkg")
            .Concat(Directory.EnumerateDirectories(folder).SelectMany(Directory.EnumerateDirectories).SelectMany(v => Directory.EnumerateFiles(v, "*.nupkg")));
        foreach (string file in files)
        {
            XElement? metadata = null;
            try
            {
                using ZipArchive zip = ZipFile.OpenRead(file);
                if (zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/', StringComparison.Ordinal)) is { } spec)
                {
                    using Stream stream = spec.Open();
                    metadata = XDocument.Load(stream).Root!.Elements().First(e => e.Name.LocalName == "metadata");
                }
            }
            catch (Exception error) when (error is InvalidDataException or IOException or System.Xml.XmlException or InvalidOperationException)
            {
                // A file that is not a package is passed over.
            }
            if (metadata is not null)
            {
                yield return metadata;
            }
        }
    }

    private IEnumerable<PackageSummary> Read(bool prerelease)
    {
        foreach (XElement metadata in Nuspecs())
        {
            string Field(string field) => metadata.Elements().FirstOrDefault(e => e.Name.LocalName == field)?.Value ?? string.Empty;
            PackageSummary package = new(Field("id"), Field("version"), Field("description"), Field("authors"), 0);
            if (prerelease || !VersionOrder.IsPrerelease(package.Version))
            {
                yield return package;
            }
        }
    }
}

/// <summary>The feed a configured source is: a folder (a path, or a file: URL), a v3 feed (protocolVersion 3, or a URL
/// ending in .json as NuGet decides), or a v2 feed.</summary>
public static class PackageFeeds
{
    public static IPackageFeed For(FeedSource source, HttpClient? http = null)
    {
        if (!source.IsHttp)
        {
            string path = Uri.TryCreate(source.Source, UriKind.Absolute, out Uri? uri) && uri.IsFile ? uri.LocalPath : source.Source;
            return new FolderFeed(path, source.Name);
        }
        FeedClient client = new(source.Name, new Uri(source.Source), http, source.Credentials, source.AllowInsecure, source.SkipCertificateCheck);
        bool v3 = source.ProtocolVersion == 3 || new Uri(source.Source).AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        return v3 ? new V3Feed(client) : new V2Feed(client);
    }
}

/// <summary>NuGet's order of versions, SemVer 2's precedence: by their numbers (1.0 is 1.0.0.0), a prerelease before
/// its release, and prerelease labels dot by dot, numbers by value and below words, words as NuGet compares them,
/// ignoring case. Build metadata after a + takes no part.</summary>
public sealed class VersionOrder : IComparer<string>
{
    public static readonly VersionOrder Instance = new();

    public static bool IsPrerelease(string version) => Release(version).Contains('-', StringComparison.Ordinal);

    public int Compare(string? x, string? y)
    {
        (long[] a, string[]? aLabel) = Parse(x ?? string.Empty);
        (long[] b, string[]? bLabel) = Parse(y ?? string.Empty);
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return a[i].CompareTo(b[i]);
            }
        }
        if (aLabel is null || bLabel is null)
        {
            // A release is above its prereleases.
            return (aLabel is null).CompareTo(bLabel is null);
        }
        for (int i = 0; i < Math.Min(aLabel.Length, bLabel.Length); i++)
        {
            int order = CompareIdentifiers(aLabel[i], bLabel[i]);
            if (order != 0)
            {
                return order;
            }
        }
        return aLabel.Length.CompareTo(bLabel.Length);
    }

    private static int CompareIdentifiers(string a, string b)
    {
        bool aNumber = a.Length > 0 && a.All(char.IsAsciiDigit);
        bool bNumber = b.Length > 0 && b.All(char.IsAsciiDigit);
        if (aNumber && bNumber)
        {
            // Digits of any length, compared by value: the longer number without leading zeros is the larger.
            string aDigits = a.TrimStart('0');
            string bDigits = b.TrimStart('0');
            return aDigits.Length != bDigits.Length ? aDigits.Length.CompareTo(bDigits.Length) : string.CompareOrdinal(aDigits, bDigits);
        }
        if (aNumber != bNumber)
        {
            return aNumber ? -1 : 1;
        }
        return StringComparer.OrdinalIgnoreCase.Compare(a, b);
    }

    private static string Release(string version) => version.Split('+', 2)[0];

    /// <summary>A version's four numbers, those it leaves out 0, and its prerelease label's identifiers, null for a release.</summary>
    private static (long[] Numbers, string[]? Label) Parse(string version)
    {
        string[] parts = Release(version.Trim()).Split('-', 2);
        long[] numbers = new long[4];
        string[] given = parts[0].Split('.');
        for (int i = 0; i < Math.Min(given.Length, numbers.Length); i++)
        {
            numbers[i] = long.TryParse(given[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long number) ? number : 0;
        }
        return (numbers, parts.Length > 1 ? parts[1].Split('.') : null);
    }
}
