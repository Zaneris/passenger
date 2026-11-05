namespace Passenger.App.Godot;

/// <summary>A property of a running game's node set from the properties pane: the node (its id, name and place), the
/// property as the game first told it (its type and hint), the value it had before the first set, and the value the game
/// holds now, as last sent or read back.</summary>
public sealed class GodotTuned(ulong node, GodotNodePlace place, GodotProperty property)
{
    public ulong Node { get; } = node;

    public GodotNodePlace Place { get; } = place;

    public GodotProperty Property { get; } = property;

    public string Name => Property.Name;

    /// <summary>The node's name, as its rows say it; a resource's, its file's name or its id in the scene that holds it.</summary>
    public string NodeName => Place.Chain.Count > 0 ? Place.Chain[^1].Name : Place.Path;

    /// <summary>Where the property is a resource's own, a node holding the resource: the resource's path as Godot gives it,
    /// its file's (<c>res://grass.tres</c>) or its scene's and its id there (<c>res://main.tscn::StyleBoxFlat_x1y2</c>), empty
    /// for one the game made as it ran; null for a node's property.</summary>
    public string? Resource { get; init; }

    /// <summary>The value it had before it was first set, or as last kept.</summary>
    public object? Original { get; internal set; } = property.Value;

    /// <summary>The value the game holds, as last sent or read back.</summary>
    public object? Value { get; internal set; } = property.Value;

    /// <summary>Whether the value differs from the one it had, or was last kept.</summary>
    public bool Changed => !GodotVariant.Same(Original, Value);
}

/// <summary>The values a running game's nodes were given from the properties pane, each marked while it differs from
/// what it had and not yet kept (<see cref="Unkept"/>); a value kept is the one changes are marked against after. Kept
/// past the game's end, so what was not kept is listed then. A UI-thread object.</summary>
public sealed class GodotTuning(string folder)
{
    /// <summary>The project's folder, which the nodes' <c>res://</c> paths are under.</summary>
    public string Folder { get; } = folder;

    private readonly Dictionary<(ulong Node, string Name), GodotTuned> tuned = [];

    /// <summary>A value was set, read back, kept or discarded.</summary>
    public event EventHandler? Changed;

    /// <summary>The values set and not kept, in the order first set.</summary>
    public IReadOnlyList<GodotTuned> Unkept => [.. tuned.Values.Where(t => t.Changed)];

    /// <summary>A node's values set and not kept.</summary>
    public IReadOnlyList<GodotTuned> UnkeptOf(ulong node) => [.. tuned.Values.Where(t => t.Node == node && t.Changed)];

    /// <summary>A property's record, once it was set.</summary>
    public GodotTuned? Of(ulong node, string name) => tuned.GetValueOrDefault((node, name));

    /// <summary>A value sent to a node's property, or to a resource's own (<paramref name="resource"/> its path, as
    /// <see cref="GodotTuned.Resource"/>): recorded, the first time against the value the property had as read.</summary>
    public void Set(ulong node, GodotNodePlace place, GodotProperty property, object? value, string? resource = null)
    {
        UiThread.Check();
        if (!tuned.TryGetValue((node, property.Name), out GodotTuned? record))
        {
            tuned[(node, property.Name)] = record = new GodotTuned(node, place, property) { Resource = resource };
        }
        record.Value = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A node's properties as the game read them back: each one set takes the value the game holds.</summary>
    public void Read(GodotObjectState state)
    {
        bool moved = false;
        foreach (GodotProperty property in state.Properties)
        {
            if (tuned.TryGetValue((state.Id, property.Name), out GodotTuned? record) && !GodotVariant.Same(record.Value, property.Value))
            {
                record.Value = property.Value;
                moved = true;
            }
        }
        if (moved)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A value kept where it comes from: what changes are marked against from now on.</summary>
    public void Kept(GodotTuned record)
    {
        record.Original = record.Value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Every value not kept let go of, unwritten.</summary>
    public void Discard()
    {
        foreach (GodotTuned record in tuned.Values)
        {
            record.Original = record.Value;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
