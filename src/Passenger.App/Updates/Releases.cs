using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Packages;

namespace Passenger.App.Updates;

/// <summary>Where Passenger asks for its latest release, GitHub's API unless <c>PASSENGER_RELEASES</c> names another, and
/// how long it waits for the answer.</summary>
public sealed record ReleaseFeed(Uri Latest, TimeSpan Timeout)
{
    [IdentifierHome]
    public const string Variable = "PASSENGER_RELEASES";

    public static readonly Uri GitHub = new("https://api.github.com/repos/Zaneris/passenger/releases/latest");

    /// <summary>The releases page, for a release that names neither its own page nor a download for this system.</summary>
    public static readonly Uri GitHubReleases = new("https://github.com/Zaneris/passenger/releases");

    /// <summary>Long enough for GitHub over a slow line, short enough that a network that swallows the request costs
    /// nothing a person waits on.</summary>
    public static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Where a release's files download from, each under its tag (v0.2.0/SHA256SUMS): GitHub's for GitHub's feed,
    /// else the same path on the feed's own host.</summary>
    public Uri Downloads => Latest == GitHub ? new Uri("https://github.com/Zaneris/passenger/releases/download/") : new Uri(Latest, "/Zaneris/passenger/releases/download/");

    /// <summary>GitHub's, or the one <c>PASSENGER_RELEASES</c> names; a value there that is no http or https URL is
    /// fatal.</summary>
    public static ReleaseFeed Default => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } named
        ? new ReleaseFeed(Parse(named), ShortTimeout)
        : new ReleaseFeed(GitHub, ShortTimeout);

    private static Uri Parse(string text) =>
        WebAddress.Of(text)
        ?? throw new FormatException($"{Variable} takes the http or https URL of a latest release as GitHub's API answers it, not '{text}'");
}

/// <summary>A release newer than the running Passenger: its version ("0.2.0"), its tag ("v0.2.0"), and what its link
/// opens, this system's download when the release has it, else the release's page.</summary>
public sealed record LatestRelease(string Version, string Tag, Uri Link, string? Asset)
{
    /// <summary>The release's page on GitHub, its notes.</summary>
    public Uri Page { get; init; } = ReleaseFeed.GitHubReleases;

    /// <summary>Every file the release carries that downloads over http or https, by name: what an update downloads.</summary>
    public IReadOnlyList<ReleaseFile> Files { get; init; } = [];

    /// <summary>The file the release carries by this name, or null.</summary>
    public ReleaseFile? File(string name) => Files.FirstOrDefault(f => f.Name == name);
}

/// <summary>A file a release carries: its name, where it downloads from, and its size as GitHub gives it.</summary>
public sealed record ReleaseFile(string Name, Uri Url, long Size);

/// <summary>What asking for the latest release came to, with a line for the IDE's log.</summary>
public abstract record ReleaseAnswer(string Said)
{
    /// <summary>A release newer than the running one, offered.</summary>
    public sealed record Newer(LatestRelease Release, string Said) : ReleaseAnswer(Said);

    /// <summary>Nothing newer to offer: the latest is this version or an older one, a pre-release or a draft, or there is
    /// no release at all.</summary>
    public sealed record Current(string Said) : ReleaseAnswer(Said);

    /// <summary>The question went unanswered or its answer did not read: no network, a refusal, an error, no answer in
    /// time.</summary>
    public sealed record Failed(string Reason, string Said) : ReleaseAnswer(Said);
}

/// <summary>Asks GitHub's releases/latest, a GET with nothing in it but a User-Agent naming Passenger and its version
/// (GitHub refuses a request without one) and the API's media type: no token, no cookie, no trace header. Pre-releases
/// and drafts are never offered, whether GitHub marks them so or their tag carries a pre-release label.</summary>
public static partial class Releases
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseCookies = false,
        // Nothing but the request itself: no traceparent header from an activity that happens to be current.
        ActivityHeadersPropagator = null,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        // The feed's own timeout is the one that applies.
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        // A release's JSON is some kilobytes; anything far past that is not one.
        MaxResponseContentBufferSize = 4 * 1024 * 1024,
    };

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The download the release workflow names for this system and processor ("Passenger-macos-arm64.dmg"), the
    /// same in every release, or null for one it builds none for. The system's processor, not the process's: x64 Passenger
    /// under Rosetta or Windows' emulation is offered the native build.</summary>
    public static string? ThisSystemsAsset()
    {
        string? processor = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => null,
        };
        return processor is null ? null
            : OperatingSystem.IsMacOS() ? $"Passenger-macos-{processor}.dmg"
            : OperatingSystem.IsWindows() ? $"Passenger-windows-{processor}.exe"
            : OperatingSystem.IsLinux() ? $"Passenger-linux-{processor}.tar.gz"
            : null;
    }

    /// <summary>Asks the feed for the latest release and compares it with <paramref name="running"/>, off the caller's
    /// thread. Never throws: a failure, the feed's timeout and <paramref name="cancel"/> included, is an answer.</summary>
    public static async Task<ReleaseAnswer> AskAsync(ReleaseFeed feed, string running, CancellationToken cancel)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(feed.Timeout);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, feed.Latest);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Passenger", running));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using HttpResponseMessage response = await Http.SendAsync(request, limit.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ReleaseAnswer.Current($"{feed.Latest} has no release (404 Not Found)");
            }
            if (!response.IsSuccessStatusCode)
            {
                return Failed(feed, $"it answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            GitHubRelease? release = await response.Content.ReadFromJsonAsync<GitHubRelease>(Json, limit.Token).ConfigureAwait(false);
            return release is null ? Failed(feed, "its answer is not a release (null)") : Compare(release, running);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return Failed(feed, FormattableString.Invariant($"no answer in {feed.Timeout.TotalSeconds:0.#} s"));
        }
        catch (OperationCanceledException)
        {
            return Failed(feed, "the window closed before it answered");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or NotSupportedException)
        {
            return Failed(feed, error.Message);
        }
        catch (JsonException error)
        {
            return Failed(feed, $"its answer is not a release ({error.Message})");
        }
    }

    private static ReleaseAnswer Compare(GitHubRelease release, string running)
    {
        string tag = release.TagName ?? string.Empty;
        Match version = Tag().Match(tag);
        if (release.Draft || release.Prerelease || !version.Success || version.Groups["label"].Success)
        {
            string why = release.Draft ? "a draft" : !version.Success ? "not a version" : "a pre-release";
            return new ReleaseAnswer.Current($"the latest release, '{tag}', is {why}, and is not offered");
        }
        string number = version.Groups["version"].Value;
        if (VersionOrder.Instance.Compare(number, running) <= 0)
        {
            return new ReleaseAnswer.Current($"Passenger {running} is up to date: the latest release is {tag}");
        }
        string? asset = ThisSystemsAsset();
        ReleaseFile[] files = [.. (release.Assets ?? []).Select(a => a.Name is { Length: > 0 } name && WebAddress.Of(a.BrowserDownloadUrl) is { } url
            ? new ReleaseFile(name, url, a.Size) : null).OfType<ReleaseFile>()];
        Uri? download = files.FirstOrDefault(f => f.Name == asset)?.Url;
        Uri page = WebAddress.Of(release.HtmlUrl) ?? ReleaseFeed.GitHubReleases;
        Uri link = download ?? page;
        return new ReleaseAnswer.Newer(new LatestRelease(number, tag, link, download is null ? null : asset) { Page = page, Files = files },
            $"Passenger {number} is out ({tag}; this is {running}): {link}");
    }

    private static ReleaseAnswer.Failed Failed(ReleaseFeed feed, string reason) =>
        new(reason, $"could not ask {feed.Latest} for the latest release: {reason}");

    /// <summary>A release's tag: a version of two to four numbers, a v before it or not, a pre-release label and build
    /// metadata after it or not.</summary>
    [GeneratedRegex(@"^[vV]?(?<version>\d+(?:\.\d+){1,3})(?<label>-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Tag();

    /// <summary>What Passenger reads of GitHub's release: its tag and page, whether it is a draft or a pre-release, and
    /// its downloads by name.</summary>
    private sealed record GitHubRelease(string? TagName, string? HtmlUrl, bool Draft, bool Prerelease, GitHubAsset[]? Assets);

    private sealed record GitHubAsset(string? Name, string? BrowserDownloadUrl, long Size);
}
