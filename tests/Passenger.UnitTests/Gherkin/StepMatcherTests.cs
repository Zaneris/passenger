using System.Globalization;
using Passenger.Workspace.Gherkin;
using Xunit;

namespace Passenger.UnitTests.Gherkin;

/// <summary>A step matched to its step definitions as Reqnroll's runtime matches it: Cucumber Expressions and regular
/// expressions told apart as Reqnroll tells them, each matching the whole text, parameters counted and converted, scopes
/// narrowing the choice, an outline's steps matched once per example.</summary>
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

    [Theory]
    [InlineData("the first number is {int}", true)]
    [InlineData("I have (\\d+) cucumbers", false)]
    [InlineData("^I am on the home page$", false)]
    [InlineData("I press (.*)", false)]
    [InlineData("a price of 3\\.50", false)]
    [InlineData("I have a cucumber", true)]
    [InlineData("I have cucumber(s)", true)]
    public void An_expression_is_a_cucumber_expression_or_a_regular_expression_as_reqnroll_tells_them(string expression, bool cucumber)
    {
        Assert.Equal(cucumber, StepExpressions.IsCucumberExpression(expression));
    }

    [Fact]
    public void A_cucumber_expressions_int_matches_a_number_and_says_where_the_argument_is()
    {
        StepMatcher matcher = Matcher(Binding(BindingKind.Given, "the first number is {int}", "GivenTheFirstNumberIs", P("number", Int)));

        StepMatch match = Match(matcher, StepKind.Given, "the first number is 50");

        Assert.Equal(MatchStatus.Defined, match.Status);
        Assert.Equal([new StepArgument(20, 2)], Assert.Single(match.Bindings).Arguments);
        Assert.Equal(MatchStatus.Undefined, Match(matcher, StepKind.Given, "the first number is fifty").Status);
        Assert.Equal(MatchStatus.Undefined, Match(matcher, StepKind.When, "the first number is 50").Status);
    }

    [Fact]
    public void A_string_takes_double_or_single_quotes_and_word_and_anything_take_their_text()
    {
        StepMatcher matcher = Matcher(
            Binding(BindingKind.When, "I enter {string} in the {word} field", "WhenIEnter", P("value", Text), P("field", Text)),
            Binding(BindingKind.Then, "it says {}", "ThenItSays", P("said", Text)));

        StepMatch quoted = Match(matcher, StepKind.When, "I enter \"a b\" in the name field");
        StepMatch single = Match(matcher, StepKind.When, "I enter 'x' in the name field");

        Assert.Equal([new StepArgument(9, 3), new StepArgument(21, 4)], Assert.Single(quoted.Bindings).Arguments);
        Assert.Equal(MatchStatus.Defined, single.Status);
        Assert.Equal(MatchStatus.Defined, Match(matcher, StepKind.Then, "it says anything at all").Status);
    }

    [Fact]
    public void Optional_text_and_alternatives_match_either_way()
    {
        StepMatcher matcher = Matcher(Binding(BindingKind.Given, "I have {int} cucumber(s) in my belly/stomach", "GivenCucumbers", P("count", Int)));

        Assert.Equal(MatchStatus.Defined, Match(matcher, StepKind.Given, "I have 1 cucumber in my belly").Status);
        Assert.Equal(MatchStatus.Defined, Match(matcher, StepKind.Given, "I have 5 cucumbers in my stomach").Status);
    }

    [Fact]
    public void A_regular_expression_matches_the_whole_step_only()
    {
        StepMatcher matcher = Matcher(Binding(BindingKind.Given, "I have (\\d+) cucumbers", "GivenCucumbers", P("count", Int)));

        Assert.Equal(MatchStatus.Defined, Match(matcher, StepKind.Given, "I have 12 cucumbers").Status);
        Assert.Equal(MatchStatus.Undefined, Match(matcher, StepKind.Given, "I have 12 cucumbers today").Status);
    }

    [Fact]
    public void A_step_definition_attribute_matches_every_kind_of_step()
    {
        StepMatcher matcher = Matcher(Binding(BindingKind.StepDefinition, "the calculator is cleared", "Clear"));

        Assert.Equal(MatchStatus.Defined, Match(matcher, StepKind.Given, "the calculator is cleared").Status);
        Assert.Equal(MatchStatus.Defined, Match(matcher, StepKind.Then, "the calculator is cleared").Status);
    }

    [Fact]
    public void A_parameter_count_that_differs_from_the_arguments_does_not_match()
    {
        StepMatcher matcher = Matcher(Binding(BindingKind.Given, "the first number is {int}", "GivenTheFirstNumberIs"));

        Assert.Equal(MatchStatus.Undefined, Match(matcher, StepKind.Given, "the first number is 50").Status);
    }

    [Fact]
    public void A_step_with_a_table_takes_the_definition_with_a_parameter_for_it()
    {
        StepMatcher matcher = Matcher(
            Binding(BindingKind.Given, "the following users", "GivenUsers", P("users", "Reqnroll.DataTable")),
            Binding(BindingKind.Given, "the following users", "GivenNoUsers"));

        StepMatch withTable = matcher.Match(StepKind.Given, "the following users", false, true, new StepContext("F", "S", []));

        Assert.Equal("GivenUsers", Assert.Single(withTable.Bindings).Binding.Method);
    }

    [Fact]
    public void Two_definitions_matching_alike_are_ambiguous_and_a_scope_that_fits_is_chosen_over_none()
    {
        StepBinding plain = Binding(BindingKind.When, "I log in", "WhenILogIn");
        StepBinding admin = Binding(BindingKind.When, "I log in", "WhenILogInAsAdmin") with { Scopes = [new StepScope("admin", null, null)] };
        StepMatcher both = Matcher(plain, Binding(BindingKind.When, "I log in", "WhenILogInAgain"));
        StepMatcher scoped = Matcher(plain, admin);

        Assert.Equal(MatchStatus.Ambiguous, Match(both, StepKind.When, "I log in").Status);
        Assert.Equal("WhenILogInAsAdmin", Assert.Single(Match(scoped, StepKind.When, "I log in", ["@admin"]).Bindings).Binding.Method);
        Assert.Equal("WhenILogIn", Assert.Single(Match(scoped, StepKind.When, "I log in", ["@other"]).Bindings).Binding.Method);
    }

    [Fact]
    public void A_scope_by_feature_or_scenario_title_ignores_letter_case()
    {
        StepBinding feature = Binding(BindingKind.When, "I add", "WhenIAdd") with { Scopes = [new StepScope(null, "calculator", null)] };

        Assert.Equal(MatchStatus.Defined, Match(Matcher(feature), StepKind.When, "I add").Status);
        Assert.Equal(MatchStatus.Undefined, Match(Matcher(feature with { Scopes = [new StepScope(null, "Other", null)] }), StepKind.When, "I add").Status);
    }

    [Fact]
    public void An_enum_is_named_by_its_type_and_a_transformation_by_its_name()
    {
        StepBinding color = Binding(BindingKind.Given, "the colour is {Color}", "GivenColour", new StepParameter("color", "Shop.Color", ["Red", "Green"]));
        StepBinding money = Binding(BindingKind.Given, "a price of {money}", "GivenPrice", P("price", "Shop.Money"));
        StepMatcher matcher = new([color, money], [new StepTransformation("\\$(\\d+)", "money", "Shop.Money")], CultureInfo.InvariantCulture);

        Assert.Equal(MatchStatus.Defined, Match(matcher, StepKind.Given, "the colour is Red").Status);
        Assert.Equal(MatchStatus.Defined, Match(matcher, StepKind.Given, "a price of $12").Status);
        Assert.Equal(MatchStatus.Undefined, Match(matcher, StepKind.Given, "a price of twelve").Status);
    }

    [Fact]
    public void A_method_named_for_its_step_matches_with_no_expression()
    {
        StepMatcher matcher = Matcher(Binding(BindingKind.Given, null, "Given_the_first_number_is_COUNT", P("count", Int)));

        Assert.Equal(MatchStatus.Defined, Match(matcher, StepKind.Given, "the first number is 50").Status);
        Assert.Equal(MatchStatus.Defined, Match(matcher, StepKind.Given, "The First number is 7").Status);
    }

    [Fact]
    public void A_broken_expression_is_a_binding_that_says_why_and_matches_nothing()
    {
        StepMatcher matcher = Matcher(Binding(BindingKind.Given, "a {nonsense} value", "GivenNonsense", P("x", Text)));

        Assert.Equal(MatchStatus.Undefined, Match(matcher, StepKind.Given, "a 1 value").Status);
        Assert.NotNull(matcher.ErrorOf(matcher.Bindings[0]));
    }

    [Fact]
    public void An_outlines_step_is_matched_once_for_each_example_with_its_values()
    {
        FeatureReading reading = FeatureReading.Read(
            "Feature: F\n\nScenario Outline: O\n  Given the first number is <a>\n\nExamples:\n  | a |\n  | 1 |\n  | 2 |\n", "en");
        StepMatcher defined = Matcher(Binding(BindingKind.Given, "the first number is {int}", "GivenTheFirstNumberIs", P("number", Int)));
        FeatureReading oneBad = FeatureReading.Read(
            "Feature: F\n\nScenario Outline: O\n  Given the first number is <a>\n\nExamples:\n  | a |\n  | 1 |\n  | x |\n", "en");

        Assert.Equal(MatchStatus.Defined, defined.Match(reading.Steps[0], reading).Status);
        Assert.Equal(MatchStatus.Undefined, defined.Match(oneBad.Steps[0], oneBad).Status);
    }

    [Fact]
    public void A_steps_context_takes_its_examples_tags()
    {
        FeatureReading reading = FeatureReading.Read(
            "Feature: F\n\nScenario Outline: O\n  When I log in\n\n@admin\nExamples:\n  | a |\n  | 1 |\n", "en");
        StepBinding admin = Binding(BindingKind.When, "I log in", "WhenILogInAsAdmin") with { Scopes = [new StepScope("@admin", null, null)] };

        Assert.Equal(MatchStatus.Defined, Matcher(admin).Match(reading.Steps[0], reading).Status);
    }
}
