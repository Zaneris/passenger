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

    /// <summary>Export presets, templates, input events and autoloads are read as Godot reads them, a template never
    /// leaving its folder.</summary>
    [Fact]
    public void Presets_templates_and_settings_are_read_as_godot_reads_them() => Cases.All(
        (nameof(Presets_are_read_with_their_platform_path_architecture_and_own_templates_and_name_the_templates_they_take),
            Presets_are_read_with_their_platform_path_architecture_and_own_templates_and_name_the_templates_they_take),
        (nameof(A_template_archives_entry_that_could_leave_its_folder_is_not_chosen_whatever_this_system_reads_it_as),
            A_template_archives_entry_that_could_leave_its_folder_is_not_chosen_whatever_this_system_reads_it_as));

    /// <summary>Project.godot's plugins, main scene, actions and layers are written as Godot writes them, comments
    /// kept.</summary>
    [Fact]
    public void Project_godot_is_written_as_godot_writes_it() => Cases.All(
        (nameof(The_plugins_enabled_are_written_over_those_listed_or_in_a_section_made_for_them),
            The_plugins_enabled_are_written_over_those_listed_or_in_a_section_made_for_them),
        (nameof(Input_actions_and_their_events_are_added_and_taken_out_as_godot_writes_them),
            Input_actions_and_their_events_are_added_and_taken_out_as_godot_writes_them),
        (nameof(A_comment_in_a_section_keeps_the_section_as_its_last_setting_goes),
            A_comment_in_a_section_keeps_the_section_as_its_last_setting_goes),
        (nameof(A_setting_written_twice_goes_from_every_line_that_gives_it), A_setting_written_twice_goes_from_every_line_that_gives_it),
        (nameof(A_new_actions_name_is_refused_where_godot_refuses_it), A_new_actions_name_is_refused_where_godot_refuses_it));

    private void Presets_are_read_with_their_platform_path_architecture_and_own_templates_and_name_the_templates_they_take()
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

    /// <summary>What an install takes from a template archive is named as one file of its folder, as Godot's templates are:
    /// an entry Windows would read as leaving the folder, a drive, or another file's stream is none of them on any system.</summary>
    private void A_template_archives_entry_that_could_leave_its_folder_is_not_chosen_whatever_this_system_reads_it_as()
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

    private void The_plugins_enabled_are_written_over_those_listed_or_in_a_section_made_for_them()
    {
        const string Project = "config_version=5\n\n[application]\n\nconfig/name=\"Hopper\"\n\n[rendering]\n\nx=1\n";
        string made = GodotWrites.WithPlugins(Project, ["res://addons/a/plugin.cfg"]);
        Assert.Equal("config_version=5\n\n[application]\n\nconfig/name=\"Hopper\"\n\n[editor_plugins]\n\nenabled=PackedStringArray(\"res://addons/a/plugin.cfg\")\n\n[rendering]\n\nx=1\n", made);
        string both = GodotWrites.WithPlugins(made, ["res://addons/a/plugin.cfg", "res://addons/b/plugin.cfg"]);
        Assert.Contains("enabled=PackedStringArray(\"res://addons/a/plugin.cfg\", \"res://addons/b/plugin.cfg\")\n\n[rendering]", both, StringComparison.Ordinal);
        Assert.Contains("enabled=PackedStringArray()\n", GodotWrites.WithPlugins(both, []), StringComparison.Ordinal);
    }

    /// <summary>A new input action goes last in [input] with Godot's deadzone and no events; an event added goes last, the
    /// first after the bracket and each next after a comma on a line of its own, as Godot writes them; one taken out leaves
    /// the others so, the last leaving an empty list; and the action taken out goes whole, the section with it where it was
    /// the last, as Godot writes no empty section.</summary>
    private void Input_actions_and_their_events_are_added_and_taken_out_as_godot_writes_them()
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

    /// <summary>A comment in a section whose last setting goes is the person's own, whether it says something of the section
    /// or of the next, so it and the section stay; a section holding nothing else goes, as Godot's save writes no empty one.</summary>
    private void A_comment_in_a_section_keeps_the_section_as_its_last_setting_goes() => Cases.Each<(string Text, string Left)>(
    [
        ("[input]\n\n; The player's keys.\ndash={\n\"deadzone\": 0.2,\n\"events\": []\n}\n\n[rendering]\n\nx=1\n", "[input]\n\n; The player's keys.\n\n[rendering]\n\nx=1\n"),
        ("[input]\n\ndash={\n\"deadzone\": 0.2,\n\"events\": []\n}\n\n; Keep this low.\n[rendering]\n\nx=1\n", "[input]\n\n\n; Keep this low.\n[rendering]\n\nx=1\n"),
        ("[input]\n\n# The player's keys.\ndash=1\n", "[input]\n\n# The player's keys.\n"),
        ("[input]\r\n\r\n; The player's keys.\r\ndash=1\r\n\r\n[rendering]\r\n", "[input]\r\n\r\n; The player's keys.\r\n\r\n[rendering]\r\n"),
    ], row => A_comment_in_a_section_keeps_the_section_as_its_last_setting_goes(row.Text, row.Left));

    private void A_comment_in_a_section_keeps_the_section_as_its_last_setting_goes(string text, string left)
    {
        Assert.Equal(left, GodotWrites.WithoutSetting(text, "input", "dash"));
        Assert.True(GodotFile.Parse(left).Sections[0].Properties.Count == 0, $"The setting taken out was to leave its section none:\n{left}");
    }

    /// <summary>Godot reads a key's last line, so an earlier one comes back into force once that goes, and it reads a tag
    /// written twice as one section: every line goes, whatever stands beside them.</summary>
    private void A_setting_written_twice_goes_from_every_line_that_gives_it() => Cases.Each<(string Text, string Left)>(
    [
        ("[input]\n\ndash=1\nrun=2\ndash=3\n", "[input]\n\nrun=2\n"),
        ("[input]\r\n\r\ndash=1\r\nrun=2\r\ndash=3\r\n", "[input]\r\n\r\nrun=2\r\n"),
        ("[input]\n\ndash=1\ndash=2\n\n[rendering]\n\nx=1\n", "[rendering]\n\nx=1\n"),
        ("[input]\n\ndash=1\nrun=2\n\n[rendering]\n\nx=1\n\n[input]\n\ndash=3\n", "[input]\n\nrun=2\n\n[rendering]\n\nx=1\n"),
    ], row => A_setting_written_twice_goes_from_every_line_that_gives_it(row.Text, row.Left));

    private void A_setting_written_twice_goes_from_every_line_that_gives_it(string text, string left)
    {
        Assert.Equal(left, GodotWrites.WithoutSetting(text, "input", "dash"));
        Assert.True(!GodotFile.Parse(left).Sections.Any(s => s.Tag == "input" && s.Property("dash") is not null),
            $"A line of the key was left, so the setting taken out is still in force:\n{left}");
    }

    /// <summary>A new action's name is refused where Godot's Input Map refuses it: empty, holding a character it refuses, or
    /// one of Godot's own actions.</summary>
    private void A_new_actions_name_is_refused_where_godot_refuses_it()
    {
        Assert.Null(GodotWrites.WhyNotActionName("dash"));
        Assert.NotNull(GodotWrites.WhyNotActionName(" "));
        Assert.NotNull(GodotWrites.WhyNotActionName("a/b"));
        Assert.NotNull(GodotWrites.WhyNotActionName("ui_accept"));
    }

    private static GodotDefinitions Defined(string project) =>
        new GodotScenes("/game", new Dictionary<string, GodotFile>(), texts: new Dictionary<string, string> { [GodotResPaths.Project] = project }).Defined;
}
