using System.Text;
using System.Text.Json;

namespace Passenger.Core;

/// <summary>What a global.json asks of the .NET SDK, as dotnet reads it to choose the SDK a folder's projects build with:
/// the file, its sdk section's version and rollForward, the line (from zero) its version is on, where a person changes
/// what it asks for, and its allowPrerelease, null where it does not say.</summary>
public sealed partial record GlobalJsonSdk(string Path, string? Version, string? RollForward, int Line, bool? AllowPrerelease = null)
{
    /// <summary>The SDK wanted as this global.json has dotnet take it: a version as its rollForward and allowPrerelease let it
    /// go, a channel by its allowPrerelease, and the newest, which no one has asked dotnet about yet, the version it asks
    /// for, where it asks for one, so one install gives the folder an SDK it will use.</summary>
    public SdkWanted Rolling(SdkWanted wanted) => wanted switch
    {
        { Channel: not null } => wanted with { AllowPrerelease = AllowPrerelease },
        { Version: null } when Version is null => wanted,
        _ => wanted with { Version = wanted.Version ?? Version, RollForward = RollForward, AllowPrerelease = AllowPrerelease },
    };

    /// <summary>Whether dotnet would take an installed SDK of this version for what this global.json asks, as the host's
    /// resolver matches one: the version asked itself, else one at or above it as far as rollForward lets it go (patch, the
    /// default, within its feature band; feature within its minor; minor within its major; major anywhere; disable nowhere),
    /// a prerelease only while allowPrerelease, or a prerelease asked for, allows one; asking no version, any SDK.</summary>
    public bool Takes(string sdk)
    {
        SdkVersion? asked = Version is null ? null : SdkVersion.Of(Version);
        if (SdkVersion.Of(sdk) is not { } offered || (Version is not null && asked is null))
        {
            return false;
        }
        int reach = Array.FindIndex(RollForwards, p => p.Equals(RollForward ?? (asked is null ? "latestMajor" : "patch"), StringComparison.OrdinalIgnoreCase));
        if (reach < 0 || RollForwards[reach] == "disable")
        {
            return reach >= 0 && asked is not null && VersionOrder.Instance.Compare(sdk, Version) == 0;
        }
        if (offered.Prerelease && AllowPrerelease == false && asked?.Prerelease != true)
        {
            return false;
        }
        return asked is null || (reach switch
        {
            < 2 => offered.Major == asked.Major && offered.Minor == asked.Minor && offered.Band == asked.Band,
            < 4 => offered.Major == asked.Major && offered.Minor == asked.Minor,
            < 6 => offered.Major == asked.Major,
            _ => true,
        } && VersionOrder.Instance.Compare(sdk, Version) >= 0);
    }

    /// <summary>global.json's rollForward values, each pair reaching further than the one before it.</summary>
    private static readonly string[] RollForwards = ["patch", "latestPatch", "feature", "latestFeature", "minor", "latestMinor", "major", "latestMajor", "disable"];

    /// <summary>An SDK's version as the host reads one (major.minor.patch, a prerelease label and build metadata after),
    /// its feature band the hundreds of its patch.</summary>
    private sealed partial record SdkVersion(int Major, int Minor, int Band, bool Prerelease)
    {
        public static SdkVersion? Of(string version) =>
            Shape().Match(version.Trim()) is { Success: true } m
            && int.TryParse(m.Groups[1].ValueSpan, System.Globalization.CultureInfo.InvariantCulture, out int major)
            && int.TryParse(m.Groups[2].ValueSpan, System.Globalization.CultureInfo.InvariantCulture, out int minor)
            && int.TryParse(m.Groups[3].ValueSpan, System.Globalization.CultureInfo.InvariantCulture, out int patch)
                ? new SdkVersion(major, minor, patch / 100, m.Groups[4].Success)
                : null;

        [System.Text.RegularExpressions.GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")]
        private static partial System.Text.RegularExpressions.Regex Shape();
    }

    /// <summary>What the global.json at a path asks, read from the disk; null for no path, or a file that does not read.</summary>
    public static GlobalJsonSdk? Read(string? path)
    {
        if (path is null)
        {
            return null;
        }
        try
        {
            return Of(path, File.ReadAllText(path));
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What a global.json's text asks (comments and trailing commas allowed, as dotnet allows them); null for one
    /// that is no JSON object.</summary>
    public static GlobalJsonSdk? Of(string path, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            using JsonDocument read = JsonDocument.Parse(bytes, LenientJson.Document);
            if (read.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            JsonElement? sdk = JsonMembers.Object(read.RootElement, "sdk");
            return new GlobalJsonSdk(path, JsonMembers.Text(sdk, "version"), JsonMembers.Text(sdk, "rollForward"), VersionLine(bytes), JsonMembers.Bool(sdk, "allowPrerelease"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The line the sdk section's version is on, else the section's own, else the first.</summary>
    private static int VersionLine(byte[] bytes)
    {
        Utf8JsonReader reader = new(bytes, LenientJson.Reader);
        long found = 0;
        bool inSdk = false;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }
            if (reader.CurrentDepth == 1)
            {
                inSdk = reader.ValueTextEquals("sdk");
                found = inSdk ? reader.TokenStartIndex : found;
            }
            else if (inSdk && reader.CurrentDepth == 2 && reader.ValueTextEquals("version"))
            {
                found = reader.TokenStartIndex;
                break;
            }
        }
        return bytes.AsSpan(0, (int)found).Count((byte)'\n');
    }
}
