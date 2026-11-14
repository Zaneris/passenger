using System.Reflection;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.MauiPreview;

/// <summary>Where the project's own dependencies are: beside its assembly in its build's folder, else where its
/// .deps.json says each package's assembly lies under one of the package folders its restore resolves into (a library's
/// build copies none of its packages). MAUI's and Avalonia's assemblies are the previewer's own, found before these.</summary>
internal sealed class ProjectDependencies(string folder, IReadOnlyDictionary<string, string> packaged)
{
    /// <summary>The dependencies of the assembly at a path, its .deps.json read against the package folders given.</summary>
    public static ProjectDependencies Of(string assembly, IReadOnlyList<string> packageFolders)
    {
        string folder = Path.GetDirectoryName(assembly)!;
        Dictionary<string, string> packaged = new(StringComparer.OrdinalIgnoreCase);
        string deps = Path.ChangeExtension(assembly, ".deps.json");
        if (File.Exists(deps) && packageFolders.Count > 0)
        {
            try
            {
                using JsonDocument read = JsonDocument.Parse(File.ReadAllBytes(deps), LenientJson.Document);
                Read(read.RootElement, packageFolders, packaged);
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"The project's dependencies ({deps}) did not read: {error.Message}");
            }
        }
        return new ProjectDependencies(folder, packaged);
    }

    /// <summary>The file of an assembly the project depends on; null where neither its build nor its packages hold it.</summary>
    public string? Find(AssemblyName name)
    {
        if (name.Name is not { } simple)
        {
            return null;
        }
        string beside = Path.Combine(folder, simple + ".dll");
        return File.Exists(beside) ? beside : packaged.GetValueOrDefault(simple);
    }

    private static void Read(JsonElement root, IReadOnlyList<string> packageFolders, Dictionary<string, string> packaged)
    {
        string? target = JsonMembers.Text(JsonMembers.Object(root, "runtimeTarget"), "name");
        JsonElement? targets = JsonMembers.Object(root, "targets");
        JsonElement? libraries = JsonMembers.Object(root, "libraries");
        foreach (JsonProperty library in target is null ? [] : JsonMembers.Properties(targets, target))
        {
            JsonElement? described = JsonMembers.Object(libraries, library.Name);
            if (JsonMembers.Text(described, "type") != "package" || JsonMembers.Text(described, "path") is not { } path)
            {
                continue;
            }
            foreach (JsonProperty runtime in JsonMembers.Properties(library.Value, "runtime"))
            {
                string? file = packageFolders.Select(f => Path.Combine(f, path, runtime.Name)).FirstOrDefault(File.Exists);
                if (file is not null)
                {
                    packaged.TryAdd(Path.GetFileNameWithoutExtension(file), file);
                }
            }
        }
    }
}
