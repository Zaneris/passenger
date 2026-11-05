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
    [InlineData("A.Sums.TwoPlusTwo", "TwoPlusTwo", "Adds", "A.Sums.Adds", "TwoPlusTwo")]
    [InlineData("A.Sums.Two.Plus.Two", "Two.Plus.Two", "Adds", "A.Sums.Adds", "Two.Plus.Two")]
    [InlineData("A.Sums.Spaced name", "Spaced name", "Adds", "A.Sums.Adds", "Spaced name")]
    [InlineData("A.Sums.Called(1)", "Called(1)", "Adds", "A.Sums.Adds", "Called(1)")]
    [InlineData("A.Sums.Other.Adds", "Other.Adds", "Adds", "A.Sums.Adds", "Other.Adds")]
    [InlineData("A.Outer+Inner.Named", "Named", "Adds", "A.Outer+Inner.Adds", "Named")]
    [InlineData("A.Worded(\"a.b\").Named", "Named", "Adds", "A.Worded(\"a.b\").Adds", "Worded(\"a.b\").Named")]
    [InlineData("A.Typed<Int32>.Named", "Named", "Adds", "A.Typed<Int32>.Adds", "Typed<Int32>.Named")]
    [InlineData("A.Sums.Named", "A.Sums.Named", "Adds", "A.Sums.Adds", "Named")]
    [InlineData("A.Worded(1).Named", "A.Worded(1).Named", "Adds", "A.Worded(1).Adds", "Worded(1).Named")]
    public void A_case_named_in_place_of_its_method_is_a_row_of_that_method_in_the_class_the_runner_names(string fullName, string shown, string method, string name, string row)
    {
        Assert.True(TestItem.NamedCase(fullName, shown, method) == (name, row),
            $"{fullName} shown as {shown}, of {method}, is {TestItem.NamedCase(fullName, shown, method)}, so it is taken for a method the code does not declare and leaves the tree");
    }

    [Theory]
    [InlineData("A.Sums.Adds", "Adds", "Adds")]
    [InlineData("A.Sums.Adds(1,2)", "Adds(1,2)", "Adds")]
    [InlineData("A.Sums.Adds<Int32>(1)", "Adds<Int32>(1)", "Adds")]
    [InlineData("A.Worded(\"a.b\").Adds", "Adds", "Adds")]
    [InlineData("A.Worded(\"a.b\").Adds(1)", "Adds(1)", "Adds")]
    [InlineData("A.Typed<Int32>.Adds", "Adds", "Adds")]
    public void A_case_named_by_its_method_is_left_as_its_runner_names_it(string fullName, string shown, string method)
    {
        Assert.True(TestItem.NamedCase(fullName, shown, method) is null, $"{fullName} shown as {shown} became {TestItem.NamedCase(fullName, shown, method)}, losing the arguments its row is named by");
    }

    [Theory]
    [InlineData("16f2c8b0-1b88-8247-91d8-2215e1512e46", "Adds", "Adds")]
    [InlineData("16f2c8b0-1b88-8247-91d8-2215e1512e46", "Named", "Adds")]
    [InlineData("Assembly.Space.Class.1.1.Adds.0.0", "Named", "Adds")]
    [InlineData("Named", "Named", "Adds")]
    [InlineData("ANamed", "Named", "Adds")]
    public void A_runners_name_that_is_no_class_and_display_name_says_nothing_of_a_case(string runnerName, string shown, string method)
    {
        Assert.True(TestItem.NamedCase(runnerName, shown, method) is null, $"The uid {runnerName} shown as {shown} was read as a class holding a case: {TestItem.NamedCase(runnerName, shown, method)}");
    }

    private static readonly CodeTests Code = new(
        [
            new TestItem("P", "A.Sums.Later", "Later") { Path = "/a/Sums.cs", Line = 30 },
            new TestItem("P", "A.Sums.Adds", "Adds") { Path = "/a/Sums.cs", Line = 10 },
            new TestItem("P", "A.Sums.Doubles", "Doubles") { Path = "/a/Sums.cs", Line = 20 },
        ],
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["/a/Sums.cs"] = new HashSet<string>(StringComparer.Ordinal) { "Adds", "Doubles", "Later", "Helper" },
            ["/a/Empty.cs"] = new HashSet<string>(StringComparer.Ordinal),
        });

    [Theory]
    [InlineData(10, "Adds")]
    [InlineData(12, "Adds")]
    [InlineData(19, "Adds")]
    [InlineData(20, "Doubles")]
    [InlineData(99, "Later")]
    public void A_line_a_runner_gives_is_in_the_last_test_declared_at_or_above_it(int line, string method)
    {
        Assert.True(new DeclaredLines(Code).MethodAt("/a/Sums.cs", line, "A.Sums.TwoPlusTwo") == method,
            $"Line {line} was read as {new DeclaredLines(Code).MethodAt("/a/Sums.cs", line, "A.Sums.TwoPlusTwo")}, so its case sits under another method");
    }

    [Theory]
    [InlineData("/a/Sums.cs", 12, "A.Sums.Doubles")]
    [InlineData("/a/Sums.cs", 12, "A.Sums.Doubles(1,2)")]
    [InlineData("/a/Sums.cs", 12, "A.Fixture(1).Helper")]
    [InlineData("/a/Sums.cs", 5, "A.Sums.TwoPlusTwo")]
    [InlineData("/a/Sums.cs", 0, "A.Sums.TwoPlusTwo")]
    [InlineData("/a/Empty.cs", 5, "A.Sums.TwoPlusTwo")]
    [InlineData("/a/Unread.cs", 5, "A.Sums.TwoPlusTwo")]
    public void A_test_named_by_a_method_of_its_file_or_on_no_tests_line_is_given_no_other_method(string path, int line, string fullName)
    {
        Assert.Null(new DeclaredLines(Code).MethodAt(path, line, fullName));
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
