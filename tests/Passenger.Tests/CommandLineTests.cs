using System.Buffers.Binary;
using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Core;

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
    [InlineData("--no-mcp --screenshot out.png", "--no-mcp stops a window listening for agents, and --screenshot's never does")]
    [InlineData("--mcp --no-mcp", "--no-mcp stops a window listening for agents, and --mcp's never does")]
    [InlineData("--mcp --size 1600x1000", "--size is for a --screenshot, and none is asked for")]
    [InlineData("--mcp --mcp-attach", "--mcp starts a Passenger of its own and --mcp-attach joins a running one; give one of them")]
    [InlineData("--mcp-attach notes.txt", "--mcp-attach finds the window by its folder, and 'notes.txt' is a file")]
    [InlineData("--mcp-attach missing", "'missing' is neither a folder nor a file")]
    [InlineData("--mcp-attach . --file notes.txt", "--mcp-attach joins a running Passenger's window and takes only its process id and a folder, not --file")]
    [InlineData("--mcp-attach --theme light", "--mcp-attach joins a running Passenger's window and takes only its process id and a folder, not --theme")]
    [InlineData("--mcp-attach 99999999999 .", "--mcp-attach takes the process id of a running Passenger, not '99999999999'")]
    public async Task A_malformed_value_or_a_flag_its_mode_does_not_take_ends_Passenger_with_exit_code_2_and_the_reason(string arguments, string reason)
    {
        (int code, string error) = await Run(arguments);

        Assert.True(code == 2 && error.StartsWith($"passenger: {reason}", StringComparison.Ordinal),
            $"'passenger {arguments}' was to be refused with exit code 2 saying \"{reason}\"; it exited {code} and wrote:\n{error}");
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

    [AvaloniaFact]
    public void A_solution_or_project_given_as_a_double_click_gives_it_opens_as_the_workspace_and_neither_as_text()
    {
        // Two solutions, so the folder alone would open Alpha: only the one named makes Zebra the workspace.
        File.WriteAllText(Path.Combine(folder, "Alpha.slnx"), "<Solution />\n");
        string zebra = Path.Combine(folder, "Zebra.slnx");
        File.WriteAllText(zebra, "<Solution />\n");
        string project = Path.Combine(folder, "sub", "Tool.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
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
    private async Task<(int Code, string Error)> Run(string arguments)
    {
        ProcessStartInfo start = new(Passenger)
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments.Split(' '))
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["PASSENGER_SETTINGS"] = settings;
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
            Assert.Fail($"'passenger {arguments}' was still running after two minutes rather than refused");
        }
        await output;
        return (process.ExitCode, (await error).Trim());
    }
}
