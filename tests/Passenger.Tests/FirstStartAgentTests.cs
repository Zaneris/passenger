using ModelContextProtocol.Client;
using Passenger.App.Mcp;
using Passenger.App.Welcome;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent's own Passenger (<c>--mcp</c>) over a new state asks nothing of the first start: nothing lies over its
/// window's rows, as the agent finds them.</summary>
public sealed class FirstStartAgentTests : McpRig
{
    [Fact]
    public async Task An_agents_Passenger_over_a_new_state_shows_no_first_start_question()
    {
        string folder = PathNames.Canonical(TestFolders.Create("first-start-agent-"));
        DeleteAtEnd(folder);
        Assert.Empty(Directory.EnumerateFileSystemEntries(OwnState));
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        string asked = Text(await Call(client, McpTools.AppTree, new() { ["app"] = "ide", ["filter"] = nameof(FirstStartView) }));
        Assert.True(asked.StartsWith("Nothing in ", StringComparison.Ordinal), $"An agent's Passenger over a new state shows the first start's question:\n{asked}");
        // Where the question would lie, which the same look finds.
        string rows = Text(await Call(client, McpTools.AppTree, new() { ["app"] = "ide", ["filter"] = "#Rows" }));
        Assert.False(rows.StartsWith("Nothing in ", StringComparison.Ordinal), $"The look finds not even the window's rows, so finding no question proves nothing:\n{rows}");
    }
}
