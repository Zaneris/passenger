using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent debugging a standalone Blazor WebAssembly app's component in the browser: the breakpoint it sets stops the
/// page there, debug_state says where with the component as this, and debug_evaluate reads its field after a step. The app
/// is the SDK's template, restored from the package cache tests/BlazorGraphs fills. Skipped without a Chromium browser.</summary>
[Collection(Alone.Name)]
public sealed class McpBlazorTests : McpRig, IAsyncLifetime
{
    private readonly string app = PathNames.Canonical(TestFolders.Create("blazor-agent-"));

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
        http["applicationUrl"] = $"http://localhost:{AspireApp.FreePort()}";
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
}
