using Passenger.App.Testing;

namespace Passenger.UnitTests.Testing;

/// <summary>NUnit's tests as each runner names them: VSTest holds a case's arguments in its full name, the Testing Platform
/// only in its display name, so a row reads the same from either and two cases of a fixture's instance stay two rows.</summary>
public class TestNamesTests
{
    [Fact]
    public void A_case_reads_as_the_same_row_from_either_runner_and_belongs_to_the_method_the_code_declares() => Cases.All(
        ("a case of a fixture's instance is named by its class and its display name", () => Cases.Each(
        [
            ("A.Worded(\"a.b\").Long", "Long(1)", "Worded(\"a.b\").Long(1)"),
            ("A.Worded(\"x(y\").Long", "Long(\"a.b\")", "Worded(\"x(y\").Long(\"a.b\")"),
            ("A.Worded(1).Long", "Called by name", "Worded(1).Called by name"),
        ], row => InstanceCase(row.Item1, row.Item2, row.Item3))),
        ("a test of a fixture's instance is a row whatever its method takes", () => Cases.Each(
            [("A.Typed<Int32>.Named", "Named", "Typed<Int32>.Named"), ("A.Worded(1).Long(2)", "A.Worded(1).Long(2)", "Worded(1).Long(2)")],
            row => InstanceTest(row.Item1, row.Item2, row.Item3))),
        ("a case of a plain fixture is its display name from either runner", () => Cases.Each(
            [("A.Sums.Each(1,2)", "Each(1,2)", "Each(1,2)"), ("A.Sums.Words", "Words(\"a.b(c\")", "Words(\"a.b(c\")")],
            row => PlainCase(row.Item1, row.Item2, row.Item3))),
        ("a test named as its method is no row", () => NoRow("A.Sums.Plain", "Plain")),
        ("a case named in place of its method is a row of that method in the class the runner names", () => Cases.Each(
        [
            ("A.Sums.Two.Plus.Two", "Two.Plus.Two", "Adds", "A.Sums.Adds", "Two.Plus.Two"),
            ("A.Outer+Inner.Named", "Named", "Adds", "A.Outer+Inner.Adds", "Named"),
            ("A.Worded(\"a.b\").Named", "Named", "Adds", "A.Worded(\"a.b\").Adds", "Worded(\"a.b\").Named"),
            ("A.Worded(1).Named", "A.Worded(1).Named", "Adds", "A.Worded(1).Adds", "Worded(1).Named"),
        ], row => NamedCase(row.Item1, row.Item2, row.Item3, row.Item4, row.Item5))),
        ("a case named by its method is left as its runner names it", () => Cases.Each(
            [("A.Sums.Adds<Int32>(1)", "Adds<Int32>(1)", "Adds"), ("A.Worded(\"a.b\").Adds(1)", "Adds(1)", "Adds")],
            row => ByMethod(row.Item1, row.Item2, row.Item3))),
        ("a runner's name that is no class and display name says nothing of a case", () => Cases.Each(
            [("16f2c8b0-1b88-8247-91d8-2215e1512e46", "Named", "Adds"), ("Assembly.Space.Class.1.1.Adds.0.0", "Named", "Adds"), ("ANamed", "Named", "Adds")],
            row => NoClass(row.Item1, row.Item2, row.Item3))),
        ("a line a runner gives is in the last test declared at or above it", () => Cases.Each([(12, "Adds"), (20, "Doubles"), (99, "Later")], row => LineIn(row.Item1, row.Item2))),
        ("a test named by a method of its file or on no test's line is given no other method", () => Cases.Each(
            [("/a/Sums.cs", 12, "A.Fixture(1).Helper"), ("/a/Sums.cs", 5, "A.Sums.TwoPlusTwo"), ("/a/Unread.cs", 5, "A.Sums.TwoPlusTwo")],
            row => NoOther(row.Item1, row.Item2, row.Item3))),
        ("each runner's name of a case is of the method the code declares", () => Cases.Each(
            [("A.Worded(\"a.b\").Long(1)", "A.Worded.Long"), ("A.Typed<Int32>.Named", "A.Typed.Named"), ("A.Sums.Words(\"a.b(c\")", "A.Sums.Words")],
            row => MethodDeclared(row.Item1, row.Item2))));

    private static void InstanceCase(string fullName, string shown, string row)
    {
        Assert.True(TestItem.RowOf(fullName, shown) == row,
            $"{fullName} shown as {shown} is the row '{TestItem.RowOf(fullName, shown)}', so the two cases of one instance read as one test told apart by number");
    }

    private static void InstanceTest(string fullName, string shown, string row)
    {
        Assert.True(TestItem.RowOf(fullName, shown) == row, $"{fullName} shown as {shown} is the row '{TestItem.RowOf(fullName, shown)}', not one of its method");
    }

    private static void PlainCase(string fullName, string shown, string row)
    {
        Assert.True(TestItem.RowOf(fullName, shown) == row, $"{fullName} shown as {shown} is the row '{TestItem.RowOf(fullName, shown)}'");
    }

    private static void NoRow(string fullName, string shown)
    {
        Assert.Null(TestItem.RowOf(fullName, shown));
    }

    private static void NamedCase(string fullName, string shown, string method, string name, string row)
    {
        Assert.True(TestItem.NamedCase(fullName, shown, method) == (name, row),
            $"{fullName} shown as {shown}, of {method}, is {TestItem.NamedCase(fullName, shown, method)}, so it is taken for a method the code does not declare and leaves the tree");
    }

    private static void ByMethod(string fullName, string shown, string method)
    {
        Assert.True(TestItem.NamedCase(fullName, shown, method) is null, $"{fullName} shown as {shown} became {TestItem.NamedCase(fullName, shown, method)}, losing the arguments its row is named by");
    }

    private static void NoClass(string runnerName, string shown, string method)
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

    private static void LineIn(int line, string method)
    {
        Assert.True(new DeclaredLines(Code).MethodAt("/a/Sums.cs", line, "A.Sums.TwoPlusTwo") == method,
            $"Line {line} was read as {new DeclaredLines(Code).MethodAt("/a/Sums.cs", line, "A.Sums.TwoPlusTwo")}, so its case sits under another method");
    }

    private static void NoOther(string path, int line, string fullName)
    {
        Assert.Null(new DeclaredLines(Code).MethodAt(path, line, fullName));
    }

    private static void MethodDeclared(string fullName, string method)
    {
        Assert.True(TestItem.MethodOf(fullName) == method, $"{fullName} belongs to '{TestItem.MethodOf(fullName)}', which the code does not declare");
    }
}
