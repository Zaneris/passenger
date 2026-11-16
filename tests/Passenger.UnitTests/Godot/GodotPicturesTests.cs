using Passenger.App.Godot;
using Passenger.Core;

namespace Passenger.UnitTests.Godot;

/// <summary>The file a game's answer to a picture names, which Passenger reads and then deletes: a PNG directly in the
/// temporary folder the game was given, and nothing else, whatever answers on the game's debug port.</summary>
public sealed class GodotPicturesTests : IDisposable
{
    private readonly string temporary = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(temporary, recursive: true);

    [Fact]
    public void A_png_godot_wrote_in_the_games_temporary_folder_is_read()
    {
        string picture = Written("scr-20261116120000.png");
        Assert.Null(GodotPictures.WhyNot(picture, temporary));
        Assert.Null(GodotPictures.WhyNot(picture, temporary + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void A_file_of_another_kind_or_place_or_one_not_there_is_left_alone()
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

    [Fact]
    public void A_link_named_as_a_png_in_the_folder_is_left_alone()
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

    [Fact]
    public void The_games_temporary_folder_is_the_one_its_environment_gives_else_passengers()
    {
        string variable = OperatingSystem.IsWindows() ? "TMP" : "TMPDIR";
        Assert.Equal(temporary, GodotPictures.TemporaryOf(new Dictionary<string, string> { [variable] = temporary }));
        Assert.Equal(Path.GetTempPath(), GodotPictures.TemporaryOf(new Dictionary<string, string> { [variable] = "relative" }));
        Assert.Equal(Path.GetTempPath(), GodotPictures.TemporaryOf(new Dictionary<string, string>()));
    }

    private string Written(string name)
    {
        string path = Path.Join(temporary, name);
        File.WriteAllBytes(path, [1]);
        return path;
    }
}
