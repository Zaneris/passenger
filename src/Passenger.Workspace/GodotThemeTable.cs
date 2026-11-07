using System.Collections.Concurrent;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>Godot's default theme as the table Passenger carries says it (<c>GodotDefaultTheme.json</c>, written by
/// <c>just godot-theme</c> from Godot's own sources): each theme type's items by their kind as a theme resource names it
/// (<see cref="Kinds"/>), the type variations it makes, each class deriving from Control or Window with its base, along
/// which a node looks its theme items up, the class reference's words for each item a class documents, and what each
/// Godot it tells apart added and removed, so a project is read by its own Godot's items (<see cref="For"/>). Pure.</summary>
public sealed class GodotThemeTable
{
    /// <summary>The kinds of theme items, as a theme resource writes them in its keys (<c>Label/colors/font_color</c>).</summary>
    public static readonly IReadOnlyList<string> Kinds = ["colors", "constants", "fonts", "font_sizes", "icons", "styles"];

    private const string Resource = "Passenger.Workspace.GodotDefaultTheme.json";

    private static readonly Lazy<GodotThemeTable> carried = new(() =>
    {
        using Stream stream = typeof(GodotThemeTable).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"Passenger carries no {Resource}");
        using StreamReader reader = new(stream);
        return Read(reader.ReadToEnd());
    });

    private readonly IReadOnlyDictionary<string, string> bases;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> types;
    private readonly IReadOnlyDictionary<(string Type, string Kind, string Name), string> docs;
    private readonly IReadOnlyList<Change> changes;
    private readonly ConcurrentDictionary<Version, GodotThemeTable> byVersion = new();

    private GodotThemeTable(IReadOnlyDictionary<string, string> bases, IReadOnlyDictionary<string, string> variations,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> types, IReadOnlyDictionary<(string, string, string), string> docs,
        IReadOnlyList<Change> changes)
    {
        this.bases = bases;
        Variations = variations;
        this.types = types;
        this.docs = docs;
        this.changes = changes;
    }

    /// <summary>What a Godot's default theme added and removed from the one before it, the oldest told apart adding nothing.</summary>
    private sealed record Change(Version Godot, IReadOnlyList<(string Type, string Kind, string Name)> Added, IReadOnlyList<(string Type, string Kind, string Name)> Removed);

    /// <summary>The table Passenger carries.</summary>
    public static GodotThemeTable Carried => carried.Value;

    /// <summary>The type variations the default theme makes, each with the type it varies (<c>HeaderSmall</c>'s <c>Label</c>).</summary>
    public IReadOnlyDictionary<string, string> Variations { get; }

    /// <summary>The Godot whose default theme it says; null for the table as carried, a project naming no Godot's.</summary>
    public Version? Godot { get; private init; }

    /// <summary>Whether it holds all of its Godot's default theme: not where that Godot is one it tells no version of, so a
    /// name it lacks may still be there.</summary>
    public bool IsWhole { get; private init; } = true;

    /// <summary>A table as the JSON <c>just godot-theme</c> writes says it.</summary>
    public static GodotThemeTable Read(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Dictionary<string, string> Strings(string name) => JsonMembers.Properties(root, name).Where(p => p.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> types = new(StringComparer.Ordinal);
        foreach (JsonProperty type in JsonMembers.Properties(root, "types").Where(p => p.Value.ValueKind == JsonValueKind.Object))
        {
            types[type.Name] = Kinds.ToDictionary(k => k, k => JsonMembers.Texts(type.Value, k), StringComparer.Ordinal);
        }
        Dictionary<(string, string, string), string> docs = [];
        foreach (JsonProperty type in JsonMembers.Properties(root, "docs").Where(p => p.Value.ValueKind == JsonValueKind.Object))
        {
            foreach (JsonProperty kind in type.Value.EnumerateObject().Where(k => k.Value.ValueKind == JsonValueKind.Object))
            {
                foreach (JsonProperty item in kind.Value.EnumerateObject().Where(i => i.Value.ValueKind == JsonValueKind.String))
                {
                    docs[(type.Name, kind.Name, item.Name)] = item.Value.GetString()!;
                }
            }
        }
        List<Change> changes = [];
        foreach (JsonProperty version in JsonMembers.Properties(root, "versions"))
        {
            changes.Add(new Change(Version.Parse(version.Name), Items("added"), Items("removed")));

            List<(string, string, string)> Items(string change) =>
                [.. JsonMembers.Properties(version.Value, change).SelectMany(type => Kinds.SelectMany(kind => JsonMembers.Texts(type.Value, kind).Select(name => (type.Name, kind, name))))];
        }
        return new GodotThemeTable(Strings("bases"), Strings("variations"), types, docs, [.. changes.OrderBy(c => c.Godot)]);
    }

    /// <summary>The default theme as a Godot's major and minor version has it; this table where none is named.</summary>
    public GodotThemeTable For(Version? godot) => godot is null ? this : byVersion.GetOrAdd(new Version(godot.Major, godot.Minor), As);

    private GodotThemeTable As(Version godot)
    {
        Dictionary<(string Type, string Kind), HashSet<string>> held = [];
        foreach ((string type, IReadOnlyDictionary<string, IReadOnlyList<string>> kinds) in types)
        {
            foreach ((string kind, IReadOnlyList<string> names) in kinds)
            {
                held[(type, kind)] = [.. names];
            }
        }
        foreach (Change later in changes.Where(c => c.Godot > godot).Reverse())
        {
            foreach ((string type, string kind, string name) in later.Added)
            {
                held[(type, kind)].Remove(name);
            }
            foreach ((string type, string kind, string name) in later.Removed)
            {
                if (!held.TryGetValue((type, kind), out HashSet<string>? names))
                {
                    held[(type, kind)] = names = [];
                }
                names.Add(name);
            }
        }
        Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> own = held.GroupBy(h => h.Key.Type, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => (IReadOnlyDictionary<string, IReadOnlyList<string>>)g.ToDictionary(h => h.Key.Kind, h => (IReadOnlyList<string>)[.. h.Value.Order(StringComparer.Ordinal)], StringComparer.Ordinal),
            StringComparer.Ordinal);
        return new GodotThemeTable(bases, Variations, own, docs, changes)
        {
            Godot = godot,
            IsWhole = changes.Count > 0 && godot >= changes[0].Godot && godot <= changes[^1].Godot,
        };
    }

    /// <summary>The names of a type's items of a kind that only Godots after this table's have, each with the first that does.</summary>
    public IReadOnlyDictionary<string, Version> Later(string type, string kind)
    {
        Dictionary<string, Version> later = new(StringComparer.Ordinal);
        foreach (Change change in changes.Where(c => Godot is { } godot && c.Godot > godot))
        {
            foreach ((_, _, string name) in change.Added.Where(a => a.Type == type && a.Kind == kind))
            {
                later.TryAdd(name, change.Godot);
            }
        }
        return later;
    }

    /// <summary>The names of a type's items of a kind that a Godot the table tells apart had and it lacks, for a table of no
    /// Godot's, which may be read for one of those.</summary>
    public IEnumerable<string> Removed(string type, string kind) =>
        Godot is null ? changes.SelectMany(c => c.Removed).Where(r => r.Type == type && r.Kind == kind).Select(r => r.Name) : [];

    /// <summary>The class reference's words for a class's theme item of a kind (<see cref="Kinds"/>); null where it documents none.</summary>
    public string? Described(string type, string kind, string name) => docs.GetValueOrDefault((type, kind, name));

    /// <summary>Whether a theme type is known: a class the table names, or a type or variation of the default theme.</summary>
    public bool Knows(string type) => bases.ContainsKey(type) || types.ContainsKey(type) || Variations.ContainsKey(type);

    /// <summary>The theme types Godot looks a theme item up in for a type, as it looks for a node's own class or a type given
    /// by name: a class, then each of its bases; anything else, itself alone.</summary>
    public IReadOnlyList<string> Chain(string type)
    {
        List<string> chain = [type];
        for (string at = type; bases.TryGetValue(at, out string? up) && !chain.Contains(up); at = up)
        {
            chain.Add(up);
        }
        return chain;
    }

    /// <summary>The classes deriving from a class, near or far, which a node the code knows only as that class may be.</summary>
    public IEnumerable<string> Below(string type) => bases.Keys.Where(c => c != type && Chain(c).Contains(type));

    /// <summary>The default theme's items of a kind (<see cref="Kinds"/>) for one theme type, by name.</summary>
    public IReadOnlyList<string> Items(string type, string kind) =>
        types.TryGetValue(type, out IReadOnlyDictionary<string, IReadOnlyList<string>>? kinds) && kinds.TryGetValue(kind, out IReadOnlyList<string>? names) ? names : [];
}
