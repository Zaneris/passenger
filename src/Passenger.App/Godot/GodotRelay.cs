namespace Passenger.App.Godot;

/// <summary>A game's messages as Passenger's addon relays them from the Godot editor that holds the game's debugger: the
/// editor has read each already, an object the game sent by its id held as Godot's EncodedObjectAsID, which Godot's own
/// encoding would write again as that holder's id, so the addon sends such an object as a dictionary of one entry, its key
/// <see cref="ObjectKey"/> and its value the id, which <see cref="Objects"/> puts back as the object's id.</summary>
public static class GodotRelay
{
    /// <summary>The one key of the dictionary that stands for an object sent by its id.</summary>
    public const string ObjectKey = "passenger:object";

    /// <summary>The message with each object the addon sent by its id put back as that id, its data still the list a message
    /// read holds.</summary>
    public static GodotMessage Objects(GodotMessage message) => message with { Data = message.Data.Select(Object).ToList() };

    private static object? Object(object? value) => value switch
    {
        GodotDictionary { Entries: [{ Key: string key, Value: long id }] } when key == ObjectKey => new GodotObjectId((ulong)id),
        GodotDictionary dictionary => dictionary with { Entries = [.. dictionary.Entries.Select(e => new KeyValuePair<object?, object?>(Object(e.Key), Object(e.Value)))] },
        GodotArray array => new GodotArray(array.Select(Object)) { Element = array.Element },
        List<object?> list => list.Select(Object).ToList(),
        _ => value,
    };
}
