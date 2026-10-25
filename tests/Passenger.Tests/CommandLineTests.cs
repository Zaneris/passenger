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
public sealed class CommandLineTests : IDisposable
{
    private static readonly string Passenger = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Passenger.exe" : "Passenger");

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

    [Theory]
    [InlineData("--screenshot out.png --scale NaN", "--scale takes a number from 0.5 to 8, not 'NaN'")]
    [InlineData("--screenshot out.png --size 1600xInfinity", "--size takes WIDTHxHEIGHT, each a number of 0 or more, not '1600xInfinity'")]
    [InlineData("--screenshot out.png --wait 1e300", "--wait takes milliseconds from 1 to 600000, not '1e300'")]
    [InlineData("--screenshot out.png --size 400x300", "--size is at least the window's smallest, 640x500, not '400x300'")]
    [InlineData("--screenshot out.png --size 3000x3000 --scale 8", "--size 3000x3000 at --scale 8 is 24000x24000 pixels, more than 16384 a side")]
    [InlineData("--screenshot out.png --crop 0,0,2000,100", "--crop 0,0,2000,100 reaches past the 1600x1000 window")]
    [InlineData("--screenshot out.png --click 1600,10", "--click 1600,10 is outside the 1600x1000 window")]
    [InlineData("--screenshot out.png --key 1000", "--key takes a keystroke such as cmd+shift+L, not '1000': unknown key '1000'")]
    [InlineData("--screenshot out.png --click-name Run.Button", "--click-name takes a control's x:Name, not 'Run.Button'")]
    [InlineData("--screenshot out.png --file Typo.cs", "--file 'Typo.cs' does not exist")]
    [InlineData("--screenshot out.png --file sub", "--file 'sub' is a folder; a folder to open is the PATH")]
    [InlineData("--screenshot missing/out.png", "--screenshot 'missing/out.png' would be written to a folder that does not exist")]
    [InlineData("notes.txt --caret +2:1 --screenshot out.png", "--caret takes LINE or LINE:COLUMN from 1, not '+2:1'")]
    [InlineData("--screenshot out.png --caret 3:1", "--caret needs a file to put the caret in, the PATH or a --file")]
    [InlineData(". notes.txt --screenshot out.png", "'notes.txt' would be a second folder or file after '.'; Passenger opens one, and more files with --file")]
    [InlineData("--screenshot out.png --theme dark --theme light", "--theme is given twice")]
    [InlineData("--screenshot out.png --file-icons default", "unknown flag '--file-icons'")]
    [InlineData("--no-mcp --screenshot out.png", "--no-mcp stops a window listening for agents, and --screenshot's lets in only its own --agent")]
    [InlineData("--screenshot out.png --agent no-agent-by-this-name", "--agent's program 'no-agent-by-this-name' is neither a file nor on the PATH")]
    [InlineData(". --agent ./notes.txt", "--agent is for a --screenshot, and none is asked for")]
    [InlineData("--mcp --no-mcp", "--no-mcp stops a window listening for agents, and --mcp's never does")]
    [InlineData("--mcp --size 1600x1000", "--size is for a --screenshot, and none is asked for")]
    [InlineData(". --window-buttons", "--window-buttons is for a --screenshot, and none is asked for")]
    [InlineData("--mcp --mcp-attach", "--mcp starts a Passenger of its own and --mcp-attach joins a running one; give one of them")]
    [InlineData("--mcp-attach notes.txt", "--mcp-attach finds the window by its folder, and 'notes.txt' is a file")]
    [InlineData("--mcp-attach missing", "'missing' is neither a folder nor a file")]
    [InlineData("--mcp-attach . --file notes.txt", "--mcp-attach joins a running Passenger's window and takes only its process id and a folder, not --file")]
    [InlineData("--mcp-attach --theme light", "--mcp-attach joins a running Passenger's window and takes only its process id and a folder, not --theme")]
    [InlineData("--mcp-attach 99999999999 .", "--mcp-attach takes the process id of a running Passenger, not '99999999999'")]
    [InlineData("--headless --mcp", "--headless is a person's window with nobody at it, a test run's")]
    public async Task A_malformed_value_or_a_flag_its_mode_does_not_take_ends_Passenger_with_exit_code_2_and_the_reason(string arguments, string reason)
    {
        (int code, string error) = await Run(arguments);

        Assert.True(code == 2 && error.StartsWith($"passenger: {reason}", StringComparison.Ordinal),
            $"'passenger {arguments}' was to be refused with exit code 2 saying \"{reason}\"; it exited {code} and wrote:\n{error}");
    }

    private const string Milliseconds = "a whole number of milliseconds above 0";

    /// <summary>A variable the IDE or its debug adapter reads is checked as Passenger starts, before any window or
    /// server, so a malformed one ends it at once rather than failing every Debug later.</summary>
    [Theory]
    [InlineData(global::Passenger.Dap.AdapterEnvironment.EvaluationTimeoutVariable, "3s", Milliseconds)]
    [InlineData(global::Passenger.Dap.AdapterEnvironment.EvaluationTimeoutVariable, "0", Milliseconds)]
    [InlineData(global::Passenger.App.Mcp.AgentCall.EveryVariable, "-1000", Milliseconds)]
    [InlineData(global::Passenger.App.Mcp.McpHost.LanguageServerVariable, "no", "off or on")]
    [InlineData(global::Passenger.Core.Waits.ScaleVariable, "0", "a number above 0")]
    public async Task A_malformed_variable_ends_Passenger_with_exit_code_2_and_the_reason_before_anything_starts(string variable, string value, string takes)
    {
        (int code, string error) = await Run(["--mcp", "."], settings, new Dictionary<string, string> { [variable] = value });

        string reason = $"passenger: {variable} takes {takes}, not '{value}'";
        Assert.True(code == 2 && error.StartsWith(reason, StringComparison.Ordinal),
            $"Passenger with {variable}={value} was to be refused with exit code 2 saying \"{reason}\"; it exited {code} and wrote:\n{error}");
    }

    [Fact]
    public async Task A_screenshot_named_with_the_file_before_its_folder_and_a_click_at_the_corner_is_taken_at_its_crop()
    {
        (int code, string error) = await Run("--file notes.txt . --caret 2:3 --click 0,0 --size 800x500 --scale 1 --crop 0,0,400,250 --screenshot out.png --theme light");

        Assert.True(code == 0, $"The screenshot was not taken: exit code {code}\n{error}");
        // A PNG's width and height are the IHDR chunk's first words, big-endian, after the signature and the chunk's head.
        byte[] png = await File.ReadAllBytesAsync(Path.Combine(folder, "out.png"), TestContext.Current.CancellationToken);
        Assert.Equal((400, 250), (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20))));
    }

    [Fact]
    public async Task A_screenshot_step_clicks_a_control_by_its_name_and_a_name_no_shown_control_has_ends_it_with_exit_code_2()
    {
        (int code, string error) = await Run(". --click-name RunConfigButton --size 800x500 --scale 1 --screenshot out.png");
        Assert.True(code == 0 && File.Exists(Path.Combine(folder, "out.png")), $"The screenshot after a click on RunConfigButton was not taken: exit code {code}\n{error}");

        (code, error) = await Run(". --click-name Nothing --screenshot nothing.png");
        Assert.True(code == 2 && error.Contains("screenshot: --click-name Nothing: no control named Nothing is shown in the window", StringComparison.Ordinal)
            && !File.Exists(Path.Combine(folder, "nothing.png")),
            $"A click on a name no control has was to end the screenshot with exit code 2 saying so; it exited {code} and wrote:\n{error}");
    }

    /// <summary>The headless capture has none of the system's chrome, so on macOS the traffic lights are missing unless
    /// --window-buttons draws them, where macOS puts them on the merged bar, at any scale. Windows and Linux draw the bar's
    /// own buttons, so there the flag changes nothing. The places and colours are AppKit's own drawing of an active Dark
    /// Aqua window made as Avalonia makes Passenger's, measured in points from its top-left and in sRGB.</summary>
    [Fact]
    public async Task A_screenshot_with_window_buttons_shows_the_traffic_lights_a_person_sees_on_macOS_and_changes_nothing_elsewhere()
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
    [Fact]
    public async Task A_screenshot_s_agent_uses_the_window_s_tools_unasked_through_mcp_attach_and_one_that_fails_takes_no_picture()
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

    [AvaloniaFact]
    public void A_solution_or_project_given_as_a_double_click_gives_it_opens_as_the_workspace_and_neither_as_text()
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
    private Task<(int Code, string Error)> Run(string arguments) => Run(arguments.Split(' '), settings);

    /// <summary>Passenger run in the test's folder with these arguments and its state in a folder of their own, with these
    /// variables in its environment over the run's.</summary>
    private async Task<(int Code, string Error)> Run(IReadOnlyList<string> arguments, string state, IReadOnlyDictionary<string, string>? environment = null)
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
