using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Mobile;
using Passenger.App.Run;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>McpWindowTests' agents on the person's window where one debugs through the window (a program in the terminal,
/// stopped, evaluated and typed into), so the class runs alone; with a project file's diagnostics during a build, and the
/// window's keys while ten thousand usages land.</summary>
[Collection(Alone.Name)]
public sealed class McpWindowDebuggingTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpWindowDebuggingTests(RestoredFolders folders)
        : base(folders)
    {
    }

    /// <summary>An agent's diagnostics for a project file wait for that file's own checks alone, never for a build running
    /// meanwhile.</summary>
    [AvaloniaFact]
    public void An_agents_diagnostics_for_a_project_file_come_from_its_own_checks_while_a_build_runs()
    {
        File.WriteAllText(Path.Combine(Folder, "Directory.Build.targets"), """
            <Project>
              <Target Name="Slow" BeforeTargets="Build" Condition="'$(DesignTimeBuild)' != 'true' And Exists('slow')">
                <Exec Condition="'$(OS)' != 'Windows_NT'" Command="echo $$ &gt; slow.pid; exec sleep 120" />
                <Exec Condition="'$(OS)' == 'Windows_NT'" Command="powershell -NoProfile -Command &quot;Set-Content -Path slow.pid -Value $PID; Start-Sleep -Seconds 120&quot;" />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(Folder, "slow"), string.Empty);
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        OwnProcess step = default;
        try
        {
            Assert.True(window.Perform(AppActions.Build));
            step = OwnProcess.Of(Processes.WrittenId(Path.Combine(Folder, "slow.pid"), "the build's slow step to start"));

            window.OpenFile(Path.Combine(Folder, "Sum.csproj"));
            window.EditorArea.CodeEditor.GoTo(new Passenger.Text.TextPosition(6, 0));
            window.KeyTextInput("<Import Project=\"Missing.props\" />");
            Task<JsonElement> diagnostics = Waiting.Apart(() => Call(client, McpTools.Diagnostics, new() { ["path"] = "Sum.csproj" }));
            JsonElement answer = Ui.AwaitPumped(diagnostics, "the project file's diagnostics", AgentLimit);
            JsonElement missing = Assert.Single(answer.EnumerateArray());
            Assert.Contains("Missing.props", missing.GetProperty("message").GetString(), StringComparison.Ordinal);
            // The build's step sleeps two minutes, so an answer that waited for the build comes after it.
            Assert.True(window.IsBuilding, "The build ended before the agent's diagnostics were answered: they waited for it");
        }
        finally
        {
            window.Perform(AppActions.Stop);
            step.Kill();
            Ui.PumpUntil(() => !window.IsBuilding, "the build to stop", TimeSpan.FromMinutes(1));
            Gone(client);
            window.Close();
        }
    }

    /// <summary>On the person's window an agent is asked once, the first time, before it runs a configuration, sets a
    /// breakpoint, debugs, evaluates code in the paused program (setting a value there unasked after) or types into its
    /// input; saving and stopping are never asked about. The configuration uses the terminal: run answers with what the
    /// program's tab shows, debug_start stops at a breakpoint on Main's first line, debug_input types into its tab, and
    /// output reads the tab, the program's end in it.</summary>
    [AvaloniaFact]
    public void An_agent_is_asked_once_before_it_first_runs_sets_a_breakpoint_evaluates_or_types_into_a_program_in_the_terminal()
    {
        Write("Program.cs", File.ReadAllText(Debugging.Helpers.Debuggees.Source("TerminalConsoleApp", "Program.cs")));
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        window.SetConfigurations([new global::Passenger.App.Run.RunConfiguration
        {
            Name = "In the terminal",
            Project = Path.Combine(window.Folder!, "Sum.csproj"),
            Console = global::Passenger.App.Run.ConsoleKind.Terminal,
        }], "In the terminal");
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Assert.Equal("Nothing to save", Unasked(window, client, McpTools.SaveAll).GetString());
            JsonElement ran = Asked(window, client, McpTools.Run, "run a configuration", new() { ["waitSeconds"] = 5 });
            string output = ran.GetProperty("output").GetString()!;
            Assert.True(ran.GetProperty("stillRunning").GetBoolean() && output.Contains("red at the third row's fifth column", StringComparison.Ordinal),
                $"The run in the terminal was answered without the program waiting for its key in its tab: {ran}");
            Assert.Equal("Stopped", Unasked(window, client, McpTools.Stop).GetString());
            WaitFor(() => window.RunInTerminal is { IsRunning: false }, "the program in the terminal to end");

            Asked(window, client, McpTools.BreakpointSet, "set a breakpoint", new() { ["path"] = "Program.cs", ["line"] = 1 });
            Unasked(window, client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = 24 });
            JsonElement stop = Asked(window, client, McpTools.DebugStart, "debug a configuration");
            Assert.True(stop.GetProperty("state").GetString() == "paused" && stop.GetProperty("location").GetString()!.StartsWith("Program.cs:1:", StringComparison.Ordinal),
                $"Debugged in the terminal, the program did not stop on Main's first line: {stop}");
            Assert.Equal("42", Asked(window, client, McpTools.DebugEvaluate, "evaluate an expression", new() { ["expression"] = "6 * 7" }).GetProperty("value").GetString());
            Assert.Equal("0", Unasked(window, client, McpTools.DebugEvaluate, new() { ["expression"] = "args.Length" }).GetProperty("value").GetString());
            string set = Unasked(window, client, McpTools.DebugEvaluate, new() { ["expression"] = "line", ["value"] = "\"forty\"" }).GetProperty("value").GetString()!;
            Assert.Contains("forty", set, StringComparison.Ordinal);
            set = Unasked(window, client, McpTools.DebugEvaluate, new() { ["expression"] = "line", ["value"] = "\"again\"" }).GetProperty("value").GetString()!;
            Assert.Contains("again", set, StringComparison.Ordinal);

            Await(Waiting.Apart(() => Call(client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = 1, ["remove"] = true })), "the breakpoint's removal");
            Await(Waiting.Apart(() => Call(client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = 24, ["remove"] = true })), "the other breakpoint's removal");
            Await(Waiting.Apart(() => Call(client, McpTools.DebugContinue, new() { ["waitSeconds"] = 1 })), "the program let go");
            WaitFor(() => window.Debugger.Session?.Terminal?.Session.Screen.Lines.Any(l => l.Text() == "key?") == true, "the program to ask for a key in its tab");
            Asked(window, client, McpTools.DebugInput, "type into the program's console", new() { ["text"] = "q", ["newline"] = false });
            Unasked(window, client, McpTools.DebugInput, new() { ["text"] = "hello" });
            WaitFor(() => !window.Debugger.IsDebugging && window.Debugger.Session?.Terminal?.Ended.IsCompleted == true, "the program's end");
            string shown = Await(Waiting.Apart(() => Call(client, McpTools.Output, new() { ["of"] = "debug" })), "the program's output").GetString()!;
            // A key typed before ReadKey takes the terminal is echoed by the terminal's own line discipline, as anywhere.
            Assert.True((shown.Contains("key? key q", StringComparison.Ordinal) || shown.Contains("key? qkey q", StringComparison.Ordinal)) && shown.Contains("line hello", StringComparison.Ordinal)
                && shown.Contains("Process finished with exit code 3.", StringComparison.Ordinal), $"output read, of the program's tab:\n{shown}");
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>While an attached agent's ten thousand usages are found, each with its line read from its closed file, the
    /// window takes every key typed; the files the answer only counted are read through within, and from where one file's
    /// places run past the limit, to every place.</summary>
    [AvaloniaFact]
    public void The_window_takes_keys_while_an_attached_agent_finds_ten_thousand_usages_each_with_its_line()
    {
        // Every call under a long comment, so a usage's line read from its file's start reads each file 2,500 times over.
        for (int file = 0; file < 4; file++)
        {
            System.Text.StringBuilder text = new($"static class Uses{file}\n{{\n    public static int All()\n    {{\n        int total = 0;\n");
            for (int call = 0; call < 2500; call++)
            {
                text.Append("        // ").Append('=', 100).Append('\n');
                text.Append("        total += Helper.Twice(").Append(call).Append(");\n");
            }
            Write($"Uses{file}.cs", text.Append("        return total;\n    }\n}\n").ToString());
        }
        Write("Notes.txt", string.Empty);
        MainWindow window = AgentsWindow(Folder);
        window.OpenFile(Path.Combine(window.Folder!, "Helper.cs"));
        window.OpenFile(Path.Combine(window.Folder!, "Notes.txt"));
        window.EditorArea.CodeEditor.Focus();
        Ui.Settle();
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            // The language server has the folder loaded before the window is timed, which is the server's work, not the agent's.
            Await(Waiting.Apart(() => Call(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" })), "the language server");

            Task<JsonElement> usages = Waiting.Apart(() => Call(client, McpTools.Usages, new() { ["path"] = "Helper.cs", ["line"] = 3, ["column"] = 23 }));
            int typed = 0;
            // The call's own limit ends this if Passenger never answers.
            while (!usages.IsCompleted)
            {
                window.KeyTextInput("x");
                typed++;
                Ui.Hold(TimeSpan.FromMilliseconds(50), "a pause between keys, as a person types");
            }
            JsonElement answered = Ui.AwaitPumped(usages, "the usages");
            Ui.Settle();
            Assert.Equal(new string('x', typed), window.EditorArea.Active!.Text.Snapshot.ToString());

            List<(string Path, int Line, int Column, string Text)> found = [.. Used(answered)];
            foreach (string counted in answered.GetProperty("files").EnumerateArray().Where(f => f.TryGetProperty("count", out _)).Select(f => f.GetProperty("path").GetString()!))
            {
                for (int? from = null; ;)
                {
                    Dictionary<string, object?> asked = new() { ["path"] = "Helper.cs", ["line"] = 3, ["column"] = 23, ["within"] = counted };
                    if (from is int line)
                    {
                        asked["from"] = line;
                    }
                    JsonElement part = Await(Waiting.Apart(() => Call(client, McpTools.Usages, asked)), $"the usages within {counted} from {from}");
                    found.AddRange(Used(part));
                    if (!part.TryGetProperty("more", out JsonElement more))
                    {
                        break;
                    }
                    int next = int.Parse(Regex.Match(more.GetString()!, @"from (\d+) with within").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    Assert.True(next > (from ?? 0), $"usages within {counted} from {from} said to read on from {next}: {more}");
                    from = next;
                }
            }

            Assert.Equal(10_002, found.Distinct().Count());
            Assert.Equal(10_002, found.Count);
            Assert.All(found, place => Assert.Contains("Twice(", place.Text, StringComparison.Ordinal));
            Assert.Contains(found, place => place.Path == "Uses3.cs" && place.Line == 5005 && place.Text == "total += Helper.Twice(2499);");

            JsonElement callers = Await(Waiting.Apart(() => Call(client, McpTools.Hierarchy, new() { ["path"] = "Helper.cs", ["line"] = 3, ["column"] = 23 })), "the callers");
            Assert.Equal(["Uses0.cs:7 total += Helper.Twice(0);", "Uses1.cs:7 total += Helper.Twice(0);", "Uses2.cs:7 total += Helper.Twice(0);", "Uses3.cs:7 total += Helper.Twice(0);"],
                callers.EnumerateArray().Select(c => $"{c.GetProperty("path").GetString()}:{c.GetProperty("line").GetInt32()} {c.GetProperty("text").GetString()}")
                    .Where(c => c.StartsWith("Uses", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }
}
