using System.IO.Compression;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What a Godot project's page and its export read: export_presets.cfg's presets and the templates each takes, the
/// folder a Godot keeps its templates in and the version they are of, an input action's events in words, and what the page
/// writes into project.godot: the plugins enabled, an autoload's star, the main scene, input actions and their events, and
/// layer names, each where Godot's own save puts it.</summary>
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
        // The executable's folder as this system spells it.
        string beside = Path.GetDirectoryName("/opt/godot/godot")!;
        Assert.Equal(Path.Combine(beside, "editor_data", "export_templates", "4.7.2.stable.mono"),
            GodotExports.TemplatesFolder("/opt/godot/godot", "4.7.2.stable.mono", windows: false, macOS: false, linux.GetValueOrDefault, p => p == Path.Combine(beside, "._sc_")));
        Assert.Equal(Path.Combine("/Users/ann", "Library", "Application Support", "Godot", "export_templates", "4.7.2.stable.mono"),
            GodotExports.TemplatesFolder("/Applications/Godot.app/Contents/MacOS/Godot", "4.7.2.stable.mono", windows: false, macOS: true,
                new Dictionary<string, string> { ["HOME"] = "/Users/ann" }.GetValueOrDefault, _ => false));
    }

    /// <summary>What an install takes from a template archive is named as one file of its folder, as Godot's templates are:
    /// an entry Windows would read as leaving the folder, a drive, or another file's stream is none of them on any system.</summary>
    [Fact]
    public void A_template_archives_entry_that_could_leave_its_folder_is_not_chosen_whatever_this_system_reads_it_as()
    {
        string[] templates = ["version.txt", "icudt_godot.dat", "linux_release.x86_64", "windows_debug_x86_64_console.exe", "web_dlink_nothreads_release.zip", "macos.zip"];
        string[] crafted = [@"..\..\victim.txt", @"C:\victim.txt", "C:victim.txt", @"\\server\share\victim.txt", "macos.zip:stream", "..", ".", ".. ", "victim.txt.", "victim.txt ",
            "nested/victim.txt", "folder/"];
        using MemoryStream held = new();
        using (ZipArchive written = new(held, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string name in templates.Concat(crafted))
            {
                written.CreateEntry("templates/" + name);
            }
        }
        held.Position = 0;
        using ZipArchive read = new(held, ZipArchiveMode.Read);

        Assert.Equal(templates, GodotExports.Chosen(read, null).Select(c => c.Name));
        Assert.Equal(["macos.zip"], GodotExports.Chosen(read, new HashSet<string>(["macos.zip", @"C:\victim.txt"])).Select(c => c.Name));
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

    /// <summary>An autoload is made a global by a '*' before its path, as Godot's Autoload settings write it, and a node by
    /// taking it away; one so already, or none of the name, is left.</summary>
    [Fact]
    public void An_autoload_is_made_a_global_or_a_node_by_its_paths_star()
    {
        const string Project = "config_version=5\n\n[autoload]\n\nScore=\"*res://score.gd\"\nMusic=\"res://music.tscn\"\n";
        Assert.Equal("config_version=5\n\n[autoload]\n\nScore=\"res://score.gd\"\nMusic=\"res://music.tscn\"\n", GodotWrites.WithAutoloadGlobal(Project, "Score", global: false));
        Assert.Equal("config_version=5\n\n[autoload]\n\nScore=\"*res://score.gd\"\nMusic=\"*res://music.tscn\"\n", GodotWrites.WithAutoloadGlobal(Project, "Music", global: true));
        Assert.Null(GodotWrites.WithAutoloadGlobal(Project, "Score", global: true));
        Assert.Null(GodotWrites.WithAutoloadGlobal(Project, "Gone", global: true));
    }

    /// <summary>An autoload Godot 4.4 writes by its script's uid is read as that script, and one whose uid no file has as none.</summary>
    [Fact]
    public void An_autoload_written_by_uid_is_read_as_the_file_the_uid_names()
    {
        string project = "config_version=5\n\n[autoload]\n\nKeychain=\"*uid://dgiia2xg7fsud\"\nGone=\"*uid://nothere\"\nPlain=\"*res://plain.gd\"\n";
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile>(), new Dictionary<string, string> { ["uid://dgiia2xg7fsud"] = "res://addons/keychain/Keychain.gd" },
            texts: new Dictionary<string, string> { [GodotDefinitions.ProjectRes] = project });
        Assert.Equal("res://addons/keychain/Keychain.gd", scenes.AutoloadFile("Keychain"));
        Assert.Null(scenes.AutoloadFile("Gone"));
        Assert.Equal("res://plain.gd", scenes.AutoloadFile("Plain"));
    }
    /// <summary>The main scene is written over where project.godot sets one, else put after the project's name, description,
    /// version and tags and before the rest, where Godot saves it; the scene set already is left.</summary>
    [Fact]
    public void The_main_scene_is_written_over_or_put_after_the_projects_name_and_tags()
    {
        const string Set = "config_version=5\n\n[application]\n\nconfig/name=\"Hopper\"\nrun/main_scene=\"res://main.tscn\"\nconfig/features=PackedStringArray(\"4.7\")\n";
        Assert.Equal(Set.Replace("res://main.tscn", "res://menu.tscn", StringComparison.Ordinal), GodotWrites.WithMainScene(Set, "res://menu.tscn"));
        Assert.Null(GodotWrites.WithMainScene(Set, "res://main.tscn"));
        const string Unset = "config_version=5\n\n[application]\n\nconfig/name=\"Hopper\"\nconfig/tags=PackedStringArray(\"a\")\nconfig/features=PackedStringArray(\"4.7\")\n";
        Assert.Equal("config_version=5\n\n[application]\n\nconfig/name=\"Hopper\"\nconfig/tags=PackedStringArray(\"a\")\nrun/main_scene=\"res://menu.tscn\"\nconfig/features=PackedStringArray(\"4.7\")\n",
            GodotWrites.WithMainScene(Unset, "res://menu.tscn"));
        Assert.Equal("config_version=5\n\n[application]\n\nrun/main_scene=\"res://menu.tscn\"\nconfig/features=PackedStringArray(\"4.7\")\n",
            GodotWrites.WithMainScene("config_version=5\n\n[application]\n\nconfig/features=PackedStringArray(\"4.7\")\n", "res://menu.tscn"));
    }

    /// <summary>A new input action goes last in [input] with Godot's deadzone and no events; an event added goes last, the
    /// first after the bracket and each next after a comma on a line of its own, as Godot writes them; one taken out leaves
    /// the others so, the last leaving an empty list; and the action taken out goes whole, the section with it where it was
    /// the last, as Godot writes no empty section.</summary>
    [Fact]
    public void Input_actions_and_their_events_are_added_and_taken_out_as_godot_writes_them()
    {
        const string Project = "config_version=5\n\n[application]\n\nconfig/name=\"Hopper\"\n\n[rendering]\n\nx=1\n";
        string made = GodotWrites.WithAction(Project, "dash")!;
        Assert.Equal("config_version=5\n\n[application]\n\nconfig/name=\"Hopper\"\n\n[input]\n\ndash={\n\"deadzone\": 0.2,\n\"events\": []\n}\n\n[rendering]\n\nx=1\n", made);
        Assert.Null(GodotWrites.WithAction(made, "dash"));
        string space = GodotWrites.KeyEvent(32, 32);
        string left = GodotWrites.MouseButtonEvent(1);
        string one = GodotWrites.WithEvent(made, "dash", space)!;
        Assert.Contains($"\"events\": [{space}\n]\n}}", one, StringComparison.Ordinal);
        string two = GodotWrites.WithEvent(one, "dash", left)!;
        Assert.Contains($"\"events\": [{space}\n, {left}\n]\n}}", two, StringComparison.Ordinal);
        Assert.Equal(["Space (physical)", "Left Mouse Button"], Defined(two).EventsOf("dash"));
        Assert.Equal(one.Replace(space, left, StringComparison.Ordinal), GodotWrites.WithoutEvent(two, "dash", 0));
        Assert.Equal(made, GodotWrites.WithoutEvent(one, "dash", 0));
        Assert.Null(GodotWrites.WithoutEvent(one, "dash", 1));
        Assert.Null(GodotWrites.WithEvent(made, "jump", space));
        Assert.Equal(Project, GodotWrites.WithoutSetting(two, "input", "dash"));
        string both = GodotWrites.WithAction(made, "jump")!;
        Assert.Equal("config_version=5\n\n[application]\n\nconfig/name=\"Hopper\"\n\n[input]\n\njump={\n\"deadzone\": 0.2,\n\"events\": []\n}\n\n[rendering]\n\nx=1\n",
            GodotWrites.WithoutSetting(GodotWrites.WithEvent(both, "dash", space)!, "input", "dash"));
        Assert.Null(GodotWrites.WithoutSetting(Project, "input", "dash"));
    }

    /// <summary>The events the page gives are written as Godot's editor writes them, a key by its place and a button by its
    /// index, and read back as the names they were given by.</summary>
    [Fact]
    public void Each_event_the_page_gives_is_read_back_as_its_name()
    {
        foreach (string given in GodotInputEvents.Givable)
        {
            string text = GodotWrites.WithEvent(GodotWrites.WithAction("[input]\n\n", "a")!, "a", GodotInputEvents.Written(given)!)!;
            string shown = Defined(text).EventsOf("a").Single();
            Assert.True(shown == given || shown == $"{given} (physical)", $"{given} is read back as {shown}");
        }
        Assert.Null(GodotInputEvents.Written("Nothing"));
    }

    /// <summary>A new action's name is refused where Godot's Input Map refuses it: empty, holding a character it refuses, or
    /// one of Godot's own actions.</summary>
    [Fact]
    public void A_new_actions_name_is_refused_where_godot_refuses_it()
    {
        Assert.Null(GodotWrites.WhyNotActionName("dash"));
        Assert.NotNull(GodotWrites.WhyNotActionName(" "));
        Assert.NotNull(GodotWrites.WhyNotActionName("a/b"));
        Assert.NotNull(GodotWrites.WhyNotActionName("ui_accept"));
    }

    /// <summary>A layer's name is written over where it is named, put among the others by kind then number where it is not,
    /// as Godot saves them, and taken out where it is cleared, the section with it where it was the last.</summary>
    [Fact]
    public void A_layers_name_is_written_in_godots_order_and_taken_out_where_cleared()
    {
        const string Project = "config_version=5\n\n[layer_names]\n\n2d_physics/layer_1=\"World\"\n3d_render/layer_2=\"Sky\"\n\n[rendering]\n\nx=1\n";
        Assert.Equal(Project.Replace("World", "Ground", StringComparison.Ordinal), GodotWrites.WithLayerName(Project, "2d_physics", 1, "Ground"));
        Assert.Null(GodotWrites.WithLayerName(Project, "2d_physics", 1, "World"));
        Assert.Equal("config_version=5\n\n[layer_names]\n\n2d_physics/layer_1=\"World\"\n2d_physics/layer_3=\"Enemy\"\n3d_render/layer_2=\"Sky\"\n\n[rendering]\n\nx=1\n",
            GodotWrites.WithLayerName(Project, "2d_physics", 3, "Enemy"));
        Assert.Equal("config_version=5\n\n[layer_names]\n\n2d_render/layer_4=\"Back\"\n2d_physics/layer_1=\"World\"\n3d_render/layer_2=\"Sky\"\n\n[rendering]\n\nx=1\n",
            GodotWrites.WithLayerName(Project, "2d_render", 4, "Back"));
        Assert.Equal("config_version=5\n\n[layer_names]\n\n2d_physics/layer_1=\"World\"\n3d_render/layer_2=\"Sky\"\navoidance/layer_1=\"Crowd\"\n\n[rendering]\n\nx=1\n",
            GodotWrites.WithLayerName(Project, "avoidance", 1, "Crowd"));
        Assert.Equal("config_version=5\n\n[layer_names]\n\n3d_render/layer_2=\"Sky\"\n\n[rendering]\n\nx=1\n", GodotWrites.WithLayerName(Project, "2d_physics", 1, string.Empty));
        Assert.Null(GodotWrites.WithLayerName(Project, "2d_physics", 2, string.Empty));
        Assert.Equal("config_version=5\n\n[rendering]\n\nx=1\n",
            GodotWrites.WithLayerName(GodotWrites.WithLayerName(Project, "2d_physics", 1, string.Empty)!, "3d_render", 2, string.Empty));
        Assert.Equal("config_version=5\n\n[layer_names]\n\n3d_physics/layer_5=\"Hit\"\n\n[rendering]\n\nx=1\n",
            GodotWrites.WithLayerName("config_version=5\n\n[rendering]\n\nx=1\n", "3d_physics", 5, "Hit"));
    }

    private static GodotDefinitions Defined(string project) =>
        new GodotScenes("/game", new Dictionary<string, GodotFile>(), texts: new Dictionary<string, string> { [GodotDefinitions.ProjectRes] = project }).Defined;
}
