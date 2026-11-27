using System.Text.Json.Nodes;
using Passenger.App.State;

namespace Passenger.App.Testing;

/// <summary>What the Unit Tests window groups tests by above their classes, in the order the Group By menu lists them and
/// a tree nests them.</summary>
public enum TestGroupLevel
{
    TargetFramework,
    Project,
    FileSystem,
    Namespace,
    Category,
    Duration,
}

/// <summary>The groupings: the predefined ones, the levels a person turns on or off, and how a test's traits and time
/// name the groups it is in.</summary>
public static class TestGroupings
{
    /// <summary>What a test without a category is under.</summary>
    public const string Uncategorized = "<Uncategorized>";

    /// <summary>The default: each project's tests by namespace.</summary>
    public static readonly IReadOnlyList<TestGroupLevel> Default = [TestGroupLevel.Project, TestGroupLevel.Namespace];

    /// <summary>The predefined groupings.</summary>
    public static readonly IReadOnlyList<(string Name, IReadOnlyList<TestGroupLevel> Levels)> Predefined =
    [
        ("Test Hierarchy", []),
        ("Project Structure", [TestGroupLevel.Project, TestGroupLevel.FileSystem]),
        ("Projects", [TestGroupLevel.Project]),
        ("Namespaces", [TestGroupLevel.Namespace]),
        ("Projects and Namespaces", Default),
        ("Categories", [TestGroupLevel.Category]),
        ("Project and Categories", [TestGroupLevel.Project, TestGroupLevel.Category]),
    ];

    /// <summary>The grades of a test's time: the most each takes, and its group's name.</summary>
    private static readonly (TimeSpan Under, string Name)[] Grades =
    [
        (TimeSpan.FromMilliseconds(100), "Fast (<100 ms)"),
        (TimeSpan.FromSeconds(1), "Medium (>100 ms)"),
        (TimeSpan.FromSeconds(15), "Slow (>1 sec)"),
        (TimeSpan.FromSeconds(30), "Slower (>15 sec)"),
        (TimeSpan.MaxValue, "Slowest (>30 sec)"),
    ];

    public static string Title(TestGroupLevel level) => level switch
    {
        TestGroupLevel.TargetFramework => "Target Framework",
        TestGroupLevel.FileSystem => "File System",
        _ => level.ToString(),
    };

    /// <summary>The levels as a tree nests them, each once, those this build does not have left out.</summary>
    public static IReadOnlyList<TestGroupLevel> Ordered(IEnumerable<TestGroupLevel> levels) => [.. levels.Where(Enum.IsDefined).Distinct().Order()];

    /// <summary>A test's categories: NUnit's Category and MSTest's TestCategory, and an xUnit trait named Category, by each
    /// of their comma-separated values; any other trait as "Name [Value]".</summary>
    public static IEnumerable<string> Categories(IEnumerable<KeyValuePair<string, string>> traits)
    {
        foreach ((string name, string value) in traits)
        {
            bool category = name.Equals("Category", StringComparison.OrdinalIgnoreCase) || name.Equals("TestCategory", StringComparison.OrdinalIgnoreCase);
            foreach (string each in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                yield return category ? each : $"{name.Trim()} [{each}]";
            }
        }
    }

    /// <summary>The grade of a test that ran for this long, with its rank among them; Not Run, after them all, when it never
    /// ran.</summary>
    public static (string Name, int Rank) Grade(TimeSpan? took)
    {
        if (took is not { } time)
        {
            return ("Not Run", Grades.Length);
        }
        int rank = Array.FindIndex(Grades, g => time < g.Under);
        return (Grades[rank].Name, rank);
    }
}

/// <summary>The Unit Tests window's choices for a folder: how its tests are grouped, which outcomes it shows (none for
/// all of them), its test sessions and the one it shows (none for every test).</summary>
public sealed record UnitTestsState : IStateDocument<UnitTestsState>
{
    /// <summary>The most sessions kept, and the most tests each keeps taken in and taken out.</summary>
    public const int MostSessions = 50;

    /// <inheritdoc cref="MostSessions"/>
    public const int MostSessionTests = 5000;

    public List<TestGroupLevel> Grouping { get; init; } = [.. TestGroupings.Default];

    public List<TestState> Outcomes { get; init; } = [];

    public List<KeptTestSession> Sessions { get; init; } = [];

    /// <summary>The name of the session shown; null while every test is.</summary>
    public string? Session { get; init; }

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    /// <summary>A level or outcome this build does not have is left out, and each is kept once, levels in their order; a
    /// session without a name or a project, or with the name of one before it, is left out, and the one shown is one kept.</summary>
    public UnitTestsState Clamped()
    {
        List<KeptTestSession> sessions = [.. (Sessions ?? []).Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Name))
            .DistinctBy(s => s.Name, StringComparer.Ordinal).Take(MostSessions)
            .Select(s => s with { Tests = Kept(s.Tests), Left = Kept(s.Left) })];
        return this with
        {
            Grouping = [.. TestGroupings.Ordered(Grouping ?? [])],
            Outcomes = [.. (Outcomes ?? []).Where(Enum.IsDefined).Distinct().Order()],
            Sessions = sessions,
            Session = sessions.Any(s => s.Name == Session) ? Session : null,
        };

        static List<SessionTest> Kept(List<SessionTest>? tests) =>
            [.. (tests ?? []).Where(t => t is not null && !string.IsNullOrEmpty(t.Project) && (t.Row is null || t.Name is not null)).Distinct().Take(MostSessionTests)];
    }
}

/// <summary>A test session as a folder keeps it: its name, what it takes in and what was taken out of that.</summary>
public sealed record KeptTestSession
{
    public string Name { get; init; } = string.Empty;

    public List<SessionTest> Tests { get; init; } = [];

    public List<SessionTest> Left { get; init; } = [];
}
