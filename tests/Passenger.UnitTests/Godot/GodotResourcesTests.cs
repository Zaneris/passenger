using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>The files a resource property's picker offers: each by the class it loads as, so a sound only for its own format's
/// class or AudioStream, a picture for the texture classes it loads as, a script for its own language's class, never one
/// the property would refuse as it is set; a file its <c>.import</c> says loads as another class goes by that.</summary>
public class GodotResourcesTests
{
    [Theory]
    [InlineData("res://hit.wav", "AudioStreamWAV", true)]
    [InlineData("res://hit.wav", "AudioStream", true)]
    [InlineData("res://hit.wav", "AudioStreamMP3", false)]
    [InlineData("res://hit.wav", "AudioStreamOggVorbis", false)]
    [InlineData("res://hit.ogg", "AudioStreamOggVorbis", true)]
    [InlineData("res://hit.ogg", "AudioStreamWAV", false)]
    [InlineData("res://hit.mp3", "AudioStreamMP3", true)]
    [InlineData("res://hit.mp3", "AudioStream", true)]
    [InlineData("res://icon.png", "Texture2D", true)]
    [InlineData("res://icon.png", "Texture", true)]
    [InlineData("res://icon.svg", "CompressedTexture2D", true)]
    [InlineData("res://icon.png", "ImageTexture", false)]
    [InlineData("res://main.gd", "GDScript", true)]
    [InlineData("res://main.gd", "Script", true)]
    [InlineData("res://main.gd", "CSharpScript", false)]
    [InlineData("res://Main.cs", "CSharpScript", true)]
    [InlineData("res://Main.cs", "GDScript", false)]
    [InlineData("res://body.ttf", "Font", true)]
    [InlineData("res://body.ttf", "FontFile", true)]
    [InlineData("res://body.ttf", "FontVariation", false)]
    [InlineData("res://main.tscn", "PackedScene", true)]
    [InlineData("res://glow.gdshader", "Shader", true)]
    public void A_file_is_offered_for_the_classes_it_loads_as_and_no_other(string path, string kind, bool offered)
    {
        Assert.Equal(offered, GodotResources.Takes(path, [kind], _ => null));
    }

    [Fact]
    public void A_file_imported_as_another_class_is_offered_only_for_that_class()
    {
        Assert.False(GodotResources.Takes("res://mask.png", ["Texture2D"], _ => "Image"));
        Assert.True(GodotResources.Takes("res://mask.png", ["Image"], _ => "Image"));
        Assert.True(GodotResources.Takes("res://icon.png", ["Texture2D"], _ => "CompressedTexture2D"));
    }

    [Fact]
    public void An_import_file_says_the_class_its_file_loads_as()
    {
        Assert.Equal("AudioStreamWAV", GodotResources.ImportedType("[remap]\n\nimporter=\"wav\"\ntype=\"AudioStreamWAV\"\nuid=\"uid://b1\"\n\n[deps]\n\nsource_file=\"res://hit.wav\"\n"));
        Assert.Null(GodotResources.ImportedType("[remap]\n\nimporter=\"keep\"\n"));
    }

    [Fact]
    public void A_resource_file_goes_by_its_header_and_a_files_pattern_by_its_name()
    {
        Assert.True(GodotResources.Takes("res://shine.tres", ["ImageTexture"], _ => "ImageTexture"));
        Assert.False(GodotResources.Takes("res://shine.tres", ["AudioStreamMP3"], _ => "ImageTexture"));
        Assert.True(GodotResources.Takes("res://shine.tres", ["Resource"], _ => "ImageTexture"));
        Assert.True(GodotResources.Takes("res://hit.wav", ["AudioStreamMP3", "*.wav"], _ => null));
    }
}
