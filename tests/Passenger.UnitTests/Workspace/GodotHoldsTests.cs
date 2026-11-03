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

    [Fact]
    public void A_scene_with_unsaved_edits_is_held_unsaved_and_not_read_again()
    {
        Assert.Equal(GodotHold.Unsaved, GodotHolds.Of(Main, [Main, Other], [Main], saysUnsaved: true, inspected: null));
        Assert.False(GodotHolds.Reloads(Main, [Main, Other], [Main], saysUnsaved: true), "A write over unsaved edits would have Godot drop them");
    }

    [Fact]
    public void A_scene_open_and_saved_holds_nothing_and_is_read_again_after_a_write()
    {
        Assert.Null(GodotHolds.Of(Other, [Main, Other], [Main], saysUnsaved: true, inspected: null));
        Assert.True(GodotHolds.Reloads(Other, [Main, Other], [Main], saysUnsaved: true), "Godot would go on showing the scene as it was before the write");
    }

    [Fact]
    public void A_Godot_that_tells_no_unsaved_scenes_holds_every_open_scene_perhaps_unsaved_and_reads_none_again()
    {
        Assert.Equal(GodotHold.PerhapsUnsaved, GodotHolds.Of(Main, [Main], [], saysUnsaved: false, inspected: null));
        Assert.False(GodotHolds.Reloads(Main, [Main], [], saysUnsaved: false), "A reload could drop edits this Godot never said were there");
        Assert.Null(GodotHolds.Of(Other, [Main], [], saysUnsaved: false, inspected: null));
    }

    [Fact]
    public void A_resource_the_inspector_shows_is_held_as_inspected_but_a_scene_it_shows_by_its_openness_alone()
    {
        Assert.Equal(GodotHold.Inspected, GodotHolds.Of(Theme, [Main], [], saysUnsaved: true, inspected: Theme));
        Assert.Null(GodotHolds.Of(Theme, [Main], [], saysUnsaved: true, inspected: null));
        Assert.Null(GodotHolds.Of(Other, [Main], [], saysUnsaved: true, inspected: Other));
    }

    [Fact]
    public void Each_hold_says_what_a_write_would_go_over()
    {
        Assert.Equal("Godot's editor holds unsaved edits in main.tscn", GodotHolds.Said("main.tscn", GodotHold.Unsaved));
        Assert.Contains("does not say whether its edits there are saved", GodotHolds.Said("main.tscn", GodotHold.PerhapsUnsaved));
        Assert.Contains("does not say whether a resource holds unsaved edits", GodotHolds.Said("theme.tres", GodotHold.Inspected));
    }
}
