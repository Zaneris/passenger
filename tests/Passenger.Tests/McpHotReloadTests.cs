using System.Text.Json;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using Passenger.App.Mcp;
using Passenger.App;

namespace Passenger.Tests;

/// <summary>An agent's Hot Reload: the edits it made in the editor applied to the program it debugs, and an edit the
/// runtime cannot take named at its line, the program restarted for it only when asked.</summary>
[Collection(Alone.Name)]
public sealed class McpHotReloadTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpHotReloadTests(RestoredFolders folders)
        : base(folders)
    {
    }

    /// <summary>An agent's debugging with no window, its tools' answers each checked once: an exception breakpoint on a type,
    /// listed and stopping where that type is thrown; Run to Cursor and Set Next Statement; and Hot Reload of an edit made
    /// while paused, then of one the runtime cannot take, named at its line and the program restarted for it only when
    /// asked.</summary>
    [Fact]
    public async Task An_agent_stops_at_an_exception_type_runs_to_a_line_moves_back_and_applies_hot_reload_changes_told_each_rude_edit_at_its_line()
    {
        Write("Program.cs", "int total = 0;\ntotal += 1;\ntotal += 10;\ntotal += 100;\nfor (int turn = 1; turn <= 2; turn++)\n{\n"
            + "    try { throw new ArgumentException(\"argument\"); } catch (ArgumentException) { }\n"
            + "    try { throw new InvalidOperationException($\"operation {turn}\"); } catch (InvalidOperationException) { }\n}\n"
            + "for (int i = 1; i <= 3; i++)\n{\n    total += Helper.Twice(i);\n}\nConsole.WriteLine($\"total {total}\");\n");
        Write("Helper.cs", "static class Helper\n{\n    static int calls;\n\n    public static int Twice(int value)\n    {\n        calls++;\n        return value * 2;\n    }\n}\n");
        await using McpClient client = await Start(["--mcp", Folder]);
        await Call(client, McpTools.BreakpointSet, new() { ["exception"] = "System.InvalidOperationException" });
        // Listed with "Any exception", the new one in every scenario.
        JsonElement[] exceptions = [.. (await Call(client, McpTools.DebugState, new() { ["of"] = "breakpoints" })).EnumerateArray()
            .Where(b => b.TryGetProperty("exception", out JsonElement type) && type.ValueKind == JsonValueKind.String)];
        Assert.Equal(["Any exception", "System.InvalidOperationException"], exceptions.Select(b => b.GetProperty("exception").GetString()));
        Assert.Equal(["thrownInUserCode", "thrownInExternalCode", "handledInUserCode", "handledInExternalCode", "unhandled"],
            exceptions[1].GetProperty("scenarios").EnumerateArray().Select(s => s.GetString()));

        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = 2 });
        Assert.StartsWith("Program.cs:2:", (await Call(client, McpTools.DebugStart)).GetProperty("location").GetString(), StringComparison.Ordinal);
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = 2, ["remove"] = true });
        JsonElement ranTo = await Call(client, McpTools.DebugContinue, new() { ["path"] = "Program.cs", ["line"] = 4 });
        Assert.StartsWith("Program.cs:4:", ranTo.GetProperty("location").GetString(), StringComparison.Ordinal);
        Assert.Equal("11", Local(ranTo, "total"));
        JsonElement moved = await Call(client, McpTools.DebugSetNextStatement, new() { ["path"] = "Program.cs", ["line"] = 3 });
        Assert.StartsWith("Program.cs:3:", moved.GetProperty("location").GetString(), StringComparison.Ordinal);
        Assert.Equal("11", Local(moved, "total"));

        // The argument exception passes; the stop is the first turn's, at the line that throws the type named.
        JsonElement thrown = await Call(client, McpTools.DebugContinue);
        Assert.Equal("exception", thrown.GetProperty("reason").GetString());
        Assert.StartsWith("System.InvalidOperationException: operation 1", thrown.GetProperty("exception").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("Program.cs:8:", thrown.GetProperty("location").GetString(), StringComparison.Ordinal);
        // Removed while paused, it lets the second turn's pass: the next stop is the loop's, line 3 done twice.
        await Call(client, McpTools.BreakpointSet, new() { ["exception"] = "System.InvalidOperationException", ["remove"] = true });
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = 12 });
        JsonElement looped = await Call(client, McpTools.DebugContinue);
        Assert.True(looped.GetProperty("location").GetString()!.StartsWith("Program.cs:12:", StringComparison.Ordinal) && Local(looped, "total") == "121",
            $"With the exception breakpoint removed, the program did not run on to the loop, line 3 done twice: {looped}");

        await Call(client, McpTools.Open, new() { ["path"] = "Helper.cs", ["line"] = 8, ["column"] = 25 });
        await Call(client, McpTools.AppType, new() { ["app"] = "ide", ["text"] = " + 1" });
        JsonElement applied = await Call(client, McpTools.HotReload);
        Assert.True(applied.GetProperty("result").GetString() == "applied" && applied.GetProperty("methods").GetInt32() == 1
            && applied.GetProperty("files").EnumerateArray().Select(f => f.GetString()).SequenceEqual(["Helper.cs"]) && !applied.GetProperty("restartNeeded").GetBoolean(),
            $"The edit to Twice's body was not applied: {applied}");
        Assert.Equal("124", Local(await Call(client, McpTools.DebugContinue), "total"));

        // The runtime cannot change a field's type; with restart false the program stays paused in the code it has.
        await Call(client, McpTools.Open, new() { ["path"] = "Helper.cs", ["line"] = 3, ["column"] = 12 });
        await Call(client, McpTools.AppType, new() { ["app"] = "ide", ["text"] = "u" });
        JsonElement rude = await Call(client, McpTools.HotReload, new() { ["restart"] = false });
        Assert.True(rude.GetProperty("result").GetString() == "restartNeeded" && rude.GetProperty("restartNeeded").GetBoolean() && !rude.GetProperty("restarted").GetBoolean(),
            $"Changing a field's type did not say a restart is needed, or restarted unasked: {rude}");
        JsonElement edit = Assert.Single(rude.GetProperty("edits").EnumerateArray(), e => e.GetProperty("severity").GetString() == "error");
        Assert.True(edit.GetProperty("path").GetString() == "Helper.cs" && edit.GetProperty("line").GetInt32() == 3
            && edit.GetProperty("code").GetString()!.StartsWith("ENC", StringComparison.Ordinal), $"The rude edit is not named at its line: {edit}");
        JsonElement still = await Call(client, McpTools.DebugState);
        Assert.True(still.GetProperty("state").GetString() == "paused" && Local(still, "total") == "124", $"The program did not stay where it was: {still}");

        JsonElement restarted = await Call(client, McpTools.HotReload, new() { ["restart"] = true });
        Assert.True(restarted.GetProperty("restarted").GetBoolean() && restarted.GetProperty("debugger").GetProperty("state").GetString() == "paused"
            && restarted.GetProperty("debugger").GetProperty("location").GetString()!.StartsWith("Program.cs:12:", StringComparison.Ordinal),
            $"The restart for the rude edit did not stop at the breakpoint again: {restarted}");
        Assert.Equal("114", Local(await Call(client, McpTools.DebugContinue), "total"));
        await Call(client, McpTools.Stop);
    }

    /// <summary>An agent's hot_reload on the person's window is the window's own Apply Hot Reload Changes: it shows as that
    /// action's work while it applies, and its outcome is the window's last apply.</summary>
    [AvaloniaFact]
    public void An_agents_hot_reload_on_the_persons_window_is_its_apply_hot_reload_changes_shown_as_its_work()
    {
        File.WriteAllText(Path.Combine(Folder, "Helper.cs"),
            "static class Helper\n{\n    public static int Twice(int value)\n    {\n        return value * 2;\n    }\n}\n");
        MainWindow window = AgentsWindow(Folder);
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to load the project ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
        List<string> worked = [];
        window.Actions.Work.Changed += (_, change) =>
        {
            if (change.Work.Id == AppActions.ApplyHotReloadChanges && !change.Ended)
            {
                worked.Add(change.Work.Stage);
            }
        };
        McpClient? client = null;
        try
        {
            window.OpenFile(Path.Combine(Folder, "Program.cs"));
            window.EditorArea.CodeEditor.GoTo(new global::Passenger.Text.TextPosition(3, 4));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Debug);
            Ui.WaitFor(() => window.Debugger.Session?.State == global::Passenger.App.Debugging.DebugState.Paused && window.Debugger.Stop is not null,
                $"the program to stop at its breakpoint{Ui.BuildErrors(window)}");
            window.OpenFile(Path.Combine(Folder, "Helper.cs"));
            Typing.Replace(window, "value * 2", "value * 2 + 1");

            client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
            Task<JsonElement> applying = Task.Run(() => Call(client, McpTools.HotReload));
            Press(Question(window, "the question for the agent's hot_reload"), "Allow");
            JsonElement applied = Await(applying, "the agent's hot_reload");
            Assert.True(applied.GetProperty("result").GetString() == "applied" && applied.GetProperty("methods").GetInt32() == 1, $"The agent's edit was not applied: {applied}");
            Assert.True(worked.Contains("Applying Hot Reload changes…"), $"The agent's apply did not show as Apply Hot Reload Changes' work; it showed: {string.Join(" | ", worked)}");
            Assert.Equal(global::Passenger.App.Debugging.HotReloadResult.Applied, window.Debugger.LastApplied?.Result);
        }
        finally
        {
            if (window.Debugger.IsDebugging)
            {
                window.Perform(AppActions.StopAll);
                Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
            }
            if (client is not null)
            {
                Gone(client);
            }
            window.Close();
        }
    }

    /// <summary>Run with Hot Reload (Settings' Hot Reload, or run's hotReload, since the headless IDE keeps the default settings)
    /// runs the program under dotnet watch, which takes saved edits: hot_reload saves them and answers with what dotnet watch
    /// said of them, and output shows the program running them.</summary>
    [Fact]
    public async Task An_agent_applies_hot_reload_changes_to_what_run_runs_under_dotnet_watch_and_is_told_what_dotnet_watch_made_of_them()
    {
        File.WriteAllText(Path.Combine(Folder, "Program.cs"),
            "while (true)\n{\n    Console.WriteLine(Talk.Say());\n    Thread.Sleep(200);\n}\n\nstatic class Talk\n{\n    public static string Say() => \"before the edit\";\n}\n");
        await using McpClient client = await Start(["--mcp", Folder]);
        // As where run comes first, never built: the design-time build's output folder goes, for dotnet watch to make.
        Assert.Contains((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Talk" })).EnumerateArray(), s => s.GetProperty("path").GetString() == "Program.cs");
        TestFolders.Delete(Path.Combine(Folder, "bin"));
        Assert.True((await Call(client, McpTools.Run, new() { ["waitSeconds"] = 0, ["hotReload"] = true })).GetProperty("stillRunning").GetBoolean());
        // dotnet watch watches once it has loaded the project, which is after the program has started.
        string output = string.Empty;
        await Waiting.UntilAsync(async () =>
        {
            output = (await Call(client, McpTools.Output, new() { ["lines"] = 100000 })).GetString()!;
            return output.Contains("before the edit", StringComparison.Ordinal) && output.Contains("Waiting for changes", StringComparison.Ordinal);
        }, TimeSpan.FromMinutes(2), Cancel, TimeSpan.FromMilliseconds(500));
        Assert.True(output.Contains("Waiting for changes", StringComparison.Ordinal), $"dotnet watch never watched the program:\n{output[..Math.Min(output.Length, 3000)]}");

        await Call(client, McpTools.Open, new() { ["path"] = "Program.cs", ["line"] = 9, ["column"] = 51 });
        await Call(client, McpTools.AppType, new() { ["app"] = "ide", ["text"] = ", edited" });
        JsonElement applied = await Call(client, McpTools.HotReload);
        Assert.True(applied.GetProperty("result").GetString() == "applied" && applied.GetProperty("said").GetString()!.Contains("dotnet watch applied the changes", StringComparison.Ordinal),
            $"dotnet watch did not apply the saved edit: {applied}\n{(await Call(client, McpTools.Output)).GetString()}");
        await Waiting.UntilAsync(async () =>
        {
            output = (await Call(client, McpTools.Output)).GetString()!;
            return output.Contains("before the edit, edited", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(30), Cancel, TimeSpan.FromMilliseconds(500));
        Assert.True(output.Contains("before the edit, edited", StringComparison.Ordinal), $"The running program never ran the edit:\n{output[Math.Max(0, output.Length - 3000)..]}");
        await Call(client, McpTools.Stop);
    }
}
