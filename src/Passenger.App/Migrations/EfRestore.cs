using System.Text.Json;
using Passenger.Packages;

namespace Passenger.App.Migrations;

/// <summary>A provider of EF Core a project restored (Npgsql's, SQLite's): its version, the range of EF Core its package
/// depends on, as its own metadata gives it, and whether the project's EF Core is in it.</summary>
public sealed record EfProvider(string Id, string Version, string Range, bool Holds);

/// <summary>What a project's restore (its <c>project.assets.json</c>) says of EF Core, for a framework: the EF Core runtime it
/// resolved, its Design package's version, and its providers; the one place "the right version" is read from, since a
/// provider's range, not EF Core's newest, decides what fits.</summary>
public sealed record EfRestore(string? Runtime, string? Design, IReadOnlyList<EfProvider> Providers)
{
    /// <summary>The framework of the restore's target read (net10.0), whose group of a package's dependencies applies.</summary>
    public string? Framework { get; init; }

    /// <summary>EF Core's own package, whose version is the runtime the tools and the Design package match.</summary>
    public const string Core = "Microsoft.EntityFrameworkCore";

    /// <summary>The package <c>dotnet ef</c> loads from the startup project.</summary>
    public const string DesignPackage = "Microsoft.EntityFrameworkCore.Design";

    /// <summary>EF Core's own packages, which move together and are never a provider.</summary>
    private static readonly HashSet<string> CoreSet = new(StringComparer.OrdinalIgnoreCase)
    {
        Core, "Microsoft.EntityFrameworkCore.Relational", "Microsoft.EntityFrameworkCore.Abstractions", "Microsoft.EntityFrameworkCore.Analyzers",
        DesignPackage, "Microsoft.EntityFrameworkCore.Tools", "Microsoft.EntityFrameworkCore.Proxies",
    };

    /// <summary>The first provider whose range leaves the project's EF Core out, or null.</summary>
    public EfProvider? Behind => Providers.FirstOrDefault(p => !p.Holds);

    /// <summary>The project's restore for a framework (its first with none given); null when it is not restored or its file
    /// does not read.</summary>
    public static EfRestore? Of(string project, string? framework = null)
    {
        if (Passenger.Xaml.Projects.ProjectInputs.FindAssets(project) is not { } assets)
        {
            return null;
        }
        try
        {
            using FileStream stream = new(assets, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument read = JsonDocument.Parse(stream);
            if (!read.RootElement.TryGetProperty("targets", out JsonElement targets) || targets.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            // A target per framework, and per runtime too ("net10.0/osx-arm64"): the framework's own.
            JsonProperty? target = targets.EnumerateObject().Cast<JsonProperty?>()
                .FirstOrDefault(t => framework is null || t!.Value.Name.Equals(framework, StringComparison.OrdinalIgnoreCase));
            if (target is not { Value.ValueKind: JsonValueKind.Object } chosen)
            {
                return null;
            }
            Dictionary<string, (string Version, JsonElement Library)> libraries = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty library in chosen.Value.EnumerateObject())
            {
                string[] named = library.Name.Split('/', 2);
                if (named.Length == 2 && library.Value.TryGetProperty("type", out JsonElement type) && type.GetString() == "package")
                {
                    libraries[named[0]] = (named[1], library.Value);
                }
            }
            string? runtime = libraries.TryGetValue(Core, out (string Version, JsonElement Library) core) ? core.Version : null;
            string? design = libraries.TryGetValue(DesignPackage, out (string Version, JsonElement Library) designed) ? designed.Version : null;
            List<EfProvider> providers = [];
            foreach ((string id, (string version, JsonElement library)) in libraries)
            {
                if (CoreSet.Contains(id) || !library.TryGetProperty("dependencies", out JsonElement dependencies) || dependencies.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                // A provider depends on EF Core or its relational layer; a meta-package (Sqlite over Sqlite.Core) on a provider.
                JsonProperty? onEf = dependencies.EnumerateObject().Cast<JsonProperty?>()
                    .FirstOrDefault(d => d!.Value.Name.Equals("Microsoft.EntityFrameworkCore.Relational", StringComparison.OrdinalIgnoreCase)
                        || d!.Value.Name.Equals(Core, StringComparison.OrdinalIgnoreCase));
                if (onEf is { } range && range.Value.ValueKind == JsonValueKind.String && runtime is not null && id.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase))
                {
                    string given = range.Value.GetString()!;
                    providers.Add(new EfProvider(id, version, given, InRange(given, runtime)));
                }
            }
            return new EfRestore(runtime, design, providers) { Framework = chosen.Name.Split('/')[0] };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // A restore being written, or one that does not read: none read, and the build restores again.
            return null;
        }
    }

    /// <summary>Whether a version is in a NuGet range as a restore writes it: "10.0.12" (that or later), "[9.0.1, 10.0.0)",
    /// "[10.0.0]".</summary>
    public static bool InRange(string range, string version)
    {
        string given = range.Trim();
        if (!given.StartsWith('[') && !given.StartsWith('('))
        {
            return VersionOrder.Instance.Compare(version, given) >= 0;
        }
        bool fromIncluded = given[0] == '[';
        bool toIncluded = given[^1] == ']';
        string[] ends = given[1..^1].Split(',');
        string from = ends[0].Trim();
        string to = ends.Length > 1 ? ends[1].Trim() : from;
        if (from.Length > 0)
        {
            int order = VersionOrder.Instance.Compare(version, from);
            if (order < 0 || (order == 0 && !fromIncluded))
            {
                return false;
            }
        }
        if (to.Length > 0)
        {
            int order = VersionOrder.Instance.Compare(version, to);
            if (order > 0 || (order == 0 && !toIncluded))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The EF Core versions a range speaks of, said as a person would: "EF Core 9" for [9.0.1, 10.0.0).</summary>
    public static string Supports(string range)
    {
        string given = range.Trim();
        if (!given.StartsWith('[') && !given.StartsWith('('))
        {
            return $"EF Core {given} and later";
        }
        string[] ends = given[1..^1].Split(',');
        string from = ends[0].Trim();
        string to = ends.Length > 1 ? ends[1].Trim() : from;
        if (from == to)
        {
            return $"EF Core {from}";
        }
        if (from.Length > 0 && to.Length > 0 && given[^1] == ')' && Major(to) == Major(from) + 1 && to.EndsWith(".0.0", StringComparison.Ordinal))
        {
            return $"EF Core {Major(from)}";
        }
        return to.Length == 0 ? $"EF Core {from} and later" : $"EF Core {from} to {to}";
    }

    private static int Major(string version) => int.TryParse(version.Split('.')[0], out int major) ? major : -1;
}
