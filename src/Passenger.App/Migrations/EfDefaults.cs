using System.Text.Json;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Migrations;

/// <summary>The defaults of dotnet ef's options that EF Core 11's tool reads from <c>.config/dotnet-ef.json</c>, the nearest
/// from the folder it runs in up: the project and startup project (a project file or the folder of one, from the folder
/// holding <c>.config</c>), the context and the framework. An option given on the command line wins over each, as a choice
/// made in the Migrations window does.</summary>
public sealed record EfDefaults(string? Project, string? StartupProject, string? Context, string? Framework)
{
    /// <summary>Whether the file gives an option beyond those four (a configuration, a runtime), which the window leaves to
    /// dotnet ef.</summary>
    public bool Beyond { get; init; }

    /// <summary>The defaults the tool would read running in a folder; null for a tool before EF Core 11, which reads none, or
    /// a folder with no such file, or one that does not read.</summary>
    public static EfDefaults? Find(string folder, EfTool? tool)
    {
        if (tool is null || !ReadsDefaults(tool))
        {
            return null;
        }
        for (string? at = folder; at is not null; at = Path.GetDirectoryName(at))
        {
            string file = Path.Combine(at, ".config", "dotnet-ef.json");
            if (File.Exists(file))
            {
                return Read(file, at);
            }
        }
        return null;
    }

    /// <summary>Whether a tool is EF Core 11's or later, which reads the defaults and removes a migration offline.</summary>
    public static bool ReadsDefaults(EfTool tool) => tool.Major >= 11;

    /// <summary>Whether a path the file gives names a project: its file, or its folder.</summary>
    public static bool Names(string? named, ProjectModel project) =>
        named is not null && (PathNames.Same(named, project.Path) || PathNames.Same(named, Path.GetDirectoryName(project.Path)!));

    private static EfDefaults? Read(string file, string root)
    {
        try
        {
            using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument read = JsonDocument.Parse(stream);
            if (read.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            string? Value(string name) =>
                read.RootElement.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text
                    && !string.IsNullOrWhiteSpace(text) ? text : null;
            string? Placed(string? path) => path is null ? null : PathNames.Canonical(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, root)));
            return new EfDefaults(Placed(Value("project")), Placed(Value("startupProject")), Value("context"), Value("framework"))
            {
                Beyond = read.RootElement.EnumerateObject().Any(option => option.Name is not ("project" or "startupProject" or "context" or "framework")),
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // A file that does not read gives no defaults here; dotnet ef says what is wrong with it when it runs.
            return null;
        }
    }
}
