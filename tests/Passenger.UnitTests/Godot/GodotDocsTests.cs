using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>The address of a Godot class's or member's place in Godot's class reference, for the version of the Godot found,
/// by the ids its pages give (read from docs.godotengine.org's pages: class-node2d-property-position,
/// class-node-method-get-tree, class-node-private-method-ready, class-basebutton-signal-pressed, enum-node-processmode,
/// class-globalscope-method-print); and the native class or member a card of Godot's GDScript server is of.</summary>
public class GodotDocsTests
{
    /// <summary>The class reference is the found Godot's, each member at its place on its class's page.</summary>
    [Fact]
    public void The_class_reference_is_read_at_the_found_godots_version_and_its_members_places() => Cases.All(
        (nameof(The_reference_read_is_the_found_godots_minor_version_else_the_stable_one),
            The_reference_read_is_the_found_godots_minor_version_else_the_stable_one),
        (nameof(A_member_is_at_its_place_on_its_classs_page), A_member_is_at_its_place_on_its_classs_page),
        (nameof(A_native_card_names_its_class_and_member), A_native_card_names_its_class_and_member));

    private void The_reference_read_is_the_found_godots_minor_version_else_the_stable_one() => Cases.Each<(string? Godot, string Version)>(
    [
        ("4.7.2", "4.7"),
        ("4.10.1.rc2", "4.10"),
        ("x.y", "stable"),
        (null, "stable"),
    ], row => The_reference_read_is_the_found_godots_minor_version_else_the_stable_one(row.Godot, row.Version));

    private void The_reference_read_is_the_found_godots_minor_version_else_the_stable_one(string? godot, string version) =>
        Assert.Equal(version, GodotDocs.VersionOf(godot));

    private void A_member_is_at_its_place_on_its_classs_page() => Cases.Each<(string Type, string? Kind, string? Member, string Page)>(
    [
        ("Node2D", GodotDocs.Property, "position", "class_node2d.html#class-node2d-property-position"),
        ("Node", GodotDocs.Method, "_ready", "class_node.html#class-node-private-method-ready"),
        ("Node", GodotDocs.Enum, "ProcessMode", "class_node.html#enum-node-processmode"),
        ("@GlobalScope", GodotDocs.Method, "print", "class_@globalscope.html#class-globalscope-method-print"),
        ("CharacterBody2D", null, null, "class_characterbody2d.html"),
        ("Label", "theme_color", "font_color", "class_label.html#class-label-theme-color-font-color"),
    ], row => A_member_is_at_its_place_on_its_classs_page(row.Type, row.Kind, row.Member, row.Page));

    private void A_member_is_at_its_place_on_its_classs_page(string type, string? kind, string? member, string page) =>
        Assert.Equal($"https://docs.godotengine.org/en/4.7/classes/{page}", GodotDocs.Page("4.7", new GodotDocItem(type, kind, member)).AbsoluteUri);

    private void A_native_card_names_its_class_and_member() => Cases.Each<(string Card, string Type, string? Kind, string? Member)>(
    [
        ("\tfunc Node.get_tree() -> SceneTree\n\nReturns the SceneTree that contains this node.", "Node", GodotDocs.Method, "get_tree"),
        ("\tsignal BaseButton.pressed()\n", "BaseButton", GodotDocs.Signal, "pressed"),
        ("\tfunc @GlobalScope.print(...) -> void\n", "@GlobalScope", GodotDocs.Method, "print"),
        ("\t<Native> class Node2D\n\nA 2D game object.", "Node2D", null, null),
    ], row => A_native_card_names_its_class_and_member(row.Card, row.Type, row.Kind, row.Member));

    private void A_native_card_names_its_class_and_member(string card, string type, string? kind, string? member) =>
        Assert.Equal(new GodotDocItem(type, kind, member), GodotDocs.OfScriptCard(card));

}
