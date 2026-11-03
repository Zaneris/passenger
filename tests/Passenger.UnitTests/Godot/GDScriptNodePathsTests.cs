using Passenger.Workspace.GDScript;

namespace Passenger.UnitTests.Godot;

/// <summary>The node path a GDScript names at the caret, read by Passenger's GDScript parser: a $ path, a % unique name, and
/// a string get_node is given; none away from them.</summary>
public class GDScriptNodePathsTests
{
    private const string Script = """
        extends Control

        func _ready() -> void:
        	var button := $Panel/Button
        	var unique := %Button
        	var named = get_node("Panel/Button")
        	var quoted := $"Panel/Button"
        	print(button, unique, named, quoted)

        """;

    private static string? At(string after) => GDScriptNodePaths.At(GDScriptTree.Parse(Script), Script.IndexOf(after, StringComparison.Ordinal) + after.Length);

    [Fact]
    public void A_dollar_path_a_unique_name_a_quoted_path_and_a_get_node_string_are_each_read_at_the_caret()
    {
        Assert.Equal("Panel/Button", At("$Panel/But"));
        Assert.Equal("%Button", At("%But"));
        Assert.Equal("Panel/Button", At("get_node(\"Pan"));
        Assert.Equal("Panel/Button", At("$\"Panel/Bu"));
    }

    [Fact]
    public void Away_from_a_path_none_is_read()
    {
        Assert.Null(At("var butt"));
        Assert.Null(At("print(but"));
    }
}
