using Passenger.App.Testing;

namespace Passenger.UnitTests.Testing;

/// <summary>NUnit's tests as each runner names them: VSTest holds a case's arguments in its full name, the Testing Platform
/// only in its display name, so a row reads the same from either and two cases of a fixture's instance stay two rows.</summary>
public class TestNamesTests
{
    [Theory]
    [InlineData("A.Worded(\"a.b\").Long(1)", "Long(1)", "Worded(\"a.b\").Long(1)")]
    [InlineData("A.Worded(\"a.b\").Long", "Long(1)", "Worded(\"a.b\").Long(1)")]
    [InlineData("A.Worded(\"x(y\").Long", "Long(\"a.b\")", "Worded(\"x(y\").Long(\"a.b\")")]
    [InlineData("Worded(1).Long", "Long(2)", "Worded(1).Long(2)")]
    [InlineData("A.Typed<Int32>.Long", "Long(2)", "Typed<Int32>.Long(2)")]
    [InlineData("A.Worded(1).Long", "Called by name", "Worded(1).Called by name")]
    public void A_case_of_a_fixtures_instance_is_named_by_its_class_and_its_display_name(string fullName, string shown, string row)
    {
        Assert.True(TestItem.RowOf(fullName, shown) == row,
            $"{fullName} shown as {shown} is the row '{TestItem.RowOf(fullName, shown)}', so the two cases of one instance read as one test told apart by number");
    }

    [Theory]
    [InlineData("A.Parameterised(-1).Positive", "Positive", "Parameterised(-1).Positive")]
    [InlineData("A.Typed<Int32>.Named", "Named", "Typed<Int32>.Named")]
    [InlineData("A.Worded(1).Long(2)", "A.Worded(1).Long(2)", "Worded(1).Long(2)")]
    public void A_test_of_a_fixtures_instance_is_a_row_whatever_its_method_takes(string fullName, string shown, string row)
    {
        Assert.True(TestItem.RowOf(fullName, shown) == row, $"{fullName} shown as {shown} is the row '{TestItem.RowOf(fullName, shown)}', not one of its method");
    }

    [Theory]
    [InlineData("A.Sums.Each(1,2)", "Each(1,2)", "Each(1,2)")]
    [InlineData("A.Sums.Each", "Each(1,2)", "Each(1,2)")]
    [InlineData("A.Sums.Words(\"a.b(c\")", "Words(\"a.b(c\")", "Words(\"a.b(c\")")]
    [InlineData("A.Sums.Words", "Words(\"a.b(c\")", "Words(\"a.b(c\")")]
    public void A_case_of_a_plain_fixture_is_its_display_name_from_either_runner(string fullName, string shown, string row)
    {
        Assert.True(TestItem.RowOf(fullName, shown) == row, $"{fullName} shown as {shown} is the row '{TestItem.RowOf(fullName, shown)}'");
    }

    [Theory]
    [InlineData("A.Sums.Plain", "Plain")]
    [InlineData("A.Sums.OnePlusOne", "OnePlusOne")]
    public void A_test_named_as_its_method_is_no_row(string fullName, string shown)
    {
        Assert.Null(TestItem.RowOf(fullName, shown));
    }

    [Theory]
    [InlineData("A.Worded(\"a.b\").Long(1)", "A.Worded.Long")]
    [InlineData("A.Worded(\"a.b\").Long", "A.Worded.Long")]
    [InlineData("A.Typed<Int32>.Named", "A.Typed.Named")]
    [InlineData("A.Sums.Words(\"a.b(c\")", "A.Sums.Words")]
    public void Each_runners_name_of_a_case_is_of_the_method_the_code_declares(string fullName, string method)
    {
        Assert.True(TestItem.MethodOf(fullName) == method, $"{fullName} belongs to '{TestItem.MethodOf(fullName)}', which the code does not declare");
    }
}
