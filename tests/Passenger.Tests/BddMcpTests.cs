using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App.Bdd;
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

    private static readonly string Broken = Path.Combine("Calc.Specs", "Features", "Broken.feature");

    private static Dictionary<string, object?> At(string path, int line, int column) => new() { ["path"] = path, ["line"] = line, ["column"] = column };

    /// <summary>An agent's BDD as one flow, each step one test's check in an instance of its own: steps and their
    /// definitions gone between, a feature's parts listed, a missing step defined and renamed, a feature's first
    /// diagnostics listing its undefined step and the argument its definition cannot take, a feature read in its
    /// project's language or found to hold none, and a scenario's run answering its failing step with its definition,
    /// green once fixed.</summary>
    [Fact]
    public async Task An_agent_works_a_features_steps_definitions_diagnostics_and_runs_as_Reqnroll_reads_them()
    {
        await Flow.StepApartAsync("an agent goes between steps and their definitions lists a features parts defines a missing step and renames one", () => new BddMcpTests(folders), t => t.An_agent_goes_between_steps_and_their_definitions_lists_a_features_parts_defines_a_missing_step_and_renames_one());
        await Flow.StepApartAsync("an agents first diagnostics of a feature lists its undefined step and the argument its definition cannot take", () => new BddMcpTests(folders), t => t.An_agents_first_diagnostics_of_a_feature_lists_its_undefined_step_and_the_argument_its_definition_cannot_take());
        await Flow.StepApartAsync("an agents diagnostics say a feature is read in its projects language and finds no feature and what that leaves unmatched", () => new BddMcpTests(folders), t => t.An_agents_diagnostics_say_a_feature_is_read_in_its_projects_language_and_finds_no_feature_and_what_that_leaves_unmatched());
        await Flow.StepApartAsync("an agents run of a scenario answers its failing step with its definition and arguments and runs green once the definition is fixed", () => new BddMcpTests(folders), t => t.An_agents_run_of_a_scenario_answers_its_failing_step_with_its_definition_and_arguments_and_runs_green_once_the_definition_is_fixed());
    }

    private async Task An_agent_goes_between_steps_and_their_definitions_lists_a_features_parts_defines_a_missing_step_and_renames_one()
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

    /// <summary>An agent's first call of a fresh session, diagnostics on a feature, lists the marks its steps' definitions
    /// make (a step none binds, an argument its definition cannot take), the definitions read as the C# server loads, never
    /// answering as of none read.</summary>
    private async Task An_agents_first_diagnostics_of_a_feature_lists_its_undefined_step_and_the_argument_its_definition_cannot_take()
    {
        string folder = PathNames.Canonical(await folders.TakeAsync("bddmcp-", made => ReqnrollProjects.Write(made)));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, Broken, "Feature: Broken\n\nScenario: Too much\n  Given the second number is 99999999999\n  And a step nobody wrote\n");
        McpClient client = await Start(["--mcp", folder]);

        JsonElement[] found = [.. (await Call(client, McpTools.Diagnostics, new() { ["path"] = Broken })).EnumerateArray()];
        string said = string.Join("; ", found.Select(f => f.GetRawText()));
        Assert.True(found.Length == 2
            && found.Any(f => (f.GetProperty("code").GetString(), f.GetProperty("severity").GetString(), f.GetProperty("line").GetInt32(), f.GetProperty("column").GetInt32())
                == (FeatureAnalysis.Argument, "error", 4, 30))
            && found.Any(f => (f.GetProperty("code").GetString(), f.GetProperty("line").GetInt32()) == (FeatureAnalysis.Undefined, 5)),
            $"diagnostics, an agent's first call, is to list the too large number and the step nobody wrote: {said}");
    }

    /// <summary>An agent's diagnostics where the project reads its features in Austrian German and one is written in English
    /// with no language line, failing the project's load: that feature's finding says the language it is read in and how its
    /// own is named, a German feature beside it says its steps are matched to no definitions as the load failed, never an
    /// empty list, and the folder's toolset names the feature.</summary>
    private async Task An_agents_diagnostics_say_a_feature_is_read_in_its_projects_language_and_finds_no_feature_and_what_that_leaves_unmatched()
    {
        string folder = PathNames.Canonical(await folders.TakeAsync("bddmcp-", made => ReqnrollProjects.Write(made)));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "Calc.Specs/reqnroll.json", "{\n  \"language\": { \"feature\": \"de-AT\" },\n  \"bindingAssemblies\": [ { \"assembly\": \"Calc.Steps\" } ]\n}\n");
        string laden = Path.Combine("Calc.Specs", "Features", "Laden.feature");
        TestFolders.Write(folder, laden, "Funktionalität: Laden\n\nSzenario: Öffnen\n  Angenommen a step nobody wrote\n");
        McpClient client = await Start(["--mcp", folder]);

        JsonElement[] found = [.. (await Call(client, McpTools.Diagnostics, new() { ["path"] = Feature })).EnumerateArray()];
        JsonElement none = Assert.Single(found);
        Assert.True((none.GetProperty("code").GetString(), none.GetProperty("severity").GetString(), none.GetProperty("line").GetInt32(), none.GetProperty("column").GetInt32())
            == (FeatureAnalysis.Language, "error", 2, 1), $"diagnostics is to list the English feature read in German once, where it was to begin: {none}");
        string why = none.GetProperty("message").GetString()!;
        Assert.StartsWith("Reqnroll reads this file in de-AT, the language.feature of its project's reqnroll.json, where a feature begins with Funktionalität:", why, StringComparison.Ordinal);

        JsonElement unmatched = Assert.Single((await Call(client, McpTools.Diagnostics, new() { ["path"] = laden })).EnumerateArray());
        string said = unmatched.GetProperty("message").GetString()!;
        Assert.True(unmatched.GetProperty("severity").GetString() == "info"
            && said.StartsWith($"No step definitions were read to match its steps against (the C# language server took in nothing of {Path.Combine("Calc.Specs", "Calc.Specs.csproj")}, whose load failed: ", StringComparison.Ordinal)
            && said.Contains(why, StringComparison.Ordinal),
            $"diagnostics of the German feature is to say its steps are matched to no definitions as its project's load failed, and why: {unmatched}");

        JsonElement toolset = Assert.Single((await Call(client, McpTools.Diagnostics, new())).GetProperty("toolset").EnumerateArray());
        Assert.Equal($"{Path.Combine("Features", "Calculator.feature")}(2,1): {why}", toolset.GetProperty("message").GetString());
    }

    private async Task An_agents_run_of_a_scenario_answers_its_failing_step_with_its_definition_and_arguments_and_runs_green_once_the_definition_is_fixed()
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
