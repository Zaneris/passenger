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

    public static bool IsSingleFile(string path)
    {
        if (!Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        bool comment = false;
        foreach (string line in SharedRead.Lines(path))
        {
            string text = line.Trim();
            if (comment)
            {
                int end = text.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                {
                    continue;
                }
                comment = false;
                text = text[(end + 2)..].TrimStart();
            }
            while (text.StartsWith("/*", StringComparison.Ordinal))
            {
                int end = text.IndexOf("*/", 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    comment = true;
                    break;
                }
                text = text[(end + 2)..].TrimStart();
            }
            if (comment)
            {
                continue;
            }
            if (text.StartsWith("#:sdk", StringComparison.Ordinal) && text.Length > 5 && char.IsWhiteSpace(text[5])
                && text[6..].TrimStart().Split('@')[0].Equals("Aspire.AppHost.Sdk", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (text.Length > 0 && !text.StartsWith('#') && !text.StartsWith("//", StringComparison.Ordinal))
            {
                return false;
            }
        }
        return false;
    }
}
