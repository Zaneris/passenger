using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What a Godot project's page and its export read: export_presets.cfg's presets and the templates each takes, the
/// folder a Godot keeps its templates in and the version they are of, an input action's events in words, and the plugins
/// enabled written into project.godot.</summary>
public class GodotProjectReadsTests
{
    private const string Presets = """
        [preset.0]

        name="Desktop"
        platform="Windows Desktop"
        runnable=true
        export_path="build/Hopper.exe"

        [preset.0.options]

        custom_template/debug=""
        custom_template/release=""
        binary_format/architecture="arm64"

        [preset.1]

        name="Mac"
        platform="macOS"
        export_path="build/Hopper.zip"

        [preset.1.options]

        custom_template/release="res://own/macos.zip"

        """;

    [Fact]
    public void Presets_are_read_with_their_platform_path_architecture_and_own_templates_and_name_the_templates_they_take()
    {
        IReadOnlyList<GodotPreset> presets = GodotExports.Read(Presets);
        Assert.Equal(["Desktop", "Mac"], presets.Select(p => p.Name));
        GodotPreset desktop = presets[0];
        Assert.Equal(("Windows Desktop", "build/Hopper.exe", "arm64", true, 2), (desktop.Platform, desktop.ExportPath, desktop.Architecture, desktop.Runnable, desktop.Line));
        Assert.Null(desktop.Custom(debug: true));
        Assert.Equal(["windows_release_arm64.exe"], GodotExports.TemplatesOf(desktop, debug: false));
        Assert.Equal(["windows_debug_arm64.exe"], GodotExports.TemplatesOf(desktop, debug: true));
        Assert.Equal("res://own/macos.zip", presets[1].Custom(debug: false));
        Assert.Equal(["linux_release.x86_64"], GodotExports.TemplatesOf(new GodotPreset("Pc", "Linux", null, null, null, null, false, 0), debug: false));

        IReadOnlySet<string> needed = GodotExports.NeededBy(presets)!;
        Assert.Equal(["icudt_godot.dat", "macos.zip", "version.txt", "windows_debug_arm64.exe", "windows_debug_arm64_console.exe", "windows_release_arm64.exe",
            "windows_release_arm64_console.exe"], needed.Order(StringComparer.Ordinal));
        Assert.Null(GodotExports.NeededBy([]));
    }

    [Fact]
    public void A_preset_lacks_the_templates_not_in_its_godots_folder_or_its_own_template_where_it_names_one()
    {
        GodotPreset[] presets = [.. GodotExports.Read(Presets)];
        HashSet<string> there = [Path.Combine("t", "windows_release_arm64.exe"), Path.Combine("game", "own", "macos.zip")];
        Assert.Empty(GodotExports.Missing(presets[0], debug: false, "t", there.Contains, "game"));
        Assert.Equal(["windows_debug_arm64.exe"], GodotExports.Missing(presets[0], debug: true, "t", there.Contains, "game"));
        Assert.Empty(GodotExports.Missing(presets[1], debug: false, "t", there.Contains, "game"));
        Assert.Equal(["macos.zip"], GodotExports.Missing(presets[1], debug: true, "t", there.Contains, "game"));
    }

    [Fact]
    public void A_godots_templates_are_of_its_version_status_and_build_in_its_data_folder()
    {
        Assert.Equal("4.7.2.stable.mono", GodotExports.TemplatesVersion("4.7.2.stable.mono.official.ed1daf0bf"));
        Assert.Equal("4.3.rc1", GodotExports.TemplatesVersion("4.3.rc1.official.77dcf97d8"));
        Assert.Null(GodotExports.TemplatesVersion("Godot Engine"));
        Dictionary<string, string> linux = new() { ["HOME"] = "/home/ann" };
        Assert.Equal(Path.Combine("/home/ann", ".local", "share", "godot", "export_templates", "4.7.2.stable.mono"),
            GodotExports.TemplatesFolder("/opt/godot/godot", "4.7.2.stable.mono", windows: false, macOS: false, linux.GetValueOrDefault, _ => false));
        linux["XDG_DATA_HOME"] = "/data";
        Assert.Equal(Path.Combine("/data", "godot", "export_templates", "4.7.2.stable.mono"),
            GodotExports.TemplatesFolder("/opt/godot/godot", "4.7.2.stable.mono", windows: false, macOS: false, linux.GetValueOrDefault, _ => false));
        Assert.Equal(Path.Combine("/opt/godot", "editor_data", "export_templates", "4.7.2.stable.mono"),
            GodotExports.TemplatesFolder("/opt/godot/godot", "4.7.2.stable.mono", windows: false, macOS: false, linux.GetValueOrDefault, p => p == Path.Combine("/opt/godot", "._sc_")));
        Assert.Equal(Path.Combine("/Users/ann", "Library", "Application Support", "Godot", "export_templates", "4.7.2.stable.mono"),
            GodotExports.TemplatesFolder("/Applications/Godot.app/Contents/MacOS/Godot", "4.7.2.stable.mono", windows: false, macOS: true,
                new Dictionary<string, string> { ["HOME"] = "/Users/ann" }.GetValueOrDefault, _ => false));
    }

    [Fact]
    public void An_actions_events_are_said_as_keys_buttons_and_axes()
    {
        const string Project = """
            [input]

            jump={
            "deadzone": 0.2,
            "events": [Object(InputEventKey,"resource_local_to_scene":false,"device":-1,"ctrl_pressed":true,"shift_pressed":true,"keycode":0,"physical_keycode":4194309,"key_label":0,"unicode":0,"script":null)
            , Object(InputEventMouseButton,"device":-1,"button_index":2,"double_click":true,"script":null)
            , Object(InputEventJoypadMotion,"device":-1,"axis":1,"axis_value":-1.0,"script":null)
            , Object(InputEventKey,"device":-1,"keycode":81,"physical_keycode":0,"command_or_control_autoremap":true,"script":null)
            ]
            }
            idle={
            "deadzone": 0.5,
            "events": []
            }

            """;
        GodotDefinitions defined = new GodotScenes("/game", new Dictionary<string, GodotFile>(), texts: new Dictionary<string, string> { [GodotDefinitions.ProjectRes] = Project }).Defined;
        Assert.Equal(["Ctrl+Shift+Enter (physical)", "Right Mouse Button (double-click)", "Joypad Left Stick Up", "Ctrl/Cmd+Q"], defined.EventsOf("jump"));
        Assert.Empty(defined.EventsOf("idle"));
        Assert.Empty(defined.EventsOf("missing"));
        Assert.Equal(["F12", "Space", "Kp 3", "É", "(unset)"], new long[] { (1L << 22) + 0x27, 32, (1L << 22) + 0x89, 0xE9, 0 }.Select(GodotInputEvents.KeyName));
    }

    [Fact]
    public void The_plugins_enabled_are_written_over_those_listed_or_in_a_section_made_for_them()
    {
        const string Project = "config_version=5\n\n[application]\n\nconfig/name=\"Hopper\"\n\n[rendering]\n\nx=1\n";
        string made = GodotWrites.WithPlugins(Project, ["res://addons/a/plugin.cfg"]);
        Assert.Equal("config_version=5\n\n[application]\n\nconfig/name=\"Hopper\"\n\n[editor_plugins]\n\nenabled=PackedStringArray(\"res://addons/a/plugin.cfg\")\n\n[rendering]\n\nx=1\n", made);
        string both = GodotWrites.WithPlugins(made, ["res://addons/a/plugin.cfg", "res://addons/b/plugin.cfg"]);
        Assert.Contains("enabled=PackedStringArray(\"res://addons/a/plugin.cfg\", \"res://addons/b/plugin.cfg\")\n\n[rendering]", both, StringComparison.Ordinal);
        Assert.Contains("enabled=PackedStringArray()\n", GodotWrites.WithPlugins(both, []), StringComparison.Ordinal);
    }
}
