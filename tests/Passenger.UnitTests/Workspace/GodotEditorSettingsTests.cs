using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Godot's editor settings file as Passenger reads and writes its script editors: the values set where the file
/// gives them and added where it does not, every other line as Godot wrote it (sub-resources, values over several
/// lines, its line endings), a new file begun as Godot begins one, and the file found where the Godot of a version
/// reads and writes it on each system.</summary>
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

    [Fact]
    public void The_script_editors_values_are_read_from_the_resource_section_by_their_kinds()
    {
        IReadOnlyDictionary<string, object> read = GodotEditorSettings.Read(Written, Keys);

        Assert.True(read.Count == 4 && Equals(read[GodotEditorSettings.ExternalEditor], 0L) && Equals(read[GodotEditorSettings.CustomExecPath], string.Empty)
            && Equals(read[GodotEditorSettings.ExecFlags], "{file}") && Equals(read[GodotEditorSettings.ExecPath], string.Empty),
            $"Passenger would misread what Godot's editor opens scripts in: {string.Join(", ", read.Select(r => $"{r.Key}={r.Value}"))}");
    }

    [Fact]
    public void Passenger_s_values_replace_the_lines_that_give_them_and_the_rest_are_added_at_the_section_s_end_every_other_line_kept()
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

    /// <summary>Godot reads a key set twice as its last line says, so a value written at an earlier line would be reported
    /// set and do nothing.</summary>
    [Fact]
    public void A_key_the_file_sets_twice_is_set_at_its_last_line_which_is_the_one_Godot_reads()
    {
        const string Twice = """
            [gd_resource type="EditorSettings" format=3]

            [resource]
            text_editor/external/use_external_editor = false
            interface/editor/display_scale = 0
            text_editor/external/use_external_editor = false

            """;

        string text = GodotEditorSettings.With(Twice, [new(GodotEditorSettings.UseExternalEditor, true)]);

        Assert.Equal(true, GodotEditorSettings.Read(text, [GodotEditorSettings.UseExternalEditor])[GodotEditorSettings.UseExternalEditor]);
        Assert.Equal("""
            [gd_resource type="EditorSettings" format=3]

            [resource]
            text_editor/external/use_external_editor = false
            interface/editor/display_scale = 0
            text_editor/external/use_external_editor = true

            """, text);
    }

    [Fact]
    public void A_path_with_backslashes_and_quotes_is_escaped_and_reads_back_as_it_was()
    {
        const string Executable = @"C:\Users\Ted ""T""\AppData\Local\Passenger\current\Passenger.exe";

        string text = GodotEditorSettings.With(Written, GodotEditorSettings.ForPassenger(Executable));

        Assert.True(Equals(GodotEditorSettings.Read(text, Keys)[GodotEditorSettings.CustomExecPath], Executable)
            && text.Contains(@"dotnet/editor/custom_exec_path = ""C:\\Users\\Ted \""T\""\\AppData", StringComparison.Ordinal),
            $"Godot would read another program from the path written:\n{text}");
    }

    [Fact]
    public void A_file_written_with_windows_line_endings_keeps_them()
    {
        string windows = Written.Replace("\n", "\r\n", StringComparison.Ordinal);

        string text = GodotEditorSettings.With(windows, GodotEditorSettings.ForPassenger("C:/Passenger/Passenger.exe"));

        Assert.True(!text.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n'),
            $"A line was written with another ending than the file's own:\n{text.Replace("\r", "\\r", StringComparison.Ordinal)}");
    }

    [Fact]
    public void Setting_the_values_twice_writes_the_file_as_the_first_time_did()
    {
        IReadOnlyList<KeyValuePair<string, object>> values = GodotEditorSettings.ForPassenger("/opt/passenger/Passenger");
        string once = GodotEditorSettings.With(Written, values);

        string twice = GodotEditorSettings.With(once, values);

        Assert.True(once == twice, $"A second set-up wrote the file again differently:\n{once}\n---\n{twice}");
    }

    [Fact]
    public void With_no_file_yet_the_values_go_in_a_new_settings_file_as_Godot_begins_one()
    {
        string text = GodotEditorSettings.With(string.Empty, GodotEditorSettings.ForPassenger("/opt/passenger/Passenger"));

        Assert.True(text.StartsWith("[gd_resource type=\"EditorSettings\" format=3]\n\n[resource]\n", StringComparison.Ordinal)
            && GodotEditorSettings.Read(text, Keys).Count == Keys.Length, $"A new settings file Godot would not read as its own:\n{text}");
    }

    [Theory]
    [InlineData(new[] { "editor_settings-4.7.tres", "editor_settings-4.6.tres" }, 7, "editor_settings-4.7.tres")]
    [InlineData(new[] { "editor_settings-4.5.tres", "editor_settings-4.tres" }, 7, "editor_settings-4.5.tres")]
    [InlineData(new[] { "editor_settings-4.tres" }, 7, "editor_settings-4.tres")]
    [InlineData(new[] { "editor_settings-4.8.tres" }, 7, null)]
    [InlineData(new string[0], 7, null)]
    public void The_file_read_is_the_version_s_own_or_the_newest_older_one_and_the_file_written_the_version_s_own(string[] present, int minor, string? read)
    {
        string folder = Path.Combine(Path.GetTempPath(), "godot-settings");
        HashSet<string> there = [.. present.Select(name => Path.Combine(folder, name))];

        (string? reads, string writes) = GodotEditorSettings.Files(folder, 4, minor, there.Contains);

        Assert.True(reads == (read is null ? null : Path.Combine(folder, read)) && writes == Path.Combine(folder, $"editor_settings-4.{minor}.tres"),
            $"Godot 4.{minor} with [{string.Join(", ", present)}] reads {read ?? "none"} and writes its own; Passenger would read {reads ?? "none"} and write {writes}");
    }

    [Fact]
    public void The_settings_folder_is_the_user_s_on_each_system_and_editor_data_beside_a_self_contained_Godot()
    {
        Dictionary<string, string> variables = new()
        {
            ["APPDATA"] = @"C:\Users\ted\AppData\Roaming",
            ["HOME"] = "/home/ted",
            ["XDG_CONFIG_HOME"] = "/home/ted/.xdg",
        };
        string? Variable(string name) => variables.GetValueOrDefault(name);
        string bundle = Path.Combine("/Applications", "Godot_mono.app", "Contents", "MacOS", "Godot");

        string? windows = GodotEditorSettings.Folder(@"C:\Godot\Godot.exe", windows: true, macOS: false, Variable, _ => false);
        string? macOS = GodotEditorSettings.Folder(bundle, windows: false, macOS: true, Variable, _ => false);
        string? xdg = GodotEditorSettings.Folder("/opt/godot/Godot", windows: false, macOS: false, Variable, _ => false);
        variables.Remove("XDG_CONFIG_HOME");
        string? linux = GodotEditorSettings.Folder("/opt/godot/Godot", windows: false, macOS: false, Variable, _ => false);
        string godot = Path.GetDirectoryName("/opt/godot/Godot")!;
        string applications = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(bundle))))!;
        string? contained = GodotEditorSettings.Folder("/opt/godot/Godot", windows: false, macOS: false, Variable, path => path == Path.Combine(godot, "_sc_"));
        string? containedBundle = GodotEditorSettings.Folder(bundle, windows: false, macOS: true, Variable, path => path == Path.Combine(applications, "._sc_"));

        Assert.True(windows == Path.Combine(@"C:\Users\ted\AppData\Roaming", "Godot") && macOS == Path.Combine("/home/ted", "Library", "Application Support", "Godot")
            && xdg == Path.Combine("/home/ted/.xdg", "godot") && linux == Path.Combine("/home/ted", ".config", "godot")
            && contained == Path.Combine(godot, "editor_data") && containedBundle == Path.Combine(applications, "editor_data"),
            $"Passenger would write settings no Godot reads: {windows}, {macOS}, {xdg}, {linux}, {contained}, {containedBundle}");
    }
}
