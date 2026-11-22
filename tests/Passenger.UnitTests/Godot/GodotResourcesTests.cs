using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>The files a resource property's picker offers: each by the class it loads as, so a sound only for its own format's
/// class or AudioStream, a picture for the texture classes it loads as, a script for its own language's class, never one
/// the property would refuse as it is set; a file its <c>.import</c> says loads as another class goes by that.</summary>
public class GodotResourcesTests
{
    /// <summary>A file is offered for the classes it loads as, by its import, header or name.</summary>
    [Fact]
    public void A_file_is_offered_for_the_classes_it_loads_as() => Cases.All(
        (nameof(A_file_is_offered_for_the_classes_it_loads_as_and_no_other), A_file_is_offered_for_the_classes_it_loads_as_and_no_other),
        (nameof(A_file_imported_as_another_class_is_offered_only_for_that_class), A_file_imported_as_another_class_is_offered_only_for_that_class));

    private void A_file_is_offered_for_the_classes_it_loads_as_and_no_other() => Cases.Each<(string Path, string Kind, bool Offered)>(
    [
        ("res://hit.wav", "AudioStreamWAV", true),
        ("res://hit.wav", "AudioStreamMP3", false),
        ("res://hit.ogg", "AudioStreamOggVorbis", true),
        ("res://icon.png", "Texture2D", true),
        ("res://icon.png", "ImageTexture", false),
        ("res://main.gd", "GDScript", true),
        ("res://main.gd", "CSharpScript", false),
        ("res://Main.cs", "CSharpScript", true),
        ("res://body.ttf", "FontVariation", false),
        ("res://main.tscn", "PackedScene", true),
    ], row => A_file_is_offered_for_the_classes_it_loads_as_and_no_other(row.Path, row.Kind, row.Offered));

    private void A_file_is_offered_for_the_classes_it_loads_as_and_no_other(string path, string kind, bool offered)
    {
        Assert.Equal(offered, GodotResources.Takes(path, [kind], _ => null));
    }

    private void A_file_imported_as_another_class_is_offered_only_for_that_class()
    {
        Assert.False(GodotResources.Takes("res://mask.png", ["Texture2D"], _ => "Image"));
        Assert.True(GodotResources.Takes("res://mask.png", ["Image"], _ => "Image"));
        Assert.True(GodotResources.Takes("res://icon.png", ["Texture2D"], _ => "CompressedTexture2D"));
    }

}
