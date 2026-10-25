using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent with no window (<c>--mcp</c>) where it debugs, or where what it tests is held to a time (a call's
/// progress told every second): the code found, its warnings read and the program debugged, launched and attached to; a
/// debugged test run's tally; a debug session stopped as it starts; and a long call's progress. So it runs alone.</summary>
[Collection(Alone.Name)]
public sealed class McpDebuggingTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpDebuggingTests(RestoredFolders folders)
        : base(folders)
    {
    }

    [Fact]
    public async Task An_agent_with_a_windowless_Passenger_finds_code_reads_its_warnings_and_debugs_it()
    {
        // A name that has the one asked for in it, in a file the server reads before Helper.cs.
        Write("Aaa.cs", "static class Aaa\n{\n    public static int TwiceMore(int value) => value;\n}\n");
        await using McpClient client = await Start(["--mcp", Folder]);
        IList<McpClientTool> tools = await Exchange("Listing the tools", TimeSpan.FromMinutes(1), cancel => client.ListToolsAsync(cancellationToken: cancel).AsTask());
        Assert.Contains(tools, t => t.Name == McpTools.DebugStart);
        string testsRun = Assert.Single(tools, t => t.Name == McpTools.TestsRun).Description;
        Assert.True(testsRun.Contains("Testing Platform", StringComparison.Ordinal) && testsRun.Contains("xUnit v3", StringComparison.Ordinal),
            $"tests_run tells an agent its tests run otherwise than they do: {testsRun}");
        // Every session pays to read the list, so it says each thing once and is held under 62 KB (the owner's call).
        Assert.Equal([McpTools.AppTree], tools.Where(t => JsonSerializer.Serialize(t.ProtocolTool).Contains("#Panel/Button", StringComparison.Ordinal)).Select(t => t.Name));
        Assert.DoesNotContain(tools, t => SaysNothing(t.ProtocolTool.InputSchema));
        Assert.DoesNotContain(tools, t => t.ProtocolTool.Annotations?.Title is not null);
        int listed = tools.Sum(t => JsonSerializer.Serialize(t.ProtocolTool, ModelContextProtocol.McpJsonUtilities.DefaultOptions).Length);
        Assert.True(listed < 62_000, $"The tools an agent reads as each session starts take {listed} bytes");

        // The symbol named as asked comes first, so a limit of one finds it rather than one with the name inside its own.
        JsonElement symbol = Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Twice", ["limit"] = 1 })).EnumerateArray());
        Assert.Equal(("Twice", "Helper.cs", 3), (symbol.GetProperty("name").GetString(), symbol.GetProperty("path").GetString(), symbol.GetProperty("line").GetInt32()));

        // Code in an answer reads as written, its quotes and angle brackets as they are, not as \u0022s an agent pays for.
        string found = Text(await Result(client, McpTools.FindInFiles, new() { ["text"] = "total {total}" }));
        Assert.True(found.Contains("Console.WriteLine($\\\"total {total}\\\");", StringComparison.Ordinal) && !found.Contains("\\u00", StringComparison.Ordinal),
            $"find_in_files wrote a line of code as: {found}");

        JsonElement warning = Assert.Single((await Call(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" })).EnumerateArray(),
            d => d.GetProperty("code").GetString() == "CS0219");
        Assert.Equal("warning", warning.GetProperty("severity").GetString());
        Assert.Equal(5, warning.GetProperty("line").GetInt32());

        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = 4 });
        JsonElement stop = await Call(client, McpTools.DebugStart);
        Assert.Equal("paused", stop.GetProperty("state").GetString());
        Assert.StartsWith("Program.cs:4:", stop.GetProperty("location").GetString(), StringComparison.Ordinal);
        Assert.Equal("1", Local(stop, "i"));
        Assert.Equal("0", Local(stop, "total"));
        Assert.Equal("42", (await Call(client, McpTools.DebugEvaluate, new() { ["expression"] = "Helper.Twice(i) + 40" })).GetProperty("value").GetString());
        int depth = stop.GetProperty("frames").GetArrayLength();
        foreach ((string tool, Dictionary<string, object?> arguments) in new (string, Dictionary<string, object?>)[]
        {
            (McpTools.DebugEvaluate, new() { ["expression"] = "i", ["frame"] = 99 }),
            (McpTools.DebugEvaluate, new() { ["expression"] = "i", ["value"] = "1", ["frame"] = 99 }),
        })
        {
            CallToolResult past = await Result(client, tool, arguments);
            Assert.True(past.IsError == true && Text(past).Contains("There is no frame 99: the stopped thread has ", StringComparison.Ordinal)
                && Text(past).Contains($"{depth} frame", StringComparison.Ordinal),
                $"{tool} in frame 99 of a stack {depth} deep answered: {Text(past)}");
        }

        stop = await Call(client, McpTools.DebugContinue);
        Assert.Equal("2", Local(stop, "i"));

        // An agent's next step may be a click the debugger never sees, so the answer comes once the breakpoint is bound.
        JsonElement later = Assert.Single((await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = 6 })).EnumerateArray(),
            b => b.GetProperty("line").GetInt32() == 6);
        Assert.Equal("bound", later.GetProperty("status").GetString());
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = 6, ["remove"] = true });

        // An edit made while paused runs once it goes on: Twice(2) and Twice(3) add one each.
        await Call(client, McpTools.Open, new() { ["path"] = "Helper.cs", ["line"] = 6, ["column"] = 25 });
        await Call(client, McpTools.AppType, new() { ["app"] = "ide", ["text"] = " + 1" });

        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = 4, ["remove"] = true });
        stop = await Call(client, McpTools.DebugContinue);
        Assert.Equal("exception", stop.GetProperty("reason").GetString());
        Assert.StartsWith("System.InvalidOperationException: the end", stop.GetProperty("exception").GetString(), StringComparison.Ordinal);
        string output = (await Call(client, McpTools.Output, new() { ["of"] = "debug" })).GetString()!;
        Assert.True(output.Contains("total 14", StringComparison.Ordinal), $"The edit made while paused did not run once the program went on:\n{output}");

        CallToolResult picture = await Result(client, McpTools.AppScreenshot, new() { ["app"] = "ide" });
        ImageContentBlock image = Assert.IsType<ImageContentBlock>(picture.Content[0]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], image.DecodedData.Slice(0, 4).ToArray());

        await Call(client, McpTools.Stop);
        Assert.Equal("not debugging", (await Call(client, McpTools.DebugState)).GetProperty("state").GetString());

        CallToolResult refused = await Result(client, McpTools.DebugContinue);
        Assert.True(refused.IsError);
        Assert.Contains("debug_start starts it", Assert.IsType<TextContentBlock>(Assert.Single(refused.Content)).Text, StringComparison.Ordinal);

        // A program already running, started as a terminal would start it.
        ProcessStartInfo start = ChildProcesses.Command(Debugging.Helpers.Debuggees.Apphost("LoopingConsoleApp"), ["sleep"]);
        start.RedirectStandardOutput = true;
        using (Process looping = ChildProcesses.Start(start))
        {
            try
            {
                Assert.Equal("looping", looping.StandardOutput.ReadLine());
                JsonElement program = Assert.Single((await Call(client, McpTools.DebugState, new() { ["of"] = "processes" })).EnumerateArray(), p => p.GetProperty("processId").GetInt32() == looping.Id);
                Assert.Equal("LoopingConsoleApp", program.GetProperty("program").GetString());
                string source = Debugging.Helpers.Debuggees.Source("LoopingConsoleApp", "Program.cs");
                int line = Debugging.Helpers.Debuggees.Line("LoopingConsoleApp", "turns++;");
                await Call(client, McpTools.BreakpointSet, new() { ["path"] = source, ["line"] = line });
                JsonElement attached = await Call(client, McpTools.DebugAttach, new() { ["processId"] = looping.Id });
                Assert.Equal(("paused", Passenger.Dap.StopReasons.Breakpoint), (attached.GetProperty("state").GetString(), attached.GetProperty("reason").GetString()));
                Assert.EndsWith($"Program.cs:{line}:5", attached.GetProperty("location").GetString(), StringComparison.Ordinal);
                Assert.True(long.TryParse(Local(attached, "turns"), out long turns) && turns >= 0, $"The stop's locals had no count of turns: {attached}");
                Assert.Equal("Detached from LoopingConsoleApp, which runs on", (await Call(client, McpTools.Stop, new() { ["detach"] = true })).GetString());
                Assert.Equal("not debugging", (await Call(client, McpTools.DebugState)).GetProperty("state").GetString());
                Assert.False(looping.WaitForExit(TimeSpan.FromSeconds(2)), "The program the agent detached from ended");
            }
            finally
            {
                Processes.Kill(looping.Id);
            }
        }

        // The C# server never takes a project file, so its problems must come at once from the editor's marks.
        await Call(client, McpTools.Open, new() { ["path"] = "Sum.csproj", ["line"] = 7, ["column"] = 1 });
        await Call(client, McpTools.AppType, new() { ["app"] = "ide", ["text"] = "<Import Project=\"Missing.props\" />" });
        Stopwatch asked = Stopwatch.StartNew();
        JsonElement missing = Assert.Single((await Call(client, McpTools.Diagnostics, new() { ["path"] = "Sum.csproj" })).EnumerateArray());
        Assert.True(asked.Elapsed < TimeSpan.FromSeconds(60).Scaled(), $"The project file's diagnostics took {asked.Elapsed}");
        Assert.Equal(("error", 7), (missing.GetProperty("severity").GetString(), missing.GetProperty("line").GetInt32()));
        Assert.Contains("Missing.props", missing.GetProperty("message").GetString(), StringComparison.Ordinal);
        // The hover card's lines end as the system's do (Windows' "\r\n").
        string framework = (await Call(client, McpTools.SymbolInfo, new() { ["path"] = "Sum.csproj", ["line"] = 4, ["column"] = 8 })).GetString()!.ReplaceLineEndings("\n");
        Assert.True(framework.StartsWith($"TargetFramework = {TestProjects.Framework}\nMSBuild property, set in Sum.csproj, line 4", StringComparison.Ordinal),
            $"symbol_info on a project file's property did not say its value and where it was set: {framework}");
    }

    /// <summary>A debugged test run an agent goes on with from its stop answers, as its test host ends, with the tally and
    /// failures tests_run gives.</summary>
    [Fact]
    public async Task An_agent_going_on_from_a_stop_in_a_debugged_test_run_gets_its_tally_as_it_ends()
    {
        string tests = PathNames.Canonical(TestFolders.Create("mcp-debug-tests-"));
        DeleteAtEnd(tests);
        TestFolders.Prepare(() =>
        {
            File.WriteAllText(Path.Combine(tests, "Sums.Tests.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n" +
                "    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.1.5\" />\n  </ItemGroup>\n</Project>\n");
            File.WriteAllText(Path.Combine(tests, "SumTests.cs"),
                "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class SumTests\n{\n    [Fact]\n    public void Two_and_two_make_four()\n    {\n        int sum = 2 + 2;\n" +
                "        Assert.Equal(4, sum);\n    }\n\n    [Fact]\n    public void Two_and_two_make_five() => Assert.Equal(5, 2 + 2);\n}\n");
            Restore.Run(tests);
        }, tests);
        await using McpClient client = await Start(["--mcp", tests], languageServer: false);
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "SumTests.cs", ["line"] = 10 });
        // Stopped at the breakpoint, and at the failing test's exception where the exception breakpoints say so, in either order.
        JsonElement ended = (await Call(client, McpTools.TestsRun, new() { ["debug"] = true })).GetProperty("debugger");
        List<string> stops = [];
        while (ended.GetProperty("state").GetString() == "paused" && stops.Count < 5)
        {
            stops.Add(ended.GetProperty("location").GetString()!);
            ended = await Call(client, McpTools.DebugContinue);
        }
        Assert.Contains(stops, s => s.StartsWith("SumTests.cs:10:", StringComparison.Ordinal));
        Assert.Equal("not debugging", ended.GetProperty("state").GetString());
        Assert.True(ended.TryGetProperty("tests", out JsonElement tally), $"debug_continue answered the debugged run's end without its tests: {ended}");
        Assert.StartsWith("Tests passed: 1, failed: 1 of 2", tally.GetProperty("summary").GetString(), StringComparison.Ordinal);
        JsonElement failure = Assert.Single(tally.GetProperty("failures").EnumerateArray());
        Assert.Equal("Sums.Tests.SumTests.Two_and_two_make_five", failure.GetProperty("test").GetString());
        Assert.Contains("Assert.Equal() Failure", failure.GetProperty("details").GetString(), StringComparison.Ordinal);
    }

    /// <summary>An agent's stop ends a debug session still starting: while debug_start waits for a program whose runtime is
    /// held at its start (the test's own diagnostic port), stop ends the session and the program at once, and debug_start
    /// answers that it was stopped.</summary>
    [Fact]
    public async Task An_agents_stop_ends_a_debug_session_whose_program_is_still_starting()
    {
        using Debugging.Helpers.StartHold hold = new();
        Directory.CreateDirectory(Path.Combine(Folder, "Properties"));
        File.WriteAllText(Path.Combine(Folder, "Properties", "launchSettings.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["profiles"] = new Dictionary<string, object> { ["Held"] = new Dictionary<string, object> { ["commandName"] = "Project", ["environmentVariables"] = hold.Environment } },
        }));
        await using McpClient client = await Start(["--mcp", Folder], languageServer: false);
        Task<int> held = hold.Held(TimeSpan.FromMinutes(3));
        Task<CallToolResult> debugging = Result(client, McpTools.DebugStart, new() { ["configuration"] = "Sum: Held" });
        int program = 0;
        try
        {
            if (await Task.WhenAny(held, debugging) == debugging)
            {
                Assert.Fail($"debug_start answered before the program came to the hold: {Text(await debugging)}{Said()}");
            }
            program = await held;

            Assert.Equal("Stopped", (await Call(client, McpTools.Stop)).GetString());
            Assert.True(await Waiting.Within(debugging, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken),
                $"debug_start still waited for the program {TimeSpan.FromSeconds(15).Scaled().TotalSeconds} s after the agent's stop{Said()}");
            CallToolResult stopped = await debugging;
            Assert.True(stopped.IsError == true && Text(stopped).Contains("Debugging Sum: Held was stopped as it started", StringComparison.Ordinal),
                $"debug_start, stopped as it started, answered: {Text(stopped)}");
            Assert.True(Processes.Gone(program), $"The agent's stop left the program running: {Processes.Describe(program)}");
            Assert.Equal("not debugging", (await Call(client, McpTools.DebugState)).GetProperty("state").GetString());
        }
        finally
        {
            Processes.Kill(program);
        }
    }

    /// <summary>A call that takes a while tells an agent that asked for progress what it is doing as it goes (the run's
    /// stages, then the wait for the program), at rising counts, so a client that gives up on a silent call (OpenCode's
    /// 60 s) waits on; and the agent's own Passenger writes nothing to the standard error an agent may never read.</summary>
    [Fact]
    public async Task A_call_that_takes_a_while_tells_its_agent_what_it_is_doing_and_nothing_reaches_standard_error()
    {
        Write("Program.cs", "Console.WriteLine(\"waiting\");\nThread.Sleep(Timeout.Infinite);\n");
        await using McpClient client = await Start(["--mcp", Folder]);
        Heard heard = new();
        CallToolResult ran = await Result(client, McpTools.Run, new() { ["waitSeconds"] = 4 }, heard: heard);
        Assert.False(ran.IsError == true, $"run failed: {Text(ran)}{Said()}");
        IReadOnlyList<ModelContextProtocol.ProgressNotificationValue> told = heard.Told;
        Assert.True(told.Count >= 2, $"A run that waited 4 s for its program told its agent {told.Count} times (every second asked): {heard}");
        // The SDK's client hands the progress over as it comes, not always in order.
        Assert.True(told.Select(t => t.Progress).Distinct().Count() == told.Count, $"The progress told repeated a count: {heard}");
        Assert.True(told.Any(t => t.Message?.StartsWith("Running Sum: waiting up to 4 s for it to end", StringComparison.Ordinal) == true),
            $"The agent was never told the run waits for its program: {heard}");
        // Each word says how long the call has taken so far.
        Assert.Matches(@"\(\d+\.\d s\)$", told[^1].Message ?? string.Empty);
        await Call(client, McpTools.Stop);
        Assert.True(Errors().Length == 0, $"The agent's Passenger wrote to its standard error, which an agent may never read:\n{string.Join('\n', Errors())}");
    }

    /// <summary>Whether a schema says what leaving a parameter out says already: a null default, or null among its types.</summary>
    private static bool SaysNothing(JsonElement schema) => schema.ValueKind switch
    {
        JsonValueKind.Object => schema.EnumerateObject().Any(p => (p.Name == "default" && p.Value.ValueKind == JsonValueKind.Null)
            || (p.Name == "type" && p.Value.ValueKind == JsonValueKind.Array) || SaysNothing(p.Value)),
        JsonValueKind.Array => schema.EnumerateArray().Any(SaysNothing),
        _ => false,
    };
}
