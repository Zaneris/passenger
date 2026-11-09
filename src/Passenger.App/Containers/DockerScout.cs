using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>A known vulnerability of an image's, as Docker Scout reports it: its id (a CVE's), severity, the package that
/// brings it at its version, and the version that fixes it, where one does.</summary>
public sealed record ScoutCve(string Id, string Severity, string Package, string? Version, string? FixedIn);

/// <summary>What Docker Scout said of an image: its known vulnerabilities, worst first, and the newer tag of its base it
/// recommends, where it names one; or why it could not say (no scout, the image unknown, no Docker login).</summary>
public sealed record ScoutReport(IReadOnlyList<ScoutCve> Cves, string? Recommended, string? Problem)
{
    /// <summary>Its counts by severity, critical first, as words ("2 critical, 5 high").</summary>
    public string Counts => Cves.Count == 0 ? "No known vulnerabilities"
        : string.Join(", ", DockerScout.Severities.Select(s => (s, Cves.Count(c => c.Severity == s))).Where(c => c.Item2 > 0).Select(c => $"{c.Item2} {c.s}"));

    /// <summary>Whether it has a critical or high vulnerability, which a Dockerfile's FROM is marked for.</summary>
    public bool Grave => Cves.Any(c => c.Severity is "critical" or "high");
}

/// <summary>An image's known vulnerabilities through the person's own Docker Scout, the docker CLI's plugin: its CVEs
/// (<c>docker scout cves --format sarif</c>) and its base's recommended update (<c>docker scout recommendations
/// --only-update</c>), never a registry API or a scanner of Passenger's. Nothing is asked of it but when the person asks;
/// what it sends is said by <see cref="Sends"/>. Without the plugin, <see cref="Missing"/> says why and how to get it.</summary>
public static class DockerScout
{
    /// <summary>The severities Scout gives, worst first.</summary>
    public static readonly IReadOnlyList<string> Severities = ["critical", "high", "medium", "low", "unspecified"];

    /// <summary>What a check sends, as the window says before it asks.</summary>
    public const string Sends = "Docker Scout makes the image's package list (its SBOM) on this machine and sends it to Docker's Scout service under your "
        + "Docker login, to match it with known vulnerabilities; an image this machine lacks it reads from its registry first. Passenger sends nothing itself.";

    /// <summary>Why there is no scout, and how to get it.</summary>
    public const string Missing = "Docker Scout is not installed: it comes with Docker Desktop, or install the docker-scout CLI plugin (github.com/docker/scout-cli)";

    /// <summary>How long a check may take: a first scan of a large image included.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    /// <summary>Asks Scout of an image: its CVEs, then its base's recommended update. Any thread.</summary>
    public static async Task<ScoutReport> CheckAsync(ContainerEngine engine, string image, CancellationToken cancel)
    {
        ChildResult cves;
        try
        {
            cves = await engine.RunAsync(["scout", "cves", "--format", "sarif", image], Limit, cancel);
        }
        catch (Exception error) when (ChildFailures.Of(error))
        {
            return new ScoutReport([], null, error.Message);
        }
        if (!cves.Succeeded)
        {
            // Without the plugin the CLI refuses the flags first: its version, asked alone, says whether there is a scout.
            bool missing = IsMissing(cves) || IsMissing(await engine.RunAsync(["scout", "version"], TimeSpan.FromSeconds(20), cancel));
            return new ScoutReport([], null, missing ? Missing : $"Docker Scout could not check {image}: {ContainerEngine.Said(cves)}");
        }
        IReadOnlyList<ScoutCve> found;
        try
        {
            found = Read(cves.Output);
        }
        catch (JsonException error)
        {
            return new ScoutReport([], null, $"Docker Scout's report on {image} does not read: {error.Message}");
        }
        string? recommended = null;
        try
        {
            ChildResult advice = await engine.RunAsync(["scout", "recommendations", "--only-update", image], Limit, cancel);
            recommended = advice.Succeeded ? RecommendedTag(advice.Output) : null;
        }
        catch (TimeoutException)
        {
            // Its CVEs stand without a recommendation.
        }
        return new ScoutReport(found, recommended, null);
    }

    /// <summary>Whether the docker CLI said it has no scout command.</summary>
    private static bool IsMissing(ChildResult result) =>
        $"{result.Output}\n{result.Error}".Contains("is not a docker command", StringComparison.OrdinalIgnoreCase)
        || $"{result.Output}\n{result.Error}".Contains("unknown command: docker scout", StringComparison.OrdinalIgnoreCase);

    /// <summary>The CVEs of Scout's SARIF report: each rule a CVE with its severity, package (its purl) and fixed version.</summary>
    internal static IReadOnlyList<ScoutCve> Read(string sarif)
    {
        using JsonDocument read = JsonDocument.Parse(sarif);
        List<ScoutCve> found = [];
        foreach (JsonElement run in JsonMembers.Items(read.RootElement, "runs"))
        {
            foreach (JsonElement rule in JsonMembers.Items(JsonMembers.Object(JsonMembers.Object(run, "tool"), "driver"), "rules"))
            {
                if (JsonMembers.Text(rule, "id") is not { Length: > 0 } id)
                {
                    continue;
                }
                JsonElement? properties = JsonMembers.Object(rule, "properties");
                (string package, string? version) = JsonMembers.Texts(properties, "purls") is [var purl, ..] ? Package(purl) : ("unknown", null);
                found.Add(new ScoutCve(id, SeverityOf(properties), package, version ?? JsonMembers.Text(properties, "affected_version"),
                    JsonMembers.Text(properties, "fixed_version") is { Length: > 0 } fixedIn and not "not fixed" ? fixedIn : null));
            }
        }
        return [.. found.OrderBy(c => IndexOf(c.Severity)).ThenBy(c => c.Id, StringComparer.Ordinal)];
    }

    private static int IndexOf(string severity)
    {
        for (int at = 0; at < Severities.Count; at++)
        {
            if (Severities[at] == severity)
            {
                return at;
            }
        }
        return Severities.Count;
    }

    /// <summary>A rule's severity: its CVSS severity's word, else its score's band.</summary>
    private static string SeverityOf(JsonElement? properties)
    {
        if (JsonMembers.Text(properties, "cvssV3_severity")?.ToLowerInvariant() is { } said && Severities.Contains(said))
        {
            return said;
        }
        return double.TryParse(JsonMembers.Scalar(properties, "security-severity"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double score)
            ? score >= 9 ? "critical" : score >= 7 ? "high" : score >= 4 ? "medium" : score > 0 ? "low" : "unspecified"
            : "unspecified";
    }

    /// <summary>A package's name and version from its purl (<c>pkg:deb/debian/openssl@3.0.11?arch=amd64</c>).</summary>
    private static (string Name, string? Version) Package(string purl)
    {
        string path = purl.Split('?', '#')[0];
        int at = path.LastIndexOf('@');
        string named = at > 0 ? path[..at] : path;
        string name = Uri.UnescapeDataString(named[(named.LastIndexOf('/') + 1)..]);
        return (name.Length > 0 ? name : purl, at > 0 ? Uri.UnescapeDataString(path[(at + 1)..]) : null);
    }

    /// <summary>The first tag of the table Scout's recommendations list under Change base image, the one it rates best.</summary>
    internal static string? RecommendedTag(string said)
    {
        string[] lines = said.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        for (int at = 0; at < lines.Length; at++)
        {
            string[] cells = lines[at].Split('│');
            if (cells.Length < 2 || cells[0].Trim() != "Tag")
            {
                continue;
            }
            foreach (string row in lines.Skip(at + 1))
            {
                if (row.Trim().Length == 0 || row.TrimStart().StartsWith('─'))
                {
                    continue;
                }
                return row.Split('│')[0].Trim() is { Length: > 0 } tag && !tag.Contains(' ', StringComparison.Ordinal) ? tag : null;
            }
        }
        return null;
    }
}
