using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A Passenger nobody sees (an agent's own, a screenshot's, a test's window) runs every Godot game it starts headless,
/// whatever its configuration and the Godot setting say, while a person's window runs its game as configured. Godot is a
/// stand-in that keeps the arguments it was started with and ends, so no window opens here whatever Passenger asks of it,
/// and no Godot is needed; the game is one of GDScript alone, which needs no build.</summary>
public sealed class GodotUnseenTests : McpRig
{
    private readonly string game = PathNames.Canonical(TestFolders.Create("godot-unseen-"));

    private readonly StandInGodot godot = StandInGodot.Make();

    public GodotUnseenTests()
    {
        File.WriteAllText(Path.Combine(game, "project.godot"), "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Lonely\"\n"
            + "run/main_scene=\"res://main.tscn\"\nconfig/features=PackedStringArray(\"4.7\")\n");
        File.WriteAllText(Path.Combine(game, "main.tscn"), "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://main.gd\" id=\"1\"]\n\n"
            + "[node name=\"Main\" type=\"Node\"]\nscript = ExtResource(\"1\")\n");
        File.WriteAllText(Path.Combine(game, "main.gd"), "extends Node\n");
        DeleteAtEnd(game);
        DeleteAtEnd(godot.Folder);
    }

    /// <summary>The generated configuration of the game.</summary>
    private const string Lonely = "Lonely";

    /// <summary>An agent's own Passenger, started outside a test run (no PASSENGER_START), runs the game with no window though
    /// neither its configuration nor the Godot setting asks it to: headless, or on macOS through Godot's embedded display, a
    /// background process whose frames reach no screen.</summary>
    [Fact]
    public async Task An_agents_own_passenger_runs_a_godot_game_with_no_window_though_nothing_it_was_given_says_so()
    {
        await using McpClient client = await Start(["--mcp", game], environment: new() { [McpProxy.StartVariable] = null, [GodotExecutables.Variable] = godot.Executable }, languageServer: false);
        await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["godotExecutable"] = godot.Executable } });
        JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = Lonely, ["waitSeconds"] = 60 });
        IReadOnlyList<string> started = await godot.StartedAsync($"the game's start ({ran})");
        string unseen = GodotExecutables.DrawsWithoutWindow("4.7.2") ? GodotExecutables.Embedded : GodotExecutables.Headless;
        Assert.True(started.Contains(unseen), $"The agent's own Passenger started Godot with a window, as {string.Join(' ', started)}");
    }

    /// <summary>A screenshot, started outside a test run, whose agent runs the game: Godot is started headless, so no game
    /// window comes up on the screen of whoever took the picture.</summary>
    [Fact]
    public async Task A_screenshots_agent_runs_a_godot_game_headless_with_no_window_on_the_screen()
    {
        string agent = TestFolders.Create("unseen-agent-");
        DeleteAtEnd(agent);
        string[] calls =
        [
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = McpTools.SettingsSet, arguments = new { changes = new { godotExecutable = godot.Executable } } } }),
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = McpTools.Run, arguments = new { configuration = Lonely, waitSeconds = 60 } } }),
        ];
        (string command, string answers) = await ScreenshotAgent.WriteAsync(agent, calls);
        string picture = Path.Combine(OwnState, "unseen.png");
        Process passenger = StartPassenger([game, "--agent", command, "--size", "800x500", "--scale", "1", "--screenshot", picture],
            environment: new() { [McpProxy.StartVariable] = null, [GodotExecutables.Variable] = godot.Executable }, languageServer: false);
        Assert.True(await Waiting.Within(passenger.WaitForExitAsync(Cancel), TimeSpan.FromMinutes(5), Cancel), $"The screenshot whose agent runs the game never ended.{Said()}");
        string answered = File.Exists(answers) ? await File.ReadAllTextAsync(answers, Cancel) : "(no answers)";
        Assert.True(passenger.ExitCode == 0, $"The screenshot ended with exit code {passenger.ExitCode}; its agent was answered:\n{answered}{Said()}");
        IReadOnlyList<string> started = await godot.StartedAsync($"the game's start (the agent was answered:\n{answered})");
        Assert.True(started.Contains(GodotExecutables.Headless), $"The screenshot started Godot with a window, as {string.Join(' ', started)}");
    }

    /// <summary>A person's window runs the game as its configuration says, with a window; a window nobody sees runs it headless,
    /// once where its configuration says --headless itself.</summary>
    [AvaloniaFact]
    public void A_persons_window_runs_the_game_with_a_window_and_one_nobody_sees_runs_it_headless_once()
    {
        MainWindow persons = Open(unseen: false);
        try
        {
            Ui.Launched(persons, persons.RunAsync(), "the game's start from a person's window");
            Ui.WaitFor(() => godot.Started is not null, "the stand-in Godot's start from a person's window");
            IReadOnlyList<string> started = godot.Started!;
            Assert.False(started.Contains(GodotExecutables.Headless), $"A person's window started its game headless, as {string.Join(' ', started)}");
        }
        finally
        {
            persons.Close();
        }
        Ui.Settle();

        MainWindow unseen = Open(unseen: true);
        try
        {
            RunConfiguration generated = unseen.Configurations.Single(c => c.Generated && c.Name == Lonely);
            foreach (RunConfiguration chosen in (RunConfiguration[])[generated, generated with { Name = "Lonely headless", Generated = false, Arguments = GodotExecutables.Headless }])
            {
                godot.Forget();
                if (!chosen.Generated)
                {
                    unseen.SetConfigurations([chosen], chosen.Name);
                }
                Ui.Launched(unseen, unseen.RunAsync(), $"{chosen.Name}'s start from a window nobody sees");
                Ui.WaitFor(() => godot.Started is not null, $"the stand-in Godot's start for {chosen.Name} from a window nobody sees");
                IReadOnlyList<string> started = godot.Started!;
                Assert.True(started.Count(a => a == GodotExecutables.Headless) == 1,
                    $"A window nobody sees did not start {chosen.Name} headless once, with \"{chosen.Arguments}\" its own arguments: it started Godot with {string.Join(' ', started)}");
                // The stand-in has written what it was started with a moment before it ends, which the next start waits out.
                Ui.WaitFor(() => !unseen.IsRunning, () => $"{chosen.Name}'s stand-in Godot to end ({unseen.Status})");
            }
        }
        finally
        {
            unseen.Close();
        }
    }

    /// <summary>A window on the game, Godot the stand-in, the game's configuration chosen.</summary>
    private MainWindow Open(bool unseen)
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, Unseen = unseen };
        window.Show();
        window.ApplySettings(window.Settings with { GodotExecutable = godot.Executable });
        Ui.Await(window.OpenFolder(game), "the game's folder to open");
        Ui.Settle();
        Ui.WaitFor(() => window.Configurations.Any(c => c.Generated && c.Name == Lonely),
            () => $"the game's configuration ({string.Join(", ", window.Configurations.Select(c => c.Name))})");
        Assert.True(window.SelectConfiguration(Lonely));
        return window;
    }
}

/// <summary>A stand-in Godot: it answers <c>--version</c> as Godot 4.7.2 does, and otherwise keeps the arguments it was
/// started with, one a line, and ends.</summary>
public sealed class StandInGodot
{
    private StandInGodot(string folder, string executable)
    {
        Folder = folder;
        Executable = executable;
    }

    public string Folder { get; }

    public string Executable { get; }

    private string Kept => Path.Combine(Folder, "arguments.txt");

    public static StandInGodot Make()
    {
        string folder = PathNames.Canonical(TestFolders.Create("standin-godot-"));
        string kept = Path.Combine(folder, "arguments.txt");
        const string Answer = "4.7.2.stable.official.standin";
        if (OperatingSystem.IsWindows())
        {
            string batch = Path.Combine(folder, "godot.cmd");
            // The redirection first, so an argument ending in a digit is never read as a handle.
            File.WriteAllText(batch, $"@echo off\r\nif \"%~2\"==\"--version\" (\r\n  echo {Answer}\r\n  exit /b 0\r\n)\r\n"
                + $"for %%a in (%*) do >>\"{kept}.part\" echo %%~a\r\nmove /y \"{kept}.part\" \"{kept}\" >nul\r\n");
            return new StandInGodot(folder, batch);
        }
        string script = Path.Combine(folder, "godot");
        File.WriteAllText(script, $"#!/bin/sh\ncase \" $* \" in *\" --version \"*) echo {Answer}; exit 0;; esac\n"
            + $"printf '%s\\n' \"$@\" > '{kept}.part'\nmv '{kept}.part' '{kept}'\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new StandInGodot(folder, script);
    }

    /// <summary>The arguments of its last start but <c>--version</c>, or null before it is started.</summary>
    public IReadOnlyList<string>? Started => File.Exists(Kept) ? [.. File.ReadAllLines(Kept).Select(l => l.Trim()).Where(l => l.Length > 0)] : null;

    /// <summary>The arguments of its last start but <c>--version</c>, once it has been started, off the UI thread.</summary>
    public async Task<IReadOnlyList<string>> StartedAsync(string what)
    {
        Assert.True(await Waiting.UntilAsync(() => File.Exists(Kept), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken),
            $"The stand-in Godot was never started for {what}");
        return Started!;
    }

    /// <summary>Forgets its last start, so the next one is waited for.</summary>
    public void Forget()
    {
        if (File.Exists(Kept))
        {
            File.Delete(Kept);
        }
    }
}

/// <summary>An agent for a screenshot's <c>--agent</c> step: a shell script (PowerShell's on Windows) that joins the capture's
/// window through <c>passenger --mcp-attach</c>, as an agent's MCP client does, initializes, and makes the calls given one at
/// a time, each answered before the next, every answer kept. Its relay starts headless where it finds no window.</summary>
public static class ScreenshotAgent
{
    private const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";

    private const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    /// <summary>Writes the agent into its folder: the command that runs it, and the file its answers are kept in. Each call
    /// is a JSON-RPC line whose id counts from 2.</summary>
    public static async Task<(string Command, string Answers)> WriteAsync(string folder, IReadOnlyList<string> calls)
    {
        string answers = Path.Combine(folder, "answers.txt");
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(folder, "agent.ps1");
            await File.WriteAllTextAsync(script, $$"""
                $env:{{Passenger.App.Mcp.McpProxy.StartVariable}} = 'headless'
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
                {{string.Join("\n", calls.Select((call, i) => $"$answers += Ask '{call}' {i + 2}"))}}
                $server.StandardInput.Close()
                $server.WaitForExit()
                Set-Content -Path '{{answers}}' -Value $answers
                """, TestContext.Current.CancellationToken);
            return ($"powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\"", answers);
        }
        string shell = Path.Combine(folder, "agent.sh");
        await File.WriteAllTextAsync(shell, $$"""
            cd '{{folder}}'
            mkfifo calls
            {{Passenger.App.Mcp.McpProxy.StartVariable}}=headless "$PASSENGER" --mcp-attach "$PASSENGER_PID" < calls > answers.txt &
            exec 3> calls
            answered() { until grep -q "\"id\":$1" answers.txt; do sleep 0.1; done; }
            printf '%s\n' '{{Initialize}}' >&3
            answered 1
            printf '%s\n' '{{Initialized}}' >&3
            {{string.Join("\n", calls.Select((call, i) => $"printf '%s\\n' '{call}' >&3\nanswered {i + 2}"))}}
            exec 3>&-
            wait
            """, TestContext.Current.CancellationToken);
        return ($"sh \"{shell}\"", answers);
    }
}
