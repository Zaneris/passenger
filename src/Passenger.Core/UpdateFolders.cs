using System.Text.Json;

namespace Passenger.Core;

/// <summary>The folder a Linux update keeps beside the install it updates (<c>~/.local/opt/.passenger.update-1.2.0</c>),
/// named by the release unpacked into it: it holds that release until the two trade places, then the release it replaced
/// until the new one starts. Whichever copy runs, a folder named for a version newer than its own is an update not yet in
/// place, and any other is what an update left behind.</summary>
public static class UpdateFolders
{
    /// <summary>What every such folder's name begins with, the version after it.</summary>
    public const string Prefix = ".passenger.update-";

    /// <summary>The folder beside an install for a release of this version.</summary>
    public static string Beside(string install, string version) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(install))!, Prefix + version);

    /// <summary>The version a folder is named for, or null for a folder that is not one of these.</summary>
    public static string? VersionOf(string folder) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { } name && name.StartsWith(Prefix, StringComparison.Ordinal) && name.Length > Prefix.Length
            ? name[Prefix.Length..]
            : null;

    /// <summary>Whether a copy of <paramref name="running"/> removes a folder named for <paramref name="version"/>: every one
    /// not newer than it, and, while a release is made ready (<paramref name="keep"/>), every one but that release's.</summary>
    public static bool Stale(string version, string running, string? keep) =>
        keep is null ? VersionOrder.Instance.Compare(version, running) <= 0 : version != keep;

    /// <summary>The version an app's deps.json gives its own project (<c>"Passenger/1.2.0"</c> among its libraries), or
    /// null where it names none, or does not read as JSON.</summary>
    public static string? AppVersion(string depsJson)
    {
        try
        {
            using JsonDocument deps = JsonDocument.Parse(depsJson);
            return JsonMembers.Properties(deps.RootElement, "libraries")
                .Where(library => library.Name.StartsWith("Passenger/", StringComparison.Ordinal) && JsonMembers.Text(library.Value, "type") == "project")
                .Select(library => library.Name["Passenger/".Length..])
                .FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
