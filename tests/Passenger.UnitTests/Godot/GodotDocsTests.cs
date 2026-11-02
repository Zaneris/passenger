using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>The address of a Godot class's or member's place in Godot's class reference, for the version of the Godot found,
/// by the ids its pages give (read from docs.godotengine.org's pages: class-node2d-property-position,
/// class-node-method-get-tree, class-node-private-method-ready, class-basebutton-signal-pressed, enum-node-processmode,
/// class-globalscope-method-print); and the native class or member a card of Godot's GDScript server is of.</summary>
public class GodotDocsTests
{
    [Theory]
    [InlineData("4.7.2", "4.7")]
    [InlineData("4.10.1.rc2", "4.10")]
    [InlineData("4", "stable")]
    [InlineData("x.y", "stable")]
    [InlineData(null, "stable")]
    public void The_reference_read_is_the_found_godots_minor_version_else_the_stable_one(string? godot, string version) =>
        Assert.Equal(version, GodotDocs.VersionOf(godot));

    [Theory]
    [InlineData("Node2D", GodotDocs.Property, "position", "class_node2d.html#class-node2d-property-position")]
    [InlineData("Node", GodotDocs.Method, "get_tree", "class_node.html#class-node-method-get-tree")]
    [InlineData("Node", GodotDocs.Method, "_ready", "class_node.html#class-node-private-method-ready")]
    [InlineData("BaseButton", GodotDocs.Signal, "pressed", "class_basebutton.html#class-basebutton-signal-pressed")]
    [InlineData("Node", GodotDocs.Constant, "NOTIFICATION_READY", "class_node.html#class-node-constant-notification-ready")]
    [InlineData("Node", GodotDocs.Enum, "ProcessMode", "class_node.html#enum-node-processmode")]
    [InlineData("@GlobalScope", GodotDocs.Method, "print", "class_@globalscope.html#class-globalscope-method-print")]
    [InlineData("CharacterBody2D", null, null, "class_characterbody2d.html")]
    public void A_member_is_at_its_place_on_its_classs_page(string type, string? kind, string? member, string page) =>
        Assert.Equal($"https://docs.godotengine.org/en/4.7/classes/{page}", GodotDocs.Page("4.7", new GodotDocItem(type, kind, member)).AbsoluteUri);

    [Theory]
    [InlineData("\tfunc Node.get_tree() -> SceneTree\n\nReturns the SceneTree that contains this node.", "Node", GodotDocs.Method, "get_tree")]
    [InlineData("\tvar Node2D.position: Vector2\n\nPosition, relative to the node's parent.", "Node2D", GodotDocs.Property, "position")]
    [InlineData("\tsignal BaseButton.pressed()\n", "BaseButton", GodotDocs.Signal, "pressed")]
    [InlineData("\tconst Node.NOTIFICATION_READY = 13\n", "Node", GodotDocs.Constant, "NOTIFICATION_READY")]
    [InlineData("\tfunc @GlobalScope.print(...) -> void\n", "@GlobalScope", GodotDocs.Method, "print")]
    [InlineData("\t<Native> class Node2D\n\nA 2D game object.", "Node2D", null, null)]
    public void A_native_card_names_its_class_and_member(string card, string type, string? kind, string? member) =>
        Assert.Equal(new GodotDocItem(type, kind, member), GodotDocs.OfScriptCard(card));

    [Theory]
    [InlineData("\tfunc twice(value: int) -> int\n\nDefined in [res://main.gd](file:///game/main.gd)")]
    [InlineData("\tvar Main.counter: int\n\nDefined in [res://main.gd](file:///game/main.gd)")]
    [InlineData("A plain word.")]
    public void A_card_of_the_projects_own_code_or_of_nothing_named_is_no_native_ones(string card) =>
        Assert.Null(GodotDocs.OfScriptCard(card));
}
