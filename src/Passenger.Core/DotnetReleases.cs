using System.Runtime.InteropServices;
using System.Text.Json;

namespace Passenger.Core;

/// <summary>The .NET SDK a machine or a folder wants: a version a global.json asks for with its rollForward, a channel
/// ("11.0") a project's framework needs, or neither for the newest Microsoft supports.</summary>
public sealed record SdkWanted(string? Version = null, string? RollForward = null, string? Channel = null)
{
    public static readonly SdkWanted Newest = new();

    /// <summary>As a person is told what is installed: the version asked, the channel's newest, or the newest there is.</summary>
    public string Shown => Version ?? (Channel is { } channel ? $".NET {channel}'s newest" : "the newest supported");
}

/// <summary>An SDK to install: its version, the runtime it carries, its archive for this system and the SHA-512 Microsoft
/// publishes of it (hex).</summary>
public sealed record SdkDownload(string Version, string Runtime, Uri Url, string Sha512)
{
    public string FileName => Path.GetFileName(Url.AbsolutePath);
}

/// <summary>One of .NET's channels as the index lists it: its version ("10.0"), its support phase (active, maintenance,
/// go-live, preview, eol) and where its releases are listed.</summary>
public sealed record DotnetChannel(string Version, string Support, Uri Releases)
{
    /// <summary>Microsoft supports it in production: neither a preview nor at its end of life.</summary>
    public bool Supported => Support is "active" or "maintenance";
}

/// <summary>Microsoft's release metadata of .NET, read through <see cref="OutboundHttp"/>: the index of its channels, and
/// each channel's releases.json naming every SDK with its archive for each system and that archive's SHA-512.</summary>
public static class DotnetReleases
{
    /// <summary>Where the metadata is read, in place of Microsoft's: an http or https URL of a folder holding releases-index.json.</summary>
    [IdentifierHome]
    public const string Variable = "PASSENGER_DOTNET_RELEASES";

    public static readonly Uri Microsoft = new("https://builds.dotnet.microsoft.com/dotnet/release-metadata/");

    /// <summary>The page a person downloads the SDK from by hand.</summary>
    public static readonly Uri DownloadPage = new("https://dotnet.microsoft.com/download");

    /// <summary>The most a channel's releases.json may be: some megabytes, growing with each release.</summary>
    private const long MostMetadata = 64 * 1024 * 1024;

    private static readonly HttpClient Http = OutboundHttp.Client(TimeSpan.FromSeconds(60), MostMetadata);

    /// <summary>Microsoft's, or the one <see cref="Variable"/> names; a value there that is no http or https URL is fatal.</summary>
    public static Uri Feed => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } named
        ? WebAddress.Of(named.EndsWith('/') ? named : named + "/")
            ?? throw new FormatException($"{Variable} takes the http or https URL of a folder holding .NET's releases-index.json, not '{named}'")
        : Microsoft;

    /// <summary>The download page of a channel's SDKs ("10.0"), or of them all.</summary>
    public static Uri PageOf(string? channel) => channel is null ? DownloadPage : new Uri($"https://dotnet.microsoft.com/download/dotnet/{channel}");

    /// <summary>This system's runtime identifier as Microsoft names its archives ("osx-arm64", "linux-musl-x64"), by the
    /// system's processor, not the process's: an x64 Passenger under emulation installs the native SDK.</summary>
    public static string ThisSystem()
    {
        string own = RuntimeInformation.RuntimeIdentifier;
        int dash = own.LastIndexOf('-');
        string system = dash > 0 ? own[..dash] : OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        return $"{system}-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
    }

    /// <summary>The SDK to install for what is wanted, its archive this system's: the index read, then each channel that may
    /// hold it until one does. A <see cref="DotnetInstallFailed"/> saying why when none does or the metadata does not come.</summary>
    public static async Task<SdkDownload> ChooseAsync(SdkWanted wanted, string system, CancellationToken cancel)
    {
        Uri index = new(Feed, "releases-index.json");
        IReadOnlyList<DotnetChannel> channels;
        using (JsonDocument read = await ReadAsync(index, cancel).ConfigureAwait(false))
        {
            channels = Channels(read.RootElement);
        }
        foreach (DotnetChannel channel in ChannelsFor(wanted, channels))
        {
            using JsonDocument releases = await ReadAsync(channel.Releases, cancel).ConfigureAwait(false);
            if (Choose(wanted, releases.RootElement, system) is { } chosen)
            {
                return chosen;
            }
        }
        throw new DotnetInstallFailed($"Microsoft publishes no .NET SDK {wanted.Shown} for {system}" + (wanted.RollForward is { } roll ? $" (global.json's rollForward {roll})" : string.Empty));
    }

    /// <summary>The channels the index lists, as it orders them (newest first).</summary>
    public static IReadOnlyList<DotnetChannel> Channels(JsonElement index) =>
    [
        .. JsonMembers.Items(index, "releases-index").Select(c => JsonMembers.Text(c, "channel-version") is { } version
            && JsonMembers.Text(c, "releases.json") is { } listed && WebAddress.Of(listed) is { } releases
                ? new DotnetChannel(version, JsonMembers.Text(c, "support-phase") ?? string.Empty, releases)
                : null).OfType<DotnetChannel>(),
    ];

    /// <summary>The channels that may hold the SDK wanted, in the order they are read: the one asked for (a channel, or the
    /// version's own), then, where global.json's rollForward lets a version go past its minor or major, the supported ones
    /// newest first; with nothing asked, the newest supported.</summary>
    public static IEnumerable<DotnetChannel> ChannelsFor(SdkWanted wanted, IReadOnlyList<DotnetChannel> channels)
    {
        if (wanted.Channel is { } asked)
        {
            return channels.Where(c => c.Version == asked).Take(1);
        }
        if (wanted.Version is not { } version)
        {
            return channels.Where(c => c.Supported).OrderByDescending(c => c.Version, VersionOrder.Instance).Take(1);
        }
        string own = string.Join('.', version.Split('.').Take(2));
        IEnumerable<DotnetChannel> first = channels.Where(c => c.Version == own);
        return Roll(wanted.RollForward) >= Rolls.Minor
            ? first.Concat(channels.Where(c => c.Supported && c.Version != own && Satisfies(wanted, c.Version + ".0")).OrderByDescending(c => c.Version, VersionOrder.Instance))
            : first;
    }

    /// <summary>The newest SDK a channel's releases.json lists that the wanted takes, with this system's archive (a .zip on
    /// Windows, a .tar.gz elsewhere); null for none.</summary>
    public static SdkDownload? Choose(SdkWanted wanted, JsonElement releases, string system)
    {
        string kind = system.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
        return JsonMembers.Items(releases, "releases")
            .SelectMany(r => JsonMembers.Items(r, "sdks") is { Count: > 0 } sdks ? sdks : JsonMembers.Object(r, "sdk") is { } one ? [one] : [])
            .Select(sdk => JsonMembers.Text(sdk, "version") is { } version && Satisfies(wanted, version)
                && JsonMembers.Items(sdk, "files").FirstOrDefault(f => JsonMembers.Text(f, "rid") == system
                    && JsonMembers.Text(f, "name")?.EndsWith(kind, StringComparison.OrdinalIgnoreCase) == true) is { ValueKind: JsonValueKind.Object } file
                && JsonMembers.Text(file, "url") is { } url && WebAddress.Of(url) is { } at && JsonMembers.Text(file, "hash") is { Length: > 0 } hash
                    ? new SdkDownload(version, JsonMembers.Text(sdk, "runtime-version") ?? string.Empty, at, hash.ToLowerInvariant())
                    : null)
            .OfType<SdkDownload>()
            .DistinctBy(d => d.Version)
            .OrderByDescending(d => d.Version, VersionOrder.Instance)
            .FirstOrDefault();
    }

    /// <summary>How far global.json's rollForward lets an SDK go past the version it asks for.</summary>
    private enum Rolls
    {
        Exact,
        Patch,
        Feature,
        Minor,
        Major,
    }

    private static Rolls Roll(string? rollForward) => rollForward?.ToLowerInvariant() switch
    {
        "disable" => Rolls.Exact,
        "feature" or "latestfeature" => Rolls.Feature,
        "minor" or "latestminor" => Rolls.Minor,
        "major" or "latestmajor" => Rolls.Major,
        _ => Rolls.Patch,
    };

    /// <summary>Whether an SDK's version is one the wanted takes: with nothing asked, any release; with a channel, any of
    /// it; with a version, that one or one its rollForward rolls to (a patch within its feature band, say), a prerelease
    /// only where the version asked is one.</summary>
    public static bool Satisfies(SdkWanted wanted, string version)
    {
        bool prerelease = VersionOrder.IsPrerelease(version);
        if (wanted.Version is not { } asked)
        {
            return wanted.Channel is { } channel ? version.StartsWith(channel + ".", StringComparison.Ordinal) : !prerelease;
        }
        if (prerelease && !VersionOrder.IsPrerelease(asked))
        {
            return false;
        }
        if (Parts(asked) is not { } a || Parts(version) is not { } v)
        {
            return false;
        }
        Rolls roll = Roll(wanted.RollForward);
        if (roll == Rolls.Exact)
        {
            return string.Equals(asked, version, StringComparison.OrdinalIgnoreCase);
        }
        bool sameMajor = v.Major == a.Major;
        bool sameMinor = sameMajor && v.Minor == a.Minor;
        bool sameBand = sameMinor && v.Patch / 100 == a.Patch / 100;
        bool within = roll switch
        {
            Rolls.Patch => sameBand,
            Rolls.Feature => sameMinor,
            Rolls.Minor => sameMajor,
            _ => true,
        };
        return within && VersionOrder.Instance.Compare(version, asked) >= 0;
    }

    private static (int Major, int Minor, int Patch)? Parts(string version)
    {
        string[] numbers = version.Split('-', 2)[0].Split('.');
        return numbers.Length >= 3 && int.TryParse(numbers[0], out int major) && int.TryParse(numbers[1], out int minor) && int.TryParse(numbers[2], out int patch)
            ? (major, minor, patch)
            : null;
    }

    private static async Task<JsonDocument> ReadAsync(Uri uri, CancellationToken cancel)
    {
        if (!DotnetInstall.Secure(uri))
        {
            throw new DotnetInstallFailed($"{uri} would be read over {uri.Scheme}, not HTTPS");
        }
        try
        {
            using HttpRequestMessage request = OutboundHttp.Request(HttpMethod.Get, uri);
            using HttpResponseMessage response = await Http.SendAsync(request, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new DotnetInstallFailed($"{uri} answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            await using Stream body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(body, cancellationToken: cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new DotnetInstallFailed($"{uri} gave no answer in time");
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new DotnetInstallFailed($"{uri} could not be read: {error.Message}", error);
        }
        catch (JsonException error)
        {
            throw new DotnetInstallFailed($"{uri} is not .NET's release metadata: {error.Message}", error);
        }
    }
}

/// <summary>A .NET SDK not installed, in words a person reads: the metadata or archive did not come, did not read, was not
/// what Microsoft publishes (its SHA-512), or could not be written.</summary>
public sealed class DotnetInstallFailed : Exception
{
    public DotnetInstallFailed(string reason)
        : base(reason)
    {
    }

    public DotnetInstallFailed(string reason, Exception inner)
        : base(reason, inner)
    {
    }
}
