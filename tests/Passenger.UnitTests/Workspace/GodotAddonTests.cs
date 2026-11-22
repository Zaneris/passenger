using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Installing Passenger's addon in a Godot project: its files under addons/passenger and project.godot enabling it
/// beside the plugins enabled there, the section made where there is none; nothing to do where the copy there is
/// Passenger's own and enabled; an earlier copy updated in the folder it is in, never a second one; a newer Passenger's
/// copy left as it is; another addon's files in that folder never written over, the addon going in a folder beside it.</summary>
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

    /// <summary>Passenger's addon is put in, updated and enabled once, after the plugins the project enables.</summary>
    [Fact]
    public void The_addon_is_put_in_and_enabled_once_as_godot_enables_plugins() => Cases.All(
        (nameof(A_project_without_it_takes_its_files_and_enables_it_after_the_plugins_it_enables),
            A_project_without_it_takes_its_files_and_enables_it_after_the_plugins_it_enables),
        (nameof(An_earlier_copy_is_updated_in_the_folder_it_is_in_and_enabled_once),
            An_earlier_copy_is_updated_in_the_folder_it_is_in_and_enabled_once),
        (nameof(A_newer_Passengers_copy_is_left_as_it_is), A_newer_Passengers_copy_is_left_as_it_is));

    /// <summary>Another plugin's files and folders are never taken for the addon's nor written over.</summary>
    [Fact]
    public void Another_plugins_files_and_folders_are_never_taken_for_the_addons() => Cases.All(
        (nameof(Another_addon_in_its_folder_keeps_its_files_and_the_addon_goes_in_a_folder_beside_it),
            Another_addon_in_its_folder_keeps_its_files_and_the_addon_goes_in_a_folder_beside_it),
        (nameof(A_file_of_one_of_its_names_with_other_words_is_never_written_over_and_each_taken_folder_is_passed),
            A_file_of_one_of_its_names_with_other_words_is_never_written_over_and_each_taken_folder_is_passed));

    private void A_project_without_it_takes_its_files_and_enables_it_after_the_plugins_it_enables()
    {
        GodotAddonInstall install = GodotAddon.Plan(Project, Carried, [], Enabled, _ => null);

        Assert.Equal(Path.Combine(Project, "addons", "passenger"), install.Folder);
        Assert.Equal(["plugin.cfg", "passenger.gd"], install.Files.Select(f => Path.GetFileName(f.Path)));
        Assert.True(install.Files.All(f => f.Made), "Both files were to be new");
        Assert.Equal(Enabled.Replace("PackedStringArray(\"res://addons/gut/plugin.cfg\")", "PackedStringArray(\"res://addons/gut/plugin.cfg\", \"res://addons/passenger/plugin.cfg\")",
            StringComparison.Ordinal), install.Project);
        Assert.False(install.Updated);
    }

    private void An_earlier_copy_is_updated_in_the_folder_it_is_in_and_enabled_once()
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

    private void A_newer_Passengers_copy_is_left_as_it_is()
    {
        string folder = Path.Combine(Project, "addons", "passenger");
        GodotPlugin plugin = new("res://addons/passenger/plugin.cfg", Path.Combine(folder, "plugin.cfg"), "Passenger", "3", "Passenger", "passenger.gd", false);

        GodotAddonInstall install = GodotAddon.Plan(Project, Carried, [plugin], Enabled, _ => "newer");

        Assert.True(install.Newer == "3" && install.Done, $"A newer copy was to be left: {install.Files.Count} files, project {install.Project}");
    }

    private void Another_addon_in_its_folder_keeps_its_files_and_the_addon_goes_in_a_folder_beside_it()
    {
        string theirs = Path.Combine(Project, "addons", "passenger");
        GodotPlugin cargo = new("res://addons/passenger/plugin.cfg", Path.Combine(theirs, "plugin.cfg"), "Cargo", "1", "Ann", "cargo.gd", true);
        string folder = Path.Combine(Project, "addons", "passenger_2");

        GodotAddonInstall install = GodotAddon.Plan(Project, Carried, [cargo], Enabled, path => path == cargo.File ? "[plugin]\n\nname=\"Cargo\"\n" : null);

        Assert.Equal(folder, install.Folder);
        Assert.Equal([(Path.Combine(folder, "plugin.cfg"), true), (Path.Combine(folder, "passenger.gd"), true)], install.Files.Select(f => (f.Path, f.Made)));
        Assert.Equal(["res://addons/gut/plugin.cfg", "res://addons/passenger_2/plugin.cfg"], GodotAddon.Enabled(install.Project!));
        Assert.False(install.Updated, "Another addon's files were taken for an earlier copy of Passenger's");
    }

    /// <summary>A file of one of the addon's names, in a folder no plugin.cfg makes an addon, is not known to be the addon's.</summary>
    private void A_file_of_one_of_its_names_with_other_words_is_never_written_over_and_each_taken_folder_is_passed()
    {
        HashSet<string> taken = [Path.Combine(Project, "addons", "passenger", "passenger.gd"), Path.Combine(Project, "addons", "passenger_2", "plugin.cfg")];

        GodotAddonInstall install = GodotAddon.Plan(Project, Carried, [], Enabled, path => taken.Contains(path) ? "extends Node\n" : null);

        Assert.Equal(Path.Combine(Project, "addons", "passenger_3"), install.Folder);
        Assert.True(install.Files.All(f => f.Made && !taken.Contains(f.Path)), $"A file another wrote was to be left: {string.Join(", ", install.Files.Select(f => f.Path))}");
    }

}
