using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A Godot project's files told apart by their extensions in one place (<see cref="GodotFileKinds"/>), in any letter
/// case, as Godot's loaders take them.</summary>
public sealed class GodotFileKindsTests
{
    [Theory]
    [InlineData("res://main.tscn", "scene text")]
    [InlineData("res://Main.TSCN", "scene text")]
    [InlineData("res://level.scn", "scene")]
    [InlineData("res://imported.escn", "scene textScene")]
    [InlineData("res://theme.tres", "text")]
    [InlineData("res://walk.res", "binary")]
    [InlineData("res://walk.anim", "binary")]
    [InlineData("res://player.gd", "script")]
    [InlineData("res://Player.GD", "script")]
    [InlineData("res://wind.gdshader", "shader")]
    [InlineData("res://noise.gdshaderinc", "shader include")]
    [InlineData("res://Noise.GDSHADERINC", "shader include")]
    [InlineData("res://player.gd.uid", "")]
    [InlineData("res://notes.txt", "")]
    public void A_file_is_of_the_kinds_its_extension_names_in_any_letter_case(string path, string kinds)
    {
        string[] expected = kinds.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] read =
        [
            .. GodotFileKinds.IsScene(path) ? ["scene"] : Array.Empty<string>(),
            .. GodotFileKinds.IsText(path) ? ["text"] : Array.Empty<string>(),
            .. GodotFileKinds.IsTextScene(path) && !GodotFileKinds.IsText(path) ? ["textScene"] : Array.Empty<string>(),
            .. GodotFileKinds.IsBinary(path) ? ["binary"] : Array.Empty<string>(),
            .. GodotFileKinds.IsScript(path) ? ["script"] : Array.Empty<string>(),
            .. GodotFileKinds.IsShader(path) ? ["shader"] : Array.Empty<string>(),
            .. GodotFileKinds.IsShaderInclude(path) ? ["include"] : Array.Empty<string>(),
        ];
        Assert.True(expected.Order(StringComparer.Ordinal).SequenceEqual(read.Order(StringComparer.Ordinal)),
            $"{path} was read as [{string.Join(", ", read)}] where it is [{string.Join(", ", expected)}], so a check or a move takes it for another kind of file");
    }
}
