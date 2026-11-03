using System.Text.Json;
using Passenger.App.Mcp;

namespace Passenger.UnitTests.Mcp;

/// <summary>What godot_editor's read tells an agent of the scene Godot's editor edits: whether it is saved where its Godot
/// says, and that it does not say where a Godot before 4.7 gives no word; its nodes with what their file stores, its
/// connections as source signal → target method, and its selection.</summary>
public class GodotEditedScenesTests
{
    private static GodotEditorAnswer Read(string json)
    {
        using JsonDocument read = JsonDocument.Parse(json);
        return GodotEditedScenes.Of(read.RootElement);
    }

    private const string Nodes = """
        "scene": "res://main.tscn",
        "nodes": [{ "path": ".", "type": "Node", "script": "res://Main.cs", "properties": {} },
                  { "path": "Ui/Press", "type": "Button", "properties": { "text": "\"Go\"" } }],
        "connections": [{ "from": "Ui/Press", "signal": "toggled", "to": ".", "method": "OnToggled" }],
        "selected": ["Ui/Press"]
        """;

    [Fact]
    public void A_Godot_that_says_whether_the_scene_is_saved_is_taken_at_its_word()
    {
        GodotEditorAnswer unsaved = Read($$"""{ {{Nodes}}, "unsaved": true }""");
        Assert.True(unsaved.Unsaved);
        Assert.Equal("res://main.tscn as Godot's editor holds it, with changes not yet saved", unsaved.Said);
        Assert.Equal("res://main.tscn as Godot's editor holds it, saved", Read($$"""{ {{Nodes}}, "unsaved": false }""").Said);
    }

    [Fact]
    public void A_Godot_that_gives_no_word_of_it_is_said_not_to()
    {
        GodotEditorAnswer read = Read($$"""{ {{Nodes}} }""");
        Assert.Null(read.Unsaved);
        Assert.Equal("res://main.tscn as Godot's editor holds it (this Godot does not say whether it is saved)", read.Said);
    }

    [Fact]
    public void Its_nodes_connections_and_selection_are_given_as_the_editor_holds_them()
    {
        GodotEditorAnswer read = Read($$"""{ {{Nodes}} }""");
        Assert.Equal([".", "Ui/Press"], read.Nodes!.Select(n => n.Path));
        Assert.Equal("res://Main.cs", read.Nodes![0].Script);
        Assert.Null(read.Nodes![0].Properties);
        Assert.Equal("\"Go\"", read.Nodes![1].Properties!["text"]);
        Assert.Equal(["Ui/Press toggled → . OnToggled"], read.Connections!);
        Assert.Equal(["Ui/Press"], read.Selected!);
    }
}
