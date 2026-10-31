using System.Text.Json;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>A solution filter (.slnf), as <c>dotnet sln</c> writes it and MSBuild builds it: the solution it narrows (its
/// path from the filter's folder) and the projects of that solution it keeps (theirs from the solution's), each canonical.</summary>
public sealed record SolutionFilter(string Path, string Solution, IReadOnlySet<string> Projects)
{
    /// <summary>Whether a file is a solution filter.</summary>
    public static bool Is(string path) => System.IO.Path.GetExtension(path).Equals(".slnf", StringComparison.OrdinalIgnoreCase);

    /// <summary>The filter a file holds; null for one that does not read or names no solution.</summary>
    public static SolutionFilter? Read(string path)
    {
        path = PathNames.Canonical(path);
        try
        {
            using JsonDocument read = SharedRead.Json(path);
            return Of(path, read);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The filter a text holds, its paths taken from where a file of it is (a canonical path); null for one that
    /// does not read or names no solution.</summary>
    public static SolutionFilter? Read(string path, string text)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(text, LenientJson.Document);
            return Of(path, read);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Where a path the filter writes names, from the folder it is written from (the filter's for the solution, the
    /// solution's for a project), canonical.</summary>
    public static string Named(string written, string from) => PathNames.Canonical(System.IO.Path.GetFullPath(Separated(written), from));

    private static SolutionFilter? Of(string path, JsonDocument read)
    {
        if (read.RootElement.ValueKind != JsonValueKind.Object || !read.RootElement.TryGetProperty("solution", out JsonElement solution)
            || solution.ValueKind != JsonValueKind.Object || !solution.TryGetProperty("path", out JsonElement named)
            || named.GetString() is not { Length: > 0 } relative)
        {
            return null;
        }
        string solutionPath = Named(relative, System.IO.Path.GetDirectoryName(path)!);
        string root = System.IO.Path.GetDirectoryName(solutionPath)!;
        HashSet<string> projects = new(PathNames.Comparer);
        if (solution.TryGetProperty("projects", out JsonElement listed) && listed.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement project in listed.EnumerateArray())
            {
                if (project.ValueKind == JsonValueKind.String && project.GetString() is { Length: > 0 } each)
                {
                    projects.Add(Named(each, root));
                }
            }
        }
        return new SolutionFilter(path, solutionPath, projects);
    }

    /// <summary>Whether another read of a filter keeps the same projects of the same solution.</summary>
    public bool Keeps(SolutionFilter? other) =>
        other is not null && PathNames.Same(Solution, other.Solution) && Projects.SetEquals(other.Projects);

    private static string Separated(string path) => path.Replace('\\', '/');
}
