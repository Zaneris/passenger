using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App.Mcp;

namespace Passenger.Tests;

/// <summary>A solution's multi-project launch profile (Visual Studio's <c>.slnLaunch</c>) as an agent works with it: workspace
/// lists it with what it does with each project, and run with it as the configuration runs each project in the launch
/// profile it names, answering once each has ended with how each ended and what each wrote.</summary>
public sealed class McpLaunchProfileTests : McpRig, IClassFixture<RestoredFolders>
{
    private readonly RestoredFolders folders;

    public McpLaunchProfileTests(RestoredFolders folders)
        : base(folders) => this.folders = folders;

    [Fact]
    public async Task An_agents_run_of_a_launch_profile_runs_each_project_and_answers_with_each_ones_end_and_output()
    {
        string folder = await folders.TakeAsync("mcp-launch-", root => SolutionLaunchProfileTests.Shop(root, waits: false));
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        Assert.True(await Waiting.UntilAsync(async () => (await Call(client, McpTools.Workspace)).GetProperty("runConfigurations").EnumerateArray()
            .Select(c => c.GetString()).SequenceEqual(["Api: first", "Api: second", "Worker", "Both"]), TimeSpan.FromMinutes(2), Cancel),
            $"The solution's launch profile was not listed after its projects' own.{Said()}");
        JsonElement workspace = await Call(client, McpTools.Workspace);
        Assert.Equal(["Both: Api Start in second, Worker StartWithoutDebugging"], workspace.GetProperty("multiProject").EnumerateArray().Select(p => p.GetString()));

        JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = "Both", ["waitSeconds"] = 120 });
        string status = ran.GetProperty("status").GetString()!;
        string output = ran.GetProperty("output").GetString()!;
        Assert.True(status == "Running Both: Api: second and Worker, each in a tab of the Terminal window. Api: second ended with exit code 0; Worker ended with exit code 3",
            $"run of the profile did not say how each of its projects ended: {ran}");
        int[] places = [.. ((string[])["Api: second:\n", "Api says second", "Worker:\n", "Worker up"]).Select(said => output.IndexOf(said, StringComparison.Ordinal))];
        Assert.True(places[0] >= 0 && places.SequenceEqual(places.Order()),
            $"run of the profile did not answer with each project's own output under its name, Api's in the launch profile the file names: {ran}");
        Assert.False(ran.GetProperty("stillRunning").GetBoolean(), $"run said the profile still runs once both ended: {ran}");
    }
}
