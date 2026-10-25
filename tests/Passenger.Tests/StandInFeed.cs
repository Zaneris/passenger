using System.IO.Compression;
using System.Text;
using Passenger.Packages;

namespace Passenger.Tests;

/// <summary>A NuGet v3 feed on a loopback port, so no test reaches nuget.org: its service index, search and flat container,
/// and once an advisory is given its VulnerabilityInfo resource, which restore's audit and the NuGet window read as they
/// read nuget.org's. Made with a user and password, it answers only a request with those Basic credentials (401 and a
/// challenge otherwise, as a private feed asks); every request is kept with whether it had them and the Authorization it
/// carried.</summary>
public sealed class StandInFeed : IDisposable
{
    private readonly StandInHttp http;
    private readonly Dictionary<string, Dictionary<string, byte[]>> packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> advisories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Path, bool Authorized, string? Authorization)> requests = [];
    private string? accepted;

    public StandInFeed(string? user = null, string? password = null)
    {
        Password = password;
        accepted = user is null ? null : Basic(user, password ?? string.Empty);
        http = new StandInHttp(AnswerAsync);
    }

    public string Root => http.Root;

    public Uri Index => new($"{Root}/v3/index.json");

    public string? Password { get; }

    public IReadOnlyList<(string Path, bool Authorized, string? Authorization)> Requests
    {
        get
        {
            lock (requests)
            {
                return [.. requests];
            }
        }
    }

    /// <summary>Where its service index says its search is: its own, unless another host's is given.</summary>
    public Uri? SearchAt { get; set; }

    public void Add(string id, string version) => (packages.TryGetValue(id, out Dictionary<string, byte[]>? versions) ? versions : packages[id] = [])[version] = Package(id, version);

    /// <summary>A known vulnerability of a package's versions (a NuGet range, "[1.0.0, 1.0.1)"), at NuGet's severity (0
    /// low to 3 critical), with its advisory's address.</summary>
    public void Advise(string id, int severity, string url, string versions)
    {
        lock (advisories)
        {
            (advisories.TryGetValue(id, out List<string>? known) ? known : advisories[id] = []).Add(
                $$"""{ "severity": {{severity}}, "url": "{{url}}", "versions": "{{versions}}" }""");
        }
    }

    /// <summary>Takes these credentials from now on, in place of the ones it was made with.</summary>
    public void Accept(string user, string password) => Volatile.Write(ref accepted, Basic(user, password));

    public void Dispose() => http.Dispose();

    /// <summary>A package with nothing in it but its manifest and an empty lib folder, so any framework takes it.</summary>
    public static byte[] Package(string id, string version)
    {
        using MemoryStream bytes = new();
        using (ZipArchive zip = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (StreamWriter spec = new(zip.CreateEntry($"{id}.nuspec").Open()))
            {
                spec.Write($"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                      <metadata>
                        <id>{id}</id>
                        <version>{version}</version>
                        <authors>Passenger Tests</authors>
                        <description>A package for the tests.</description>
                      </metadata>
                    </package>
                    """);
            }
            zip.CreateEntry("lib/netstandard2.0/_._");
        }
        return bytes.ToArray();
    }

    private static string Basic(string user, string password) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    private Task AnswerAsync(StandInHttp.Exchange exchange)
    {
        string? authorization = exchange.Header("Authorization");
        string? wanted = Volatile.Read(ref accepted);
        bool authorized = wanted is null || authorization == wanted;
        lock (requests)
        {
            requests.Add((exchange.Path, authorized, authorization));
        }
        (int status, string type, byte[] body) = authorized ? Route(exchange.Path) : (401, "text/plain", Encoding.UTF8.GetBytes("Who are you?"));
        return exchange.ReplyAsync(status, type, body, status == 401 ? "WWW-Authenticate: Basic realm=\"private\"\r\n" : string.Empty);
    }

    private (int Status, string Type, byte[] Body) Route(string path)
    {
        const string Json = "application/json";
        string[] parts = path.Split('?', 2)[0].Trim('/').Split('/');
        bool advising;
        lock (advisories)
        {
            advising = advisories.Count > 0;
        }
        if (path == "/v3/index.json")
        {
            string search = SearchAt?.AbsoluteUri ?? $"{Root}/query";
            string vulnerabilities = advising ? $$""", { "@id": "{{Root}}/vulnerabilities/index.json", "@type": "VulnerabilityInfo/6.7.0" }""" : string.Empty;
            return (200, Json, Encoding.UTF8.GetBytes($$"""
                {
                  "version": "3.0.0",
                  "resources": [
                    { "@id": "{{search}}", "@type": "SearchQueryService/3.5.0" },
                    { "@id": "{{Root}}/flat/", "@type": "PackageBaseAddress/3.0.0" }{{vulnerabilities}}
                  ]
                }
                """));
        }
        if (advising && parts is ["vulnerabilities", "index.json"])
        {
            return (200, Json, Encoding.UTF8.GetBytes($$"""[ { "@name": "base", "@id": "{{Root}}/vulnerabilities/base.json", "@updated": "2026-10-01T00:00:00Z" } ]"""));
        }
        if (advising && parts is ["vulnerabilities", "base.json"])
        {
            lock (advisories)
            {
                IEnumerable<string> known = advisories.Select(a => $"\"{a.Key.ToLowerInvariant()}\": [{string.Join(", ", a.Value)}]");
                return (200, Json, Encoding.UTF8.GetBytes($"{{ {string.Join(", ", known)} }}"));
            }
        }
        if (parts is ["query"])
        {
            string query = System.Web.HttpUtility.ParseQueryString(path.Split('?', 2)[1])["q"] ?? string.Empty;
            IEnumerable<string> found = packages.Where(p => p.Key.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(p =>
            {
                string newest = p.Value.Keys.OrderByDescending(v => v, VersionOrder.Instance).First();
                return $$"""{ "id": "{{p.Key}}", "version": "{{newest}}", "description": "A private package.", "authors": "Private Tests", "totalDownloads": 0 }""";
            });
            return (200, Json, Encoding.UTF8.GetBytes($$"""{ "totalHits": {{found.Count()}}, "data": [{{string.Join(", ", found)}}] }"""));
        }
        if (parts is ["flat", string id, "index.json"] && packages.TryGetValue(id, out Dictionary<string, byte[]>? versions))
        {
            return (200, Json, Encoding.UTF8.GetBytes($$"""{ "versions": [{{string.Join(", ", versions.Keys.Select(v => $"\"{v}\""))}}] }"""));
        }
        if (parts is ["flat", string package, string version, string file] && packages.TryGetValue(package, out Dictionary<string, byte[]>? held)
            && held.TryGetValue(version, out byte[]? bytes) && file == $"{package}.{version}.nupkg".ToLowerInvariant())
        {
            return (200, "application/octet-stream", bytes);
        }
        return (404, "text/plain", []);
    }
}
