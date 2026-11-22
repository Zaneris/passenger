using System.Xml.Linq;
using Passenger.App.Testing;
using Passenger.Core;

namespace Passenger.UnitTests.Godot;

/// <summary>Whether Godot's arguments run it headless, a GdUnit4 run's settings made to run its Godot headless and given the
/// arguments Settings gives Godot, the parameters they give kept, and GUT's and GoDotTest's Godot started with them.</summary>
public class GodotHeadlessTests
{
    /// <summary>Godot runs headless by its flag or driver, without SDL's HID gamepads, each test runner's after its
    /// settings.</summary>
    [Fact]
    public void A_headless_godot_is_started_without_sdls_hid_gamepads() => Cases.All(
        (nameof(Godot_runs_headless_by_its_flag_or_the_headless_display_driver_and_not_otherwise),
            Godot_runs_headless_by_its_flag_or_the_headless_display_driver_and_not_otherwise),
        (nameof(Every_headless_godot_is_started_without_SDLs_HID_gamepads_and_a_godot_someone_plays_keeps_them),
            Every_headless_godot_is_started_without_SDLs_HID_gamepads_and_a_godot_someone_plays_keeps_them),
        (nameof(GUT_and_GoDotTest_run_godot_headless_after_the_settings_arguments_unless_its_window_is_asked_for),
            GUT_and_GoDotTest_run_godot_headless_after_the_settings_arguments_unless_its_window_is_asked_for));

    private void Godot_runs_headless_by_its_flag_or_the_headless_display_driver_and_not_otherwise()
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

    private void Every_headless_godot_is_started_without_SDLs_HID_gamepads_and_a_godot_someone_plays_keeps_them()
    {
        Assert.Equal("0", ChildProcesses.Command("/godot", ["--headless", "--path", "/game", "--import"]).Environment[SdlHidGamepads]);
        Assert.Equal("0", ChildProcesses.Command("/godot", ["--display-driver", "headless", "--path", "/game"]).Environment[SdlHidGamepads]);
        Assert.False(ChildProcesses.Command("/godot", ["--path", "/game"]).Environment.ContainsKey(SdlHidGamepads),
            "A game with a window lost SDL's HID gamepads, which the person playing it may be holding");
        Assert.False(ChildProcesses.Command("/godot", ["--path", "/game", "--", "--headless"]).Environment.ContainsKey(SdlHidGamepads),
            "A game's own --headless, which Godot never reads, took SDL's HID gamepads from a game with a window");
    }

    private void GUT_and_GoDotTest_run_godot_headless_after_the_settings_arguments_unless_its_window_is_asked_for()
    {
        Assert.Equal(["--headless"], GodotStart.Headless.Leading);
        Assert.Equal(["--rendering-driver", "opengl3", "--headless"], new GodotStart(["--rendering-driver", "opengl3"], Windowed: false).Leading);
        Assert.Equal(["--rendering-driver", "opengl3"], new GodotStart(["--rendering-driver", "opengl3"], Windowed: true).Leading);
        Assert.Equal(["--headless", "--verbose"], new GodotStart(["--headless", "--verbose"], Windowed: false).Leading);
    }
}
