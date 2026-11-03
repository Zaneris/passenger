using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Installing Passenger's addon in a Godot project: its files under addons/passenger and project.godot enabling it
/// beside the plugins enabled there, the section made where there is none; nothing to do where the copy there is
/// Passenger's own and enabled; an earlier copy updated in the folder it is in, never a second one; a newer Passenger's
/// copy left as it is.</summary>
public class GodotAddonTests
{
    private static readonly string Project = Path.Combine(Path.GetTempPath(), "game");

    private static readonly (string Name, string Text)[] Carried =
    [
        ("plugin.cfg", "[plugin]\n\nname=\"Passenger\"\ndescription=\"\"\nauthor=\"Passenger\"\nversion=\"2\"\nscript=\"passenger.gd\"\n"),
        ("passenger.gd", "@tool\nextends EditorPlugin\n"),
    ];

    private const string Enabled = """
        config_version=5

        [application]

        config/name="Spinner"

        [editor_plugins]

        enabled=PackedStringArray("res://addons/gut/plugin.cfg")

        """;

    [Fact]
    public void A_project_without_it_takes_its_files_and_enables_it_after_the_plugins_it_enables()
    {
        GodotAddonInstall install = GodotAddon.Plan(Project, Carried, [], Enabled, _ => null);

        Assert.Equal(Path.Combine(Project, "addons", "passenger"), install.Folder);
        Assert.Equal(["plugin.cfg", "passenger.gd"], install.Files.Select(f => Path.GetFileName(f.Path)));
        Assert.True(install.Files.All(f => f.Made), "Both files were to be new");
        Assert.Equal(Enabled.Replace("PackedStringArray(\"res://addons/gut/plugin.cfg\")", "PackedStringArray(\"res://addons/gut/plugin.cfg\", \"res://addons/passenger/plugin.cfg\")",
            StringComparison.Ordinal), install.Project);
        Assert.False(install.Updated);
    }

    [Fact]
    public void A_project_enabling_no_plugin_takes_the_section_where_Godot_puts_it()
    {
        string text = "config_version=5\n\n[application]\n\nconfig/name=\"Spinner\"\n";

        GodotAddonInstall install = GodotAddon.Plan(Project, Carried, [], text, _ => null);

        Assert.Equal(["res://addons/passenger/plugin.cfg"], GodotAddon.Enabled(install.Project!));
        Assert.StartsWith(text.TrimEnd('\n'), install.Project, StringComparison.Ordinal);
    }

    [Fact]
    public void Its_own_copy_enabled_leaves_nothing_to_do_whatever_its_line_endings()
    {
        string folder = Path.Combine(Project, "addons", "passenger");
        GodotPlugin plugin = new("res://addons/passenger/plugin.cfg", Path.Combine(folder, "plugin.cfg"), "Passenger", "2", "Passenger", "passenger.gd", true);
        string text = Enabled.Replace("\"res://addons/gut/plugin.cfg\"", "\"res://addons/passenger/plugin.cfg\"", StringComparison.Ordinal);

        GodotAddonInstall install = GodotAddon.Plan(Project, Carried, [plugin], text,
            path => Carried.Single(c => c.Name == Path.GetFileName(path)).Text.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.True(install.Done, $"Nothing was to be written: {string.Join(", ", install.Files.Select(f => f.Path))}; {install.Project}");
    }

    [Fact]
    public void An_earlier_copy_is_updated_in_the_folder_it_is_in_and_enabled_once()
    {
        string folder = Path.Combine(Project, "addons", "passenger_ide");
        GodotPlugin plugin = new("res://addons/passenger_ide/plugin.cfg", Path.Combine(folder, "plugin.cfg"), "Passenger", "1", "Passenger", "passenger.gd", false);
        GodotPlugin other = new("res://addons/gut/plugin.cfg", Path.Combine(Project, "addons", "gut", "plugin.cfg"), "Gut", "9", null, "gut.gd", true);

        GodotAddonInstall install = GodotAddon.Plan(Project, Carried, [other, plugin], Enabled, path => Path.GetFileName(path) == "plugin.cfg" ? "[plugin]\nversion=\"1\"\n" : null);

        Assert.True(install.Updated, "The earlier copy was to be updated");
        Assert.Equal(folder, install.Folder);
        Assert.Equal([(Path.Combine(folder, "plugin.cfg"), false), (Path.Combine(folder, "passenger.gd"), true)], install.Files.Select(f => (f.Path, f.Made)));
        Assert.Equal(["res://addons/gut/plugin.cfg", "res://addons/passenger_ide/plugin.cfg"], GodotAddon.Enabled(install.Project!));
    }

    [Fact]
    public void A_newer_Passengers_copy_is_left_as_it_is()
    {
        string folder = Path.Combine(Project, "addons", "passenger");
        GodotPlugin plugin = new("res://addons/passenger/plugin.cfg", Path.Combine(folder, "plugin.cfg"), "Passenger", "3", "Passenger", "passenger.gd", false);

        GodotAddonInstall install = GodotAddon.Plan(Project, Carried, [plugin], Enabled, _ => "newer");

        Assert.True(install.Newer == "3" && install.Done, $"A newer copy was to be left: {install.Files.Count} files, project {install.Project}");
    }

    [Fact]
    public void Another_plugin_named_Passenger_is_not_taken_for_it()
    {
        GodotPlugin plugin = new("res://addons/mine/plugin.cfg", Path.Combine(Project, "addons", "mine", "plugin.cfg"), "Passenger", "1", null, "mine.gd", true);

        GodotAddonInstall install = GodotAddon.Plan(Project, Carried, [plugin], Enabled, _ => null);

        Assert.Equal(Path.Combine(Project, "addons", "passenger"), install.Folder);
        Assert.False(install.Updated);
    }
}
