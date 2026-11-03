using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>A game's message as Passenger's addon relays it from Godot's editor: an object the game sent by its id, which
/// the addon sends as a dictionary of one entry, is read as that id again wherever it is (in the data, a list, a typed
/// array, a dictionary's values), the array keeping its element type; every other value, a dictionary of more entries or
/// of that key holding no id among them, is as it came.</summary>
public class GodotRelayTests
{
    private static GodotDictionary Sent(long id) => new([new KeyValuePair<object?, object?>(GodotRelay.ObjectKey, id)]);

    [Fact]
    public void An_object_sent_by_its_id_is_read_as_that_id_wherever_it_is()
    {
        GodotArray typed = new([Sent(7), Sent(8)]) { Element = GodotElement.Of(GodotType.Object) };
        GodotMessage relayed = new("scene:inspect_objects", GodotMessage.MainThread,
            [Sent(42), new List<object?> { "parent", Sent(43) }, typed, new GodotDictionary([new KeyValuePair<object?, object?>("held", Sent(44))])]);

        GodotMessage read = GodotRelay.Objects(relayed);

        Assert.IsType<List<object?>>(read.Data, exactMatch: false);
        Assert.Equal(new GodotObjectId(42), read.Data[0]);
        Assert.Equal(["parent", new GodotObjectId(43)], (List<object?>)read.Data[1]!);
        GodotArray array = Assert.IsType<GodotArray>(read.Data[2]);
        Assert.True(array.Element == typed.Element && array.SequenceEqual([new GodotObjectId(7), new GodotObjectId(8)]), "The typed array was to keep its type and read its ids");
        Assert.Equal(new GodotObjectId(44), Assert.IsType<GodotDictionary>(read.Data[3]).Entries[0].Value);
    }

    [Fact]
    public void Every_other_value_is_as_it_came()
    {
        GodotDictionary two = new([new KeyValuePair<object?, object?>(GodotRelay.ObjectKey, 1L), new KeyValuePair<object?, object?>("other", 2L)]);
        GodotDictionary named = new([new KeyValuePair<object?, object?>(GodotRelay.ObjectKey, "not an id")]);
        GodotMessage relayed = new("error", GodotMessage.MainThread, [1L, "Spinner fell over", 2.5, two, named, null]);

        GodotMessage read = GodotRelay.Objects(relayed);

        Assert.Equal([1L, "Spinner fell over", 2.5], read.Data.Take(3));
        Assert.Equal(two.Entries, Assert.IsType<GodotDictionary>(read.Data[3]).Entries);
        Assert.Equal(named.Entries, Assert.IsType<GodotDictionary>(read.Data[4]).Entries);
        Assert.Null(read.Data[5]);
    }
}
