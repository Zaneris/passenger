using System.Globalization;
using Passenger.Workspace.Gherkin;
using Xunit;

namespace Passenger.UnitTests.Gherkin;

/// <summary>A missing step's definition written as Reqnroll's editors write one: its keyword's attribute with a Cucumber
/// Expression (or a regular expression, as the project's skeleton style says), quoted text and numbers made parameters, an
/// outline's placeholders parameters named for them, a doc string and a table taken last, a pending body.</summary>
public sealed class StepSnippetsTests
{
    private static StepSnippet Snippet(string feature, SkeletonStyle style = SkeletonStyle.CucumberExpression, bool specFlow = false, int step = 0)
    {
        FeatureReading reading = FeatureReading.Read(feature, "en");
        return StepSnippets.Of(reading.Steps[step], reading, style, specFlow, CultureInfo.InvariantCulture, []);
    }

    [Fact]
    public void An_undefined_steps_definition_is_written_as_Reqnroll_would_write_it() => Cases.All(
        ("numbers and quoted text become typed parameters of a cucumber expression", () =>
        {
            StepSnippet snippet = Snippet("Feature: F\nScenario: S\n  Given I have entered 50 into the \"main\" calculator\n");

            Assert.Equal(
                "[Given(\"I have entered {int} into the {string} calculator\")]\n" +
                "public void GivenIHaveEnteredIntoTheCalculator(int p0, string main)\n" +
                "{\n" +
                "    throw new PendingStepException();\n" +
                "}\n",
                snippet.Code);
            Assert.Equal("GivenIHaveEnteredIntoTheCalculator", snippet.Method);
        }),
        ("a regular expression style writes groups in a verbatim string", () =>
        {
            StepSnippet snippet = Snippet("Feature: F\nScenario: S\n  When I pay 3.50 for \"a b\"\n", SkeletonStyle.Regex);

            Assert.Equal(
                "[When(@\"I pay (.*) for \"\"([^\"\"]*)\"\"\")]\n" +
                "public void WhenIPayFor(decimal p0, string p1)\n" +
                "{\n" +
                "    throw new PendingStepException();\n" +
                "}\n",
                snippet.Code);
        }),
        ("an outline's placeholder's become parameters named for them typed by their examples", () =>
        {
            StepSnippet snippet = Snippet("Feature: F\nScenario Outline: O\n  Given <name> has <count> apples\n\nExamples:\n  | name  | count |\n  | Ann   | 1     |\n  | Bob   | 22    |\n");

            Assert.StartsWith("[Given(\"{word} has {int} apples\")]\npublic void GivenHasApples(string name, int count)\n", snippet.Code, StringComparison.Ordinal);
        }),
        ("a doc string and a table are taken last and and step's take the kind before them", () =>
        {
            StepSnippet snippet = Snippet("Feature: F\nScenario: S\n  Given one\n  And the users\n    | name |\n    | Ann  |\n", step: 1);
            StepSnippet docString = Snippet("Feature: F\nScenario: S\n  When I post\n    \"\"\"\n    {}\n    \"\"\"\n", SkeletonStyle.Regex, specFlow: true);

            Assert.StartsWith("[Given(\"the users\")]\npublic void GivenTheUsers(DataTable dataTable)\n", snippet.Code, StringComparison.Ordinal);
            Assert.StartsWith("[When(@\"I post\")]\npublic void WhenIPost(string multilineText)\n", docString.Code, StringComparison.Ordinal);
        }),
        ("a cucumber expression's special characters are escaped and a taken name is numbered", () =>
        {
            FeatureReading reading = FeatureReading.Read("Feature: F\nScenario: S\n  Given a/b (c) {d}\n", "en");

            StepSnippet snippet = StepSnippets.Of(reading.Steps[0], reading, SkeletonStyle.CucumberExpression, false, CultureInfo.InvariantCulture, ["GivenABCD"]);

            Assert.StartsWith("[Given(\"a\\\\/b \\\\(c) \\\\{d}\")]\npublic void GivenABCD2()\n", snippet.Code, StringComparison.Ordinal);
        }),
        ("a binding class is named for its feature's title in a csharp names characters keeping a script's marks", () =>
        {
            Assert.Equal("लॉगिनStepDefinitions", StepSnippets.ClassFor("लॉगिन"));
            Assert.Equal("UserLoginPageStepDefinitions", StepSnippets.ClassFor("user login-page"));
            Assert.Equal("CantLoginStepDefinitions", StepSnippets.ClassFor("Can't login"));
            Assert.Equal("_३StepsStepDefinitions", StepSnippets.ClassFor("३ steps"));
            Assert.Equal("ABStepDefinitions", StepSnippets.ClassFor("A\u200DB"));
            Assert.Equal("LoginStepDefinitions", StepSnippets.ClassFor("\U0001D49C login"));
            Assert.Equal("FeatureStepDefinitions", StepSnippets.ClassFor("!?"));
        }),
        ("a step's method and a placeholder's parameter keep a script's marks", () =>
        {
            StepSnippet snippet = Snippet("Feature: F\nScenario Outline: O\n  Given मैं <नाम> से लॉगिन करता हूँ\n\nExamples:\n  | नाम |\n  | राम |\n");

            Assert.StartsWith("[Given(\"मैं {word} से लॉगिन करता हूँ\")]\npublic void Givenमैंसेलॉगिनकरताहूँ(string नाम)\n", snippet.Code, StringComparison.Ordinal);
        }),
        ("a placeholder named as a csharp keyword is a verbatim parameter", () =>
        {
            StepSnippet snippet = Snippet("Feature: F\nScenario Outline: O\n  Given the <class> list\n\nExamples:\n  | class |\n  | a     |\n");

            Assert.Contains("(string @class)", snippet.Code, StringComparison.Ordinal);
        }));

}
