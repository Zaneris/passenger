using System.Text;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Run;

/// <summary>Projects built side by side in one dotnet build: <see cref="Project"/>, Passenger's own in the folder's state,
/// restores them as one graph (NuGet's RestoreGraphProjectInput) and builds them at once (BuildInParallel), each with the
/// global properties a build of it alone has (the configuration and platform it is given its own), so each is left as that
/// build would leave it and each error names its own project.</summary>
public sealed record TogetherBuild(string Project, IReadOnlyList<string> Projects)
{
    /// <summary>The projects as a build names them, by their files' names.</summary>
    public string Shown => string.Join(", ", Projects.Select(Path.GetFileName));

    /// <summary>The build of these projects, each given its global properties (<paramref name="properties"/>, as a build of it
    /// alone is given them), its project written into the folder's state unless it is there as written; null
    /// where the state could not take it, or where a project's folder or one above it gives switches of its own
    /// (Directory.Build.rsp), which MSBuild reads only for a build begun under it. On any thread.</summary>
    public static TogetherBuild? Write(string stateFolder, string folder, IReadOnlyList<string> projects, Func<string, IReadOnlyDictionary<string, string>> properties)
    {
        foreach (string project in projects)
        {
            foreach (string at in Path.GetDirectoryName(project) is { } start ? FoldersUp.From(start) : [])
            {
                if (FoldersUp.Found(at, "Directory.Build.rsp") is { } switches && File.Exists(switches))
                {
                    return null;
                }
            }
        }
        string text = Text(projects, properties);
        string name = FileNames.Hash(text, 12) + ".proj";
        string path = Path.Combine(State.ProjectState.FolderOf(stateFolder, folder), "builds", name);
        try
        {
            if (!File.Exists(path) || File.ReadAllText(path) != text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFile.Write(path, Encoding.UTF8.GetBytes(text));
            }
        }
        catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return new TogetherBuild(path, projects);
    }

    /// <summary>The project, its default target building the others, each with its own global properties; the paths and
    /// values escaped for MSBuild, then for XML.</summary>
    private static string Text(IReadOnlyList<string> projects, Func<string, IReadOnlyDictionary<string, string>> properties) => $"""
        <Project DefaultTargets="Build">
          <PropertyGroup>
            <RestoreGraphProjectInput>{XmlEntities.Encode(string.Join(';', projects.Select(Escaped)))}</RestoreGraphProjectInput>
          </PropertyGroup>
          <ItemGroup>
        {string.Concat(projects.Select(project => Item(project, properties(project))))}  </ItemGroup>
          <Import Project="$(MSBuildToolsPath)/NuGet.targets" />
          <Target Name="Build">
            <MSBuild Projects="@(Together)" BuildInParallel="true" />
          </Target>
        </Project>

        """;

    /// <summary>One project built, with the global properties its build alone is given as its AdditionalProperties.</summary>
    private static string Item(string project, IReadOnlyDictionary<string, string> properties) => properties.Count == 0
        ? $"    <Together Include=\"{XmlEntities.Encode(Escaped(project), '"')}\" />\n"
        : $"    <Together Include=\"{XmlEntities.Encode(Escaped(project), '"')}\" AdditionalProperties=\"{XmlEntities.Encode(string.Join(';', properties
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Key}={Escaped(p.Value)}")), '"')}\" />\n";

    /// <summary>A path or value as MSBuild reads it literally: its list separator, wildcards and property, item and metadata marks escaped.</summary>
    private static string Escaped(string path)
    {
        StringBuilder escaped = new(path.Length);
        foreach (char c in path)
        {
            if (c is '%' or '$' or '@' or '\'' or ';' or '?' or '*' or '(' or ')')
            {
                escaped.Append('%').Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                escaped.Append(c);
            }
        }
        return escaped.ToString();
    }
}
