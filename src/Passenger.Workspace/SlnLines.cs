using System.Text.RegularExpressions;

namespace Passenger.Workspace;

/// <summary>A classic .sln's grammar, which <see cref="SolutionModel"/> reads and <see cref="SolutionEdits"/> changes by: its
/// Project lines and the blocks they open, its global sections and a project's own, the GUID pairs NestedProjects and
/// ProjectDependencies hold, a configuration line's project and a Solution Items line's file. A GUID keeps its braces as
/// written, and a line may keep the '\r' of its line break.</summary>
[IdentifierHome]
internal static partial class SlnLines
{
    /// <summary>The solution folder's project type, which marks an entry as a folder.</summary>
    public const string FolderType = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";

    public const string EndProject = "EndProject";
    public const string EndProjectSection = "EndProjectSection";
    public const string Global = "Global";
    public const string EndGlobalSection = "EndGlobalSection";
    public const string EndGlobal = "EndGlobal";

    public const string NestedProjects = "NestedProjects";
    public const string SolutionConfigurationPlatforms = "SolutionConfigurationPlatforms";
    public const string ProjectConfigurationPlatforms = "ProjectConfigurationPlatforms";
    public const string ExtensibilityGlobals = "ExtensibilityGlobals";
    public const string SolutionItems = "SolutionItems";
    public const string ProjectDependencies = "ProjectDependencies";

    public const string PreSolution = "preSolution";
    public const string PostSolution = "postSolution";
    public const string PreProject = "preProject";

    /// <summary>Each Project block in the file's order. A block ends at its EndProject; one with none before the next
    /// Project line is its Project line alone.</summary>
    public static List<SlnProject> Projects(IReadOnlyList<string> lines)
    {
        List<SlnProject> found = [];
        for (int i = 0; i < lines.Count; i++)
        {
            if (ProjectPattern().Match(lines[i]) is not { Success: true } m)
            {
                continue;
            }
            int end = i;
            for (int j = i + 1; j < lines.Count && !ProjectPattern().IsMatch(lines[j]); j++)
            {
                if (Is(lines[j], EndProject))
                {
                    end = j;
                    break;
                }
            }
            found.Add(new SlnProject(m.Groups["type"].Value, m.Groups["name"].Value, m.Groups["path"].Value, m.Groups["id"].Value, i, end));
            i = end;
        }
        return found;
    }

    /// <summary>The first global section of a name: its first and last lines, or null where the file has none whole.</summary>
    public static (int Start, int End)? GlobalSection(IReadOnlyList<string> lines, [IdentifierOf(typeof(SlnLines))] string name)
    {
        string opening = $"GlobalSection({name})";
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].AsSpan().TrimStart().StartsWith(opening, StringComparison.Ordinal))
            {
                int end = Find(lines, i + 1, lines.Count, EndGlobalSection);
                return end < 0 ? null : (i, end);
            }
        }
        return null;
    }

    /// <summary>A project's first section of a name inside its block: its first and last lines, or null where it has none
    /// whole.</summary>
    public static (int Start, int End)? ProjectSection(IReadOnlyList<string> lines, SlnProject project, [IdentifierOf(typeof(SlnLines))] string name)
    {
        string opening = $"ProjectSection({name})";
        for (int i = project.Start + 1; i < project.End; i++)
        {
            if (lines[i].AsSpan().TrimStart().StartsWith(opening, StringComparison.Ordinal))
            {
                int end = Find(lines, i + 1, project.End, EndProjectSection);
                return end < 0 ? null : (i, end);
            }
        }
        return null;
    }

    /// <summary>Each entry's parent folder by their ids, from NestedProjects.</summary>
    public static Dictionary<string, string> Parents(IReadOnlyList<string> lines)
    {
        Dictionary<string, string> parents = new(StringComparer.OrdinalIgnoreCase);
        if (GlobalSection(lines, NestedProjects) is { } nested)
        {
            for (int i = nested.Start + 1; i < nested.End; i++)
            {
                if (Pair(lines[i]) is { } pair)
                {
                    parents[pair.Left] = pair.Right;
                }
            }
        }
        return parents;
    }

    /// <summary>The two GUIDs of a line pairing them (<c>{child} = {parent}</c>, <c>{dependency} = {dependency}</c>).</summary>
    public static (string Left, string Right)? Pair(string line) =>
        PairPattern().Match(line) is { Success: true } m ? (m.Groups["left"].Value, m.Groups["right"].Value) : null;

    /// <summary>The file a Solution Items line names (<c>docs\README.md = docs\README.md</c>), as written, relative to the
    /// solution: what follows its first '='.</summary>
    public static string? ItemPath(string line)
    {
        int equals = line.IndexOf('=', StringComparison.Ordinal);
        return equals >= 0 && line.AsSpan(equals + 1).Trim() is { Length: > 0 } path ? path.ToString() : null;
    }

    /// <summary>The project a configuration line is about (<c>{id}.Debug|Any CPU.ActiveCfg = Debug|Any CPU</c>).</summary>
    public static string? ConfiguredId(string line) =>
        line.AsSpan().TrimStart() is { Length: > 38 } entry && entry[0] == '{' && entry[38] == '.' ? entry[..38].ToString() : null;

    /// <summary>Whether a line is a keyword alone (<see cref="EndProject"/>), whatever white space stands around it.</summary>
    public static bool Is(string line, [IdentifierOf(typeof(SlnLines))] string keyword) => line.AsSpan().Trim().Equals(keyword, StringComparison.Ordinal);

    public static string ProjectLine(string type, string name, string path, string id) => $"Project(\"{type}\") = \"{name}\", \"{path}\", \"{id}\"";

    /// <summary>A Project line with a new name and path, the rest of it as it was.</summary>
    public static string Respelled(string line, string name, string path)
    {
        Match m = ProjectPattern().Match(line);
        Group oldName = m.Groups["name"];
        Group oldPath = m.Groups["path"];
        return $"{line[..oldName.Index]}{name}{line[(oldName.Index + oldName.Length)..oldPath.Index]}{path}{line[(oldPath.Index + oldPath.Length)..]}";
    }

    public static string GlobalSectionLine([IdentifierOf(typeof(SlnLines))] string name, [IdentifierOf(typeof(SlnLines))] string when) =>
        $"\tGlobalSection({name}) = {when}";

    public static string ProjectSectionLine([IdentifierOf(typeof(SlnLines))] string name, [IdentifierOf(typeof(SlnLines))] string when) =>
        $"\tProjectSection({name}) = {when}";

    /// <summary>The project type Visual Studio and dotnet sln give a project file of its kind.</summary>
    public static string ProjectType(string project) => System.IO.Path.GetExtension(project).ToLowerInvariant() switch
    {
        ".fsproj" => "{F2A71F9B-5D33-465A-A702-920D77279786}",
        ".vbproj" => "{F184B08F-C81C-45F6-A57F-5ABD9991F28F}",
        SharedProjects.Extension => "{D954291E-2A0B-460D-934E-DC6B0785DB48}",
        _ => "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}",
    };

    /// <summary>The first line from <paramref name="from"/> up to <paramref name="to"/> that is a keyword alone; -1 for none.</summary>
    private static int Find(IReadOnlyList<string> lines, int from, int to, string keyword)
    {
        for (int i = from; i < to; i++)
        {
            if (Is(lines[i], keyword))
            {
                return i;
            }
        }
        return -1;
    }

    [GeneratedRegex(@"^Project\(""(?<type>\{[^}]+\})""\)\s*=\s*""(?<name>[^""]*)"",\s*""(?<path>[^""]*)"",\s*""(?<id>\{[^}]+\})""")]
    private static partial Regex ProjectPattern();

    [GeneratedRegex(@"^\s*(?<left>\{[^}]+\})\s*=\s*(?<right>\{[^}]+\})")]
    private static partial Regex PairPattern();
}

/// <summary>One Project … EndProject block of a .sln: its type, name, path relative to the solution and id as its Project
/// line writes them, and the lines it starts and ends on.</summary>
internal sealed record SlnProject(string Type, string Name, string Path, string Id, int Start, int End)
{
    public bool IsFolder => Type.Equals(SlnLines.FolderType, StringComparison.OrdinalIgnoreCase);
}
