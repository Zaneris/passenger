using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>What a test session takes in, whatever the tree's grouping: a project's every test (no name), every test of a
/// class or a method by its full name (a nested class's after a '+'), or one data row of a method.</summary>
public sealed record SessionTest
{
    /// <summary>The project file, as its tests' <see cref="TestItem.Project"/> names it.</summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>A class's or a method's full name; null for the project's every test.</summary>
    public string? Name { get; init; }

    /// <summary>A data row of the method <see cref="Name"/> names, as the tree names it; null for the method whole.</summary>
    public string? Row { get; init; }

    /// <summary>Whether it takes in every test of a method of this project and full name.</summary>
    public bool TakesMethod(string project, string method) => Row is null && PathNames.Same(Project, project) && (Name is null || Under(method, Name));

    /// <summary>Whether it takes in this data row of a method, the method whole or the row alone.</summary>
    public bool TakesRow(string project, string method, string row) =>
        TakesMethod(project, method) || (Row == row && Name == method && PathNames.Same(Project, project));

    /// <summary>Whether it takes in everything another takes in.</summary>
    public bool Covers(SessionTest other) => other.Name is { } name
        ? (other.Row is { } row ? TakesRow(other.Project, name, row) : TakesMethod(other.Project, name))
        : Name is null && Row is null && PathNames.Same(Project, other.Project);

    /// <summary>A full name at or under another: the same, or a member or nested type of it.</summary>
    private static bool Under(string name, string above) =>
        name.StartsWith(above, StringComparison.Ordinal) && (name.Length == above.Length || name[above.Length] is '.' or '+');
}

/// <summary>A test session: a named set of tests the Unit Tests window shows in a tab of its own, which New Session makes
/// from the tests chosen, Append to Session adds to and Run Current Session runs. It keeps what it took in
/// (<see cref="SessionTest"/>), so a test written later in a class it holds is in it, and what was taken out of that; of
/// what takes a test in or out, the narrowest says (a method taken in again inside a class taken out is in).</summary>
public sealed class TestSession(string name, IEnumerable<SessionTest> tests, IEnumerable<SessionTest>? left = null)
{
    private readonly List<SessionTest> tests = [.. tests];
    private readonly List<SessionTest> left = [.. left ?? []];

    public string Name { get; internal set; } = name;

    /// <summary>What it takes in.</summary>
    public IReadOnlyList<SessionTest> Tests => tests;

    /// <summary>What was taken out of what it takes in.</summary>
    public IReadOnlyList<SessionTest> Left => left;

    /// <summary>Whether it holds a test the tree counts: a method without data rows, or a data row.</summary>
    public bool Holds(TestNode test) => test.Kind == TestNodeKind.Row
        ? test.Parent is { ProjectPath: { } project, FullName: { } method } && Holds(t => t.TakesRow(project, method, test.Name))
        : test is { ProjectPath: { } own, FullName: { } full } && Holds(t => t.TakesMethod(own, full));

    /// <summary>What of a method a run of it runs: the method, where it holds every test of it, else the data rows it holds.</summary>
    public IEnumerable<TestNode> Runs(TestNode method)
    {
        if (method is not { ProjectPath: { } project, FullName: { } full } || !tests.Any(t => t.TakesMethod(project, full) || (t.Name == full && t.Row is not null)))
        {
            return [];
        }
        bool whole = Holds(t => t.TakesMethod(project, full)) && !left.Any(l => l.Row is not null && l.Name == full && PathNames.Same(l.Project, project));
        return whole ? [method] : method.Children.Where(Holds);
    }

    /// <summary>Whether what takes a test in holds it: of what takes it in and what takes it out, the narrowest takes it in.</summary>
    private bool Holds(Func<SessionTest, bool> takes) => Narrowest(tests.Where(takes), [.. left.Where(takes)]);

    /// <summary>Whether all that one takes in is held.</summary>
    private bool Holding(SessionTest test) => Narrowest(tests.Where(t => t.Covers(test)), [.. left.Where(l => l.Covers(test))]);

    /// <summary>Whether one of what takes a test in is narrower than all that takes it out, which lie in one line from the
    /// project down.</summary>
    private static bool Narrowest(IEnumerable<SessionTest> taking, IReadOnlyList<SessionTest> leaving) => taking.Any(t => leaving.All(l => !t.Covers(l)));

    /// <summary>Takes these in, each no longer left out; false where it held them all already.</summary>
    internal bool Add(IEnumerable<SessionTest> added)
    {
        bool changed = false;
        foreach (SessionTest test in added)
        {
            changed |= left.RemoveAll(test.Covers) > 0;
            if (!Holding(test))
            {
                tests.RemoveAll(test.Covers);
                tests.Add(test);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Takes these out: what lies wholly in them goes, and what still takes them in has them left out.</summary>
    internal bool Remove(IEnumerable<SessionTest> removed)
    {
        bool changed = false;
        foreach (SessionTest test in removed)
        {
            changed |= tests.RemoveAll(test.Covers) > 0;
            if (Holding(test))
            {
                left.RemoveAll(test.Covers);
                left.Add(test);
                changed = true;
            }
        }
        left.RemoveAll(l => !tests.Any(t => t.Covers(l)));
        return changed;
    }

    /// <summary>The tests a row of the tree stands for, as a session takes them in: a project's every test, a class's, a
    /// method's or a data row's, and the methods under any other group (a namespace's classes, as its name alone would take
    /// in those of the namespaces under it).</summary>
    public static IEnumerable<SessionTest> Of(TestNode row) => row.Kind switch
    {
        TestNodeKind.Project when row.Project is { } project => [new SessionTest { Project = project }],
        TestNodeKind.Row when row.Parent is { ProjectPath: { } project, FullName: { } method } => [new SessionTest { Project = project, Name = method, Row = row.Name }],
        TestNodeKind.Method when row is { ProjectPath: { } project, FullName: { } method } => [new SessionTest { Project = project, Name = method }],
        TestNodeKind.Class or TestNodeKind.Namespace => row.Methods
            .Where(m => m.ProjectPath is not null && m.FullName is not null)
            .Select(m => new SessionTest { Project = m.ProjectPath!, Name = ClassOf(m.FullName!) })
            .Distinct(),
        _ => row.Methods.Where(m => m.ProjectPath is not null && m.FullName is not null)
            .Select(m => new SessionTest { Project = m.ProjectPath!, Name = m.FullName })
            .Distinct(),
    };

    /// <summary>A method's class's full name.</summary>
    public static string ClassOf(string method) => method.LastIndexOf('.') is int dot and > 0 ? method[..dot] : method;
}
