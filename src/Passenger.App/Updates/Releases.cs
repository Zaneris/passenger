using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Packages;

namespace Passenger.App.Updates;

/// <summary>Where Passenger asks for its releases, GitHub's API's list of them unless <c>PASSENGER_RELEASES</c> names
/// another, and how long it waits for the answer.</summary>
public sealed record ReleaseFeed(Uri Listing, TimeSpan Timeout)
{
    [IdentifierHome]
    public const string Variable = "PASSENGER_RELEASES";

    /// <summary>GitHub's list of the releases, newest first, a page of the newest 30.</summary>
    public static readonly Uri GitHub = new("https://api.github.com/repos/Zaneris/passenger/releases");

    /// <summary>The releases page, for a release that names neither its own page nor a download for this system.</summary>
    public static readonly Uri GitHubReleases = new("https://github.com/Zaneris/passenger/releases");

    /// <summary>Long enough for GitHub over a slow line, short enough that a network that swallows the request costs
    /// nothing a person waits on.</summary>
    public static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Where a release's files download from, each under its tag (v0.2.0/SHA256SUMS): GitHub's for GitHub's feed,
    /// else the same path on the feed's own host.</summary>
    public Uri Downloads => Listing == GitHub ? new Uri("https://github.com/Zaneris/passenger/releases/download/") : new Uri(Listing, "/Zaneris/passenger/releases/download/");

    /// <summary>GitHub's, or the one <c>PASSENGER_RELEASES</c> names (<see cref="EnvironmentValues.Address(string, string)"/>).</summary>
    public static ReleaseFeed Default =>
        new(EnvironmentValues.Address(Variable, "of the releases as GitHub's API lists them") ?? GitHub, ShortTimeout);
}

/// <summary>A release newer than the running Passenger: its version ("0.2.0"), its tag ("v0.2.0"), and what its link
/// opens, this system's download when the release has it, else the release's page.</summary>
public sealed record LatestRelease(string Version, string Tag, Uri Link, string? Asset)
{
    /// <summary>The release's page on GitHub, its notes.</summary>
    public Uri Page { get; init; } = ReleaseFeed.GitHubReleases;

    /// <summary>Every file the release carries that downloads over http or https, by name: what an update downloads.</summary>
    public IReadOnlyList<ReleaseFile> Files { get; init; } = [];

    /// <summary>What updating to it fixes of security: what the newest security release between the running version and it
    /// says it fixes, its notes' Security section's first line (<see cref="ReleaseNotes.SecurityOf"/>), empty where they say
    /// nothing more; null where no release after the running one is a security release.</summary>
    public string? Security { get; init; }

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

/// <summary>Asks GitHub's list of releases, one GET as <see cref="OutboundHttp"/> makes it with the API's media type added:
/// no token. The newest release listed by its version is the latest; pre-releases and drafts are never offered, whether
/// GitHub marks them so or their tag carries a pre-release label, and say nothing of security.</summary>
public static partial class Releases
{
    /// <summary>The feed's own timeout, or a download's stall, is the one that applies.</summary>
    private static readonly HttpClient Http = OutboundHttp.Client(System.Threading.Timeout.InfiniteTimeSpan, most: 4 * 1024 * 1024);

    /// <summary>The most the list may be: a release's notes and downloads are some tens of kilobytes, so a page of 30 a
    /// few megabytes at most, and anything far past that is no list of Passenger's releases.</summary>
    private const long ListingLimit = 8 * 1024 * 1024;

    private const string GitHubJson = "application/vnd.github+json";

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

    /// <summary>The latest release's download for this system and its own processor (<see cref="ThisSystemsAsset"/>), else
    /// the latest release's page.</summary>
    public static Uri ThisSystemsDownload => new(ReleaseFeed.GitHubReleases, ThisSystemsAsset() is { } asset ? $"releases/latest/download/{asset}" : "releases/latest");

    /// <summary>Asks the feed for its releases and compares the latest with <paramref name="running"/>, off the caller's
    /// thread. Never throws: a failure, the feed's timeout and <paramref name="cancel"/> included, is an answer.</summary>
    public static async Task<ReleaseAnswer> AskAsync(ReleaseFeed feed, string running, CancellationToken cancel)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(feed.Timeout);
        try
        {
            string missing = $"{feed.Listing} has no releases (404 Not Found)";
            (byte[]? bytes, string? why) = await OutboundHttp.ReadAsync(Http, feed.Listing, ListingLimit, "The list of releases", missing, GitHubJson, limit.Token).ConfigureAwait(false);
            if (bytes is null)
            {
                return why == missing ? new ReleaseAnswer.Current(missing) : Failed(feed, why ?? "it gave no answer");
            }
            GitHubRelease[]? releases = JsonSerializer.Deserialize<GitHubRelease[]>(bytes, Json);
            return releases is null ? Failed(feed, "its answer is not a list of releases (null)") : Compare(releases, running);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return Failed(feed, FormattableString.Invariant($"no answer in {Durations.Said(feed.Timeout)}"));
        }
        catch (OperationCanceledException)
        {
            return Failed(feed, "the window closed before it answered");
        }
        catch (NotSupportedException error)
        {
            return Failed(feed, error.Message);
        }
        catch (JsonException error)
        {
            return Failed(feed, $"its answer is not a list of releases ({error.Message})");
        }
    }

    /// <summary>The newest release listed, offered where it is newer than <paramref name="running"/>, a security update where
    /// any release after <paramref name="running"/> up to it is a security release, saying what the newest of those fixes.</summary>
    private static ReleaseAnswer Compare(IReadOnlyList<GitHubRelease?> listed, string running)
    {
        List<(GitHubRelease Release, string Version)> offered = [];
        foreach (GitHubRelease? listing in listed)
        {
            if (listing is not null && Offered(listing) is { } version)
            {
                offered.Add((listing, version));
            }
        }
        if (offered.Count == 0)
        {
            return new ReleaseAnswer.Current(listed.Count == 0 ? "no release is out"
                : $"no release listed is offered, each a draft, a pre-release or no version: {string.Join(", ", listed.Select(r => $"'{r?.TagName}'"))}");
        }
        offered = [.. offered.OrderByDescending(r => r.Version, VersionOrder.Instance)];
        (GitHubRelease release, string number) = offered[0];
        string tag = release.TagName!;
        if (VersionOrder.Instance.Compare(number, running) <= 0)
        {
            return new ReleaseAnswer.Current($"Passenger {running} is up to date: the latest release is {tag}");
        }
        (GitHubRelease By, string Fixes)? marked = null;
        foreach ((GitHubRelease newer, _) in offered.TakeWhile(r => VersionOrder.Instance.Compare(r.Version, running) > 0))
        {
            if (newer.Body is { } body && ReleaseNotes.SecurityOf(body) is { } fixes)
            {
                marked = (newer, fixes);
                break;
            }
        }
        string? asset = ThisSystemsAsset();
        ReleaseFile[] files = [.. (release.Assets ?? []).Select(a => a.Name is { Length: > 0 } name && WebAddress.Of(a.BrowserDownloadUrl) is { } url
            ? new ReleaseFile(name, url, a.Size) : null).OfType<ReleaseFile>()];
        Uri? download = files.FirstOrDefault(f => f.Name == asset)?.Url;
        Uri page = WebAddress.Of(release.HtmlUrl) ?? ReleaseFeed.GitHubReleases;
        Uri link = download ?? page;
        string security = marked is not { By: var by } ? string.Empty
            : by == release ? ", a security release"
            : $", a security update as {by.TagName} is a security release";
        return new ReleaseAnswer.Newer(new LatestRelease(number, tag, link, download is null ? null : asset) { Page = page, Files = files, Security = marked?.Fixes },
            $"Passenger {number} is out ({tag}; this is {running}){security}: {link}");
    }

    /// <summary>A release's version where it may be offered, a release that is neither a draft nor a pre-release by GitHub's
    /// mark or its tag's label; null for any other.</summary>
    private static string? Offered(GitHubRelease release) =>
        !release.Draft && !release.Prerelease && Tag().Match(release.TagName ?? string.Empty) is { Success: true } version && !version.Groups["label"].Success
            ? version.Groups["version"].Value
            : null;

    private static ReleaseAnswer.Failed Failed(ReleaseFeed feed, string reason) =>
        new(reason, $"could not ask {feed.Listing} for the latest release: {reason}");

    /// <summary>A release's tag: a version of two to four numbers, a v before it or not, a pre-release label and build
    /// metadata after it or not.</summary>
    [GeneratedRegex(@"^[vV]?(?<version>\d+(?:\.\d+){1,3})(?<label>-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Tag();

    /// <summary>What Passenger reads of GitHub's release: its tag and page, whether it is a draft or a pre-release, its
    /// downloads by name, and its notes.</summary>
    private sealed record GitHubRelease(string? TagName, string? HtmlUrl, bool Draft, bool Prerelease, GitHubAsset[]? Assets, string? Body);

    private sealed record GitHubAsset(string? Name, string? BrowserDownloadUrl, long Size);
}
