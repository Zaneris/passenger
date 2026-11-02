using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Godot;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Publish of a Godot project of GDScript alone, as a person exports it: the Export dialog lists its preset for this
/// system and, with no export templates in the run's Godot user folders, says which the export takes and greys Export, an
/// export asked for anyway refused saying so; with the archive <see cref="TemplatesVariable"/> names, Install Templates…
/// installs those the preset takes from it, through the dialog, into a Godot user folder of the test's own (deleted after),
/// and Export writes the game where the preset says, Godot's output in the Run window. Godot runs headless; without a
/// Godot 4, or for the second without the archive, each test skips saying why. Godot runs, so alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotExportTests
{
    /// <summary>The variable naming a .tpz of Godot's export templates of the Godot found, its version's, which the export
    /// test installs from.</summary>
    public const string TemplatesVariable = "PASSENGER_TEST_GODOT_TEMPLATES";

    internal const string Preset = "Here";

    private static readonly GodotFound? Godot = GodotExecutables.Find(null);

    /// <summary>This system's architecture as Godot's presets name it.</summary>
    private static string Arch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x86_64";

    /// <summary>The file the preset exports the game to, from the project's folder.</summary>
    private static string Output => OperatingSystem.IsMacOS() ? "build/Ticker.zip" : OperatingSystem.IsWindows() ? "build/Ticker.exe" : $"build/Ticker.{Arch}";

    /// <summary>The template the preset's release export takes.</summary>
    internal static string Template => OperatingSystem.IsMacOS() ? "macos.zip" : OperatingSystem.IsWindows() ? $"windows_release_{Arch}.exe" : $"linux_release.{Arch}";

    [AvaloniaFact]
    public void Without_templates_the_dialog_names_those_the_preset_takes_and_an_export_is_refused_saying_so()
    {
        Assert.SkipWhen(Godot is null || Godot.WhyNotScripts is not null, Skipped);
        (string folder, IReadOnlyDictionary<string, string> homes) = Game();
        MainWindow window = Open(folder, homes);
        try
        {
            Ui.Run(window, AppActions.FindAction);
            Avalonia.Headless.HeadlessWindowExtensions.KeyTextInput(window, "Publish");
            Ui.Press(window, "ENTER");
            GodotExportDialog dialog = Asked(window);
            Assert.Equal(Preset, dialog.Preset?.Name);
            Assert.True(dialog.TemplatesSaid.Contains($"has no {Template}", StringComparison.Ordinal), $"The dialog says of the templates: {dialog.TemplatesSaid}");
            Assert.Equal($"Install the templates {Preset} takes first", dialog.Problem);
            Assert.False(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Export").IsEnabled, "Export is enabled without the templates it takes");
            dialog.Close();

            BuildOutcome refused = Ui.Await(window.Builds.ExportGodotAsync(new GodotExportRequest(PathNames.Canonical(folder), Preset)), "the export refused");
            Assert.True(refused.Status == BuildStatus.Refused && refused.Message.Contains($"has no {Template} among its export templates", StringComparison.Ordinal),
                $"An export without its templates ended {refused.Status}: {refused.Message}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Templates_installed_from_an_archive_through_the_dialog_let_Export_write_the_game_where_the_preset_says()
    {
        Assert.SkipWhen(Godot is null || Godot.WhyNotScripts is not null, Skipped);
        string? archive = Environment.GetEnvironmentVariable(TemplatesVariable);
        Assert.SkipWhen(archive is null || !File.Exists(archive),
            $"Exports with Godot's export templates of the Godot found, from the .tpz {TemplatesVariable} names (Godot's download page offers it): {archive ?? "unset"}");
        (string folder, IReadOnlyDictionary<string, string> homes) = Game();
        MainWindow window = Open(folder, homes);
        try
        {
            window.PickFiles = (_, _, _) => Task.FromResult<IReadOnlyList<string>>([archive!]);
            Assert.Null(window.Actions.Run(AppActions.Publish));
            GodotExportDialog dialog = Asked(window);
            Ui.ClickButton(dialog, GodotExportDialog.InstallWords);
            Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), "the question before the templates are installed");
            ConfirmDialog asked = DialogButtonsTests.Check(window.OwnedWindows.OfType<ConfirmDialog>().Single());
            Ui.ClickButton(asked, "Install");
            Ui.WaitFor(() => dialog.IsRead && dialog.TemplatesSaid.StartsWith("Installed for Godot", StringComparison.Ordinal),
                () => $"the templates installed ({dialog.TemplatesSaid}; {window.Status})", TimeSpan.FromMinutes(3));

            Ui.ClickButton(dialog, "Export");
            Ui.WaitFor(() => window.LastPublish is not null, () => $"the export to start ({window.Status})");
            BuildOutcome exported = Ui.Await(window.LastPublish!, "the export", TimeSpan.FromMinutes(5));
            string game = Path.Combine(PathNames.Canonical(folder), Output.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(exported.Succeeded && File.Exists(game), $"The export ended {exported.Status} ({exported.Message}), the game {(File.Exists(game) ? "there" : "not there")}: {Run(window)}");
            Assert.Contains($"Exported {Preset} to", Run(window), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            // The templates are large: the run's copy goes with the test.
            Directory.Delete(Path.Combine(folder, "..", Path.GetFileName(folder) + "-home"), recursive: true);
        }
    }

    private static string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4 to run these tests"
        : $"The Godot found cannot run a Godot 4 project: {Godot.WhyNotScripts}";

    /// <summary>A Godot 4 project of GDScript alone with one export preset for this system, and user folders of its own.</summary>
    internal static (string Folder, IReadOnlyDictionary<string, string> Homes) Game()
    {
        string folder = TestFolders.Create("godot-export-");
        TestFolders.Write(folder, GodotProject.FileName, "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Ticker\"\n"
            + "run/main_scene=\"res://main.tscn\"\nconfig/features=PackedStringArray(\"4.7\")\n\n[rendering]\n\ntextures/vram_compression/import_etc2_astc=true\n");
        TestFolders.Write(folder, "main.tscn", "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://ticker.gd\" id=\"1\"]\n\n"
            + "[node name=\"Main\" type=\"Node\"]\nscript = ExtResource(\"1\")\n");
        TestFolders.Write(folder, "ticker.gd", "extends Node\n\nfunc _ready() -> void:\n\tprint(\"ready\")\n");
        string options = OperatingSystem.IsMacOS() ? "application/bundle_identifier=\"net.passenger-ide.ticker\"\ncodesign/codesign=0\nnotarization/notarization=0\n"
            : OperatingSystem.IsWindows() ? $"binary_format/architecture=\"{Arch}\"\napplication/modify_resources=false\ncodesign/enable=false\n"
            : $"binary_format/architecture=\"{Arch}\"\n";
        string platform = OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows Desktop" : "Linux";
        TestFolders.Write(folder, GodotExports.FileName, $"[preset.0]\n\nname=\"{Preset}\"\nplatform=\"{platform}\"\nrunnable=true\nexport_filter=\"all_resources\"\n"
            + $"include_filter=\"\"\nexclude_filter=\"\"\nexport_path=\"{Output}\"\n\n[preset.0.options]\n\n{options}");
        return (folder, GodotUserFolders.Under(Path.Combine(folder, "..", Path.GetFileName(folder) + "-home")));
    }

    private static MainWindow Open(string folder, IReadOnlyDictionary<string, string> homes)
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, GodotEnvironment = homes };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    private static GodotExportDialog Asked(MainWindow window)
    {
        Ui.WaitFor(() => window.GodotExportDialog is { IsRead: true }, "the Export dialog, the Godot found read", TimeSpan.FromMinutes(1));
        return DialogButtonsTests.Check(window.GodotExportDialog!);
    }

    private static string Run(MainWindow window)
    {
        window.BottomPanel.Run.Flush();
        return window.BottomPanel.Run.Document.Snapshot.ToString();
    }
}
