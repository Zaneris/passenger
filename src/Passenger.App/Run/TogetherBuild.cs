using System.Security.Cryptography;
using System.Text;
using Passenger.Core;

namespace Passenger.App.Run;

/// <summary>Projects built side by side in one dotnet build: <see cref="Project"/>, Passenger's own in the folder's state,
/// restores them as one graph (NuGet's RestoreGraphProjectInput) and builds them at once (BuildInParallel), each with the
/// global properties a build of it alone has, so each is left as that build would leave it and each error names its own
/// project.</summary>
public sealed record TogetherBuild(string Project, IReadOnlyList<string> Projects)
{
    /// <summary>The projects as a build names them, by their files' names.</summary>
    public string Shown => string.Join(", ", Projects.Select(Path.GetFileName));

    /// <summary>The build of these projects, its project written into the folder's state unless it is there as written;
    /// null where the state could not take it, or where a project's folder or one above it gives switches of its own
    /// (Directory.Build.rsp), which MSBuild reads only for a build begun under it. On any thread.</summary>
    public static TogetherBuild? Write(string stateFolder, string folder, IReadOnlyList<string> projects)
    {
        foreach (string project in projects)
        {
            for (string? at = Path.GetDirectoryName(project); at is not null; at = Path.GetDirectoryName(at))
            {
                if (File.Exists(Path.Combine(at, "Directory.Build.rsp")))
                {
                    return null;
                }
            }
        }
        string text = Text(projects);
        string name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12] + ".proj";
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

    /// <summary>The project, its default target building the others; the paths escaped for MSBuild, then for XML.</summary>
    private static string Text(IReadOnlyList<string> projects) => $"""
        <Project DefaultTargets="Build">
          <PropertyGroup>
            <RestoreGraphProjectInput>{System.Security.SecurityElement.Escape(string.Join(';', projects.Select(Escaped)))}</RestoreGraphProjectInput>
          </PropertyGroup>
          <ItemGroup>
            <Together Include="$(RestoreGraphProjectInput)" />
          </ItemGroup>
          <Import Project="$(MSBuildToolsPath)/NuGet.targets" />
          <Target Name="Build">
            <MSBuild Projects="@(Together)" BuildInParallel="true" />
          </Target>
        </Project>

        """;

    /// <summary>A path as MSBuild reads it literally: its list separator, wildcards and property, item and metadata marks escaped.</summary>
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
