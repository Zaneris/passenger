using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Make Passenger Godot's Script Editor on a Godot project, against a stand-in Godot 4.7 .NET build that is
/// self-contained (its <c>_sc_</c> file), so its settings are a folder of the test's and never the person's: asked
/// first, it writes Passenger as the external editor of C# and of other scripts into the 4.7 settings file, taking the
/// rest from the 4.6 file Godot would read, and says what it set; asked again it says there is nothing to
/// change.</summary>
public sealed class GodotScriptEditorTests
{
    private static readonly string[] Keys =
    [
        GodotEditorSettings.ExternalEditor, GodotEditorSettings.CustomExecPath, GodotEditorSettings.CustomExecPathArgs,
        GodotEditorSettings.UseExternalEditor, GodotEditorSettings.ExecPath, GodotEditorSettings.ExecFlags,
    ];

    /// <summary>Passenger writes itself into Godot's own settings as its script editor when asked, and asks it once a
    /// state.</summary>
    [AvaloniaFact]
    public void Passenger_is_offered_as_godots_script_editor()
    {
        Flow.Step(nameof(Asked_for_it_Passenger_writes_itself_into_the_Godot_s_own_version_s_settings_keeping_the_rest_and_says_what_it_set),
            Asked_for_it_Passenger_writes_itself_into_the_Godot_s_own_version_s_settings_keeping_the_rest_and_says_what_it_set);
        OpenWindows.CloseAll();
        Flow.Step(nameof(A_Godot_projects_first_opening_asks_once_a_state_whether_passenger_is_its_script_editor_and_writes_it_on_yes),
            A_Godot_projects_first_opening_asks_once_a_state_whether_passenger_is_its_script_editor_and_writes_it_on_yes);
    }

    private void Asked_for_it_Passenger_writes_itself_into_the_Godot_s_own_version_s_settings_keeping_the_rest_and_says_what_it_set()
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
        string self = Product.Program;
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

    /// <summary>A Godot project's first opening in a window the person sees asks once a state, without taking the keys,
    /// whether Passenger is to be Godot's script editor, naming the file it writes: Not Now leaves Godot's settings as they
    /// were and another Godot project opened on that state asks nothing, while on a state of its own Write Settings writes
    /// Passenger into the Godot's settings and says so.</summary>
    private void A_Godot_projects_first_opening_asks_once_a_state_whether_passenger_is_its_script_editor_and_writes_it_on_yes()
    {
        string godot = StandInGodot(out string data);
        string state = TestFolders.Create("godot-offer-state-");
        MainWindow first = Kept(state, godot, NewGodotProject());
        ConfirmDialog asked = Asked(first);
        string written = Path.Combine(data, "editor_settings-4.7.tres");
        string words = string.Join(" ", asked.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.True(words.Contains(PathNames.Shown(written, null), StringComparison.Ordinal), $"The question did not name the file it writes: {words}");
        Ui.ClickButton(asked, "Not Now");
        Assert.False(File.Exists(written), "Not Now wrote Godot's settings");
        first.Close();

        MainWindow other = Kept(state, godot, NewGodotProject());
        Assert.True(other.Questions.Showing(Declared.GodotScriptEditor) is null && !other.Questions.WouldAsk(Declared.GodotScriptEditor),
            "Another Godot project opened on the same state asked again");
        other.Close();

        MainWindow fresh = Kept(TestFolders.Create("godot-offer-state-"), godot, NewGodotProject());
        Ui.ClickButton(Asked(fresh), "Write Settings");
        Ui.WaitFor(() => fresh.Status?.Contains("now opens its scripts in Passenger", StringComparison.Ordinal) == true,
            () => $"the status to say Godot's editor now opens its scripts in Passenger; it says {fresh.Status}");
        Assert.True(File.Exists(written) && Equals(GodotEditorSettings.Read(File.ReadAllText(written), Keys)[GodotEditorSettings.UseExternalEditor], true),
            $"Write Settings did not make Passenger Godot's script editor in {written}");
        fresh.Close();
    }

    /// <summary>The window's question whether Passenger is Godot's script editor, once it shows: unfocused, with no default button.</summary>
    private static ConfirmDialog Asked(MainWindow window)
    {
        Ui.WaitFor(() => window.Questions.Showing(Declared.GodotScriptEditor) is not null,
            () => $"the question whether Passenger is Godot's script editor, as the Godot project opens ({window.Status})");
        ConfirmDialog asked = window.Questions.Showing(Declared.GodotScriptEditor)!;
        Assert.False(asked.IsActive, "The question took the keys from what the person was doing");
        Assert.DoesNotContain(asked.GetVisualDescendants().OfType<Button>(), b => b.IsDefault);
        return asked;
    }

    /// <summary>A window the person sees that keeps its folders' state in <paramref name="state"/>, Settings naming the
    /// stand-in Godot, on a folder, once it is quiet.</summary>
    private static MainWindow Kept(string state, string godot, string folder)
    {
        MainWindow window = new() { Width = 1000, Height = 700, StateFolder = state, StartsLanguageServer = false, Autosaves = false, Unseen = false };
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
