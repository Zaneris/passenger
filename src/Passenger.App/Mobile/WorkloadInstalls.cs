using Passenger.Core;
using Passenger.Terminal;

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

    /// <summary>Where a workload install puts the workloads, in the words of an offer or a question ("into the .NET in
    /// /usr/share/dotnet"): the user's own folder where the .NET installs workloads for each user, else the .NET's own.</summary>
    public static string Into(WorkloadFolders folders) => folders.UserLocal is { } own
        ? $"into {PathNames.Shown(own, null)}, for you alone, as the .NET in {PathNames.Shown(folders.Root, null)} installs workloads for each user"
        : $"into the .NET in {PathNames.Shown(folders.Root, null)}";

    /// <summary>dotnet workload install of <paramref name="ids"/> as an administrator runs it for the .NET in
    /// <paramref name="root"/>: through sudo, naming that .NET's dotnet by its path, as sudo's own PATH may find another;
    /// on Windows as written, in a terminal run as administrator, whose PowerShell and cmd quote a path apart.</summary>
    public static string AsAdministrator(string root, IReadOnlyList<string> ids) => OperatingSystem.IsWindows()
        ? $"dotnet workload install {string.Join(' ', ids)}"
        : $"sudo {ShellWords.Quoted(Path.Combine(root, Dotnet.HostName), ShellKind.Posix)} workload install {string.Join(' ', ids)}";
}

/// <summary>What a test's workload install takes in place of the .NET it installs into: that .NET's folder, which is
/// checked writable as the real one is, and what runs in place of dotnet given dotnet's arguments (workload install,
/// workload update).</summary>
public sealed record WorkloadStandIn(string Root, Func<IReadOnlyList<string>, System.Diagnostics.ProcessStartInfo> Start);
