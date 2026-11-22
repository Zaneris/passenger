using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A Godot project's files told apart by their extensions in one place (<see cref="GodotFileKinds"/>), in any letter
/// case, as Godot's loaders take them.</summary>
public sealed class GodotFileKindsTests
{
    /// <summary>A file is of the kinds its extension names, in any letter case.</summary>
    [Fact]
    public void A_files_kinds_are_those_its_extension_names() => Cases.All(
        (nameof(A_file_is_of_the_kinds_its_extension_names_in_any_letter_case), A_file_is_of_the_kinds_its_extension_names_in_any_letter_case));

    private void A_file_is_of_the_kinds_its_extension_names_in_any_letter_case() => Cases.Each<(string Path, string Kinds)>(
    [
        ("res://Main.TSCN", "scene text"),
        ("res://imported.escn", "scene textScene"),
        ("res://walk.res", "binary"),
        ("res://player.gd", "script"),
        ("res://noise.gdshaderinc", "shader include"),
        ("res://player.gd.uid", ""),
        ("res://notes.txt", ""),
    ], row => A_file_is_of_the_kinds_its_extension_names_in_any_letter_case(row.Path, row.Kinds));

    private void A_file_is_of_the_kinds_its_extension_names_in_any_letter_case(string path, string kinds)
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
