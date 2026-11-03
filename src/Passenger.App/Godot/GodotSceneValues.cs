using Passenger.App.LiveTree;
using Passenger.Workspace;
using Passenger.Workspace.GDScript;
using Passenger.Xaml.Features;

namespace Passenger.App.Godot;

/// <summary>A property of a scene's node as its outline shows it: the property as Godot's inspector takes it, its value
/// what the scene sets, else its default; its value as Godot's text writes it; where that comes from; and whether the
/// scene sets it.</summary>
public sealed record GodotSceneProperty(GodotProperty Property, string Text, string Source, bool Set);

/// <summary>A node of a scene as its file writes it, the source of the values its outline's properties show and edit
/// (<see cref="IGodotValues"/>): what the scene sets, the script's exports and the engine class's properties at their
/// defaults. A value edited lands in the scene's text once it settles, one edit of its node's lines through the window's
/// change of files (<see cref="GodotKeep.InScene"/>), one command of Undo; a value moving before is only shown.</summary>
public sealed class GodotSceneValues(string path, string node, string name, string? type, Func<string, string?> textOf,
    Func<string, IReadOnlyList<XamlFileChange>, Task<string?>> change, Func<string, IReadOnlyList<GodotSceneProperty>> read) : IGodotValues
{
    /// <summary>The value types a scene's text holds that no value editor here writes.</summary>
    private static readonly HashSet<GodotType> Unwritten = [GodotType.Nil, GodotType.Object, GodotType.Array, GodotType.Dictionary, GodotType.Callable,
        GodotType.Signal, GodotType.Rid, GodotType.PackedByteArray, GodotType.PackedInt32Array, GodotType.PackedInt64Array, GodotType.PackedFloat32Array,
        GodotType.PackedFloat64Array, GodotType.PackedStringArray, GodotType.PackedVector2Array, GodotType.PackedVector3Array, GodotType.PackedColorArray,
        GodotType.PackedVector4Array];

    private IReadOnlyList<GodotSceneProperty> properties = read(textOf(path) ?? string.Empty);

    /// <summary>The scene's file.</summary>
    public string File => path;

    /// <summary>The node's path from the scene's root as the file writes it ("." for the root).</summary>
    public string Node => node;

    public string Name => name;

    /// <summary>The node's engine class, where it is known.</summary>
    public string? Type => type;

    public IReadOnlyList<GodotSceneProperty> Properties => properties;

    /// <summary>A value was written into the scene, or moved as an editor drags: the rows show what the node holds now.</summary>
    public event EventHandler? Changed;

    public GodotProperty? PropertyOf(long element, string property) => properties.FirstOrDefault(p => p.Property.Name == property)?.Property;

    public string? WhyNotSet(long element, string property) => PropertyOf(element, property) is not { } known ? $"{name} has no {property}"
        : Unwritten.Contains(known.Type) ? $"{property} is set in the scene's text" : null;

    public GodotTuned? TunedOf(long element, string property) => null;

    public async Task SetValueAsync(long element, string property, object? value, bool settles)
    {
        if (properties.FirstOrDefault(p => p.Property.Name == property) is not { } shown)
        {
            throw new LiveAppException($"{name} has no {property}");
        }
        if (!settles)
        {
            // The editor shows the value as it moves; the scene takes it once it settles.
            properties = [.. properties.Select(p => p == shown ? shown with { Property = shown.Property with { Value = value } } : p)];
            return;
        }
        string text = textOf(path) ?? throw new LiveAppException($"{System.IO.Path.GetFileName(path)} cannot be read");
        if (GodotKeep.NodeIn(GodotFile.Parse(text), node) is not { } section)
        {
            throw new LiveAppException($"{System.IO.Path.GetFileName(path)} no longer writes {name}");
        }
        if (Equals(GodotVariant.OfScene(shown.Property.Type, section.Property(property) is { } entry ? text[entry.Value.Start..entry.Value.End] : shown.Text).Value, value)
            && section.Property(property) is not null)
        {
            Show(shown with { Property = shown.Property with { Value = value } });
            return;
        }
        (GodotKept? kept, string? whyNot) = GodotKeep.InScene(path, text, section, property, value, shown.Property);
        if (kept is null)
        {
            throw new LiveAppException(whyNot ?? $"{property} cannot be written");
        }
        if (await change($"Set {name}.{property}", [new XamlFileChange(path, now => now == text ? kept.Edits : [])]) is { } refused)
        {
            properties = read(textOf(path) ?? string.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
            throw new LiveAppException(refused);
        }
        properties = read(textOf(path) ?? string.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The scene's text read again (an edit of it, an Undo): the rows show what it holds now.</summary>
    public void ReadAgain()
    {
        properties = read(textOf(path) ?? string.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Show(GodotSceneProperty moved)
    {
        properties = [.. properties.Select(p => p.Property.Name == moved.Property.Name ? moved : p)];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A node's properties as Godot's inspector lists them, each in its place whether the scene sets it or not: its
    /// script's exports (<paramref name="exports"/>, C#'s or GDScript's, by <paramref name="script"/>), then its engine class's properties the
    /// inspector shows (<paramref name="classes"/>), its own class's first; then any other the scene sets. A value is the
    /// scene's where it sets one, else the default, read as its type reads it.</summary>
    public static IReadOnlyList<GodotSceneProperty> Of(string scene, string text, GodotSection section, string? type, GodotClassDefaults? classes,
        IReadOnlyList<GodotScriptExport> exports, string? script)
    {
        string named = System.IO.Path.GetFileName(scene);
        List<(GodotProperty Declared, string? Default, string Source)> declared = [];
        HashSet<string> listed = new(StringComparer.Ordinal);
        foreach (GodotScriptExport export in exports.Where(e => listed.Add(e.Name)))
        {
            declared.Add((new GodotProperty(export.Name, (GodotType)export.Type, export.Hint, export.HintString, 6, null, script ?? "Script"),
                export.Default, $"Default of {script ?? "its script"}"));
        }
        if (type is not null && classes is not null)
        {
            string category = string.Empty;
            foreach (GodotClassProperty property in classes.PropertiesOf(type))
            {
                if (property.IsHeading)
                {
                    category = $"{property.Owner}: {property.Name}";
                }
                else if (property.Shown && listed.Add(property.Name))
                {
                    declared.Add((new GodotProperty(property.Name, (GodotType)property.Type, property.Hint, property.HintString, property.Usage, null,
                        category.StartsWith(property.Owner + ":", StringComparison.Ordinal) ? category : property.Owner), classes.DefaultOf(type, property.Name), "Default"));
                }
            }
        }
        foreach (GodotEntry entry in section.Properties.Where(p => p.Key != "script" && section.Property(p.Key) == p && listed.Add(p.Key)))
        {
            string written = text[entry.Value.Start..entry.Value.End];
            declared.Add((new GodotProperty(entry.Key, GodotVariant.TypeOf(GodotVariant.Guessed(written)), 0, string.Empty, 6, null, named), null, named));
        }
        return [.. declared.Select(d => section.Property(d.Declared.Name) is { } entry && text[entry.Value.Start..entry.Value.End] is var written
            ? new GodotSceneProperty(d.Declared with { Value = GodotVariant.OfScene(d.Declared.Type, written).Value }, written, $"Set in {named}:{TextLine(text, entry.KeyStart) + 1}", true)
            : new GodotSceneProperty(d.Declared with { Value = d.Default is null ? null : GodotVariant.OfScene(d.Declared.Type, d.Default).Value }, d.Default ?? string.Empty, d.Source, false))];
    }

    private static int TextLine(string text, int offset) => text.AsSpan(0, Math.Min(offset, text.Length)).Count('\n');
}
