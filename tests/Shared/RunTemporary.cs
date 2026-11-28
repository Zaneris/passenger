using System.Globalization;

namespace Passenger.TestTables;

/// <summary>A test process's temporary folder of its own, named for the process (prun-), which its run gives every program
/// and test of its as their temporary folder, so what they make there goes with the run, and one a run killed left goes as
/// the next run of either project begins. Short, as a socket's path holds 103 bytes: in the person's temporary folder, else
/// the system's (macOS's per-user one is too long), the first where the longest socket name made there fits. Linked into
/// both test projects.</summary>
public static class RunTemporary
{
    public const string Prefix = "prun-";

    /// <summary>The temporary folder the process started with: the person's own.</summary>
    public static string Persons { get; } = Path.GetTempPath();

    /// <summary>The longest socket name a run makes in its folder: a stand-in diagnostics router's, for a 7-digit id and a
    /// 10-digit start.</summary>
    private const int Longest = 52;

    /// <summary>Where a run's folders are made: the person's temporary folder, then, on macOS and Linux, the system's, as
    /// its real path names it.</summary>
    public static IEnumerable<string> Bases() =>
        OperatingSystem.IsWindows() ? [Persons] : [Persons, OperatingSystem.IsMacOS() ? "/private/tmp" : "/tmp"];

    /// <summary>This process's own folder, in the first place it fits; null where none is short enough.</summary>
    public static string? Own() => Bases().Select(under => Path.Combine(under, Prefix + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)))
        .FirstOrDefault(folder => OperatingSystem.IsWindows() || folder.Length + 1 + Longest <= 103);

    /// <summary>The process a run's folder is named for, or null for a folder that is no run's.</summary>
    public static int? RunOf(string name) =>
        name.StartsWith(Prefix, StringComparison.Ordinal) && int.TryParse(name.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : null;
}
