namespace Passenger.App.Mobile;

/// <summary>How a .NET workload install is run and what its words say: its downloads (gigabytes for Android's or MAUI's)
/// kept out of a temporary folder held in memory, as Linux's /tmp often is (a tmpfs sized by the machine's memory, often
/// given each user a quota), the .NET's own words for a workload it knows no workload set or manifest of, and for a
/// download with no room left.</summary>
public static class WorkloadInstalls
{
    /// <summary>The folder under Passenger's cache that a workload install downloads into in place of the temporary folder.</summary>
    public const string Downloads = "workload-downloads";

    /// <summary>The folder a workload install is to download into in place of <paramref name="temporary"/>, the one dotnet
    /// takes: a new one under <paramref name="cache"/> where the temporary folder's file system
    /// (<paramref name="formatOf"/>, its name as Linux's statfs gives it) is held in memory; null where it is on disk.</summary>
    public static string? DownloadsFolder(string temporary, Func<string, string?> formatOf, string cache) =>
        formatOf(temporary) is "tmpfs" or "ramfs" ? Path.Combine(cache, Downloads, Guid.NewGuid().ToString("N")) : null;

    /// <summary>Whether the .NET said it knows none of the workloads asked for ("Workload ID android is not recognized."), as
    /// one with no workload set installed says before its manifests are updated.</summary>
    public static bool Unknown(IReadOnlyList<string> said) => said.Any(l => l.Contains("Workload ID ", StringComparison.Ordinal) && l.Contains(" is not recognized", StringComparison.Ordinal));

    /// <summary>Whether a download failed for want of room where it was written: the system's words for a disk full or a
    /// user's quota spent.</summary>
    public static bool NoRoom(IReadOnlyList<string> said) =>
        said.Any(l => l.Contains("No space left on device", StringComparison.Ordinal) || l.Contains("Disk quota exceeded", StringComparison.Ordinal));

    /// <summary>The file whose presence in the .NET at <paramref name="root"/> makes the SDK <paramref name="sdk"/> install
    /// workloads into each user's own folder (~/.dotnet), so an install needs no rights over that .NET: the marker the SDK
    /// reads under metadata/workloads/ for its feature band, its pre-release left out (11.0.100 for 11.0.100-rc.3.26522.148,
    /// 10.0.100 for 10.0.113), which Fedora's packaged .NET and others carry. Null for a version that names no band.</summary>
    public static string? UserLocalMarker(string root, string sdk)
    {
        string[] parts = sdk.Split('-', '+')[0].Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor)
            || !int.TryParse(parts[2], out int patch))
        {
            return null;
        }
        return Path.Combine(root, "metadata", "workloads", $"{major}.{minor}.{patch / 100 * 100}", "userlocal");
    }
}

/// <summary>What a test's workload install takes in place of the .NET it installs into: that .NET's folder, which is
/// checked writable as the real one is, and what runs in place of dotnet given dotnet's arguments (workload install,
/// workload update).</summary>
public sealed record WorkloadStandIn(string Root, Func<IReadOnlyList<string>, System.Diagnostics.ProcessStartInfo> Start);
