using System.Text.Json;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Aspire;

/// <summary>Tells single-file AppHosts by their head, without treating their C# source as an MSBuild project, and AppHosts of
/// another language (Go, TypeScript, Python) by the aspire.config.json that names them; the folder's are found among the
/// files of its one walk (<see cref="AspireFiles"/>).</summary>
public static class AspireAppHosts
{
    /// <summary>The file that names an AppHost the aspire CLI runs, its language and its launch profiles.</summary>
    public const string ConfigFile = "aspire.config.json";

    /// <summary>Whether an AppHost runs through the aspire CLI: one in another language than C#, neither a project nor
    /// apphost.cs, which only aspire run builds and starts.</summary>
    public static bool RunsThroughCli(string path) =>
        !WorkspaceModel.IsProject(path) && !Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>The AppHost an aspire.config.json names (its appHost's path, from the file's folder) where that runs through
    /// the CLI and is there; null for one in C# (found as a project or apphost.cs is), none, or a file that does not read as
    /// JSON. Throws as its read does, so a file that could not be read keeps what was known of it.</summary>
    public static string? NamedBy(string configFile)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(File.ReadAllText(configFile), LenientJson.Document);
            if (JsonMembers.Text(JsonMembers.Object(read.RootElement, "appHost"), "path") is not { Length: > 0 } named)
            {
                return null;
            }
            string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configFile)!, named));
            return RunsThroughCli(path) && File.Exists(path) ? PathNames.Canonical(path) : null;
        }
        catch (Exception unread) when (unread is JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>What aspire run gives an AppHost of another language (<see cref="RunsThroughCli"/>) to run with, as it reads
    /// it: the https profile, else the first, of the nearest aspire.config.json at or above its folder, else of its
    /// apphost.run.json or Properties/launchSettings.json, its application URLs as ASPNETCORE_URLS; empty with none. Reads
    /// the disk.</summary>
    public static Dictionary<string, string> CliProfile(string appHost)
    {
        string folder = Path.GetDirectoryName(appHost)!;
        IReadOnlyList<LaunchProfile> profiles = FoldersUp.From(folder).Select(above => FoldersUp.Found(above, ConfigFile)).OfType<string>()
            .Select(LaunchProfile.Read).FirstOrDefault() ?? [];
        if (profiles.Count == 0)
        {
            string run = Path.Combine(folder, "apphost.run.json");
            profiles = LaunchProfile.Read(File.Exists(run) ? run : LaunchProfile.SettingsIn(folder));
        }
        return (profiles.FirstOrDefault(profile => profile.Name == "https") ?? profiles.FirstOrDefault()) is { } chosen
            ? (chosen.Command is null ? chosen with { Command = LaunchProfile.ProjectCommand } : chosen).Environment()
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    internal static string? SingleFileWhyNot(string path, IEnumerable<string> projectPaths)
    {
        if (!Path.GetFileName(path).Equals("apphost.cs", StringComparison.OrdinalIgnoreCase))
        {
            return $"Aspire CLI {AspireClient.SupportedVersion} requires apphost.cs for a single-file AppHost";
        }
        string? folder = Path.GetDirectoryName(path);
        return projectPaths.Any(project => Path.GetExtension(project).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(project) is { } projectFolder && folder is not null && PathNames.Same(folder, projectFolder))
            ? $"Aspire CLI {AspireClient.SupportedVersion} requires apphost.cs to have no sibling .csproj project" : null;
    }

    /// <summary>Whether a C# file is a single-file AppHost, as its head says (<see cref="Passenger.Host.AspireHosts.IsHead"/>);
    /// throws as its read does, so a file that could not be read keeps what was known of it.</summary>
    public static bool IsSingleFile(string path) => Passenger.Host.ProgramFiles.IsProgram(path)
        && Passenger.Host.AspireHosts.IsHead(Passenger.Host.ProgramFiles.Head(SharedRead.Lines(path)));
}
