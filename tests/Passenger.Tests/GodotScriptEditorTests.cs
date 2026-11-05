using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Make Passenger Godot's Script Editor on a Godot project, against a stand-in Godot 4.7 .NET build that is
/// self-contained (its <c>_sc_</c> file), so its settings are a folder of the test's and never the person's: asked first,
/// it writes Passenger as the external editor of C# and of other scripts into the 4.7 settings file, taking the rest from
/// the 4.6 file Godot would read, and says what it set; asked again it says there is nothing to change; where the
/// settings cannot be written, it shows the values to set by hand.</summary>
public sealed class GodotScriptEditorTests
{
    private static readonly string[] Keys =
    [
        GodotEditorSettings.ExternalEditor, GodotEditorSettings.CustomExecPath, GodotEditorSettings.CustomExecPathArgs,
        GodotEditorSettings.UseExternalEditor, GodotEditorSettings.ExecPath, GodotEditorSettings.ExecFlags,
    ];

    [AvaloniaFact]
    public void Asked_for_it_Passenger_writes_itself_into_the_Godot_s_own_version_s_settings_keeping_the_rest_and_says_what_it_set()
    {
        string godot = StandInGodot(out string data);
        File.WriteAllText(Path.Combine(data, "editor_settings-4.6.tres"), """
            [gd_resource type="EditorSettings" format=3]

            [resource]
            interface/editor/localization/editor_language = "fr"
            text_editor/external/exec_flags = "{file}"
            dotnet/editor/external_editor = 0

            """);
        MainWindow window = Opened(godot);
        List<string> asked = [];
        window.Confirm = (title, text, yes) =>
        {
            asked.Add($"{title}: {text} [{yes}]");
            return Task.FromResult(true);
        };

        Assert.Null(window.Actions.Run(AppActions.GodotScriptEditor));
        Ui.WaitFor(() => window.Status?.Contains("now opens its scripts in Passenger", StringComparison.Ordinal) == true,
            () => $"the status to say Godot's editor now opens its scripts in Passenger; it says {window.Status}");

        string written = Path.Combine(data, "editor_settings-4.7.tres");
        Assert.True(asked is [{ } question] && question.Contains(PathNames.Shown(written, null), StringComparison.Ordinal),
            $"Passenger was to ask once, naming the file it writes, before writing it; it asked: {string.Join(" | ", asked)}");
        string text = File.ReadAllText(written);
        IReadOnlyDictionary<string, object> read = GodotEditorSettings.Read(text, Keys);
        string self = Environment.ProcessPath!;
        Assert.True(Equals(read[GodotEditorSettings.ExternalEditor], GodotEditorSettings.Custom) && Equals(read[GodotEditorSettings.CustomExecPath], self)
            && Equals(read[GodotEditorSettings.CustomExecPathArgs], GodotEditorSettings.ScriptArguments) && Equals(read[GodotEditorSettings.UseExternalEditor], true)
            && Equals(read[GodotEditorSettings.ExecPath], self) && Equals(read[GodotEditorSettings.ExecFlags], GodotEditorSettings.TextArguments)
            && text.Contains("interface/editor/localization/editor_language = \"fr\"", StringComparison.Ordinal),
            $"Godot 4.7's settings were to make Passenger its script editor and keep the 4.6 file's other settings:\n{text}");

        Assert.Null(window.Actions.Run(AppActions.GodotScriptEditor));
        Ui.WaitFor(() => window.Status?.Contains("already opens its scripts in this Passenger", StringComparison.Ordinal) == true,
            () => $"the status to say Godot's editor already opens its scripts in Passenger; it says {window.Status}");
        Assert.True(asked.Count == 1, $"Asked again with nothing to change, Passenger asked: {string.Join(" | ", asked)}");
    }

    [AvaloniaFact]
    public void Where_the_settings_cannot_be_written_the_values_to_set_by_hand_are_shown()
    {
        string godot = StandInGodot(out string data);
        Directory.Delete(data);
        // A file where the settings' folder would be, so neither the folder nor the file can be made.
        File.WriteAllText(data, "not a folder");
        MainWindow window = Opened(godot);
        List<string> asked = [];
        window.Confirm = (title, text, yes) =>
        {
            asked.Add(title);
            return Task.FromResult(title.StartsWith("Make", StringComparison.Ordinal));
        };

        Assert.Null(window.Actions.Run(AppActions.GodotScriptEditor));
        Ui.WaitFor(() => window.Status?.Contains("set them in Godot's Editor Settings", StringComparison.Ordinal) == true,
            () => $"the status to say the settings are to be set in Godot's Editor Settings; it says {window.Status}");
        Assert.True(asked.SequenceEqual(["Make Passenger Godot's Script Editor?", "Set Godot's Script Editor by Hand"]),
            $"Passenger was to ask to write the settings, then show the values to set by hand; it asked: {string.Join(" | ", asked)}");
    }

    /// <summary>A Godot project's first opening in a window that keeps its folders' state offers Passenger as Godot's script
    /// editor in the banner over the editor; dismissed, the banner goes, and the folder opened again in another window on
    /// the same state offers it no more, while another Godot project is offered it still.</summary>
    [AvaloniaFact]
    public void A_Godot_projects_first_opening_offers_the_script_editor_in_a_banner_once_and_never_again_once_dismissed()
    {
        string godot = StandInGodot(out _);
        string state = TestFolders.Create("godot-offer-state-");
        string folder = NewGodotProject();
        MainWindow first = Kept(state, godot, folder);
        Ui.WaitFor(() => first.GodotSaid?.Contains("editor can open this project's scripts in Passenger", StringComparison.Ordinal) == true,
            () => $"the banner to offer Passenger as Godot's script editor (it says {first.GodotSaid ?? "nothing"}; {first.Status})");
        Assert.True(first.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "Make Passenger Godot's Script Editor…")),
            "The banner does not offer Make Passenger Godot's Script Editor…");
        Ui.ClickButton(first, "Dismiss");
        Ui.WaitFor(() => first.GodotSaid is null, () => $"the banner to go once dismissed (it says {first.GodotSaid})");
        first.Close();

        MainWindow again = Kept(state, godot, folder);
        Assert.True(again.GodotSaid is null, $"The folder opened again offered it once more: {again.GodotSaid}");
        again.Close();

        MainWindow other = Kept(state, godot, NewGodotProject());
        Ui.WaitFor(() => other.GodotSaid is not null, () => $"another Godot project to be offered it ({other.Status})");
        other.Close();
    }

    /// <summary>A window that keeps its folders' state in <paramref name="state"/>, Settings naming the stand-in Godot, on a
    /// folder, once it is quiet.</summary>
    private static MainWindow Kept(string state, string godot, string folder)
    {
        MainWindow window = new() { Width = 1000, Height = 700, StateFolder = state, StartsLanguageServer = false, Autosaves = false };
        window.Show();
        window.ApplySettings(window.Settings with { GodotExecutable = godot });
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    private static string NewGodotProject()
    {
        string folder = TestFolders.Create("godot-game-");
        TestFolders.Write(folder, GodotProject.FileName, "config_version=5\n\n[application]\n\nconfig/name=\"Spinner\"\n");
        return folder;
    }

    [AvaloniaFact]
    public void A_folder_with_no_Godot_project_refuses_it_saying_why()
    {
        string folder = TestFolders.Create("no-godot-");
        TestFolders.Write(folder, "notes.txt", "notes\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);

        Assert.Equal(Refusals.NoGodotProject, window.Actions.Run(AppActions.GodotScriptEditor));
    }

    /// <summary>A window on a Godot project with Settings naming the stand-in Godot.</summary>
    private static MainWindow Opened(string godot)
    {
        string folder = TestFolders.Create("godot-game-");
        TestFolders.Write(folder, GodotProject.FileName, "config_version=5\n\n[application]\n\nconfig/name=\"Spinner\"\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.ApplySettings(window.Settings with { GodotExecutable = godot });
        Ui.Settle();
        return window;
    }

    /// <summary>A Godot 4.7 .NET build as its files say it (an executable, GodotSharp's API and packages) that keeps its
    /// settings beside it, in <paramref name="data"/>; never run.</summary>
    private static string StandInGodot(out string data)
    {
        string folder = TestFolders.Create("godot-stand-in-");
        string executable = TestFolders.Write(folder, OperatingSystem.IsWindows() ? "Godot.exe" : "Godot", "never run\n");
        Directory.CreateDirectory(Path.Combine(folder, "GodotSharp", "Api"));
        TestFolders.Write(folder, Path.Combine("GodotSharp", "Tools", "nupkgs", "GodotSharp.4.7.2.nupkg"), string.Empty);
        TestFolders.Write(folder, "_sc_", string.Empty);
        data = Directory.CreateDirectory(Path.Combine(folder, "editor_data")).FullName;
        return executable;
    }
}
