using System.Buffers.Binary;
using System.Diagnostics;
using Avalonia;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.State;
using Passenger.Core;
using SkiaSharp;

namespace Passenger.Tests;

/// <summary>The command line as a person types it: Passenger's own executable run with arguments, in a folder with a
/// file and a subfolder. A malformed value, or a flag the mode asked for does not take, ends it at once with exit code 2
/// and a line on standard error saying what is wrong, never a silent default or a stack trace. A solution or project it
/// names, as Windows does for one double-clicked, opens in the window as the workspace.</summary>
[AboutMacOS]
public sealed class CommandLineTests : IDisposable
{
    private static readonly string Passenger = Product.Program;

    private readonly string folder = TestFolders.Create("command-line-");
    private readonly string settings = TestFolders.Create("settings-");

    public CommandLineTests()
    {
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "one\ntwo\nthree\n");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
    }

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
    }

    /// <summary>The gate's malformed flags and variables as one table each: each row a value of the wrong form or a
    /// flag its mode does not take, ending Passenger with exit code 2 and the reason before anything starts, every row
    /// run and each that fails named.</summary>
    [Gate]
    [AvaloniaFact]
    public async Task The_gates_malformed_flags_and_variables_hold_as_one_table()
    {
        await Flow.StepAsync("a malformed value or a flag its mode does not take ends Passenger with exit code 2 and the reason", () => Cases.EachAsync<(string, string)>([("--screenshot out.png --scale NaN", "--scale takes a number from 0.5 to 8, not 'NaN'"), ("--screenshot out.png --size 400x300", "--size is at least the window's smallest, 640x500, not '400x300'"), ("--screenshot out.png --size 3000x3000 --scale 8", "--size 3000x3000 at --scale 8 is 24000x24000 pixels, more than 16384 a side"), ("--screenshot out.png --crop 0,0,2000,100", "--crop 0,0,2000,100 reaches past the 1600x1000 window"), ("--screenshot out.png --key 1000", "--key takes a keystroke such as cmd+shift+L, not '1000': unknown key '1000'"), ("--screenshot out.png --file Typo.cs", "--file 'Typo.cs' does not exist"), ("--screenshot missing/out.png", "--screenshot 'missing/out.png' would be written to a folder that does not exist"), ("notes.txt --caret +2:1 --screenshot out.png", "--caret takes LINE or LINE:COLUMN from 1, not '+2:1'"), (". notes.txt --screenshot out.png", "'notes.txt' would be a second folder or file after '.'; Passenger opens one, and more files with --file"), ("--screenshot out.png --theme dark --theme light", "--theme is given twice"), ("--screenshot out.png --file-icons default", "unknown flag '--file-icons'"), ("--no-mcp --screenshot out.png", "--no-mcp stops a window listening for agents, and --screenshot's lets in only its own --agent"), ("--screenshot out.png --agent no-agent-by-this-name", "--agent's program 'no-agent-by-this-name' is neither a file nor on the PATH"), ("--mcp --size 1600x1000", "--size is for a --screenshot, and none is asked for"), ("--mcp --mcp-attach", "--mcp starts a Passenger of its own and --mcp-attach joins a running one; give one of them"), ("--mcp-attach . --file notes.txt", "--mcp-attach joins a running Passenger's window and takes only its process id and a folder, not --file"), ("--mcp-attach 99999999999 .", "--mcp-attach takes the process id of a running Passenger, not '99999999999'"), ("--headless --mcp", "--headless is a person's window with nobody at it, a test run's"), (". --new-window", "--new-window opens a window with nothing open, and takes nothing else, not ."), ("--screenshot out.png --look sakura", "--look takes passenger, cubicle, high contrast, harbour"), ("--screenshot out.png --scheme relative/../folder::Dusk", "--scheme 'relative/../folder::Dusk' names no source Passenger reads")], row => Flow.StepApartAsync($"a malformed value or a flag its mode does not take ends Passenger with exit code 2 and the reason ({row})", () => new CommandLineTests(), t => t.A_malformed_value_or_a_flag_its_mode_does_not_take_ends_Passenger_with_exit_code_2_and_the_reason(row.Item1, row.Item2))));
        await Flow.StepAsync("a malformed variable ends Passenger with exit code 2 and the reason before anything starts", () => Cases.EachAsync<(string, string, string)>([(global::Passenger.Dap.AdapterEnvironment.EvaluationTimeoutVariable, "3s", Milliseconds), (global::Passenger.App.Mcp.AgentCall.EveryVariable, "-1000", Milliseconds), (global::Passenger.App.Mcp.McpHost.LanguageServerVariable, "no", "off or on"), (global::Passenger.Core.Waits.ScaleVariable, "0", "a number above 0")], row => Flow.StepApartAsync($"a malformed variable ends Passenger with exit code 2 and the reason before anything starts ({row})", () => new CommandLineTests(), t => t.A_malformed_variable_ends_Passenger_with_exit_code_2_and_the_reason_before_anything_starts(row.Item1, row.Item2, row.Item3))));
    }

    /// <summary>The gate's screenshot steps and a double-clicked solution as one flow: a crop and a click at the
    /// window's corner, a click by name, the window buttons, an agent through --mcp-attach, and a solution or project
    /// opened as the workspace.</summary>
    [Gate]
    [AvaloniaFact]
    public async Task The_gates_screenshot_steps_and_a_double_click_hold_as_one_flow()
    {
        await Flow.StepApartAsync("a screenshot named with the file before its folder and a click at the corner is taken at its crop", () => new CommandLineTests(), t => t.A_screenshot_named_with_the_file_before_its_folder_and_a_click_at_the_corner_is_taken_at_its_crop());
        await Flow.StepApartAsync("a screenshot step clicks a control by its name and a name no shown control has ends it with exit code 2", () => new CommandLineTests(), t => t.A_screenshot_step_clicks_a_control_by_its_name_and_a_name_no_shown_control_has_ends_it_with_exit_code_2());
        await Flow.StepApartAsync("a screenshot with window buttons shows the traffic lights a person sees on macOS and changes nothing elsewhere", () => new CommandLineTests(), t => t.A_screenshot_with_window_buttons_shows_the_traffic_lights_a_person_sees_on_macOS_and_changes_nothing_elsewhere());
        await Flow.StepApartAsync("a screenshot s agent uses the window s tools unasked through mcp attach and one that fails takes no picture", () => new CommandLineTests(), t => t.A_screenshot_s_agent_uses_the_window_s_tools_unasked_through_mcp_attach_and_one_that_fails_takes_no_picture());
        Flow.StepApart<CommandLineTests>("a solution or project given as a double click gives it opens as the workspace and neither as text", t => t.A_solution_or_project_given_as_a_double_click_gives_it_opens_as_the_workspace_and_neither_as_text());
    }

    /// <summary>A caret counted from zero without saying so, Run or Debug with no folder or in a mode with no window,
    /// and a variable of the wrong form or naming no HTTPS address end Passenger with exit code 2 and the reason, as
    /// one flow of tables, every row run and each that fails named.</summary>
    [AvaloniaFact]
    public async Task A_caret_a_run_or_a_variable_given_wrongly_ends_Passenger_with_exit_code_2_saying_why()
    {
        await Flow.StepAsync("a caret counted from zero takes zero based and one without it is refused with exit code 2", () => Cases.EachAsync<(string, string)>([("--screenshot out.png --zero-based", "--zero-based says --caret counts its line and column from 0, and no --caret is given"), ("notes.txt --caret 0:0 --screenshot out.png", "--caret takes LINE or LINE:COLUMN from 1, not '0:0'")], row => Flow.StepApartAsync($"a caret counted from zero takes zero based and one without it is refused with exit code 2 ({row})", () => new CommandLineTests(), t => t.A_caret_counted_from_zero_takes_zero_based_and_one_without_it_is_refused_with_exit_code_2(row.Item1, row.Item2))));
        await Flow.StepAsync("Run or debug with no folder before it or in a mode with no window of its own ends Passenger with exit code 2 and the reason", () => Cases.EachAsync<(string, string)>([("--run", "--run starts a run configuration of the folder Passenger opens, and none is given: passenger FOLDER --run [NAME], the folder first"), (". --run --debug", "--run and --debug each start a run configuration; give one of them"), ("--mcp . --run", "--mcp does not take --run; its agent starts a configuration with its run"), ("--mcp-attach . --debug", "--mcp-attach joins a running Passenger's window and takes only its process id and a folder, not --debug")], row => Flow.StepApartAsync($"Run or debug with no folder before it or in a mode with no window of its own ends Passenger with exit code 2 and the reason ({row})", () => new CommandLineTests(), t => t.Run_or_debug_with_no_folder_before_it_or_in_a_mode_with_no_window_of_its_own_ends_Passenger_with_exit_code_2_and_the_reason(row.Item1, row.Item2))));
        await Flow.StepAsync("a malformed variable of the language server's load or of a typing pause ends Passenger with exit code 2 and the reason", () => Cases.EachAsync<(string, string, string)>([(global::Passenger.App.CSharp.CSharpService.SlowLoadVariable, "5m", Milliseconds), (global::Passenger.App.Editors.TypingPauses.Variable, "0", Milliseconds)], row => Flow.StepApartAsync($"a malformed variable of the language server's load or of a typing pause ends Passenger with exit code 2 and the reason ({row})", () => new CommandLineTests(), t => t.A_malformed_variable_ends_Passenger_with_exit_code_2_and_the_reason_before_anything_starts(row.Item1, row.Item2, row.Item3))));
        await Flow.StepAsync("a variable naming no https address for a host ends Passenger with exit code 2 and the reason before anything starts", () => Cases.EachAsync<(string, string)>([(global::Passenger.Core.DotnetReleases.Variable, "releases"), (global::Passenger.App.Updates.ReleaseFeed.Variable, "http://192.0.2.10/repos/Zaneris/passenger/releases")], row => Flow.StepApartAsync($"a variable naming no https address for a host ends Passenger with exit code 2 and the reason before anything starts ({row})", () => new CommandLineTests(), t => t.A_variable_naming_no_https_address_for_a_host_ends_Passenger_with_exit_code_2_and_the_reason_before_anything_starts(row.Item1, row.Item2))));
    }

    /// <summary>A screenshot wears the look named, types a key as the character it makes, opens on the settings kept
    /// where its state is named, ends where a crop reaches past a dialog, and writes no file its steps type into but
    /// one a step saves, as one flow, each step the check one test made in an instance of its own.</summary>
    [AvaloniaFact]
    public async Task A_screenshot_wears_its_look_types_its_keys_on_the_settings_kept_and_writes_only_what_it_saves()
    {
        await Flow.StepApartAsync("look names a theme Passenger carries by its word and a screenshot wears it", () => new CommandLineTests(), t => t.Look_names_a_theme_Passenger_carries_by_its_word_and_a_screenshot_wears_it());
        await Flow.StepApartAsync("a crop reaching past the dialog the steps left in front ends the screenshot with exit code 2 saying so", () => new CommandLineTests(), t => t.A_crop_reaching_past_the_dialog_the_steps_left_in_front_ends_the_screenshot_with_exit_code_2_saying_so());
        await Flow.StepApartAsync("a screenshot s key takes a key as the character it types so meta comma opens settings", () => new CommandLineTests(), t => t.A_screenshot_s_key_takes_a_key_as_the_character_it_types_so_meta_comma_opens_settings());
        await Flow.StepApartAsync("a screenshot opens on the settings kept where its state is named and its keys type shifted characters", () => new CommandLineTests(), t => t.A_screenshot_opens_on_the_settings_kept_where_its_state_is_named_and_its_keys_type_shifted_characters());
        await Flow.StepApartAsync("a screenshot writes no file its steps type into but one a step saves and says so", () => new CommandLineTests(), t => t.A_screenshot_writes_no_file_its_steps_type_into_but_one_a_step_saves_and_says_so());
    }

    /// <summary>A start failing before its log is set up leaves why in the log folder and the next start says it once,
    /// and a relative state folder is named from where Passenger started or ends the start saying why, as one flow,
    /// each step the check one test made in an instance of its own.</summary>
    [AvaloniaFact]
    public async Task A_start_that_fails_says_why_once_and_a_relative_state_folder_is_where_it_started()
    {
        await Flow.StepApartAsync("a start failing before its log is set up leaves why in the log folder and the next start says it once", () => new CommandLineTests(), t => t.A_start_failing_before_its_log_is_set_up_leaves_why_in_the_log_folder_and_the_next_start_says_it_once());
        await Flow.StepApartAsync("a relative state folder is named from where Passenger started and one that cannot be made ends the start saying so", () => new CommandLineTests(), t => t.A_relative_state_folder_is_named_from_where_Passenger_started_and_one_that_cannot_be_made_ends_the_start_saying_so());
    }

    private async Task A_malformed_value_or_a_flag_its_mode_does_not_take_ends_Passenger_with_exit_code_2_and_the_reason(string arguments, string reason)
    {
        (int code, string error) = await Run(arguments);

        Assert.True(code == 2 && error.StartsWith($"passenger: {reason}", StringComparison.Ordinal),
            $"'passenger {arguments}' was to be refused with exit code 2 saying \"{reason}\"; it exited {code} and wrote:\n{error}");
    }

    private async Task A_caret_counted_from_zero_takes_zero_based_and_one_without_it_is_refused_with_exit_code_2(string arguments, string reason)
    {
        (int code, string error) = await Run(arguments);

        Assert.True(code == 2 && error.StartsWith($"passenger: {reason}", StringComparison.Ordinal),
            $"'passenger {arguments}' was to be refused with exit code 2 saying \"{reason}\"; it exited {code} and wrote:\n{error}");
    }

    /// <summary>A run configuration started from the command line needs the folder it is of, given first, and a mode with a
    /// window to run it in: refused at once otherwise, before any folder opens.</summary>
    private async Task Run_or_debug_with_no_folder_before_it_or_in_a_mode_with_no_window_of_its_own_ends_Passenger_with_exit_code_2_and_the_reason(string arguments, string reason)
    {
        (int code, string error) = await Run(arguments);

        Assert.True(code == 2 && error.StartsWith($"passenger: {reason}", StringComparison.Ordinal),
            $"'passenger {arguments}' was to be refused with exit code 2 saying \"{reason}\"; it exited {code} and wrote:\n{error}");
    }

    private const string Milliseconds = "a whole number of milliseconds above 0";

    /// <summary>A variable the IDE or its debug adapter reads is checked as Passenger starts, before any window or
    /// server, so a malformed one ends it at once rather than failing every Debug later.</summary>
    private async Task A_malformed_variable_ends_Passenger_with_exit_code_2_and_the_reason_before_anything_starts(string variable, string value, string takes)
    {
        (int code, string error) = await Run(["--mcp", "."], settings, new Dictionary<string, string> { [variable] = value });

        string reason = $"passenger: {variable} takes {takes}, not '{value}'";
        Assert.True(code == 2 && error.StartsWith(reason, StringComparison.Ordinal),
            $"Passenger with {variable}={value} was to be refused with exit code 2 saying \"{reason}\"; it exited {code} and wrote:\n{error}");
    }

    /// <summary>A variable naming a stand-in for a host Passenger asks is checked as Passenger starts: an address that is
    /// no URL, or plain http to another machine, ends it at once rather than failing when the host is first asked.</summary>
    private async Task A_variable_naming_no_https_address_for_a_host_ends_Passenger_with_exit_code_2_and_the_reason_before_anything_starts(string variable, string value)
    {
        (int code, string error) = await Run(["--mcp", "."], settings, new Dictionary<string, string> { [variable] = value });

        string reason = $"passenger: {variable} takes the https address (http on this machine) ";
        Assert.True(code == 2 && error.StartsWith(reason, StringComparison.Ordinal) && error.Contains($", not '{value}'", StringComparison.Ordinal),
            $"Passenger with {variable}={value} was to be refused with exit code 2 saying \"{reason}…, not '{value}'\"; it exited {code} and wrote:\n{error}");
    }

    private async Task A_screenshot_named_with_the_file_before_its_folder_and_a_click_at_the_corner_is_taken_at_its_crop()
    {
        (int code, string error) = await Run("--file notes.txt . --caret 2:3 --click 0,0 --size 800x500 --scale 1 --crop 0,0,400,250 --screenshot out.png --theme light");

        Assert.True(code == 0, $"The screenshot was not taken: exit code {code}\n{error}");
        // A PNG's width and height are the IHDR chunk's first words, big-endian, after the signature and the chunk's head.
        byte[] png = await File.ReadAllBytesAsync(Path.Combine(folder, "out.png"), TestContext.Current.CancellationToken);
        Assert.Equal((400, 250), (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20))));
    }

    /// <summary>A theme Passenger carries is named on the command line by its own word, as Passenger's own is, and a
    /// screenshot wears it.</summary>
    private async Task Look_names_a_theme_Passenger_carries_by_its_word_and_a_screenshot_wears_it()
    {
        Assert.Equal(new NamedTheme(SourcedTheme.BuiltIn("Cubicle")), LaunchOptions.Parse(["--look", "cubicle", "--screenshot", "out.png"]).Look);

        (int code, string error) = await Run(". --look cubicle --size 800x500 --scale 1 --screenshot cubicle.png");
        Assert.True(code == 0 && File.Exists(Path.Combine(folder, "cubicle.png")), $"The screenshot in Cubicle was not taken: exit code {code}\n{error}");
    }

    private async Task A_screenshot_step_clicks_a_control_by_its_name_and_a_name_no_shown_control_has_ends_it_with_exit_code_2()
    {
        (int code, string error) = await Run(". --click-name RunConfigButton --size 800x500 --scale 1 --screenshot out.png");
        Assert.True(code == 0 && File.Exists(Path.Combine(folder, "out.png")), $"The screenshot after a click on RunConfigButton was not taken: exit code {code}\n{error}");

        (code, error) = await Run(". --click-name Nothing --screenshot nothing.png");
        Assert.True(code == 2 && error.Contains("screenshot: --click-name Nothing: no control named Nothing is shown in the window", StringComparison.Ordinal)
            && !File.Exists(Path.Combine(folder, "nothing.png")),
            $"A click on a name no control has was to end the screenshot with exit code 2 saying so; it exited {code} and wrote:\n{error}");
    }

    /// <summary>A screenshot's picture is of the dialog its steps left in front, so a crop that fits the main window but
    /// reaches past that dialog ends it with exit code 2, saying so, and writes no picture.</summary>
    private async Task A_crop_reaching_past_the_dialog_the_steps_left_in_front_ends_the_screenshot_with_exit_code_2_saying_so()
    {
        string settingsKey = OperatingSystem.IsMacOS() ? "meta+comma" : "ctrl+alt+s";
        (int code, string error) = await Run($". --key {settingsKey} --size 1600x1000 --scale 1 --crop 0,950,800,40 --screenshot cut.png");
        Assert.True(code == 2 && error.Contains("screenshot: --crop 0,950,800,40 reaches past the", StringComparison.Ordinal)
            && error.Contains("window the steps left in front (Settings)", StringComparison.Ordinal) && !File.Exists(Path.Combine(folder, "cut.png")),
            $"A crop past the Settings dialog in front was to end the screenshot with exit code 2 saying so; it exited {code} and wrote:\n{error}");
    }

    /// <summary>A screenshot's --key takes a key a US keyboard types as that character, as the keymap's own keys are read:
    /// meta+, is Settings in the Mac's keymap (ctrl+, in VS Code's elsewhere, Visual Studio's having no such key there), and
    /// the steps after it go to Settings, whose search for IntelliJ shows its Keymap choice to click.</summary>
    private async Task A_screenshot_s_key_takes_a_key_as_the_character_it_types_so_meta_comma_opens_settings()
    {
        string state = TestFolders.Create("comma-state-");
        try
        {
            string settingsKey = "meta+,";
            if (!CommandKey.IsCmd)
            {
                settingsKey = "ctrl+,";
                await File.WriteAllTextAsync(Path.Combine(state, StateStore.SettingsFile), new System.Text.Json.Nodes.JsonObject
                {
                    ["version"] = global::Passenger.App.Settings.AppSettings.Upgrades.Count,
                    ["keymap"] = nameof(global::Passenger.Editor.KeymapPreset.VSCode),
                }.ToJsonString(), TestContext.Current.CancellationToken);
            }
            (int code, string error) = await Run([".", "--key", settingsKey, "--type", "IntelliJ", "--click-name", "KeymapChoice", "--size", "1600x1000", "--scale", "1",
                "--screenshot", "settings.png"], state);
            Assert.True(code == 0 && File.Exists(Path.Combine(folder, "settings.png")),
                $"--key {settingsKey} was to open Settings, whose search for IntelliJ shows its Keymap choice to click; the run exited {code} and wrote:\n{error}");
        }
        finally
        {
            TestFolders.Delete(state);
        }
    }

    /// <summary>The headless capture has none of the system's chrome, so on macOS the traffic lights are missing unless
    /// --window-buttons draws them, where macOS puts them on the merged bar, at any scale. Windows and Linux draw the bar's
    /// own buttons, so there the flag changes nothing. The places and colours are AppKit's own drawing of an active Dark
    /// Aqua window made as Avalonia makes Passenger's, measured in points from its top-left and in sRGB.</summary>
    private async Task A_screenshot_with_window_buttons_shows_the_traffic_lights_a_person_sees_on_macOS_and_changes_nothing_elsewhere()
    {
        (string Name, Point At, SKColor Centre)[] buttons =
        [
            ("close", new Point(16, 16), new SKColor(0xF3, 0x5B, 0x50)),
            ("minimise", new Point(39, 16), new SKColor(0xFD, 0xBE, 0x00)),
            ("zoom", new Point(62, 16), new SKColor(0x26, 0xC0, 0x00)),
        ];
        (int code, string error) = await Run(". --size 800x500 --scale 1 --crop 0,0,80,40 --theme dark --screenshot plain.png");
        Assert.True(code == 0, $"The screenshot without the window's buttons was not taken: exit code {code}\n{error}");
        using SKBitmap plain = SKBitmap.Decode(Path.Combine(folder, "plain.png"));
        foreach (int scale in (int[])[1, 2])
        {
            (code, error) = await Run($". --size 800x500 --scale {scale} --crop 0,0,80,40 --theme dark --window-buttons --screenshot buttons.png");
            Assert.True(code == 0, $"The screenshot with the window's buttons at scale {scale} was not taken: exit code {code}\n{error}");
            using SKBitmap shot = SKBitmap.Decode(Path.Combine(folder, "buttons.png"));
            if (!OperatingSystem.IsMacOS())
            {
                Assert.True(shot.Pixels.SequenceEqual(plain.Pixels), "On Windows and Linux the bar draws its own window buttons, yet --window-buttons changed the picture");
                return;
            }
            foreach ((string name, Point at, SKColor centre) in buttons)
            {
                SKColor seen = shot.GetPixel((int)(at.X * scale), (int)(at.Y * scale));
                Assert.True(Near(seen, centre),
                    $"At scale {scale} the {name} button's centre was to be macOS's {centre}, as a person sees it on an active window; the picture has {seen} there");
                SKColor without = plain.GetPixel((int)at.X, (int)at.Y);
                Assert.False(Near(without, centre), $"Without --window-buttons the {name} button's place was to be the bar's, as the headless capture draws no system chrome; it has {without}");
            }
        }

        static bool Near(SKColor a, SKColor b) => Math.Abs(a.Red - b.Red) <= 24 && Math.Abs(a.Green - b.Green) <= 24 && Math.Abs(a.Blue - b.Blue) <= 24;
    }

    /// <summary>--agent runs an agent during the capture, which joins the window as an agent joins a person's, through
    /// passenger --mcp-attach: here a shell script (PowerShell's on Windows) making one call at a time over JSON lines, as
    /// an agent's MCP client does. Typing into the editor is a tool a person's window asks about; nobody is at a capture,
    /// so it is used unasked, and the picture is taken once the agent has ended. An agent that fails ends the capture with
    /// exit code 2 and no picture.</summary>
    private async Task A_screenshot_s_agent_uses_the_window_s_tools_unasked_through_mcp_attach_and_one_that_fails_takes_no_picture()
    {
        string agent = TestFolders.Create("agent-");
        string state = TestFolders.Create("agent-state-");
        const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";
        const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
        const string Type = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"app_type","arguments":{"app":"ide","control":"CodeEditor","text":"agent "}}}""";
        const string Read = """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"editor_text","arguments":{"path":"notes.txt","from":2,"to":2}}}""";
        string answers = Path.Combine(agent, "answers.txt");
        string command;
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(agent, "agent.ps1");
            await File.WriteAllTextAsync(script, $$"""
                $start = New-Object System.Diagnostics.ProcessStartInfo $env:PASSENGER, "--mcp-attach $env:PASSENGER_PID"
                $start.UseShellExecute = $false
                $start.RedirectStandardInput = $true
                $start.RedirectStandardOutput = $true
                $server = [System.Diagnostics.Process]::Start($start)
                function Ask($line, $id) {
                    $server.StandardInput.WriteLine($line)
                    do { $answer = $server.StandardOutput.ReadLine() } until ($answer -match ('"id":' + $id))
                    $answer
                }
                $answers = @(Ask '{{Initialize}}' 1)
                $server.StandardInput.WriteLine('{{Initialized}}')
                $answers += Ask '{{Type}}' 2
                $answers += Ask '{{Read}}' 3
                $server.StandardInput.Close()
                $server.WaitForExit()
                Set-Content -Path '{{answers}}' -Value $answers
                """, TestContext.Current.CancellationToken);
            command = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\"";
        }
        else
        {
            string script = Path.Combine(agent, "agent.sh");
            await File.WriteAllTextAsync(script, $$"""
                cd '{{agent}}'
                mkfifo calls
                "$PASSENGER" --mcp-attach "$PASSENGER_PID" < calls > answers.txt &
                exec 3> calls
                answered() { until grep -q "\"id\":$1" answers.txt; do sleep 0.1; done; }
                echo '{{Initialize}}' >&3
                answered 1
                echo '{{Initialized}}' >&3
                echo '{{Type}}' >&3
                answered 2
                echo '{{Read}}' >&3
                answered 3
                exec 3>&-
                wait
                """, TestContext.Current.CancellationToken);
            command = $"sh \"{script}\"";
        }

        try
        {
            (int code, string error) = await Run(["notes.txt", "--caret", "2:1", "--agent", command, "--size", "800x500", "--scale", "1", "--screenshot", "out.png"], state);

            string answered = File.Exists(answers) ? await File.ReadAllTextAsync(answers, TestContext.Current.CancellationToken) : "(no answers)";
            Assert.True(code == 0 && File.Exists(Path.Combine(folder, "out.png")), $"The screenshot after its agent was not taken: exit code {code}\n{error}\nThe agent's answers:\n{answered}");
            Assert.True(answered.Contains("agent two", StringComparison.Ordinal) && !answered.Contains("\"isError\":true", StringComparison.Ordinal),
                $"The agent's app_type was to be used unasked in the capture's window, its editor_text then reading line 2 as \"agent two\"; it was answered:\n{answered}");

            string fails = OperatingSystem.IsWindows() ? "powershell -NoProfile -Command exit 3" : "sh -c \"exit 3\"";
            (code, error) = await Run([".", "--agent", fails, "--screenshot", "failed.png"], state);
            Assert.True(code == 2 && error.Contains($"screenshot: --agent '{fails}' ended with exit code 3", StringComparison.Ordinal) && !File.Exists(Path.Combine(folder, "failed.png")),
                $"An agent that failed was to end the screenshot with exit code 2 saying so, and take no picture; it exited {code} and wrote:\n{error}");
        }
        finally
        {
            // The window's socket's folder, named for its state, is outside it.
            TestFolders.Delete(global::Passenger.App.Mcp.McpHost.SocketFolderFor(state));
        }
    }

    /// <summary>A screenshot's --key presses a key as a keyboard does, the character it types with it: Shift+4 types "$" in
    /// the editor. Run where PASSENGER_SETTINGS names a folder whose settings.json turns Vim mode on, the window opens on
    /// those settings, so the same Shift+4 is Vim's "$", to the line's end, where x deletes the line's last letter.</summary>
    private async Task A_screenshot_opens_on_the_settings_kept_where_its_state_is_named_and_its_keys_type_shifted_characters()
    {
        string save = OperatingSystem.IsMacOS() ? "meta+S" : "control+S";
        string notes = Path.Combine(folder, "notes.txt");
        (int code, string error) = await Run(["notes.txt", "--caret", "1:1", "--key", "shift+4", "--key", save, "--size", "800x500", "--scale", "1", "--screenshot", "out.png"], settings);
        string typed = await File.ReadAllTextAsync(notes, TestContext.Current.CancellationToken);
        Assert.True(code == 0 && typed == "$one\ntwo\nthree\n", $"--key shift+4 was to type $ before \"one\", saved; the run exited {code} ({error}) and the file holds:\n{typed}");

        await File.WriteAllTextAsync(notes, "one\ntwo\nthree\n", TestContext.Current.CancellationToken);
        string state = TestFolders.Create("vim-state-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(state, StateStore.SettingsFile),
                new System.Text.Json.Nodes.JsonObject { ["version"] = global::Passenger.App.Settings.AppSettings.Upgrades.Count, ["vimMode"] = true }.ToJsonString(), TestContext.Current.CancellationToken);
            (code, error) = await Run(["notes.txt", "--caret", "1:1", "--key", "shift+4", "--key", "x", "--key", save, "--size", "800x500", "--scale", "1", "--screenshot", "out.png"], state);
            string edited = await File.ReadAllTextAsync(notes, TestContext.Current.CancellationToken);
            Assert.True(code == 0 && edited == "on\ntwo\nthree\n",
                $"With Vim mode on in the named state's settings.json, $ then x were to delete the first line's last letter; the run exited {code} ({error}) and the file holds:\n{edited}");
        }
        finally
        {
            TestFolders.Delete(state);
        }
    }

    /// <summary>A screenshot only looks: what its steps type stays in the editor, never written to the file, even when
    /// another program (here its --agent) writes the file meanwhile and the File Cache Conflict question takes the Escape
    /// and Enter typed after; File, Save typed as a step writes it, and the run says on standard error that it did.</summary>
    private async Task A_screenshot_writes_no_file_its_steps_type_into_but_one_a_step_saves_and_says_so()
    {
        string notes = Path.Combine(folder, "notes.txt");
        string agent = TestFolders.Create("disk-writer-");
        string state = TestFolders.Create("disk-writer-state-");
        string command;
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(agent, "write.ps1");
            await File.WriteAllTextAsync(script, "[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'notes.txt'), \"disk`n\")", TestContext.Current.CancellationToken);
            command = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\"";
        }
        else
        {
            string script = Path.Combine(agent, "write.sh");
            await File.WriteAllTextAsync(script, "printf 'disk\\n' > notes.txt\n", TestContext.Current.CancellationToken);
            command = $"sh \"{script}\"";
        }
        try
        {
            (int code, string error) = await Run(["notes.txt", "--caret", "1:1", "--type", "typed ", "--key", "shift+4", "--agent", command, "--wait", "3000",
                "--key", "escape", "--key", "enter", "--type", "more", "--size", "800x500", "--scale", "1", "--screenshot", "out.png"], state);
            string held = await File.ReadAllTextAsync(notes, TestContext.Current.CancellationToken);
            Assert.True(code == 0 && held == "disk\n" && !error.Contains("screenshot: wrote", StringComparison.Ordinal),
                $"What the steps typed was to stay out of notes.txt, which holds what the agent wrote; the run exited {code} ({error}) and the file holds:\n{held}");

            string save = OperatingSystem.IsMacOS() ? "meta+S" : "control+S";
            (code, error) = await Run(["notes.txt", "--caret", "1:1", "--type", "saved ", "--key", save, "--size", "800x500", "--scale", "1", "--screenshot", "out.png"], state);
            string saved = await File.ReadAllTextAsync(notes, TestContext.Current.CancellationToken);
            Assert.True(code == 0 && saved == "saved disk\n" && error.Contains("screenshot: wrote notes.txt", StringComparison.Ordinal),
                $"File, Save as a step was to write what was typed and say so; the run exited {code} ({error}) and the file holds:\n{saved}");
        }
        finally
        {
            TestFolders.Delete(agent);
            TestFolders.Delete(state);
            TestFolders.Delete(global::Passenger.App.Mcp.McpHost.SocketFolderFor(state));
        }
    }

    /// <summary>A login shell whose profile traces itself and then fails, as one may where Passenger starts from the Finder
    /// or the Dock with no terminal: the log's line saying why its environment was not read quotes what it printed with
    /// the secrets it exported masked, so a log attached to an issue holds none.</summary>
    [Fact]
    public async Task A_login_shell_that_fails_is_logged_with_the_secrets_its_profile_printed_masked()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows gives a program the user's whole environment, so no login shell is read there");
            return;
        }
        const string Token = "ghp_PassengerTraced0123456789";
        const string Key = "PassengerTracedAwsSecret0123";
        string shell = Path.Combine(folder, "traced-shell");
        await File.WriteAllTextAsync(shell, $"""
            #!/bin/sh
            echo "+/home/someone/.zprofile:3> export GITHUB_TOKEN={Token}" >&2
            echo "+/home/someone/.zprofile:4> export AWS_SECRET_ACCESS_KEY={Key}" >&2
            exit 1
            """, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(shell, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string state = TestFolders.Create("shell-state-");

        // Started with no terminal, as from the Finder: only then is the login shell read.
        (int code, string error) = await Run([".", "--size", "800x500", "--scale", "1", "--screenshot", "out.png"], state,
            new Dictionary<string, string> { ["SHELL"] = shell }, unset: ["TERM"]);
        Assert.True(code == 0, $"The screenshot with the failing login shell was not taken: exit code {code}\n{error}");
        string log = await File.ReadAllTextAsync(Path.Combine(state, "logs", "passenger.log"), TestContext.Current.CancellationToken);
        string? unread = log.Split('\n').FirstOrDefault(l => l.Contains("The login shell's environment was not read", StringComparison.Ordinal));
        Assert.True(unread?.Contains($"{shell} -l -i: it ended with exit code 1", StringComparison.Ordinal) == true,
            $"The log does not say the login shell's environment was not read for its exit code 1:\n{log}");
        string masked = global::Passenger.App.Containers.ContainerSecrets.Masked;
        Assert.True(!log.Contains(Token, StringComparison.Ordinal) && !log.Contains(Key, StringComparison.Ordinal)
            && unread.Contains($"GITHUB_TOKEN={masked}", StringComparison.Ordinal) && unread.Contains($"AWS_SECRET_ACCESS_KEY={masked}", StringComparison.Ordinal),
            $"The log quotes the secrets the profile printed, for anyone the log is sent to:\n{unread}");
    }

    private void A_solution_or_project_given_as_a_double_click_gives_it_opens_as_the_workspace_and_neither_as_text()
    {
        // Two solutions, so the folder alone would open Alpha: only the one named makes Zebra the workspace.
        File.WriteAllText(Path.Combine(folder, "Alpha.slnx"), "<Solution />\n");
        string zebra = Path.Combine(folder, "Zebra.slnx");
        File.WriteAllText(zebra, "<Solution />\n");
        string project = Path.Combine(folder, "sub", "Tool.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false };
        window.Show();

        window.Start(LaunchOptions.Parse([zebra]));
        Ui.Settle();
        Assert.True(window.Solution?.Path == PathNames.Canonical(zebra) && window.EditorArea.Active is null,
            $"Zebra.slnx, as Windows opens it, was to be the workspace with no file open; the solution is {window.Solution?.Path} and the editor shows {window.EditorArea.Active?.Path}");

        window.Start(LaunchOptions.Parse([project]));
        Ui.Settle();
        Assert.True(window.Folder == PathNames.Canonical(Path.GetDirectoryName(project)!) && window.WorkspaceModel?.ProjectFile == PathNames.Canonical(project)
            && window.EditorArea.Active is null,
            $"Tool.csproj was to open its folder with it as the workspace and no file open; the folder is {window.Folder}, the workspace is " +
            $"{window.WorkspaceModel?.OpenedAs} and the editor shows {window.EditorArea.Active?.Path}");
        window.Close();
    }

    /// <summary>Passenger run in the test's folder with the arguments split at spaces and nothing on standard input, its
    /// exit code and what it wrote to standard error; one still running after two minutes was not refused.</summary>
    /// <summary>On a Linux whose fontconfig lists no font (none installed, or its cache out of date), Passenger starts drawing
    /// in the fonts it carries and says why on standard error and in the log, naming fc-cache -f, where Avalonia's font
    /// manager ended it at once. The run's fontconfig is the test's own, its one folder of fonts empty.</summary>
    [Fact]
    public async Task On_a_Linux_whose_fontconfig_lists_no_font_a_start_draws_in_the_carried_fonts_and_says_why_naming_fc_cache()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "fontconfig gives the default font on Linux alone");
        string fonts = TestFolders.Create("no-fonts-");
        try
        {
            string configuration = Path.Combine(fonts, "fonts.conf");
            await File.WriteAllTextAsync(configuration, $"""
                <?xml version="1.0"?>
                <!DOCTYPE fontconfig SYSTEM "urn:fontconfig:fonts.dtd">
                <fontconfig><dir>{Directory.CreateDirectory(Path.Combine(fonts, "fonts")).FullName}</dir><cachedir>{Path.Combine(fonts, "cache")}</cachedir></fontconfig>
                """, TestContext.Current.CancellationToken);

            (int code, string error) = await Run(["notes.txt", "--size", "800x500", "--scale", "1", "--screenshot", "out.png"], settings,
                new Dictionary<string, string> { ["FONTCONFIG_FILE"] = configuration });

            Assert.True(code == 0 && File.Exists(Path.Combine(folder, "out.png")),
                $"With fontconfig listing no font, Passenger did not start: it exited {code}, saying: {error}");
            Assert.True(error.Contains($"passenger: {NoSystemFont.Said}", StringComparison.Ordinal),
                $"With fontconfig listing no font, Passenger did not say why on standard error: {error}");
            string log = await File.ReadAllTextAsync(Path.Combine(settings, "logs", "passenger.log"), TestContext.Current.CancellationToken);
            Assert.True(log.Contains($"Fonts: {NoSystemFont.Said}", StringComparison.Ordinal), "With fontconfig listing no font, Passenger's log did not say why");
            Assert.Contains("fc-cache -f", NoSystemFont.Said, StringComparison.Ordinal);
        }
        finally
        {
            TestFolders.Delete(fonts);
        }
    }

    /// <summary>A start that fails before its log is set up, made to throw in App.Initialize as a font or a theme read may,
    /// still ends as the runtime ends it, saying why on standard error, and keeps the exception in the log folder's
    /// crash.log; the next start lists it among the IDE's errors, its red mark shown for a click, writes it to the log and
    /// removes the file, and the start after says nothing more of it.</summary>
    private async Task A_start_failing_before_its_log_is_set_up_leaves_why_in_the_log_folder_and_the_next_start_says_it_once()
    {
        const string Failure = "a stand-in for a font that would not load";
        string crash = Path.Combine(settings, "logs", CrashFile.Name);
        string[] look = ["notes.txt", "--click-name", "ErrorsButton", "--size", "800x500", "--scale", "1", "--screenshot", "out.png"];

        (int code, string error) = await Run(["notes.txt", "--size", "800x500", "--scale", "1", "--screenshot", "out.png"], settings,
            new Dictionary<string, string> { [global::Passenger.App.App.FailVariable] = Failure });

        Assert.True(code is not (0 or 2) && error.Contains($"System.InvalidOperationException: {Failure}", StringComparison.Ordinal),
            $"A start made to throw in App.Initialize was to end as the runtime ends one, saying why; it exited {code} and wrote:\n{error}");
        Assert.True(File.Exists(crash), $"The start that failed before its log was set up left nothing in {Path.GetDirectoryName(crash)}");
        string kept = await File.ReadAllTextAsync(crash, TestContext.Current.CancellationToken);
        Assert.True(kept.Contains($"System.InvalidOperationException: {Failure}", StringComparison.Ordinal) && kept.Contains("App.Initialize", StringComparison.Ordinal),
            $"{CrashFile.Name} does not keep the exception the start ended on, thrown in App.Initialize:\n{kept}");

        (code, error) = await Run(look, settings);
        Assert.True(code == 0, $"The start after the failed one did not show the IDE's errors' mark for a click: it exited {code}, saying: {error}");
        Assert.False(File.Exists(crash), $"The start that said the crash left {CrashFile.Name} to say it again");
        string log = await File.ReadAllTextAsync(Path.Combine(settings, "logs", "passenger.log"), TestContext.Current.CancellationToken);
        Assert.True(log.Contains("ended on this error:", StringComparison.Ordinal) && log.Contains(Failure, StringComparison.Ordinal),
            "The start that said the crash did not write it to the log, which keeps it once its file is gone");

        (code, error) = await Run(look, settings);
        Assert.True(code == 2 && error.Contains("no control named ErrorsButton is shown in the window", StringComparison.Ordinal),
            $"The start after the one that said the crash still showed an IDE error: it exited {code}, saying: {error}");
    }

    /// <summary>Passenger started in the folder it opens, as a terminal in a repository starts it, works in the person's home
    /// once its command line is read, so a program started by its bare name (Avalonia's xdg-open) is never one the folder
    /// holds; a screenshot named from where it was started is still written there.</summary>
    [Fact]
    public async Task Started_in_the_folder_it_opens_Passenger_works_in_the_persons_home_once_its_command_line_is_read()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows tells another process's working folder only from inside its memory");
        string home = PathNames.Canonical(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        ProcessStartInfo start = ChildProcesses.Command(Passenger, ["--mcp", "."], folder);
        start.Environment[StateStore.Variable] = settings;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using Process passenger = ChildProcesses.Start(start);
        Task heard = ChildProcesses.Hear(passenger, _ => { }, _ => { });
        try
        {
            string? working = null;
            bool moved = await Waiting.UntilAsync(async () => (working = await WorkingFolderAsync(passenger.Id)) is { } now && PathNames.Same(now, home),
                TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(100));
            Assert.True(moved, $"Passenger started in {folder} works in {working ?? "a folder lsof did not tell"}, not the home folder {home}");
        }
        finally
        {
            passenger.StandardInput.Close();
            if (!await Waiting.Within(passenger.WaitForExitAsync(TestContext.Current.CancellationToken), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken))
            {
                ChildProcesses.Kill(passenger);
            }
            await heard;
        }

        (int code, string error) = await Run(". --size 800x500 --scale 1 --screenshot out.png");
        Assert.True(code == 0 && File.Exists(Path.Combine(folder, "out.png")), $"The screenshot named from the folder it was started in was not written there: exit code {code}\n{error}");
    }

    /// <summary>A state folder PASSENGER_SETTINGS names by a relative path is the one it names from the folder Passenger was
    /// started in, not from the home folder it works in once its command line is read; one that cannot be made ends the
    /// start saying so, as a malformed value does, never with a crash.</summary>
    private async Task A_relative_state_folder_is_named_from_where_Passenger_started_and_one_that_cannot_be_made_ends_the_start_saying_so()
    {
        string[] screenshot = [".", "--size", "800x500", "--scale", "1", "--screenshot", "out.png"];
        (int code, string error) = await Run(screenshot, "kept-here");
        Assert.True(code == 0 && Directory.Exists(Path.Combine(folder, "kept-here", "logs")),
            $"PASSENGER_SETTINGS=kept-here, started in {folder}, did not keep the state there: exit code {code}\n{error}");

        File.WriteAllText(Path.Combine(folder, "a-file"), "not a folder\n");
        (code, error) = await Run(screenshot, Path.Combine("a-file", "state"));
        Assert.True(code == 2 && error.Contains($"{Path.Combine(PathNames.Canonical(folder), "a-file", "state")}, which {StateStore.Variable} names, could not be made", StringComparison.Ordinal)
            && !error.Contains("Unhandled exception", StringComparison.Ordinal),
            $"A state folder that cannot be made, inside a file, did not end the start saying so: exit code {code}\n{error}");
    }

    /// <summary>The folder a process works in, as the system tells it: Linux's /proc link, macOS's lsof.</summary>
    private static async Task<string?> WorkingFolderAsync(int process)
    {
        if (OperatingSystem.IsLinux())
        {
            return Directory.ResolveLinkTarget($"/proc/{process}/cwd", returnFinalTarget: false)?.FullName;
        }
        ChildResult told = await ChildProcesses.RunAsync(ChildProcesses.Command("/usr/sbin/lsof", ["-a", "-p", process.ToString(System.Globalization.CultureInfo.InvariantCulture), "-d", "cwd", "-Fn"]),
            limit: TimeSpan.FromSeconds(30), cancel: TestContext.Current.CancellationToken);
        return told.Output.Split('\n').FirstOrDefault(line => line.StartsWith('n'))?[1..] is { Length: > 0 } named ? PathNames.Canonical(named) : null;
    }

    private Task<(int Code, string Error)> Run(string arguments) => Run(arguments.Split(' '), settings);

    /// <summary>Passenger run in the test's folder with these arguments and its state in a folder of their own, with these
    /// variables in its environment over the run's, and those unset taken out of it.</summary>
    private async Task<(int Code, string Error)> Run(IReadOnlyList<string> arguments, string state, IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyList<string>? unset = null)
    {
        ProcessStartInfo start = new(Passenger)
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment[StateStore.Variable] = state;
        foreach ((string variable, string value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[variable] = value;
        }
        foreach (string variable in unset ?? [])
        {
            start.Environment.Remove(variable);
        }
        using Process process = Process.Start(start)!;
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            // Not the runtime's tree kill of this process's own child, which can hang it (ChildProcesses.Kill says why).
            global::Passenger.Core.ChildProcesses.Kill(process);
            Assert.Fail($"'passenger {string.Join(' ', arguments)}' was still running after two minutes rather than refused");
        }
        await output;
        return (process.ExitCode, (await error).Trim());
    }
}
