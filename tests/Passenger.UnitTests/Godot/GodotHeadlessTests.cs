using System.Xml.Linq;
using Passenger.App.Testing;
using Passenger.Core;

namespace Passenger.UnitTests.Godot;

/// <summary>Whether Godot's arguments run it headless, a GdUnit4 run's settings made to run its Godot headless and given the
/// arguments Settings gives Godot, the parameters they give kept, and GUT's and GoDotTest's Godot started with them.</summary>
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

    /// <summary>The name is SDL's own, so it is written out here: spelt any other way, SDL would read nothing and go on running
    /// the main thread's run loop as Godot ends.</summary>
    private const string SdlHidGamepads = "SDL_JOYSTICK_HIDAPI";

    [Fact]
    public void Every_headless_godot_is_started_without_SDLs_HID_gamepads_and_a_godot_someone_plays_keeps_them()
    {
        Assert.Equal("0", ChildProcesses.Command("/godot", ["--headless", "--path", "/game", "--import"]).Environment[SdlHidGamepads]);
        Assert.Equal("0", ChildProcesses.Command("/godot", ["--display-driver", "headless", "--path", "/game"]).Environment[SdlHidGamepads]);
        Assert.False(ChildProcesses.Command("/godot", ["--path", "/game"]).Environment.ContainsKey(SdlHidGamepads),
            "A game with a window lost SDL's HID gamepads, which the person playing it may be holding");
        Assert.False(ChildProcesses.Command("/godot", ["--path", "/game", "--", "--headless"]).Environment.ContainsKey(SdlHidGamepads),
            "A game's own --headless, which Godot never reads, took SDL's HID gamepads from a game with a window");
    }

    [Fact]
    public void An_environment_passed_on_for_a_headless_godot_turns_SDLs_HID_gamepads_off_unless_it_says_otherwise()
    {
        Dictionary<string, string> home = new() { ["HOME"] = "/runs/home" };
        IReadOnlyDictionary<string, string> headless = GodotExecutables.EnvironmentOf(["--headless", "--path", "/game"], home);
        Assert.Equal("0", headless[SdlHidGamepads]);
        Assert.Equal("/runs/home", headless["HOME"]);
        Assert.False(GodotExecutables.EnvironmentOf(["--path", "/game"], home).ContainsKey(SdlHidGamepads),
            "A debugged game with a window lost SDL's HID gamepads, which the person playing it may be holding");
        Assert.Equal("1", GodotExecutables.EnvironmentOf(["--headless"], new Dictionary<string, string> { [SdlHidGamepads] = "1" })[SdlHidGamepads]);
    }

    [Fact]
    public void GdUnit4s_godot_is_made_headless_after_the_parameters_given_and_once_where_they_say_so_already()
    {
        XElement none = new("RunSettings", new XElement("RunConfiguration"));
        GdUnit4Settings.Given(none, [], headless: true);
        Assert.Equal("--headless", none.Element("GdUnit4")?.Element("Parameters")?.Value);

        XElement verbose = XElement.Parse("<RunSettings><GdUnit4><DisplayName>SimpleName</DisplayName><Parameters> --verbose </Parameters></GdUnit4></RunSettings>");
        GdUnit4Settings.Given(verbose, [], headless: true);
        Assert.Equal("--verbose --headless", verbose.Element("GdUnit4")!.Element("Parameters")!.Value);
        Assert.Equal("SimpleName", verbose.Element("GdUnit4")!.Element("DisplayName")!.Value);

        XElement already = XElement.Parse("<RunSettings><GdUnit4><Parameters>--headless --verbose</Parameters></GdUnit4></RunSettings>");
        GdUnit4Settings.Given(already, [], headless: true);
        Assert.Equal("--headless --verbose", already.Element("GdUnit4")!.Element("Parameters")!.Value);
    }

    [Fact]
    public void GdUnit4s_godot_takes_the_settings_arguments_ahead_of_its_own_parameters_and_is_left_windowed_where_asked()
    {
        XElement given = XElement.Parse("<RunSettings><GdUnit4><Parameters>--verbose</Parameters></GdUnit4></RunSettings>");
        GdUnit4Settings.Given(given, ["--rendering-driver", "opengl3", "--log-file", "/runs/a log.txt"], headless: false);
        Assert.Equal("--rendering-driver opengl3 --log-file \"/runs/a log.txt\" --verbose", given.Element("GdUnit4")!.Element("Parameters")!.Value);

        XElement unseen = new("RunSettings");
        GdUnit4Settings.Given(unseen, ["--display-driver", "headless"], headless: true);
        Assert.Equal("--display-driver headless", unseen.Element("GdUnit4")!.Element("Parameters")!.Value);
    }

    [Fact]
    public void GUT_and_GoDotTest_run_godot_headless_after_the_settings_arguments_unless_its_window_is_asked_for()
    {
        Assert.Equal(["--headless"], GodotStart.Headless.Leading);
        Assert.Equal(["--rendering-driver", "opengl3", "--headless"], new GodotStart(["--rendering-driver", "opengl3"], Windowed: false).Leading);
        Assert.Equal(["--rendering-driver", "opengl3"], new GodotStart(["--rendering-driver", "opengl3"], Windowed: true).Leading);
        Assert.Equal(["--headless", "--verbose"], new GodotStart(["--headless", "--verbose"], Windowed: false).Leading);
    }
}
