using Passenger.App.LiveTree;
using Passenger.Workspace;
using Passenger.Workspace.GDScript;
using Passenger.Xaml.Features;

namespace Passenger.App.Godot;

/// <summary>A property of a scene's node as its outline shows it: the property as Godot's inspector takes it, its value
/// what the scene sets, else what a scene it is instanced from sets, else its default; its value as Godot's text writes it;
/// where that comes from; and whether the scene sets it. <see cref="Base"/> is what it is where the scene sets nothing,
/// which a value set back to removes the scene's line; <see cref="Rank"/> its place in Godot's save of the node.</summary>
public sealed record GodotSceneProperty(GodotProperty Property, string Text, string Source, bool Set)
{
    public string? Base { get; init; }

    public int Rank { get; init; } = int.MaxValue;
}

/// <summary>A section of a scene the node is instanced from, which gives it its values beneath the scene's own: the scene's
/// file name, its text and the section.</summary>
public sealed record GodotBeneath(string Name, string Text, GodotSection Section);

/// <summary>A node of a scene as its file writes it, the source of the values its outline's properties show and edit
/// (<see cref="IGodotValues"/>): what the scene sets, the script's exports and the engine class's properties at their
/// defaults. A value edited lands in the scene's text once it settles, one edit of its node's lines through the window's
/// change of files, one command of Undo: its line replaced, removed where the value is back to what it is unset, or added
/// where Godot's save puts it; a value typed as text is checked as Godot's variant text first. A value moving is only shown.</summary>
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
        bool unset = shown.Base is { } beneath && Equals(GodotVariant.OfScene(shown.Property.Type, beneath).Value, value);
        await WriteAsync(shown, unset ? null : GodotText.Scene(value), value, unset);
    }

    /// <summary>A property's value written as Godot's variant text (an array, a dictionary, a resource, any value), checked
    /// as the scene's reader reads it first; text the same as what it is unset removes its line.</summary>
    public async Task SetTextAsync(string property, string written)
    {
        if (properties.FirstOrDefault(p => p.Property.Name == property) is not { } shown)
        {
            throw new LiveAppException($"{name} has no {property}");
        }
        string trimmed = written.Trim();
        if (WhyNotText(property, trimmed) is { } why)
        {
            throw new LiveAppException(why);
        }
        bool unset = shown.Base is { } beneath && beneath.Trim() == trimmed;
        await WriteAsync(shown, unset ? null : trimmed, null, unset);
    }

    /// <summary>Why text is no value of Godot's variant text for a property, as the scene's reader reads it; null where it is.</summary>
    public static string? WhyNotText(string property, string written)
    {
        if (written.Length == 0 || written.Contains('\n'))
        {
            return $"{property} takes one value on its line";
        }
        GodotFile read = GodotFile.Parse($"[node name=\"Node\" type=\"Node\"]\n{property} = {written}\n");
        int start = $"[node name=\"Node\" type=\"Node\"]\n{property} = ".Length;
        return read.Nodes is [{ Section: var section }] && section.Property(property) is { Value: var value } && value.Start == start && value.End == start + written.Length
            ? null : $"{written} is no value Godot reads";
    }

    /// <summary>Writes a property's value as text into the node's section, or set back to what it is unset
    /// (<paramref name="unset"/>) takes its line out; a value no text can hold (a resource by its path) is written as Keep
    /// writes it.</summary>
    private async Task WriteAsync(GodotSceneProperty shown, string? written, object? value, bool unset)
    {
        string property = shown.Property.Name;
        string text = textOf(path) ?? throw new LiveAppException($"{System.IO.Path.GetFileName(path)} cannot be read");
        if (GodotKeep.NodeIn(GodotFile.Parse(text), node) is not { } section)
        {
            throw new LiveAppException($"{System.IO.Path.GetFileName(path)} no longer writes {name}");
        }
        GodotEntry? entry = section.Property(property);
        string lineEnding = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        IReadOnlyList<Passenger.Text.TextEdit> edits;
        if (!unset && written is null)
        {
            (GodotKept? kept, string? whyNot) = GodotKeep.InScene(path, text, section, property, value, shown.Property);
            edits = kept?.Edits ?? throw new LiveAppException(whyNot ?? $"{property} cannot be written");
        }
        else if (unset || written is null)
        {
            if (entry is null)
            {
                Show(shown);
                return;
            }
            int from = LineStart(text, entry.KeyStart);
            int to = NextLine(text, entry.Value.End);
            edits = [new Passenger.Text.TextEdit(from, to - from, string.Empty)];
        }
        else if (entry is not null)
        {
            if (text[entry.Value.Start..entry.Value.End] == written)
            {
                Show(shown);
                return;
            }
            edits = [new Passenger.Text.TextEdit(entry.Value.Start, entry.Value.End - entry.Value.Start, written)];
        }
        else
        {
            // Where Godot's save puts it: before the first line the node writes that Godot saves after it.
            int rank = shown.Rank;
            GodotEntry? after = section.Properties.FirstOrDefault(p => RankOf(p.Key) > rank);
            int at = after is not null ? LineStart(text, after.KeyStart)
                : NextLine(text, section.Properties.Count > 0 ? Math.Max(section.End, section.Properties[^1].Value.End) : section.End);
            string line = $"{property} = {written}{lineEnding}";
            edits = [new Passenger.Text.TextEdit(at, 0, at == text.Length && at > 0 && text[^1] != '\n' ? lineEnding + line : line)];
        }
        if (await change($"Set {name}.{property}", [new XamlFileChange(path, now => now == text ? edits : [])]) is { } refused)
        {
            properties = read(textOf(path) ?? string.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
            throw new LiveAppException(refused);
        }
        properties = read(textOf(path) ?? string.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A property's place in Godot's save of the node: the script's between its engine class's and its exports, one
    /// not listed after all that are.</summary>
    private int RankOf(string property) => property == "script" ? ScriptRank : properties.FirstOrDefault(p => p.Property.Name == property)?.Rank ?? int.MaxValue;

    /// <summary>The script's place in Godot's save of a node: after every property of its engine class, before its exports.</summary>
    private const int ScriptRank = 1 << 24;

    private static int LineStart(string text, int offset) => text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;

    /// <summary>Where the line after the one holding an offset begins (the text's end on its last line).</summary>
    private static int NextLine(string text, int offset) => text.IndexOf('\n', Math.Min(offset, text.Length)) is var end and >= 0 ? end + 1 : text.Length;

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
    /// script's exports (<paramref name="exports"/>, C#'s or GDScript's, by <paramref name="script"/>), then its engine class's
    /// properties the inspector shows (<paramref name="classes"/>), its own class's first; then any other set. A value is the
    /// scene's where it sets one, else the scenes' it is instanced from (<paramref name="beneath"/>, the outermost first),
    /// else the default; each ranked as Godot saves a node: its engine classes' from the farthest, its script, then its exports.</summary>
    public static IReadOnlyList<GodotSceneProperty> Of(string scene, string text, GodotSection section, string? type, GodotClassDefaults? classes,
        IReadOnlyList<GodotScriptExport> exports, string? script, IReadOnlyList<GodotBeneath>? beneath = null)
    {
        string named = System.IO.Path.GetFileName(scene);
        IReadOnlyList<GodotBeneath> under = beneath ?? [];
        List<(GodotProperty Declared, string? Default, string Source)> declared = [];
        HashSet<string> listed = new(StringComparer.Ordinal);
        Dictionary<string, int> ranks = new(StringComparer.Ordinal);
        if (type is not null && classes is not null)
        {
            IReadOnlyList<GodotClassProperty> all = classes.PropertiesOf(type);
            foreach (string owner in classes.Chain(type).Reverse())
            {
                foreach (GodotClassProperty property in all.Where(p => p.Owner == owner && !p.IsHeading))
                {
                    ranks.TryAdd(property.Name, ranks.Count);
                }
            }
        }
        foreach (GodotScriptExport export in exports)
        {
            ranks.TryAdd(export.Name, ScriptRank + 1 + ranks.Count);
        }
        foreach (GodotScriptExport export in exports.Where(e => listed.Add(e.Name)))
        {
            declared.Add((new GodotProperty(export.Name, (GodotType)export.Type, export.Hint, export.HintString, 6, null, export.Inherited ?? script ?? "Script"),
                export.Default, $"Default of {export.Inherited ?? script ?? "its script"}"));
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
        foreach (GodotEntry entry in under.SelectMany(b => b.Section.Properties).Concat(section.Properties).Where(p => p.Key != "script" && listed.Add(p.Key)))
        {
            declared.Add((new GodotProperty(entry.Key, GodotType.Nil, 0, string.Empty, 6, null, named), null, named));
        }
        return [.. declared.Select(d =>
        {
            // What it is unset here: the last scene beneath that sets it, else its default.
            (string Text, string Source)? instanced = under.Reverse().Select(b => b.Section.Property(d.Declared.Name) is { } set
                ? (b.Text[set.Value.Start..set.Value.End], $"Set in {b.Name}:{TextLine(b.Text, set.KeyStart) + 1}") : ((string, string)?)null).FirstOrDefault(i => i is not null);
            string? unset = instanced?.Text ?? d.Default;
            GodotProperty typed = d.Declared.Type == GodotType.Nil && (section.Property(d.Declared.Name) is { } own ? text[own.Value.Start..own.Value.End] : unset) is { } guessed
                ? d.Declared with { Type = GodotVariant.TypeOf(GodotVariant.Guessed(guessed)) } : d.Declared;
            int rank = ranks.GetValueOrDefault(typed.Name, int.MaxValue);
            return section.Property(typed.Name) is { } entry && text[entry.Value.Start..entry.Value.End] is var written
                ? new GodotSceneProperty(typed with { Value = GodotVariant.OfScene(typed.Type, written).Value }, written, $"Set in {named}:{TextLine(text, entry.KeyStart) + 1}", true)
                    { Base = unset, Rank = rank }
                : new GodotSceneProperty(typed with { Value = unset is null ? null : GodotVariant.OfScene(typed.Type, unset).Value }, unset ?? string.Empty,
                    instanced?.Source ?? d.Source, false) { Base = unset, Rank = rank };
        })];
    }

    private static int TextLine(string text, int offset) => text.AsSpan(0, Math.Min(offset, text.Length)).Count('\n');
}
