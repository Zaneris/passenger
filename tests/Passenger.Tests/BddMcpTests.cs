using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Agents working a Reqnroll solution's features as the window does (<see cref="ReqnrollProjects"/>): the definition
/// of a step, the steps of a definition (from either), its outline, the fix defining a missing step, and Rename Step from a
/// step. Its Passengers are processes of their own, so it runs beside the window tests (<see cref="Beside"/>).</summary>
[Collection(Beside.Name)]
public sealed class BddMcpTests(RestoredFolders folders) : McpRig, IClassFixture<RestoredFolders>
{
    private const string Feature = "Calc.Specs/Features/Calculator.feature";
    private const string Steps = "Calc.Specs/StepDefinitions/CalculatorStepDefinitions.cs";

    private static Dictionary<string, object?> At(string path, int line, int column) => new() { ["path"] = path, ["line"] = line, ["column"] = column };

    [Fact]
    public async Task An_agent_goes_between_steps_and_their_definitions_lists_a_features_parts_defines_a_missing_step_and_renames_one()
    {
        string folder = PathNames.Canonical(await folders.TakeAsync("bddmcp-", made => ReqnrollProjects.Write(made)));
        DeleteAtEnd(folder);
        McpClient client = await Start(["--mcp", folder]);

        JsonElement declared = Assert.Single((await Call(client, McpTools.Definition, At(Feature, 9, 12))).EnumerateArray());
        Assert.Equal((Steps, 12), (declared.GetProperty("path").GetString(), declared.GetProperty("line").GetInt32()));

        JsonElement fromStep = await Call(client, McpTools.Usages, At(Feature, 9, 12));
        Assert.Equal(2, fromStep.GetProperty("count").GetInt32());
        JsonElement fromDefinition = await Call(client, McpTools.Usages, At(Steps, 12, 20));
        string[] files = [.. fromDefinition.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!)];
        Assert.Contains(Feature, files);
        Assert.Contains(Steps, files);

        string outline = (await Call(client, McpTools.Outline, new() { ["path"] = Feature })).GetString()!;
        Assert.Contains("Scenario Outline: Add numbers (line 14)", outline, StringComparison.Ordinal);

        string[] fixes = [.. (await Call(client, McpTools.CodeActions, At(Feature, 26, 10))).EnumerateArray().Select(a => a.GetString()!)];
        Assert.Equal(["Define step 'a step nobody wrote' [quickfix]"], fixes);

        string renamed = (await Call(client, McpTools.Rename, new(At(Feature, 9, 12)) { ["newName"] = "the initial number is {int}" })).GetString()!;
        Assert.StartsWith("Renamed the step to \"the initial number is {int}\"", renamed, StringComparison.Ordinal);
        Assert.Contains("[Given(\"the initial number is {int}\")]", File.ReadAllText(Path.Combine(folder, Steps)), StringComparison.Ordinal);
        Assert.Contains("  Given the initial number is 50\n", File.ReadAllText(Path.Combine(folder, Feature)), StringComparison.Ordinal);
    }
}
