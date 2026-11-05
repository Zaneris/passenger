using System.Text.Json;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>A property of an engine class as Godot's ClassDB lists it: its name, Variant type (by its number), hint, hint
/// string and usage flags, and the class that declares it.</summary>
public sealed record GodotClassProperty(string Name, int Type, long Hint, string HintString, long Usage, string Owner)
{
    /// <summary>Usage flags Godot gives a heading of the properties after it, a category, group or subgroup.</summary>
    private const long HeadingFlags = 64 | 128 | 256;

    /// <summary>The flag of a property the inspector shows.</summary>
    private const long EditorFlag = 4;

    public bool IsHeading => (Usage & HeadingFlags) != 0;

    /// <summary>Whether Godot's inspector shows it, and so a scene may set it.</summary>
    public bool Shown => (Usage & EditorFlag) != 0 && !IsHeading;
}

/// <summary>The engine's classes a scene's nodes and resources may be, as the Godot found lists them (its ClassDB, read
/// headless once per version by <c>PassengerClasses.gd</c>): what each extends, the properties it declares with their
/// hints, and the defaults of its properties, its parents' too, as Godot's variant text writes them. Pure.</summary>
public sealed class GodotClassDefaults
{
    private readonly Dictionary<string, (string? Inherits, IReadOnlyList<GodotClassProperty> Own, IReadOnlyDictionary<string, string> Defaults)> classes;

    private GodotClassDefaults(Dictionary<string, (string?, IReadOnlyList<GodotClassProperty>, IReadOnlyDictionary<string, string>)> classes) => this.classes = classes;

    /// <summary>The classes a probe's JSON gives: each class's <c>inherits</c>, its own <c>properties</c> (each of name, type,
    /// hint, hint string and usage) and its <c>defaults</c> where they differ from its parent's.</summary>
    public static GodotClassDefaults Read(ReadOnlySpan<byte> json)
    {
        using JsonDocument read = JsonDocument.Parse(json.ToArray());
        Dictionary<string, (string?, IReadOnlyList<GodotClassProperty>, IReadOnlyDictionary<string, string>)> classes = new(StringComparer.Ordinal);
        if (read.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new GodotClassDefaults(classes);
        }
        foreach (JsonProperty entry in read.RootElement.EnumerateObject())
        {
            List<GodotClassProperty> own = [];
            foreach (JsonElement property in JsonMembers.Items(entry.Value, "properties"))
            {
                if (property.ValueKind == JsonValueKind.Array && property.GetArrayLength() == 5 && property[0].ValueKind == JsonValueKind.String
                    && property[1].TryGetInt32(out int type) && property[2].TryGetInt64(out long hint) && property[3].ValueKind == JsonValueKind.String
                    && property[4].TryGetInt64(out long usage))
                {
                    own.Add(new GodotClassProperty(property[0].GetString()!, type, hint, property[3].GetString()!, usage, entry.Name));
                }
            }
            Dictionary<string, string> defaults = new(StringComparer.Ordinal);
            if (JsonMembers.Object(entry.Value, "defaults") is { } given)
            {
                foreach (JsonProperty value in given.EnumerateObject().Where(v => v.Value.ValueKind == JsonValueKind.String))
                {
                    defaults[value.Name] = value.Value.GetString()!;
                }
            }
            classes[entry.Name] = (JsonMembers.Text(entry.Value, "inherits") is { Length: > 0 } inherits ? inherits : null, own, defaults);
        }
        return new GodotClassDefaults(classes);
    }

    public bool Has(string name) => classes.ContainsKey(name);

    /// <summary>A class and those it extends, itself first; empty for one the engine lacks.</summary>
    public IEnumerable<string> Chain(string name)
    {
        for (string? at = name; at is not null && classes.TryGetValue(at, out (string? Inherits, IReadOnlyList<GodotClassProperty> Own, IReadOnlyDictionary<string, string> Defaults) known); at = known.Inherits)
        {
            yield return at;
        }
    }

    /// <summary>The properties a class has, as Godot's inspector lists them: its own first, then each parent's, each class's
    /// in the order it declares them, headings among them.</summary>
    public IReadOnlyList<GodotClassProperty> PropertiesOf(string name) => [.. Chain(name).SelectMany(c => classes[c].Own)];

    /// <summary>The default of a class's property as Godot's variant text writes it (<c>Vector2(0, 0)</c>), from the class
    /// or the nearest parent that says one; null where none does.</summary>
    public string? DefaultOf(string name, string property) =>
        Chain(name).Select(c => classes[c].Defaults.GetValueOrDefault(property)).FirstOrDefault(d => d is not null);
}
