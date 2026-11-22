using System.Globalization;
using Passenger.Workspace.Gherkin;
using Xunit;

namespace Passenger.UnitTests.Gherkin;

/// <summary>A step matched to its step definitions as Reqnroll's runtime matches it: Cucumber Expressions and regular
/// expressions told apart as Reqnroll tells them, each matching the whole text, parameters counted and converted, scopes
/// narrowing the choice, an outline's steps matched once per example; an argument a definition cannot take placed where it
/// is written, saying what the definition takes.</summary>
public sealed class StepMatcherTests
{
    private const string Int = "System.Int32";
    private const string Text = "System.String";

    private static StepBinding Binding(BindingKind kind, string? expression, string method, params StepParameter[] parameters) =>
        new(kind, expression, method, "Steps.CalculatorSteps", parameters);

    private static StepParameter P(string name, string type) => new(name, type);

    private static StepMatcher Matcher(params StepBinding[] bindings) => new(bindings, [], CultureInfo.InvariantCulture);

    private static StepMatch Match(StepMatcher matcher, StepKind kind, string text, string[]? tags = null) =>
        matcher.Match(kind, text, false, false, new StepContext("Calculator", "Add", tags ?? []));

    private static void ToldApart(string expression, bool cucumber)
    {
        Assert.Equal(cucumber, StepExpressions.IsCucumberExpression(expression));
    }

    [Fact]
    public void A_step_is_matched_to_its_definitions_as_Reqnrolls_runtime_matches_it() => Cases.All(
        ("a Cucumber Expression's int matches a number and says where the argument is", () =>
        {
            StepMatcher matcher = Matcher(Binding(BindingKind.Given, "the first number is {int}", "GivenTheFirstNumberIs", P("number", Int)));

            StepMatch match = Match(matcher, StepKind.Given, "the first number is 50");

            Assert.Equal(MatchStatus.Defined, match.Status);
            Assert.Equal([new StepArgument(20, 2)], Assert.Single(match.Bindings).Arguments);
            Assert.Equal(MatchStatus.Undefined, Match(matcher, StepKind.Given, "the first number is fifty").Status);
            Assert.Equal(MatchStatus.Undefined, Match(matcher, StepKind.When, "the first number is 50").Status);
        }),
        ("a string takes double or single quotes and word and anything take their text", () =>
        {
            StepMatcher matcher = Matcher(
                Binding(BindingKind.When, "I enter {string} in the {word} field", "WhenIEnter", P("value", Text), P("field", Text)),
                Binding(BindingKind.Then, "it says {}", "ThenItSays", P("said", Text)));

            StepMatch quoted = Match(matcher, StepKind.When, "I enter \"a b\" in the name field");
            StepMatch single = Match(matcher, StepKind.When, "I enter 'x' in the name field");

            Assert.Equal([new StepArgument(9, 3), new StepArgument(21, 4)], Assert.Single(quoted.Bindings).Arguments);
            Assert.Equal(MatchStatus.Defined, single.Status);
            Assert.Equal(MatchStatus.Defined, Match(matcher, StepKind.Then, "it says anything at all").Status);
        }),
        ("a regular expression matches the whole step only", () =>
        {
            StepMatcher matcher = Matcher(Binding(BindingKind.Given, "I have (\\d+) cucumbers", "GivenCucumbers", P("count", Int)));

            Assert.Equal(MatchStatus.Defined, Match(matcher, StepKind.Given, "I have 12 cucumbers").Status);
            Assert.Equal(MatchStatus.Undefined, Match(matcher, StepKind.Given, "I have 12 cucumbers today").Status);
        }),
        ("a parameter count that differs from the arguments does not match", () =>
        {
            StepMatcher matcher = Matcher(Binding(BindingKind.Given, "the first number is {int}", "GivenTheFirstNumberIs"));

            Assert.Equal(MatchStatus.Undefined, Match(matcher, StepKind.Given, "the first number is 50").Status);
        }),
        ("a step with a table takes the definition with a parameter for it", () =>
        {
            StepMatcher matcher = Matcher(
                Binding(BindingKind.Given, "the following users", "GivenUsers", P("users", "Reqnroll.DataTable")),
                Binding(BindingKind.Given, "the following users", "GivenNoUsers"));

            StepMatch withTable = matcher.Match(StepKind.Given, "the following users", false, true, new StepContext("F", "S", []));

            Assert.Equal("GivenUsers", Assert.Single(withTable.Bindings).Binding.Method);
        }),
        ("two definitions matching alike are ambiguous and a scope that fits is chosen over none", () =>
        {
            StepBinding plain = Binding(BindingKind.When, "I log in", "WhenILogIn");
            StepBinding admin = Binding(BindingKind.When, "I log in", "WhenILogInAsAdmin") with { Scopes = [new StepScope("admin", null, null)] };
            StepMatcher both = Matcher(plain, Binding(BindingKind.When, "I log in", "WhenILogInAgain"));
            StepMatcher scoped = Matcher(plain, admin);

            Assert.Equal(MatchStatus.Ambiguous, Match(both, StepKind.When, "I log in").Status);
            Assert.Equal("WhenILogInAsAdmin", Assert.Single(Match(scoped, StepKind.When, "I log in", ["@admin"]).Bindings).Binding.Method);
            Assert.Equal("WhenILogIn", Assert.Single(Match(scoped, StepKind.When, "I log in", ["@other"]).Bindings).Binding.Method);
        }),
        ("a scope by feature or scenario title ignores letter case", () =>
        {
            StepBinding feature = Binding(BindingKind.When, "I add", "WhenIAdd") with { Scopes = [new StepScope(null, "calculator", null)] };

            Assert.Equal(MatchStatus.Defined, Match(Matcher(feature), StepKind.When, "I add").Status);
            Assert.Equal(MatchStatus.Undefined, Match(Matcher(feature with { Scopes = [new StepScope(null, "Other", null)] }), StepKind.When, "I add").Status);
        }),
        ("an enum is named by its type and a transformation by its name", () =>
        {
            StepBinding color = Binding(BindingKind.Given, "the colour is {Color}", "GivenColour", new StepParameter("color", "Shop.Color", ["Red", "Green"]));
            StepBinding money = Binding(BindingKind.Given, "a price of {money}", "GivenPrice", P("price", "Shop.Money"));
            StepMatcher matcher = new([color, money], [new StepTransformation("\\$(\\d+)", "money", "Shop.Money")], CultureInfo.InvariantCulture);

            Assert.Equal(MatchStatus.Defined, Match(matcher, StepKind.Given, "the colour is Red").Status);
            Assert.Equal(MatchStatus.Defined, Match(matcher, StepKind.Given, "a price of $12").Status);
            Assert.Equal(MatchStatus.Undefined, Match(matcher, StepKind.Given, "a price of twelve").Status);
        }),
        ("a method named for its step matches with no expression", () =>
        {
            StepMatcher matcher = Matcher(Binding(BindingKind.Given, null, "Given_the_first_number_is_COUNT", P("count", Int)));

            Assert.Equal(MatchStatus.Defined, Match(matcher, StepKind.Given, "the first number is 50").Status);
            Assert.Equal(MatchStatus.Defined, Match(matcher, StepKind.Given, "The First number is 7").Status);
        }),
        ("a broken expression is a binding that says why and matches nothing", () =>
        {
            StepMatcher matcher = Matcher(Binding(BindingKind.Given, "a {nonsense} value", "GivenNonsense", P("x", Text)));

            Assert.Equal(MatchStatus.Undefined, Match(matcher, StepKind.Given, "a 1 value").Status);
            Assert.NotNull(matcher.ErrorOf(matcher.Bindings[0]));
        }),
        ("two attributes of one method matching a step are one definition", () =>
        {
            StepMatcher matcher = Matcher(
                Binding(BindingKind.Given, "I log in", "LogIn"),
                Binding(BindingKind.StepDefinition, "I log in", "LogIn"));

            Assert.Equal(MatchStatus.Defined, Match(matcher, StepKind.Given, "I log in").Status);
        }),
        ("a step's context takes its examples' tags", () =>
        {
            FeatureReading reading = FeatureReading.Read(
                "Feature: F\n\nScenario Outline: O\n  When I log in\n\n@admin\nExamples:\n  | a |\n  | 1 |\n", "en");
            StepBinding admin = Binding(BindingKind.When, "I log in", "WhenILogInAsAdmin") with { Scopes = [new StepScope("@admin", null, null)] };

            Assert.Equal(MatchStatus.Defined, Matcher(admin).Match(reading.Steps[0], reading).Status);
        }),
        ("an expression is a Cucumber Expression or a regular expression as Reqnroll tells them", () => Cases.Each(
            [("the first number is {int}", true), ("I have (\\d+) cucumbers", false), ("a price of 3\\.50", false), ("I have cucumber(s)", true)],
            row => ToldApart(row.Item1, row.Item2))));

    [Fact]
    public void An_outlines_steps_are_matched_once_per_example_and_an_argument_a_definition_cannot_take_is_placed_where_it_is_written() => Cases.All(
        ("an argument whose check stops at the time limit is said unchecked neither taken nor refused", () =>
        {
            StepBinding money = Binding(BindingKind.Given, "a price of {money}", "GivenPrice", P("price", "Shop.Money"));
            StepMatcher matcher = new([money], [new StepTransformation("\\$(\\d+)|held1984money", "money", "Shop.Money") { Parameters = ["System.String"] }],
                CultureInfo.InvariantCulture);
            using (Passenger.Text.TextSearch.HoldPastLimit("held1984money"))
            {
                StepMatch bound = Match(matcher, StepKind.Given, "a price of $12");
                StepMatch nearly = Match(matcher, StepKind.Given, "a price of twelve");

                Assert.Equal(MatchStatus.Defined, bound.Status);
                RefusedArgument unseen = Assert.Single(bound.Refused);
                Assert.True(unseen is { Unjudged: true, Fails: false, Column: 11, Length: 3 } && unseen.Why.EndsWith($"\"$12\" as price was not checked: {Passenger.Text.TextSearch.RegexStopped}", StringComparison.Ordinal),
                    $"A transformation that stopped at its time limit came to {unseen}, not the argument said unchecked");
                Assert.Equal(MatchStatus.Undefined, nearly.Status);
                RefusedArgument said = Assert.Single(nearly.Refused);
                Assert.True(said is { Unjudged: true, Fails: false } && said.Why.EndsWith($"whether its {{money}} takes \"twelve\" was not checked: {Passenger.Text.TextSearch.RegexStopped}", StringComparison.Ordinal),
                    $"A parameter type that stopped at its time limit came to {said}, not the argument said unchecked");
            }
            Assert.Empty(Match(matcher, StepKind.Given, "a price of $12").Refused);
        }),
        ("an outline's step is matched once for each example with its values", () =>
        {
            FeatureReading reading = FeatureReading.Read(
                "Feature: F\n\nScenario Outline: O\n  Given the first number is <a>\n\nExamples:\n  | a |\n  | 1 |\n  | 2 |\n", "en");
            StepMatcher defined = Matcher(Binding(BindingKind.Given, "the first number is {int}", "GivenTheFirstNumberIs", P("number", Int)));
            FeatureReading oneBad = FeatureReading.Read(
                "Feature: F\n\nScenario Outline: O\n  Given the first number is <a>\n\nExamples:\n  | a |\n  | 1 |\n  | x |\n", "en");

            Assert.Equal(MatchStatus.Defined, defined.Match(reading.Steps[0], reading).Status);
            Assert.Equal(MatchStatus.Undefined, defined.Match(oneBad.Steps[0], oneBad).Status);
        }),
        ("a scenario with examples is an outline whatever its keyword its steps matched once for each row", () =>
        {
            StepMatcher matcher = Matcher(Binding(BindingKind.Given, "the first number is {int}", "GivenTheFirstNumberIs", P("number", Int)));
            FeatureReading reading = FeatureReading.Read(
                "Feature: F\n\nScenario: S\n  Given the first number is <a>\n\nExamples:\n  | a |\n  | 1 |\n  | 2 |\n", "en");
            FeatureReading oneBad = FeatureReading.Read(
                "Feature: F\n\nScenario: S\n  Given the first number is <a>\n\nExamples:\n  | a |\n  | 1 |\n  | x |\n", "en");

            Assert.Equal(ScenarioKind.Outline, reading.Scenarios.Single().Kind);
            Assert.Equal(MatchStatus.Defined, matcher.Match(reading.Steps[0], reading).Status);
            Assert.Empty(Assert.Single(matcher.Match(reading.Steps[0], reading).Bindings).Arguments);
            Assert.Equal(MatchStatus.Undefined, matcher.Match(oneBad.Steps[0], oneBad).Status);
        }),
        ("an outline's step with placeholders carries no arguments however many example rows it has", () =>
        {
            StepMatcher matcher = Matcher(Binding(BindingKind.Given, "the first number is {int}", "GivenTheFirstNumberIs", P("number", Int)));
            FeatureReading one = FeatureReading.Read(
                "Feature: F\n\nScenario Outline: O\n  Given the first number is <a>\n\nExamples:\n  | a |\n  | 12345 |\n", "en");
            FeatureReading two = FeatureReading.Read(
                "Feature: F\n\nScenario Outline: O\n  Given the first number is <a>\n\nExamples:\n  | a |\n  | 12345 |\n  | 6 |\n", "en");

            Assert.Empty(Assert.Single(matcher.Match(one.Steps[0], one).Bindings).Arguments);
            Assert.Empty(Assert.Single(matcher.Match(two.Steps[0], two).Bindings).Arguments);
        }),
        ("a background step's arguments are kept however many scenarios it runs in", () =>
        {
            FeatureReading reading = FeatureReading.Read(
                "Feature: F\n\nBackground:\n  Given the user \"bob\" logs in\n\nScenario: A\n  Then one\n\nScenario: B\n  Then two\n", "en");
            StepMatcher matcher = Matcher(Binding(BindingKind.Given, "the user {string} logs in", "GivenLogsIn", P("name", Text)));

            Assert.Equal([new StepArgument(10, 3)], Assert.Single(matcher.Match(reading.Steps[0], reading).Bindings).Arguments);
        }),
        ("an outline step with no placeholder keeps its arguments and one with placeholders but no examples is not matched", () =>
        {
            FeatureReading reading = FeatureReading.Read(
                "Feature: F\n\nScenario Outline: O\n  Given the first number is 5\n  And the first number is <a>\n\nExamples:\n  | a |\n", "en");
            StepMatcher matcher = Matcher(Binding(BindingKind.Given, "the first number is {int}", "GivenTheFirstNumberIs", P("number", Int)));

            Assert.Equal([new StepArgument(20, 1)], Assert.Single(matcher.Match(reading.Steps[0], reading).Bindings).Arguments);
            Assert.False(StepMatcher.Matchable(reading.Steps[1]));
            Assert.True(StepMatcher.Matchable(reading.Steps[0]));
        }),
        ("a feature's background step is matched in the scenarios under its rules too", () =>
        {
            FeatureReading reading = FeatureReading.Read(
                "Feature: F\n\nBackground:\n  Given I log in\n\nRule: R\n\n  @admin\n  Scenario: A\n    Then one\n", "en");
            StepBinding admin = Binding(BindingKind.Given, "I log in", "GivenILogIn") with { Scopes = [new StepScope("admin", null, null)] };

            Assert.Equal(MatchStatus.Defined, Matcher(admin).Match(reading.Steps[0], reading).Status);
        }),
        ("a definition refused only an argument it cannot convert binds the step which fails at that argument", () =>
        {
            StepMatcher matcher = Matcher(Binding(BindingKind.Given, "the date is (.*)", "GivenTheDate", P("when", "System.DateTime")));

            StepMatch match = matcher.Match(StepKind.Given, "the date is 31/01/2024", false, false, new StepContext("F", "S", []));

            Assert.Equal(MatchStatus.Defined, match.Status);
            Assert.Equal("GivenTheDate", Assert.Single(match.Bindings).Binding.Method);
            RefusedArgument refused = Assert.Single(match.Refused);
            Assert.Equal((0, 12, 10, true), (refused.Line, refused.Column, refused.Length, refused.Fails));
            Assert.Equal("CalculatorSteps.GivenTheDate(DateTime) takes when as a date (as the invariant culture writes one, like 01/31/2024), which \"31/01/2024\" is not",
                refused.Why);
            Assert.Empty(matcher.Match(StepKind.Given, "the date is 01/31/2024", false, false, new StepContext("F", "S", [])).Refused);
        }),
        ("a step a Cucumber Expression would bind but for an argument its parameter type refuses is undefined at that argument", () =>
        {
            StepMatcher matcher = Matcher(
                Binding(BindingKind.Given, "the first number is {int}", "GivenTheFirstNumberIs", P("number", Int)),
                Binding(BindingKind.When, "I enter {string}", "WhenIEnter", P("value", Text)));

            StepMatch two = Match(matcher, StepKind.Given, "the first number is two");
            StepMatch unquoted = Match(matcher, StepKind.When, "I enter hello");

            Assert.Equal(MatchStatus.Undefined, two.Status);
            RefusedArgument refused = Assert.Single(two.Refused);
            Assert.Equal((0, 20, 3, false), (refused.Line, refused.Column, refused.Length, refused.Fails));
            Assert.Equal("No step definition binds \"the first number is two\": CalculatorSteps.GivenTheFirstNumberIs(Int32) would, but its {int} takes "
                + "a whole number in digits, which \"two\" is not", refused.Why);
            Assert.Contains("text in double or single quotes", Assert.Single(unquoted.Refused).Why, StringComparison.Ordinal);
            Assert.Empty(Match(matcher, StepKind.Given, "something else entirely").Refused);
        }),
        ("an int too large for its parameter matches the expression and fails converting", () =>
        {
            StepMatcher matcher = Matcher(Binding(BindingKind.Given, "the first number is {int}", "GivenTheFirstNumberIs", P("number", Int)));

            StepMatch match = Match(matcher, StepKind.Given, "the first number is 99999999999");

            Assert.Equal(MatchStatus.Defined, match.Status);
            Assert.Contains("a whole number (from -2147483648 to 2147483647), which \"99999999999\" is not", Assert.Single(match.Refused).Why, StringComparison.Ordinal);
        }),
        ("an enum and a transformation refuse what Reqnroll refuses", () =>
        {
            StepBinding color = Binding(BindingKind.Given, "the colour is (.*)", "GivenColour", new StepParameter("color", "Shop.Color", ["Red", "Green", "DarkBlue"]));
            StepBinding money = Binding(BindingKind.Given, "a price of {money}", "GivenPrice", new StepParameter("price", "Shop.Money") { Unconverted = true });
            StepMatcher matcher = new([color, money], [new StepTransformation("\\$(\\d+)", "money", "Shop.Money") { Parameters = ["System.String"] }], CultureInfo.InvariantCulture);

            Assert.Empty(Match(matcher, StepKind.Given, "the colour is dark blue").Refused);
            Assert.True(Assert.Single(Match(matcher, StepKind.Given, "the colour is Purple").Refused).Fails);
            RefusedArgument twelve = Assert.Single(Match(matcher, StepKind.Given, "a price of twelve").Refused);
            Assert.Contains("its {money} takes text matching (\\$(?:\\d+)), which \"twelve\" is not", twelve.Why, StringComparison.Ordinal);
        }),
        ("two definitions refused only their arguments leave the step undefined with each refusal", () =>
        {
            StepMatcher matcher = Matcher(
                Binding(BindingKind.Given, "the count is (.*)", "GivenCount", P("count", Int)),
                Binding(BindingKind.Given, "the (.*) is (.*)", "GivenFlag", P("name", Text), P("flag", "System.Boolean")));

            StepMatch match = Match(matcher, StepKind.Given, "the count is many");

            Assert.Equal(MatchStatus.Undefined, match.Status);
            Assert.Equal(2, match.Refused.Count);
            Assert.All(match.Refused, r => Assert.True(r.Fails));
        }),
        ("a definition that fits but for its scope leaves the step undefined with no argument refused", () =>
        {
            StepBinding scoped = Binding(BindingKind.Given, "the count is (.*)", "GivenCount", P("count", Text)) with { Scopes = [new StepScope("other", null, null)] };
            StepMatcher matcher = Matcher(scoped, Binding(BindingKind.Given, "the count is (.*)", "GivenNumber", P("count", Int)));

            StepMatch match = Match(matcher, StepKind.Given, "the count is many");

            Assert.Equal(MatchStatus.Undefined, match.Status);
            Assert.Empty(match.Refused);
        }),
        ("an outline's argument refused for one example is placed in that example's cell", () =>
        {
            FeatureReading reading = FeatureReading.Read(
                "Feature: F\n\nScenario Outline: O\n  Given the date is <d> at noon\n\nExamples:\n  | d          |\n  | 01/31/2024 |\n  | 31/01/2024 |\n", "en");
            StepMatcher matcher = Matcher(Binding(BindingKind.Given, "the date is (.*) at noon", "GivenTheDate", P("when", "System.DateTime")));

            StepMatch match = matcher.Match(reading.Steps[0], reading);

            Assert.Equal(MatchStatus.Defined, match.Status);
            RefusedArgument refused = Assert.Single(match.Refused);
            Assert.Equal((8, 4, 10), (refused.Line, refused.Column, refused.Length));
        }),
        ("an argument across a placeholder and its text is placed over both in the step", () =>
        {
            FeatureReading reading = FeatureReading.Read(
                "Feature: F\n\nScenario Outline: O\n  Given the date is <d>/2024\n\nExamples:\n  | d     |\n  | 31/01 |\n", "en");
            StepMatcher matcher = Matcher(Binding(BindingKind.Given, "the date is (.*)", "GivenTheDate", P("when", "System.DateTime")));

            RefusedArgument refused = Assert.Single(matcher.Match(reading.Steps[0], reading).Refused);

            Assert.Equal((3, 20, 8), (refused.Line, refused.Column, refused.Length));
        }));

}
