using Passenger.Workspace.Gherkin;
using Xunit;

namespace Passenger.UnitTests.Gherkin;

/// <summary>A step definition's expression renamed, and every step it binds rewritten: the new words around the step's own
/// arguments, refused where the parameters would change or an expression is more than words and parameters.</summary>
public sealed class StepRenameTests
{
    [Fact]
    public void A_steps_words_change_and_its_arguments_stay()
    {
        StepRename rename = StepRename.Of("the first number is {int}", "the initial value is {int} today", cucumber: true)!;

        Assert.Null(rename.Refused);
        Assert.Equal("the initial value is 50 today", rename.Step("the first number is 50", [new StepArgument(20, 2)]));
    }

    [Fact]
    public void A_quoted_argument_keeps_its_quotes()
    {
        StepRename rename = StepRename.Of("I enter {string} here", "I type {string} there", cucumber: true)!;

        Assert.Equal("I type 'a b' there", rename.Step("I enter 'a b' here", [new StepArgument(9, 3)]));
    }

    [Fact]
    public void A_regular_expressions_groups_are_its_parameters_and_its_escapes_its_words()
    {
        StepRename rename = StepRename.Of(@"I pay (\d+)\.00", @"I spend (\d+)\.00 now", cucumber: false)!;

        Assert.Equal("I spend 12.00 now", rename.Step("I pay 12.00", [new StepArgument(6, 2)]));
    }

    [Fact]
    public void Changed_parameters_and_operators_are_refused()
    {
        Assert.Equal("The new expression's parameters differ from the old one's: {string} against {int}",
            StepRename.Of("a {int} b", "a {string} b", cucumber: true)!.Refused);
        Assert.Equal("Only an expression of words and parameters can be renamed", StepRename.Of("a cucumber(s)", "a gherkin(s)", cucumber: true)!.Refused);
        Assert.Equal("Only an expression of words and parameters can be renamed", StepRename.Of("a (.*) b+", "c (.*) b+", cucumber: false)!.Refused);
    }

    [Fact]
    public void An_expression_is_written_back_as_the_attribute_wrote_it()
    {
        Assert.Equal("\"say \\\"hi\\\" \\\\ {int}\"", StepRename.Literal("say \"hi\" \\ {int}", verbatim: false));
        Assert.Equal("@\"say \"\"hi\"\" (\\d+)\"", StepRename.Literal("say \"hi\" (\\d+)", verbatim: true));
    }

    [Fact]
    public void A_step_given_fewer_arguments_than_parameters_is_not_rewritten()
    {
        Assert.Null(StepRename.Of("the user {string} logs in", "the member {string} logs in", cucumber: true).Step("the user \"bob\" logs in", []));
    }
}
