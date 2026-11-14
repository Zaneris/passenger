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
}
