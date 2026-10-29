using System.Text.Json;

namespace Passenger.Packages;

/// <summary>The package versions a project's restore resolved, as its project.assets.json says them: for each target
/// framework, by the name the project gives it (its alias in TargetFrameworks), each package's version.</summary>
public sealed class RestoredVersions
{
    private readonly Dictionary<string, Dictionary<string, string>> byFramework;

    private RestoredVersions(Dictionary<string, Dictionary<string, string>> byFramework) => this.byFramework = byFramework;

    public static RestoredVersions None { get; } = new(new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The version of a package a framework's restore resolved (a project of one framework names none); null where
    /// it resolved none.</summary>
    public string? Of(string? framework, string id)
    {
        Dictionary<string, string>? packages = framework is not null ? byFramework.GetValueOrDefault(framework)
            : byFramework.Count == 1 ? byFramework.Values.First() : null;
        return packages?.GetValueOrDefault(id);
    }

    /// <summary>An assets file's targets read (a runtime's own targets passed over); none where it does not read as one.</summary>
    public static RestoredVersions Read(Stream assets)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(assets);
            JsonElement root = read.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("targets", out JsonElement targets) || targets.ValueKind != JsonValueKind.Object)
            {
                return None;
            }
            // A target is named as the framework's key under project.frameworks, whose targetAlias is the project's own name for it.
            Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("project", out JsonElement project) && project.ValueKind == JsonValueKind.Object
                && project.TryGetProperty("frameworks", out JsonElement frameworks) && frameworks.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty framework in frameworks.EnumerateObject())
                {
                    aliases[framework.Name] = framework.Value.ValueKind == JsonValueKind.Object && framework.Value.TryGetProperty("targetAlias", out JsonElement alias)
                        && alias.GetString() is { Length: > 0 } named ? named : framework.Name;
                }
            }
            Dictionary<string, Dictionary<string, string>> found = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty target in targets.EnumerateObject().Where(t => !t.Name.Contains('/', StringComparison.Ordinal) && t.Value.ValueKind == JsonValueKind.Object))
            {
                Dictionary<string, string> packages = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty library in target.Value.EnumerateObject())
                {
                    if (library.Name.Split('/', 2) is [{ Length: > 0 } id, { Length: > 0 } version] && library.Value.ValueKind == JsonValueKind.Object
                        && library.Value.TryGetProperty("type", out JsonElement type) && type.ValueEquals("package"))
                    {
                        packages[id] = version;
                    }
                }
                found[aliases.GetValueOrDefault(target.Name) ?? target.Name] = packages;
            }
            return new RestoredVersions(found);
        }
        catch (JsonException)
        {
            // A restore being written, or a file that is no assets file: nothing said.
            return None;
        }
    }
}
