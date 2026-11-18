using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Aspire;

/// <summary>Tells single-file AppHosts by their head, without treating their C# source as an MSBuild project; the folder's are
/// found among the files of its one walk (<see cref="AspireFiles"/>).</summary>
public static class AspireAppHosts
{
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
