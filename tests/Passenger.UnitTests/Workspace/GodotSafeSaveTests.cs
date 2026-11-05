using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Godot's safe save is told by the name of the file it writes first, as macOS's and Linux's Godot and Windows' name
/// it, which is what says a file replaced was Godot's; any other program's temporary file says nothing of the kind.</summary>
public class GodotSafeSaveTests
{
    private static readonly string Settings = Path.Combine(Path.GetTempPath(), "game", "project.godot");

    [Theory]
    [InlineData("-2wLoDY")]
    [InlineData("-123456")]
    [InlineData("1730812345678901.tmp")]
    public void A_file_named_as_godots_save_of_a_file_says_godot_saved_it(string end)
    {
        Assert.True(GodotSafeSave.Saves(Settings + end, Settings), $"{end} is how Godot names its save of a file");
    }

    [Theory]
    [InlineData("")]
    [InlineData(".tmp")]
    [InlineData("~")]
    [InlineData(".bak")]
    [InlineData("-2wLoD")]
    [InlineData("-2wLoDYx")]
    [InlineData("-2wL.DY")]
    [InlineData("___jb_tmp___")]
    [InlineData(".tmp.1234.5678")]
    [InlineData("12ab.tmp")]
    public void Another_programs_name_beside_a_file_says_nothing_of_godot(string end)
    {
        Assert.False(GodotSafeSave.Saves(Settings + end, Settings), $"project.godot{end} is no name Godot saves through, so a change it brings is another program's");
    }

    [Fact]
    public void A_save_of_another_file_or_folder_is_no_save_of_this_one()
    {
        Assert.False(GodotSafeSave.Saves(Path.Combine(Path.GetTempPath(), "game", "main.gd.uid-2wLoDY"), Settings), "A .uid's save is not project.godot's");
        Assert.False(GodotSafeSave.Saves(Path.Combine(Path.GetTempPath(), "other", "project.godot-2wLoDY"), Settings), "Another project's save is not this one's");
    }

    [Fact]
    public void Only_macOS_and_Linuxs_kind_of_name_gives_the_file_it_saves()
    {
        Assert.Equal(Settings, GodotSafeSave.FileOf(Settings + "-2wLoDY"));
        Assert.Null(GodotSafeSave.FileOf(Settings));
        Assert.Null(GodotSafeSave.FileOf(Path.Combine(Path.GetTempPath(), "-2wLoDY")));
        Assert.Null(GodotSafeSave.FileOf(Settings + "1730812345678901.tmp"));
    }
}
