using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>What an agent's own Passenger (<c>--mcp</c>) knows of its folder: workspace calls an Android app, which .NET for
/// Android evaluates as a Library, the Exe it runs as, and a library stays a Library; and a file made while the folder's
/// watcher heard nothing is found at the agent's next call.</summary>
[Collection(Beside.Name)]
public sealed class McpWorkspaceTests : McpRig
{
    [Fact]
    public async Task Workspace_calls_an_android_app_an_exe_as_it_runs_where_its_evaluation_says_library()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-android-app-"));
        DeleteAtEnd(folder);
        // An app's evaluation as .NET for Android leaves it, a .NET MAUI app's for its Android framework among them.
        TestFolders.Write(folder, "Phone/Phone.csproj", TestProjects.Library("<AndroidApplication>true</AndroidApplication>"));
        TestFolders.Write(folder, "Phone/MainActivity.cs", "class MainActivity\n{\n}\n");
        TestFolders.Write(folder, "Parts/Parts.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Parts/Part.cs", "class Part\n{\n}\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        const string Expected = "Parts: Library, Phone: Exe; runs Phone";
        string said = string.Empty;
        await Waiting.UntilAsync(async () => (said = Said(await Call(client, McpTools.Workspace))) == Expected, TimeSpan.FromSeconds(60), Cancel, TimeSpan.FromMilliseconds(200));
        Assert.True(said == Expected, $"workspace did not call the Android app an Exe, the run configuration it lists, and the library a Library: {said}");
    }

    /// <summary>A file made while the folder's watcher heard nothing, as the system drops a change without a word, is found at
    /// the agent's next call once it has been away a while, as a person's window finds it coming in front, which an agent's
    /// own IDE never does: find_in_files, reading the folder's files as Go to File lists them, finds what it holds.</summary>
    [Fact]
    public async Task A_file_made_while_the_watcher_heard_nothing_is_found_at_the_agents_next_call_after_a_quiet_spell()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-unheard-"));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "Shop.cs", "class Shop\n{\n}\n");
        await using McpClient client = await Start(["--mcp", folder], environment: new() { [WatchedTree.UnheardVariable] = "Unheard.cs" }, languageServer: false);
        Assert.Equal(["Shop.cs:1"], Found(await Call(client, McpTools.FindInFiles, new() { ["text"] = "class" })));

        TestFolders.Write(folder, "Unheard.cs", "class Unheard\n{\n}\n");
        await Waiting.ForAsync(MainWindow.AgentAway + TimeSpan.FromMilliseconds(250), "the agent away a while, as when it works through its own shell", Cancel);
        string[] found = Found(await Call(client, McpTools.FindInFiles, new() { ["text"] = "class" }));
        Assert.True(found.SequenceEqual(["Shop.cs:1", "Unheard.cs:1"]),
            $"find_in_files at the agent's next call found [{string.Join(", ", found)}], missing the file made while the folder's watcher heard nothing");
    }

    /// <summary>Where find_in_files found its matches, by file and line.</summary>
    private static string[] Found(JsonElement found) =>
        [.. found.GetProperty("matches").EnumerateArray().Select(m => $"{m.GetProperty("path").GetString()}:{m.GetProperty("line").GetInt32()}").Order(StringComparer.Ordinal)];

    /// <summary>Each project's output type by name, then the run configurations.</summary>
    private static string Said(JsonElement workspace) =>
        string.Join(", ", workspace.GetProperty("projects").EnumerateArray()
            .Select(p => $"{p.GetProperty("name").GetString()}: {(p.TryGetProperty("outputType", out JsonElement type) ? type.GetString() : "none")}").Order(StringComparer.Ordinal))
        + "; runs " + string.Join(", ", workspace.GetProperty("runConfigurations").EnumerateArray().Select(c => c.GetString()));
}
