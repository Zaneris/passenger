using System.Xml.Linq;
using Passenger.App.Testing;
using Passenger.Core;

namespace Passenger.UnitTests.Godot;

/// <summary>Whether Godot's arguments run it headless, and a GdUnit4 run's settings made to run its Godot headless, the
/// parameters they give kept.</summary>
public class GodotHeadlessTests
{
    [Fact]
    public void Godot_runs_headless_by_its_flag_or_the_headless_display_driver_and_not_otherwise()
    {
        Assert.True(GodotExecutables.RunsHeadless(["--path", "/game", "--headless"]), "--headless among the arguments was not taken for a headless run");
        Assert.True(GodotExecutables.RunsHeadless(["--display-driver", "headless", "--path", "/game"]), "The headless display driver was not taken for a headless run");
        Assert.False(GodotExecutables.RunsHeadless(["--path", "/game", "--display-driver", "macos", "headless"]),
            "A word headless after another display driver was taken for a headless run, which would leave a window on the screen");
        Assert.False(GodotExecutables.RunsHeadless(["--path", "/game", "res://main.tscn"]), "A plain start was taken for a headless one, so it would get no --headless");
    }

    [Fact]
    public void GdUnit4s_godot_is_made_headless_after_the_parameters_given_and_once_where_they_say_so_already()
    {
        XElement none = new("RunSettings", new XElement("RunConfiguration"));
        GdUnit4Settings.Headless(none);
        Assert.Equal("--headless", none.Element("GdUnit4")?.Element("Parameters")?.Value);

        XElement verbose = XElement.Parse("<RunSettings><GdUnit4><DisplayName>SimpleName</DisplayName><Parameters> --verbose </Parameters></GdUnit4></RunSettings>");
        GdUnit4Settings.Headless(verbose);
        Assert.Equal("--verbose --headless", verbose.Element("GdUnit4")!.Element("Parameters")!.Value);
        Assert.Equal("SimpleName", verbose.Element("GdUnit4")!.Element("DisplayName")!.Value);

        XElement already = XElement.Parse("<RunSettings><GdUnit4><Parameters>--headless --verbose</Parameters></GdUnit4></RunSettings>");
        GdUnit4Settings.Headless(already);
        Assert.Equal("--headless --verbose", already.Element("GdUnit4")!.Element("Parameters")!.Value);
    }
}
