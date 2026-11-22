using Passenger.App.Godot;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Godot's editor settings file as Passenger reads and writes its script editors: the values set where the file
/// gives them and added where it does not, every other line as Godot wrote it (sub-resources, values over several
/// lines, its line endings), a new file begun as Godot begins one, and the file found where the Godot of a version
/// reads and writes it on each system, the person's editor's GDScript port read from the one it reads.</summary>
public class GodotEditorSettingsTests
{
    private const string Written = """
        [gd_resource type="EditorSettings" format=3]

        [sub_resource type="InputEventKey" id="InputEventKey_os8ac"]
        command_or_control_autoremap = true
        keycode = 75

        [resource]
        resource_local_to_scene = false
        interface/editor/localization/editor_language = "auto"
        text_editor/external/exec_path = ""
        text_editor/external/exec_flags = "{file}"
        shortcuts = [{
        "name": "script_editor/find",
        "shortcuts": [Object(InputEventKey,"keycode":70)]
        }]
        text_editor/theme/highlighting/symbol_color = Color(0.67, 0.78, 1, 1)
        dotnet/editor/external_editor = 0
        dotnet/editor/custom_exec_path = ""
        interface/editor/display_scale = 0

        """;

    private static readonly string[] Keys =
    [
        GodotEditorSettings.ExternalEditor, GodotEditorSettings.CustomExecPath, GodotEditorSettings.CustomExecPathArgs,
        GodotEditorSettings.UseExternalEditor, GodotEditorSettings.ExecPath, GodotEditorSettings.ExecFlags,
    ];

    /// <summary>The editor's settings are read and written as Godot keeps them, every other line kept.</summary>
    [Fact]
    public void Editor_settings_are_read_and_written_as_godot_keeps_them() => Cases.All(
        (nameof(Passenger_s_values_replace_the_lines_that_give_them_and_the_rest_are_added_at_the_section_s_end_every_other_line_kept),
            Passenger_s_values_replace_the_lines_that_give_them_and_the_rest_are_added_at_the_section_s_end_every_other_line_kept),
        (nameof(A_path_with_backslashes_and_quotes_is_escaped_and_reads_back_as_it_was),
            A_path_with_backslashes_and_quotes_is_escaped_and_reads_back_as_it_was),
        (nameof(A_file_written_with_windows_line_endings_keeps_them), A_file_written_with_windows_line_endings_keeps_them),
        (nameof(With_no_file_yet_the_values_go_in_a_new_settings_file_as_Godot_begins_one),
            With_no_file_yet_the_values_go_in_a_new_settings_file_as_Godot_begins_one));

    /// <summary>The settings file and folder are those the found Godot's version reads, on each system.</summary>
    [Fact]
    public void The_editor_settings_files_and_folders_are_those_godot_reads() => Cases.All(
        (nameof(The_file_read_is_the_version_s_own_or_the_newest_older_one_and_the_file_written_the_version_s_own),
            The_file_read_is_the_version_s_own_or_the_newest_older_one_and_the_file_written_the_version_s_own));

    private void Passenger_s_values_replace_the_lines_that_give_them_and_the_rest_are_added_at_the_section_s_end_every_other_line_kept()
    {
        const string Executable = "/Applications/Passenger.app/Contents/MacOS/Passenger";

        string text = GodotEditorSettings.With(Written, GodotEditorSettings.ForPassenger(Executable));

        IReadOnlyDictionary<string, object> read = GodotEditorSettings.Read(text, Keys);
        Assert.True(Equals(read[GodotEditorSettings.ExternalEditor], GodotEditorSettings.Custom) && Equals(read[GodotEditorSettings.CustomExecPath], Executable)
            && Equals(read[GodotEditorSettings.CustomExecPathArgs], GodotEditorSettings.ScriptArguments) && Equals(read[GodotEditorSettings.UseExternalEditor], true)
            && Equals(read[GodotEditorSettings.ExecPath], Executable) && Equals(read[GodotEditorSettings.ExecFlags], GodotEditorSettings.TextArguments),
            $"Godot's editor would not open scripts in Passenger with what was written:\n{text}");
        string[] before = Written.Split('\n');
        string[] after = text.Split('\n');
        string[] lost = [.. before.Where(line => !Keys.Any(k => line.StartsWith(k + " ", StringComparison.Ordinal)) && !after.Contains(line))];
        Assert.True(lost.Length == 0, $"Writing Passenger's values took lines of Godot's away: {string.Join(" | ", lost)}\n{text}");
        Assert.True(text.IndexOf("dotnet/editor/external_editor = 6", StringComparison.Ordinal) < text.IndexOf("interface/editor/display_scale", StringComparison.Ordinal)
            && text.TrimEnd().EndsWith($"{GodotEditorSettings.UseExternalEditor} = true", StringComparison.Ordinal),
            $"A value the file gave was to be set on its own line, and one it lacked added at the section's end:\n{text}");
    }

    private void A_path_with_backslashes_and_quotes_is_escaped_and_reads_back_as_it_was()
    {
        const string Executable = @"C:\Users\Ted ""T""\AppData\Local\Passenger\current\Passenger.exe";

        string text = GodotEditorSettings.With(Written, GodotEditorSettings.ForPassenger(Executable));

        Assert.True(Equals(GodotEditorSettings.Read(text, Keys)[GodotEditorSettings.CustomExecPath], Executable)
            && text.Contains(@"dotnet/editor/custom_exec_path = ""C:\\Users\\Ted \""T\""\\AppData", StringComparison.Ordinal),
            $"Godot would read another program from the path written:\n{text}");
    }

    private void A_file_written_with_windows_line_endings_keeps_them()
    {
        string windows = Written.Replace("\n", "\r\n", StringComparison.Ordinal);

        string text = GodotEditorSettings.With(windows, GodotEditorSettings.ForPassenger("C:/Passenger/Passenger.exe"));

        Assert.True(!text.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n'),
            $"A line was written with another ending than the file's own:\n{text.Replace("\r", "\\r", StringComparison.Ordinal)}");
    }

    private void With_no_file_yet_the_values_go_in_a_new_settings_file_as_Godot_begins_one()
    {
        string text = GodotEditorSettings.With(string.Empty, GodotEditorSettings.ForPassenger("/opt/passenger/Passenger"));

        Assert.True(text.StartsWith("[gd_resource type=\"EditorSettings\" format=3]\n\n[resource]\n", StringComparison.Ordinal)
            && GodotEditorSettings.Read(text, Keys).Count == Keys.Length, $"A new settings file Godot would not read as its own:\n{text}");
    }

    private void The_file_read_is_the_version_s_own_or_the_newest_older_one_and_the_file_written_the_version_s_own() => Cases.Each<(string[] Present, int Minor, string? Read)>(
    [
        (new[] { "editor_settings-4.7.tres", "editor_settings-4.6.tres" }, 7, "editor_settings-4.7.tres"),
        (new[] { "editor_settings-4.5.tres", "editor_settings-4.tres" }, 7, "editor_settings-4.5.tres"),
        (new[] { "editor_settings-4.tres", "editor_settings-4.3.tres" }, 3, "editor_settings-4.3.tres"),
        (new[] { "editor_settings-4.8.tres" }, 7, null),
        (new string[0], 7, null),
    ], row => The_file_read_is_the_version_s_own_or_the_newest_older_one_and_the_file_written_the_version_s_own(row.Present, row.Minor, row.Read));

    private void The_file_read_is_the_version_s_own_or_the_newest_older_one_and_the_file_written_the_version_s_own(string[] present, int minor, string? read)
    {
        string folder = Path.Combine(Path.GetTempPath(), "godot-settings");
        HashSet<string> there = [.. present.Select(name => Path.Combine(folder, name))];

        (string? reads, string writes) = GodotEditorSettings.Files(folder, 4, minor, there.Contains);

        Assert.True(reads == (read is null ? null : Path.Combine(folder, read)) && writes == Path.Combine(folder, $"editor_settings-4.{minor}.tres"),
            $"Godot 4.{minor} with [{string.Join(", ", present)}] reads {read ?? "none"} and writes its own; Passenger would read {reads ?? "none"} and write {writes}");
    }

}
