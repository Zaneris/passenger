using Passenger.App.Godot;
using Passenger.Core;

namespace Passenger.UnitTests.Godot;

/// <summary>The file a game's answer to a picture names, which Passenger reads and then deletes: a PNG directly in the
/// game's temporary folder, and nothing else, whatever answers on the game's debug port.</summary>
public sealed class GodotPicturesTests : IDisposable
{
    private readonly string temporary = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(temporary, recursive: true);

    /// <summary>A game's picture is read only as a PNG Godot wrote in its own temporary folder.</summary>
    [Fact]
    public void A_games_picture_is_read_only_from_godots_own_temporary_folder() => Cases.AllApart<GodotPicturesTests>(
        (nameof(The_folder_a_games_picture_is_read_from_is_godots_own_temporary_folder_whatever_tmpdir_says),
            test => test.The_folder_a_games_picture_is_read_from_is_godots_own_temporary_folder_whatever_tmpdir_says()),
        (nameof(A_png_godot_wrote_in_the_games_temporary_folder_is_read), test => test.A_png_godot_wrote_in_the_games_temporary_folder_is_read()),
        (nameof(A_file_of_another_kind_or_place_or_one_not_there_is_left_alone),
            test => test.A_file_of_another_kind_or_place_or_one_not_there_is_left_alone()),
        (nameof(A_link_named_as_a_png_in_the_folder_is_left_alone), test => test.A_link_named_as_a_png_in_the_folder_is_left_alone()));

    /// <summary>Godot writes a picture where it takes its temporary folder: on macOS the user's own whatever TMPDIR says,
    /// on Linux /tmp, on Windows the TMP it was given, else Passenger's own.</summary>
    private void The_folder_a_games_picture_is_read_from_is_godots_own_temporary_folder_whatever_tmpdir_says()
    {
        Dictionary<string, string> given = new(StringComparer.Ordinal) { ["TMPDIR"] = temporary, ["TMP"] = temporary, ["TEMP"] = temporary };
        string expected = OperatingSystem.IsMacOS() ? UserTemporary.Folder() : OperatingSystem.IsWindows() ? temporary : "/tmp";
        Assert.True(GodotPictures.TemporaryOf(given) == expected, $"A picture is looked for in {GodotPictures.TemporaryOf(given)}, where Godot writes it in {expected}: every one is refused");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.GetTempPath(), GodotPictures.TemporaryOf(new Dictionary<string, string> { ["TMP"] = "relative" }));
            Assert.Equal(Path.GetTempPath(), GodotPictures.TemporaryOf(new Dictionary<string, string>()));
        }
    }

    private void A_png_godot_wrote_in_the_games_temporary_folder_is_read()
    {
        string picture = Written("scr-20261116120000.png");
        Assert.Null(GodotPictures.WhyNot(picture, temporary));
        Assert.Null(GodotPictures.WhyNot(picture, temporary + Path.DirectorySeparatorChar));
    }

    private void A_file_of_another_kind_or_place_or_one_not_there_is_left_alone()
    {
        string below = Path.Join(temporary, "deeper");
        Directory.CreateDirectory(below);
        File.WriteAllBytes(Path.Join(below, "scr.png"), [1]);
        string key = Written("id_ed25519");

        Assert.Contains("no PNG", GodotPictures.WhyNot(key, temporary), StringComparison.Ordinal);
        Assert.Contains("not in the temporary folder", GodotPictures.WhyNot(Path.Join(below, "scr.png"), temporary), StringComparison.Ordinal);
        Assert.Contains("not in the temporary folder", GodotPictures.WhyNot(Path.Join(below, "..", "..", "scr.png"), temporary), StringComparison.Ordinal);
        Assert.Contains("not there", GodotPictures.WhyNot(Path.Join(temporary, "gone.png"), temporary), StringComparison.Ordinal);
        Assert.Contains("no PNG", GodotPictures.WhyNot("scr.png", temporary), StringComparison.Ordinal);
    }

    private void A_link_named_as_a_png_in_the_folder_is_left_alone()
    {
        string key = Written("id_ed25519");
        string link = Path.Join(temporary, "scr-1.png");
        try
        {
            File.CreateSymbolicLink(link, key);
        }
        catch (Exception refused) when (OperatingSystem.IsWindows() && refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Windows lets this user make no symlink: {refused.Message}");
        }
        Assert.Contains("a link", GodotPictures.WhyNot(link, temporary), StringComparison.Ordinal);
    }

    private string Written(string name)
    {
        string path = Path.Join(temporary, name);
        File.WriteAllBytes(path, [1]);
        return path;
    }
}
