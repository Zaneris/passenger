namespace Passenger.App.Testing;

/// <summary>What a project's GUT test classes pass on to the scripts extending them, held beside their names and paths in
/// the set its scripts are read with: each test a class has, its own or one it inherits, as one entry of the class, the
/// test's name and the script declaring it. A script's read finds what it inherits as it finds its base, and a test come to
/// or gone from a base has the scripts read again as a base come or gone does.</summary>
public static class GutBases
{
    /// <summary>What stands between a class and a test in an entry, which no class name, path or function name holds.</summary>
    private const char Between = '\n';

    /// <summary>The entry of a test a class has, the class by its class_name or its script's res:// path, and the file of the
    /// script declaring the test.</summary>
    public static string Entry(string testClass, string method, string script) => $"{testClass}{Between}{method}{Between}{script}";

    /// <summary>The tests a class has by the entries of <paramref name="bases"/>, each with the file of the script declaring it,
    /// in the order of their names.</summary>
    public static IReadOnlyList<(string Method, string Script)> TestsOf(IReadOnlySet<string>? bases, string testClass)
    {
        string head = testClass + Between;
        return bases is null ? [] : [.. bases.Where(entry => entry.StartsWith(head, StringComparison.Ordinal)).Select(entry => entry[head.Length..]).Order(StringComparer.Ordinal)
            .Select(entry => entry.Split(Between, 2)).Select(parts => (parts[0], parts[1])).DistinctBy(test => test.Item1, StringComparer.Ordinal)];
    }
}
