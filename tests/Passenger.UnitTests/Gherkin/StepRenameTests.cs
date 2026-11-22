using Passenger.Workspace.Gherkin;
using Xunit;

namespace Passenger.UnitTests.Gherkin;

/// <summary>A step definition's expression renamed, and every step it binds rewritten: the new words around the step's own
/// arguments, refused where the parameters would change or an expression is more than words and parameters.</summary>
public sealed class StepRenameTests
{
    [Fact]
    public void A_steps_text_is_rewritten_with_its_definitions_new_expression_keeping_its_arguments() => Cases.All(
        ("a step's words change and its arguments stay", () =>
        {
            StepRename rename = StepRename.Of("the first number is {int}", "the initial value is {int} today", cucumber: true)!;

            Assert.Null(rename.Refused);
            Assert.Equal("the initial value is 50 today", rename.Step("the first number is 50", [new StepArgument(20, 2)]));
        }),
        ("a quoted argument keeps its quotes", () =>
        {
            StepRename rename = StepRename.Of("I enter {string} here", "I type {string} there", cucumber: true)!;

            Assert.Equal("I type 'a b' there", rename.Step("I enter 'a b' here", [new StepArgument(9, 3)]));
        }),
        ("quotes the old expression's words write around a parameter are its words not the arguments", () =>
        {
            StepRename regex = StepRename.Of("I have entered \"(.*)\" into the calculator", "I typed \"(.*)\" into the calculator", cucumber: false)!;
            StepRename word = StepRename.Of("I enter '{word}' here", "I type '{word}' there", cucumber: true)!;

            Assert.Equal("I typed \"50\" into the calculator", regex.Step("I have entered \"50\" into the calculator", [new StepArgument(16, 2)]));
            Assert.Equal("I type 'abc' there", word.Step("I enter 'abc' here", [new StepArgument(9, 3)]));
            Assert.Equal("I typed 50 into the calculator", StepRename.Of("I have entered \"(.*)\" into the calculator", "I typed (.*) into the calculator", cucumber: false)
                .Step("I have entered \"50\" into the calculator", [new StepArgument(16, 2)]));
        }),
        ("a regular expression's groups are its parameters and its escapes its words", () =>
        {
            StepRename rename = StepRename.Of(@"I pay (\d+)\.00", @"I spend (\d+)\.00 now", cucumber: false)!;

            Assert.Equal("I spend 12.00 now", rename.Step("I pay 12.00", [new StepArgument(6, 2)]));
        }),
        ("changed parameters and operators are refused", () =>
        {
            Assert.Equal("The new expression's parameters differ from the old one's: {string} against {int}",
                StepRename.Of("a {int} b", "a {string} b", cucumber: true)!.Refused);
            Assert.Equal("Only an expression of words and parameters can be renamed", StepRename.Of("a cucumber(s)", "a gherkin(s)", cucumber: true)!.Refused);
            Assert.Equal("Only an expression of words and parameters can be renamed", StepRename.Of("a (.*) b+", "c (.*) b+", cucumber: false)!.Refused);
        }),
        ("a step given fewer arguments than parameters is not rewritten", () =>
        {
            Assert.Null(StepRename.Of("the user {string} logs in", "the member {string} logs in", cucumber: true).Step("the user \"bob\" logs in", []));
        }));

}
