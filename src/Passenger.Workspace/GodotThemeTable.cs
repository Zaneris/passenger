using System.Text.Json;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>Godot's default theme as the table Passenger carries says it (<c>GodotDefaultTheme.json</c>, written by
/// <c>just godot-theme</c> from Godot's own sources): each theme type's items by their kind as a theme resource names it
/// (<see cref="Kinds"/>), the type variations it makes, and each class deriving from Control or Window with its base, along
/// which a node looks its theme items up. Pure.</summary>
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

    private GodotThemeTable(IReadOnlyDictionary<string, string> bases, IReadOnlyDictionary<string, string> variations,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> types)
    {
        this.bases = bases;
        Variations = variations;
        this.types = types;
    }

    /// <summary>The table Passenger carries.</summary>
    public static GodotThemeTable Carried => carried.Value;

    /// <summary>The type variations the default theme makes, each with the type it varies (<c>HeaderSmall</c>'s <c>Label</c>).</summary>
    public IReadOnlyDictionary<string, string> Variations { get; }

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
        return new GodotThemeTable(Strings("bases"), Strings("variations"), types);
    }

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
