using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Install Passenger's Godot Addon on a project no Godot runs on: project.godot saved by Godot's editor while the
/// install's question waits keeps what that save wrote, a setting and a plugin it enabled, the addon enabled beside that
/// plugin. Needs no Godot.</summary>
public sealed class GodotAddonInstallTests
{
    private const string Settings = "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Spinner\"\n";

    [AvaloniaFact]
    public void Project_settings_saved_while_the_install_asks_keep_that_save_with_the_addon_enabled()
    {
        string folder = TestFolders.Create("godot-addon-install-");
        string project = TestFolders.Write(folder, GodotProject.FileName, Settings);
        TestFolders.Write(folder, Path.Combine("addons", "plays", "plugin.cfg"), "[plugin]\n\nname=\"Plays\"\ndescription=\"\"\nauthor=\"\"\nversion=\"1\"\nscript=\"plays.gd\"\n");
        TestFolders.Write(folder, Path.Combine("addons", "plays", "plays.gd"), "@tool\nextends EditorPlugin\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        List<string> asked = [];
        window.Confirm = (title, _, _) =>
        {
            asked.Add(title);
            if (title != "Install Passenger's Godot Addon?")
            {
                return Task.FromResult(false);
            }
            // As the person reads the question, Godot's editor saves the project's settings, a plugin enabled there.
            File.WriteAllText(project, Settings.Replace("config/name=\"Spinner\"\n", "config/name=\"Spinner\"\nconfig/description=\"Saved in Godot\"\n", StringComparison.Ordinal)
                + "\n[editor_plugins]\n\nenabled=PackedStringArray(\"res://addons/plays/plugin.cfg\")\n");
            return Task.FromResult(true);
        };

        Assert.Null(window.Actions.Run(AppActions.GodotAddon));
        Ui.WaitFor(() => window.Status?.Contains("Passenger's addon is installed", StringComparison.Ordinal) == true, () => $"the addon to be installed ({window.Status})");
        string text = File.ReadAllText(project);
        Assert.True(asked is ["Install Passenger's Godot Addon?"], $"The install was to ask once, and nothing else asked; it asked: {string.Join(" | ", asked)}");
        Assert.True(text.Contains("config/description=\"Saved in Godot\"\n", StringComparison.Ordinal)
            && text.Contains("enabled=PackedStringArray(\"res://addons/plays/plugin.cfg\", \"res://addons/passenger/plugin.cfg\")", StringComparison.Ordinal),
            $"project.godot was to keep what Godot's editor saved while the install asked, the addon enabled beside the plugin it enabled:\n{text}");
        Assert.True(File.Exists(Path.Combine(folder, "addons", "passenger", "plugin.cfg")), "The addon's plugin.cfg was not written");
    }
}
