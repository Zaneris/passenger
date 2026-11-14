using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A solution's shared project through an agent's own Passenger (<c>--mcp</c>): workspace lists it with the
/// projects importing it, and find_in_files' project scope takes it by name, searching the files its items file names.</summary>
[Collection(Beside.Name)]
public sealed class McpSharedProjectTests : McpRig
{
    [Fact]
    public async Task Workspace_lists_a_shared_project_with_its_importers_and_find_in_files_searches_it_by_name()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-shproj-"));
        DeleteAtEnd(folder);
        ShprojTests.Write(folder);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        string listed = string.Empty;
        await Waiting.UntilAsync(async () => (listed = Shared(await Call(client, McpTools.Workspace))) == "Shop.Shared at Shop.Shared/Shop.Shared.shproj shared by Shop.Api, Shop.Web",
            TimeSpan.FromSeconds(60), Cancel, TimeSpan.FromMilliseconds(200));
        Assert.Equal("Shop.Shared at Shop.Shared/Shop.Shared.shproj shared by Shop.Api, Shop.Web", listed);

        JsonElement found = await Call(client, McpTools.FindInFiles, new() { ["text"] = "namespace", ["scope"] = "project", ["project"] = "Shop.Shared" });
        Assert.Equal(["Shop.Shared/Greeting.cs", "Shop.Shared/Models/Order.cs"],
            found.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("path").GetString()!.Replace('\\', '/')).Order(StringComparer.Ordinal));
    }

    /// <summary>What workspace says of its shared projects: each one's name, path and importers, empty with none.</summary>
    private static string Shared(JsonElement workspace) => string.Join("; ", workspace.GetProperty("projects").EnumerateArray()
        .Where(p => p.TryGetProperty("sharedBy", out _))
        .Select(p => $"{p.GetProperty("name").GetString()} at {p.GetProperty("path").GetString()!.Replace('\\', '/')} shared by "
            + string.Join(", ", p.GetProperty("sharedBy").EnumerateArray().Select(i => i.GetString()).Order(StringComparer.Ordinal))));
}
