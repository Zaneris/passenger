using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What Godot's editor holds of a file, as its addon says: a scene with unsaved edits, an open scene a Godot that
/// tells no unsaved scenes may hold so, and a resource its inspector shows; a scene open and saved read again after a
/// write, and nothing else.</summary>
public class GodotHoldsTests
{
    private const string Main = "res://main.tscn";
    private const string Other = "res://other.tscn";
    private const string Theme = "res://theme.tres";

    /// <summary>Godot's editor holds the scenes and resources it may have unsaved, saying what a write would go
    /// over.</summary>
    [Fact]
    public void Godots_editor_holds_what_it_has_unsaved() => Cases.All(
        (nameof(A_scene_with_unsaved_edits_is_held_unsaved_and_not_read_again), A_scene_with_unsaved_edits_is_held_unsaved_and_not_read_again),
        (nameof(A_Godot_that_tells_no_unsaved_scenes_holds_every_open_scene_perhaps_unsaved_and_reads_none_again),
            A_Godot_that_tells_no_unsaved_scenes_holds_every_open_scene_perhaps_unsaved_and_reads_none_again),
        (nameof(A_resource_the_inspector_shows_is_held_as_inspected_but_a_scene_it_shows_by_its_openness_alone),
            A_resource_the_inspector_shows_is_held_as_inspected_but_a_scene_it_shows_by_its_openness_alone));

    private void A_scene_with_unsaved_edits_is_held_unsaved_and_not_read_again()
    {
        Assert.Equal(GodotHold.Unsaved, GodotHolds.Of(Main, [Main, Other], [Main], saysUnsaved: true, inspected: null));
        Assert.False(GodotHolds.Reloads(Main, [Main, Other], [Main], saysUnsaved: true), "A write over unsaved edits would have Godot drop them");
    }

    private void A_Godot_that_tells_no_unsaved_scenes_holds_every_open_scene_perhaps_unsaved_and_reads_none_again()
    {
        Assert.Equal(GodotHold.PerhapsUnsaved, GodotHolds.Of(Main, [Main], [], saysUnsaved: false, inspected: null));
        Assert.False(GodotHolds.Reloads(Main, [Main], [], saysUnsaved: false), "A reload could drop edits this Godot never said were there");
        Assert.Null(GodotHolds.Of(Other, [Main], [], saysUnsaved: false, inspected: null));
    }

    private void A_resource_the_inspector_shows_is_held_as_inspected_but_a_scene_it_shows_by_its_openness_alone()
    {
        Assert.Equal(GodotHold.Inspected, GodotHolds.Of(Theme, [Main], [], saysUnsaved: true, inspected: Theme));
        Assert.Null(GodotHolds.Of(Theme, [Main], [], saysUnsaved: true, inspected: null));
        Assert.Null(GodotHolds.Of(Other, [Main], [], saysUnsaved: true, inspected: Other));
    }

}
