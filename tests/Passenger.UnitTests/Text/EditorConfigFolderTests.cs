using Passenger.Core;
using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>What the .editorconfig files over a folder set, as Settings says it under a row: each key with its file's way
/// from the folder, its section's glob and its value, those in the folder first, then those above it up to a root; a key
/// said only where its section's glob matches a file the row is for, or a name of theirs beside the .editorconfig.</summary>
public sealed class EditorConfigFolderTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Each_key_is_said_with_its_files_way_from_the_folder_its_glob_and_its_value_and_one_taken_back_unsets_it()
    {
        string folder = Folder("repo/app");
        Write("repo/.editorconfig", "root = true\n\n[*.gd]\nmax_line_length = 120 # wide\n");
        Write("repo/app/.editorconfig", "[*]\ninsert_final_newline = true\n\n[*.gd]\nmax_line_length = unset\n");
        Write("repo/app/scripts/.editorconfig", "[{player,enemy}.gd]\ngdscript_trailing_comma = false\n");

        EditorConfigFolder read = EditorConfigFolder.Read(folder, [Path.Join(folder, ".editorconfig"), Path.Join(folder, "scripts", ".editorconfig")]);

        Assert.Equal(
            [
                "./.editorconfig sets insert_final_newline = true for *",
                "./.editorconfig unsets max_line_length for *.gd",
                "./scripts/.editorconfig sets gdscript_trailing_comma = false for {player,enemy}.gd",
                "../.editorconfig sets max_line_length = 120 # wide for *.gd",
            ],
            read.Keys.Select(k => k.Said(folder)));
        Assert.Equal([2, 5, 2, 4], read.Keys.Select(k => k.Line));
    }

    [Fact]
    public void A_folders_own_root_editorconfig_hides_those_above_it_and_one_above_marked_root_ends_the_climb()
    {
        Write("top/.editorconfig", "[*]\nindent_size = 8\n");
        Write("top/repo/.editorconfig", "root = true\n[*]\nindent_size = 4\n");
        Write("top/repo/app/.editorconfig", "root = true\n[*]\nindent_size = 2\n");
        string app = Folder("top/repo/app");
        string lib = Folder("top/repo/lib");

        Assert.Equal(["./.editorconfig sets indent_size = 2 for *"], EditorConfigFolder.Read(app, [Path.Join(app, ".editorconfig")]).Keys.Select(k => k.Said(app)));
        Assert.Equal(["../.editorconfig sets indent_size = 4 for *"], EditorConfigFolder.Read(lib, []).Keys.Select(k => k.Said(lib)));
    }

    [Theory]
    [InlineData("*.gd", true)]
    [InlineData("**.gd", true)]
    [InlineData("*", true)]
    [InlineData("*.{gd,cs}", true)]
    [InlineData("*.cs", false)]
    [InlineData("{player,enemy}.gd", true)]
    [InlineData("{boss,enemy}.gd", false)]
    [InlineData("scripts/*.gd", true)]
    [InlineData("levels/*.gd", false)]
    public void A_section_concerns_a_rows_files_where_its_glob_matches_one_of_them_or_their_name_beside_it(string glob, bool concerns)
    {
        string folder = Folder("game");
        SetKey key = new(Path.Join(folder, ".editorconfig"), glob, 2, "max_line_length", "120");

        Assert.True(key.Concerns(["file.gd"], [Path.Join(folder, "scripts", "player.gd"), Path.Join(root, "elsewhere", "enemy.gd")]) == concerns,
            $"[{glob}] {(concerns ? "did not concern" : "concerned")} the scripts");
    }

    private string Folder(string relative) => Directory.CreateDirectory(Path.Join(root, relative)).FullName;

    private void Write(string relative, string text)
    {
        string path = Path.Join(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
