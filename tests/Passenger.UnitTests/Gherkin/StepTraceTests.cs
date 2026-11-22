using Passenger.Workspace.Gherkin;
using Xunit;

namespace Passenger.UnitTests.Gherkin;

public sealed class StepTraceTests
{
    private const string Feature =
        "Feature: Steps\n" +
        "\n" +
        "Background:\n" +
        "  Given the calculator is cleared\n" +
        "\n" +
        "Scenario: Mixed outcomes\n" +
        "  Given the first number is 50\n" +
        "  And the second number is 70\n" +
        "  When the two numbers are added\n" +
        "  Then the result should be 121\n" +
        "  And nothing is ever said\n" +
        "\n" +
        "Scenario: Pending one\n" +
        "  When a step is pending\n" +
        "  Then nothing is ever said\n" +
        "\n" +
        "Scenario: Undefined one\n" +
        "  Given a step nobody wrote\n" +
        "\n" +
        "Scenario: Ambiguous one\n" +
        "  Given an ambiguous step\n" +
        "\n" +
        "Scenario Outline: Add numbers\n" +
        "  Given the first number is <a>\n" +
        "  Then the result should be <sum>\n" +
        "\n" +
        "Examples:\n" +
        "  | a | sum |\n" +
        "  | 1 | 3   |\n" +
        "  | 5 | 11  |\n";

    private static (FeatureReading Reading, FeatureScenario Scenario) Scenario(string title)
    {
        FeatureReading reading = FeatureReading.Read(Feature, GherkinDialects.English);
        return (reading, reading.Scenarios.Single(s => s.Title == title));
    }

    [Fact]
    public void A_runs_step_outcomes_are_read_from_the_step_trace_in_each_tests_output() => Cases.All(
        ("a scenario's step's are its background's then its own each said as its keyword and text", () =>
        {
            (FeatureReading reading, FeatureScenario scenario) = Scenario("Mixed outcomes");
            IReadOnlyList<RunStep> steps = StepTrace.StepsOf(reading, scenario);
            Assert.Equal([3, 6, 7, 8, 9, 10], steps.Select(s => s.Line));
            Assert.Equal("Given the calculator is cleared", steps[0].Said);
            Assert.Equal("And the second number is 70", steps[2].Said);
        }),
        ("a run with a failing step reads each step before it passed with its binding it failed with its whole message and the rest skipped", () =>
        {
            (FeatureReading reading, FeatureScenario scenario) = Scenario("Mixed outcomes");
            const string output =
                "Given the calculator is cleared\n" +
                "-> done: CalculatorStepDefinitions.Clear() (0.0s)\n" +
                "Given the first number is 50\n" +
                "-> done: CalculatorStepDefinitions.GivenTheFirstNumberIs(50) (0.0s)\n" +
                "And the second number is 70\n" +
                "said by the binding\n" +
                "-> done: CalculatorStepDefinitions.GivenTheSecondNumberIs(70) (0.0s)\n" +
                "When the two numbers are added\n" +
                "-> done: CalculatorStepDefinitions.WhenTheTwoNumbersAreAdded() (0.0s)\n" +
                "Then the result should be 121\n" +
                "-> error: Assert.Equal() Failure: Values differ\n" +
                "Expected: 121\n" +
                "Actual:   120 (0.0s)\n" +
                "And nothing is ever said\n" +
                "-> skipped because of previous errors\n";
            IReadOnlyList<StepResult> results = StepTrace.Read(output, StepTrace.StepsOf(reading, scenario), passed: false);
            Assert.Equal([StepOutcome.Passed, StepOutcome.Passed, StepOutcome.Passed, StepOutcome.Passed, StepOutcome.Failed, StepOutcome.Skipped], results.Select(r => r.Outcome));
            Assert.Equal(("CalculatorStepDefinitions.GivenTheFirstNumberIs", "50"), (results[1].Binding, results[1].Arguments));
            Assert.Equal(9, results[4].Line);
            Assert.Equal("Assert.Equal() Failure: Values differ\nExpected: 121\nActual:   120", results[4].Message);
        }),
        ("pending undefined and ambiguous step's are read as such from Reqnroll's words coloured or not", () =>
        {
            (FeatureReading reading, FeatureScenario pending) = Scenario("Pending one");
            IReadOnlyList<StepResult> results = StepTrace.Read(
                "Given the calculator is cleared\n-> done: S.Clear() (0.0s)\nWhen a step is pending\n" +
                "-> \u001b[33mpending\u001b[0m: S.Pending(): The step definition is not implemented.\nThen nothing is ever said\n-> skipped because of previous errors\n",
                StepTrace.StepsOf(reading, pending), passed: false);
            Assert.Equal([StepOutcome.Passed, StepOutcome.Pending, StepOutcome.Skipped], results.Select(r => r.Outcome));
            Assert.Equal(("S.Pending", "", "The step definition is not implemented."), (results[1].Binding, results[1].Arguments, results[1].Message));

            IReadOnlyList<StepResult> undefined = StepTrace.Read(
                "Given the calculator is cleared\n-> done: S.Clear() (0.0s)\nGiven a step nobody wrote\n" +
                "-> undefined: No matching step definition found for the step. Use the following code to create one:\n        [Given(\"a step nobody wrote\")]\n",
                StepTrace.StepsOf(reading, Scenario("Undefined one").Scenario), passed: false);
            Assert.Equal(StepOutcome.Undefined, undefined[^1].Outcome);

            IReadOnlyList<StepResult> ambiguous = StepTrace.Read(
                "Given the calculator is cleared\n-> done: S.Clear() (0.0s)\nGiven an ambiguous step\n" +
                "-> binding error: Ambiguous step definitions found for step 'Given an ambiguous step': A:S.One(), A:S.Two(String)\n",
                StepTrace.StepsOf(reading, Scenario("Ambiguous one").Scenario), passed: false);
            Assert.Equal(StepOutcome.Ambiguous, ambiguous[^1].Outcome);
            Assert.StartsWith("Ambiguous step definitions found", ambiguous[^1].Message, StringComparison.Ordinal);
        }),
        ("a step whose success is not traced passed where a later step was reached or the test passed", () =>
        {
            (FeatureReading reading, FeatureScenario scenario) = Scenario("Pending one");
            IReadOnlyList<RunStep> steps = StepTrace.StepsOf(reading, scenario);
            IReadOnlyList<StepResult> passed = StepTrace.Read("Given the calculator is cleared\nWhen a step is pending\nThen nothing is ever said\n", steps, passed: true);
            Assert.Equal([StepOutcome.Passed, StepOutcome.Passed, StepOutcome.Passed], passed.Select(r => r.Outcome));
            IReadOnlyList<StepResult> cut = StepTrace.Read("Given the calculator is cleared\nWhen a step is pending\n", steps, passed: false);
            Assert.Equal([StepOutcome.Passed], cut.Select(r => r.Outcome));
        }),
        ("an outline's step's take the values of the row its test names by Reqnroll's pickle index or else by its values", () =>
        {
            FeatureReading reading = FeatureReading.Read(Feature, GherkinDialects.English);
            FeatureScenario outline = reading.Scenarios.Single(s => s.Kind == ScenarioKind.Outline);
            IReadOnlyList<ExampleRow> rows = ExampleRows.Of(reading);
            Assert.Equal([4, 5], rows.Select(r => r.Pickle));

            ExampleRow? indexed = ExampleRows.RowOf(rows, outline, "Add numbers(a: \"5\", sum: \"11\", __pickleIndex: \"5\", exampleTags: [])");
            Assert.Equal(29, indexed?.Row.Line);
            Assert.Equal(28, ExampleRows.RowOf(rows, outline, "AddNumbers(\"1\",\"3\",null)")?.Row.Line);
            Assert.Null(ExampleRows.RowOf(rows, outline, "AddNumbers(\"2\",\"3\",null)"));

            IReadOnlyList<RunStep> steps = StepTrace.StepsOf(reading, outline, indexed!.Values);
            Assert.Equal(["Given the calculator is cleared", "Given the first number is 5", "Then the result should be 11"], steps.Select(s => s.Said));
        }),
        ("a test names arguments are read with their names their strings unescaped", () =>
        {
            IReadOnlyList<(string? Name, string Value)> arguments = ExampleRows.ArgumentsOf("Add (twice)(a: \"x, \\\"y\\\"\", b: 2, tags: [\"t\", \"u\"])");
            Assert.Equal([("a", "x, \"y\""), ("b", "2"), ("tags", "[\"t\", \"u\"]")], arguments);
            Assert.Empty(ExampleRows.ArgumentsOf("Add numbers"));
        }));

}
