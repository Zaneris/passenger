using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Agents working a Reqnroll solution's features as the window does (<see cref="ReqnrollProjects"/>): the definition
/// of a step, the steps of a definition (from either), its outline, the fix defining a missing step, Rename Step from a step,
/// and a scenario's run answered step by step. Its Passengers are processes of their own, so it runs beside the window tests (<see cref="Beside"/>).</summary>
[Collection(Beside.Name)]
public sealed class BddMcpTests(RestoredFolders folders) : McpRig, IClassFixture<RestoredFolders>
{
    /// <summary>The files by their paths in the folder as an agent is answered them, in the system's separators.</summary>
    private static readonly string Feature = Path.Combine("Calc.Specs", "Features", "Calculator.feature");

    private static readonly string Steps = Path.Combine("Calc.Specs", "StepDefinitions", "CalculatorStepDefinitions.cs");

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

    [Fact]
    public async Task An_agents_run_of_a_scenario_answers_its_failing_step_with_its_definition_and_arguments_and_runs_green_once_the_definition_is_fixed()
    {
        string folder = PathNames.Canonical(await folders.TakeAsync("bddmcp-", made => ReqnrollProjects.Write(made)));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, Steps, ReqnrollProjects.Steps.Replace("Assert.Equal(expected, result);", "Assert.Equal(expected, result + 1);", StringComparison.Ordinal));
        McpClient client = await Start(["--mcp", folder]);

        JsonElement ran = await Call(client, McpTools.TestsRun, new() { ["filter"] = "AddTwoNumbers" });
        JsonElement failure = Assert.Single(ran.GetProperty("failures").EnumerateArray());
        JsonElement[] steps = [.. failure.GetProperty("steps").EnumerateArray()];
        Assert.True(steps.Select(s => s.GetProperty("outcome").GetString()).SequenceEqual(["passed", "passed", "passed", "passed", "failed"]),
            $"tests_run is to answer each step of the scenario as it went: {failure}");
        JsonElement failed = steps[^1];
        Assert.Equal(("Then the result should be 120", $"{Feature}:12"), (failed.GetProperty("step").GetString(), failed.GetProperty("location").GetString()));
        string definition = failed.GetProperty("definition").GetString()!;
        Assert.True(definition.StartsWith($"{Steps}:", StringComparison.Ordinal) && definition.EndsWith("ThenTheResultShouldBe", StringComparison.Ordinal),
            $"The failing step's definition is to be named at its place: {definition}");
        Assert.Contains("120", failed.GetProperty("arguments").GetString()!, StringComparison.Ordinal);
        Assert.StartsWith("Assert.Equal() Failure", failed.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Contains(failed.GetProperty("frames").EnumerateArray(), f => f.GetString()!.Contains($"{Steps}:", StringComparison.Ordinal));

        await Call(client, McpTools.Edit, new() { ["path"] = Steps, ["oldText"] = "Assert.Equal(expected, result + 1);", ["newText"] = "Assert.Equal(expected, result);" });
        JsonElement again = await Call(client, McpTools.TestsRun, new() { ["filter"] = "AddTwoNumbers" });
        Assert.True(again.GetProperty("failures").GetArrayLength() == 0 && again.GetProperty("summary").GetString()!.StartsWith("Tests passed: 1", StringComparison.Ordinal),
            $"The scenario is to pass once its definition is fixed: {again}");
    }
}
