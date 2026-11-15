using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using Passenger.App.Mcp;
using Passenger.Core;
using Passenger.Dap;

namespace Passenger.Tests;

/// <summary>An agent debugging a standalone Blazor WebAssembly app's component in the browser: the breakpoint it sets stops the
/// page there, debug_state says where with the component as this, and debug_evaluate reads its field after a step; a hit
/// count counts the hits its condition held on, as every debugger's does. The app is the SDK's template, restored from the
/// package cache tests/BlazorGraphs fills. Skipped without a Chromium browser.</summary>
[Collection(Alone.Name)]
public sealed class McpBlazorTests : McpRig, IAsyncLifetime
{
    private readonly string app = PathNames.Canonical(TestFolders.Create("blazor-agent-"));

    /// <summary>The development server's port, held until the agent's Debug begins its build: the server has no code of the
    /// test's to wait at a gate.</summary>
    private readonly HeldPort port = new();

    private const string Counter = """
        @page "/counter"

        <p role="status">Current count: @currentCount</p>

        @code {
            private int currentCount;

            protected override void OnInitialized()
            {
                currentCount = 41;
                currentCount++;
            }
        }

        """;

    public McpBlazorTests() =>
        Assert.SkipWhen(ChromiumBrowsers.Find(null) is null, "No Chromium browser (Chrome, Edge, Chromium or Brave) is on this machine to debug WebAssembly in");

    public async ValueTask InitializeAsync()
    {
        ChildResult made = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(
            ["new", "blazorwasm", "--output", app, "--name", "Wasm", "--framework", TestProjects.Framework, "--no-restore"]), limit: TimeSpan.FromMinutes(3).Scaled());
        Assert.True(made.Succeeded, $"dotnet new blazorwasm did not make the agent's app (exit code {made.ExitCode}):\n{made.Output}{made.Error}");
        await File.WriteAllTextAsync(Path.Combine(app, "Pages", "Counter.razor"), Counter);
        string settings = Path.Combine(app, "Properties", "launchSettings.json");
        JsonNode launch = JsonNode.Parse(await File.ReadAllTextAsync(settings))!;
        JsonNode http = launch["profiles"]!["http"]!;
        http["applicationUrl"] = $"http://localhost:{port.Port}";
        http["launchUrl"] = "counter";
        http["launchBrowser"] = false;
        await File.WriteAllTextAsync(settings, launch.ToJsonString());
        TestFolders.Write(app, "nuget.config", "<configuration>\n  <packageSources><clear /><add key=\"local\" value=\"feed\" /></packageSources>\n</configuration>\n");
        Directory.CreateDirectory(Path.Combine(app, "feed"));
        await Restore.RunAsync(Path.Combine(app, "Wasm.csproj"));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public override void Dispose()
    {
        base.Dispose();
        port.Dispose();
        TestFolders.Delete(app);
    }

    /// <summary>The agent's breakpoint in the component stops the page as it renders it, debug_state naming the line with the
    /// component as this, and after a step debug_evaluate reads the field the line set.</summary>
    [Fact]
    public async Task An_agent_stops_a_WebAssembly_component_in_the_browser_and_reads_its_field()
    {
        await using McpClient client = await Start(["--mcp", app], languageServer: false);
        int line = Array.FindIndex(Counter.Split('\n'), l => l.Contains("currentCount = 41;", StringComparison.Ordinal)) + 1;
        JsonElement set = Assert.Single((await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Pages/Counter.razor", ["line"] = line })).EnumerateArray());
        Assert.Equal("set", set.GetProperty("status").GetString());
        port.LetGo();
        await Call(client, McpTools.DebugStart, new() { ["configuration"] = "Wasm: http", ["waitSeconds"] = 180 });
        JsonElement state = default;
        await Until(async () => (state = await Call(client, McpTools.DebugState)).GetProperty("state").GetString() == "paused",
            "the page's component to stop in the browser at the agent's breakpoint");
        Assert.StartsWith($"{Path.Combine("Pages", "Counter.razor")}:{line}:", state.GetProperty("location").GetString(), StringComparison.Ordinal);
        Assert.Contains(state.GetProperty("locals").EnumerateArray(), l => l.GetProperty("name").GetString() == "this");
        await Call(client, McpTools.DebugStep, new() { ["kind"] = "over" });
        Assert.Equal("41", (await Call(client, McpTools.DebugEvaluate, new() { ["expression"] = "currentCount" })).GetProperty("value").GetString());
        await Call(client, McpTools.Stop);
    }

    /// <summary>A component whose loop adds each turn's number, the thirteenth pausing at a line of its own, and a name it shows.</summary>
    private const string Looping = """
        @page "/counter"

        <p role="status">Current count: @currentCount</p>
        <p>@name</p>

        @code {
            private int currentCount;
            private string name = "Ada";

            protected override void OnInitialized()
            {
                for (int i = 0; i < 30; i++)
                {
                    currentCount += i;
                    if (i == 12)
                    {
                        currentCount += 0;
                    }
                }
                currentCount++;
            }
        }

        """;

    /// <summary>A hit count counts only hits whose condition held, through an edit of its message: i &gt; 10 and ==4 stop the
    /// page at i 14, as every debugger counts (the managed debugger's and GDScript's tests stop alike); one that neither stops
    /// nor logs says where it was reached. The edited message logs \{ and \} as braces, a string quoted unless ,nq and a
    /// whole number in hexadecimal under ,h, as every debugger does; one writing {{ is refused saying how to write a brace.</summary>
    [Fact]
    public async Task A_hit_count_counts_the_hits_its_condition_held_on_through_an_edit_of_its_message_logged_as_every_debugger_logs_it()
    {
        await File.WriteAllTextAsync(Path.Combine(app, "Pages", "Counter.razor"), Looping, TestContext.Current.CancellationToken);
        await using McpClient client = await Start(["--mcp", app], languageServer: false);
        string[] lines = Looping.Split('\n');
        int counted = Array.FindIndex(lines, l => l.Contains("currentCount += i;", StringComparison.Ordinal)) + 1;
        int paused = Array.FindIndex(lines, l => l.Contains("currentCount += 0;", StringComparison.Ordinal)) + 1;
        int done = Array.FindIndex(lines, l => l.Contains("currentCount++;", StringComparison.Ordinal)) + 1;
        string counter = Path.Combine("Pages", "Counter.razor");
        Dictionary<string, object?> counting = new() { ["path"] = "Pages/Counter.razor", ["line"] = counted, ["condition"] = "i > 10", ["hitCondition"] = "==4", ["logMessage"] = "first {i}" };
        await Call(client, McpTools.BreakpointSet, counting);
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Pages/Counter.razor", ["line"] = paused });
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Pages/Counter.razor", ["line"] = done, ["suspend"] = false });
        port.LetGo();
        await Call(client, McpTools.DebugStart, new() { ["configuration"] = "Wasm: http", ["waitSeconds"] = 180 });
        JsonElement state = default;
        await Until(async () => (state = await Call(client, McpTools.DebugState)).GetProperty("state").GetString() == "paused", "the page's component to stop at its loop's pause");
        Assert.StartsWith($"{counter}:{paused}:", state.GetProperty("location").GetString(), StringComparison.Ordinal);

        await Call(client, McpTools.BreakpointSet, new(counting) { ["logMessage"] = "second \\{{i}\\} {name} {name,nq} {i,h}" });
        JsonElement doubled = (await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Pages/Counter.razor", ["line"] = paused, ["logMessage"] = "{{i}}" }))
            .EnumerateArray().Single(b => b.GetProperty("line").GetInt32() == paused);
        string? refused = doubled.TryGetProperty("message", out JsonElement message) ? message.GetString() : null;
        Assert.True(refused == DisplayFormat.Misread("{{i}}"), $"The tracepoint writing a brace as {{{{ was taken, to log what it was not meant to, or refused saying something else: {doubled}");
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Pages/Counter.razor", ["line"] = paused, ["remove"] = true });
        await Call(client, McpTools.DebugContinue, new() { ["waitSeconds"] = 60 });
        string location = (await Call(client, McpTools.DebugState)).GetProperty("location").GetString()!;
        string i = (await Call(client, McpTools.DebugEvaluate, new() { ["expression"] = "i" })).GetProperty("value").GetString()!;
        Assert.True(location.StartsWith($"{counter}:{counted}:", StringComparison.Ordinal) && i == "14",
            $"The breakpoint counting where i > 10 stopped at {location} with i {i}, not on its fourth such hit at line {counted} with i 14");
        await Call(client, McpTools.DebugContinue, new() { ["waitSeconds"] = 5 });
        string said = string.Empty;
        await Until(async () => (said = Text(await Call(client, McpTools.Output, new() { ["of"] = "debug" }))).Contains("Breakpoint reached", StringComparison.Ordinal),
            "the breakpoint that neither stops nor logs to be reached");
        Assert.True(said.Contains("second {14} \"Ada\" Ada 0x0000000e\n", StringComparison.Ordinal) && !said.Contains("first 1", StringComparison.Ordinal)
            && said.Contains($"Breakpoint reached at Counter.razor:{done}", StringComparison.Ordinal),
            $"The page's console did not say the edited message at the stop, then where the breakpoint that neither stops nor logs was reached: {said}");
        await Call(client, McpTools.Stop);
    }
}
