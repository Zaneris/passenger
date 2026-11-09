using System.Buffers;
using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>Where a test or a group of them stands.</summary>
public enum TestState
{
    NotRun,
    Running,
    Passed,
    Failed,
    Skipped,
}

/// <summary>A test as the IDE knows it, whoever found it: the code (a method with a test attribute) or a runner's
/// discovery, which finds each of a theory's data rows too. Its id is its project and full name, and a data row's name,
/// so the same test found again, or found by the other, is the same test and a run's results land where the code put
/// it. A runner's tests carry the target framework they were built for, which the id leaves out.</summary>
public sealed record TestItem(string Project, string FullyQualifiedName, string DisplayName)
{
    /// <summary>The target framework of the assembly a runner found it in; null for a test found in the code.</summary>
    public string? Framework { get; init; }

    /// <summary>A data row's name as the tree shows it under its method ("Doubles(value: 1, twice: 2)"); null for a
    /// test that is its method.</summary>
    public string? Row { get; init; }

    public string? Path { get; init; }

    /// <summary>The file declaring a test its <see cref="Path"/> inherits, whose lines a failure's are; null for a test declared
    /// where it is.</summary>
    public string? DeclaredIn { get; init; }

    /// <summary>The line its name is on, from one; zero when unknown.</summary>
    public int Line { get; init; }

    /// <summary>Its traits, categories included, as name and value.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Traits { get; init; } = [];

    /// <summary>The names a test found in the code gives its cases (NUnit's TestName), which a runner names them by in
    /// place of the method's.</summary>
    public IReadOnlyList<string> Cases { get; init; } = [];

    /// <summary>The method it is or runs: its full name without the arguments NUnit names a case or a fixture's
    /// instance by.</summary>
    public string Method => MethodOf(FullyQualifiedName);

    /// <summary>The id of the method it is or belongs to, which a test found in the code has as its own.</summary>
    public string MethodId => $"{Project}::{Method}";

    public string Id => Row is null ? MethodId : $"{MethodId}::{Row}";

    /// <summary>Tests alike where the tree keeps them: by their id under the target framework they ran for. A map of
    /// thousands of tests is keyed by the tests themselves through it, holding no key of its own for each.</summary>
    internal static IEqualityComparer<TestItem> ById { get; } = new IdComparer();

    /// <summary>"A.B.Class.Method(1, 2)" as "A.B.Class.Method", and a fixture's instance's "A.B.Fixture(1).Method" or
    /// "A.B.Fixture&lt;Int32&gt;.Method" as "A.B.Fixture.Method".</summary>
    public static string MethodOf(string fullyQualifiedName) =>
        fullyQualifiedName.AsSpan().IndexOfAny('(', '<') < 0 ? fullyQualifiedName
            : string.Create(Unargued(fullyQualifiedName, []), fullyQualifiedName, (into, name) => Unargued(name, into));

    /// <summary>The data row a runner's name for a test or a result says it is, as the tree names it: a fixture's
    /// instance's class and method ("Fixture(1).Method(2)"), else its display name less its type's ("Doubles(value: 1)"
    /// for xUnit, "Doubles(1)" for NUnit, "Doubles (1)" for MSTest); null for a test that is its method, whose name is the
    /// method's or has no arguments.</summary>
    public static string? RowOf(string fullyQualifiedName, string displayName)
    {
        if (Instance(fullyQualifiedName, displayName) is { } instance)
        {
            return instance;
        }
        string method = MethodOf(fullyQualifiedName);
        int dot = method.LastIndexOf('.');
        string shown = Shown(method, displayName);
        return shown == method[(dot + 1)..] || shown == method || (!shown.Contains('(', StringComparison.Ordinal) && method.Length == fullyQualifiedName.Length) ? null : shown;
    }

    /// <summary>A display name less the type its method's full name begins with ("Doubles(value: 1)" of
    /// "A.B.Class.Doubles(value: 1)"): a data row's name as the tree shows it.</summary>
    public static string Shown(string method, string displayName)
    {
        int dot = method.LastIndexOf('.');
        return dot > 0 && displayName.StartsWith(method[..(dot + 1)], StringComparison.Ordinal) ? displayName[(dot + 1)..] : displayName;
    }

    /// <summary>A test its framework names in place of its method (NUnit's SetName and TestName) as a row of that method: the
    /// method's full name in the class the runner names ("A.B.Sums" of "A.B.Two.Plus" shown "Two.Plus", of method Sums;
    /// "A.B.F(1).Sums" of "A.B.F(1).Two" shown "Two"), and the row; null for a test named by its method, or by a name that
    /// is no class's and the display name's. A name given may hold dots: only the display name says where it begins, but
    /// where it is the full name itself.</summary>
    public static (string Name, string Row)? NamedCase(string runnerName, string displayName, string method)
    {
        int end = runnerName == displayName ? LastDots(runnerName).Method
            : runnerName.EndsWith(displayName, StringComparison.Ordinal) ? runnerName.Length - displayName.Length - 1 : -1;
        if (end <= 0 || runnerName[end] != '.')
        {
            return null;
        }
        string name = string.Concat(runnerName.AsSpan(0, end + 1), method);
        return SameMethod(name, runnerName) ? null : (name, RowOf(name, displayName) ?? Shown(MethodOf(name), displayName));
    }

    /// <summary>A test of a fixture's instance, its class named with the arguments or type arguments it was made with, as
    /// its class from its full name and its method from its display name, which alone holds a case's arguments on the
    /// Testing Platform ("Fixture(1).Method(2)" of "A.B.Fixture(1).Method" shown "Method(2)"); null for any other.</summary>
    private static string? Instance(string fullyQualifiedName, string displayName)
    {
        (int type, int method) = LastDots(fullyQualifiedName);
        if (method <= 0 || fullyQualifiedName.AsSpan(type + 1, method - type - 1).IndexOfAny('(', '<') < 0)
        {
            return null;
        }
        return displayName.AsSpan().StartsWith(fullyQualifiedName.AsSpan(0, method + 1), StringComparison.Ordinal)
            ? displayName[(type + 1)..]
            : string.Concat(fullyQualifiedName.AsSpan(type + 1, method - type), displayName);
    }

    /// <summary>The last two dots of a name outside its argument lists, those before a type's and a method's names; -1 for one
    /// it lacks.</summary>
    private static (int Type, int Method) LastDots(ReadOnlySpan<char> name)
    {
        int type = -1;
        int method = -1;
        (int Depth, char Quote) inside = default;
        for (int at = 0; at < name.Length; at++)
        {
            if (Outside(name, ref at, ref inside) && name[at] == '.')
            {
                (type, method) = (method, at);
            }
        }
        return (type, method);
    }

    /// <summary>The longest method name compared on the stack; a longer one is written into a rented array.</summary>
    private const int OnStack = 256;

    /// <summary>Whether two runners' names are of one <see cref="Method"/>, never made a string.</summary>
    private static bool SameMethod(string x, string y)
    {
        if (x.AsSpan().IndexOfAny('(', '<') < 0 && y.AsSpan().IndexOfAny('(', '<') < 0)
        {
            return string.Equals(x, y, StringComparison.Ordinal);
        }
        int length = Unargued(x, []);
        if (Unargued(y, []) != length)
        {
            return false;
        }
        char[]? rented = null;
        Span<char> both = length <= OnStack ? stackalloc char[2 * OnStack] : (rented = ArrayPool<char>.Shared.Rent(2 * length));
        _ = Unargued(x, both[..length]);
        _ = Unargued(y, both.Slice(length, length));
        bool same = both[..length].SequenceEqual(both.Slice(length, length));
        if (rented is not null)
        {
            ArrayPool<char>.Shared.Return(rented);
        }
        return same;
    }

    /// <summary>The hash of a runner's name's <see cref="Method"/>, never made a string.</summary>
    private static int MethodHash(string name)
    {
        if (name.AsSpan().IndexOfAny('(', '<') < 0)
        {
            return string.GetHashCode(name.AsSpan());
        }
        int length = Unargued(name, []);
        char[]? rented = null;
        Span<char> into = length <= OnStack ? stackalloc char[OnStack] : (rented = ArrayPool<char>.Shared.Rent(length));
        _ = Unargued(name, into[..length]);
        int hash = string.GetHashCode(into[..length]);
        if (rented is not null)
        {
            ArrayPool<char>.Shared.Return(rented);
        }
        return hash;
    }

    /// <summary>The length of a runner's name less its argument lists ("(1, \"a)\")", "&lt;Int32&gt;"), wherever they are,
    /// that much written into <paramref name="into"/> as far as it holds; a list never closed takes the rest of the name.</summary>
    private static int Unargued(ReadOnlySpan<char> name, Span<char> into)
    {
        int written = 0;
        (int Depth, char Quote) inside = default;
        for (int at = 0; at < name.Length; at++)
        {
            if (Outside(name, ref at, ref inside))
            {
                if (written < into.Length)
                {
                    into[written] = name[at];
                }
                written++;
            }
        }
        return written;
    }

    /// <summary>Whether a name's character is outside its argument lists, counting the lists and quoted words it goes into
    /// and out of; an escaped character in quotes is passed over with its backslash.</summary>
    private static bool Outside(ReadOnlySpan<char> name, ref int at, ref (int Depth, char Quote) inside)
    {
        char c = name[at];
        if (inside.Quote != '\0')
        {
            if (c == '\\')
            {
                at++;
            }
            else if (c == inside.Quote)
            {
                inside.Quote = '\0';
            }
            return false;
        }
        if (c is '(' or '<')
        {
            inside.Depth++;
            return false;
        }
        if (inside.Depth == 0)
        {
            return true;
        }
        if (c is ')' or '>')
        {
            inside.Depth--;
        }
        else if (c is '"' or '\'')
        {
            inside.Quote = c;
        }
        return false;
    }

    private sealed class IdComparer : IEqualityComparer<TestItem>
    {
        public bool Equals(TestItem? x, TestItem? y) => ReferenceEquals(x, y) || (x is not null && y is not null
            && string.Equals(x.Framework, y.Framework, StringComparison.Ordinal) && string.Equals(x.Row, y.Row, StringComparison.Ordinal)
            && string.Equals(x.Project, y.Project, StringComparison.Ordinal) && SameMethod(x.FullyQualifiedName, y.FullyQualifiedName));

        // The project is left out: tests of one name in two projects are few, and a path is long.
        public int GetHashCode(TestItem test) => HashCode.Combine(test.Framework, test.Row, MethodHash(test.FullyQualifiedName));
    }
}

/// <summary>A test project's tests as its code declares them, and the methods each of its files declares, by which a
/// test a runner found in a file and since removed from it is known to be gone.</summary>
public sealed record CodeTests(IReadOnlyList<TestItem> Tests, IReadOnlyDictionary<string, IReadOnlySet<string>> MethodsByFile)
{
    public static readonly CodeTests None = new([], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal));
}

/// <summary>The lines a project's code declares its tests on, taken for a worker as a runner looks through what was built
/// of it, while they are the build's: the test a line of a file is in, for a runner that names it otherwise and says nothing
/// more of it than the line. Lines are those of <see cref="TestItem.Line"/>.</summary>
public sealed class DeclaredLines
{
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> methods;
    private readonly Lazy<Dictionary<string, TestItem[]>> files;

    /// <summary>The code as told now: a move of lines replaces its tests in place, so they are copied here.</summary>
    public DeclaredLines(CodeTests code)
    {
        TestItem[] tests = [.. code.Tests];
        methods = code.MethodsByFile;
        files = new(() => tests.Where(t => t.Path is not null).GroupBy(t => t.Path!, StringComparer.Ordinal)
            .ToDictionary(file => file.Key, file => file.OrderBy(t => t.Line).ToArray(), StringComparer.Ordinal));
    }

    /// <summary>The name of the test method a runner's line of a file is in, the last declared at or above it, for a test
    /// whose name is of no method the file declares; null for one whose name is, or a line above every test of the file.</summary>
    public string? MethodAt(string path, int line, string fullyQualifiedName)
    {
        string method = TestItem.MethodOf(fullyQualifiedName);
        if (!methods.TryGetValue(path, out IReadOnlySet<string>? declared) || declared.Contains(method[(method.LastIndexOf('.') + 1)..])
            || !files.Value.TryGetValue(path, out TestItem[]? tests))
        {
            return null;
        }
        int after = Sorted.FirstAfter<TestItem, int>(tests, static t => t.Line, line);
        return after == 0 ? null : tests[after - 1].DisplayName;
    }
}

/// <summary>How a test, or one of its data rows, ran, as its runner reports it.</summary>
public sealed record TestOutcome(TestItem Test, TestState State, TimeSpan Duration)
{
    /// <summary>The result's own name, which for a theory whose rows the runner makes as it runs names the row.</summary>
    public string? DisplayName { get; init; }

    public string? Message { get; init; }

    public string? StackTrace { get; init; }

    /// <summary>What the test wrote.</summary>
    public string? Output { get; init; }

    /// <summary>The files its result carries: what the test attached (xunit's AddAttachment, MSTest's AddResultFile, NUnit's
    /// AddTestAttachment), and the frames of a failing headless test's windows (<see cref="TestFrames"/>).</summary>
    public IReadOnlyList<TestAttachment> Attachments { get; init; } = [];

    /// <summary>When the run it is kept from began, a run of an earlier session (<see cref="TestResultsState"/>); null for
    /// a result of this one's.</summary>
    public DateTimeOffset? Earlier { get; init; }

    /// <summary>Whether it takes the place of the outcome told before in the run for its test and row: a runner that tells
    /// each test's end as it comes tells its details once the run has ended.</summary>
    public bool Again { get; init; }

    /// <summary>The row this result is of, the test's own or one its run made; null for the method itself.</summary>
    public string? Row => Test.Row ?? (DisplayName is { } shown ? TestItem.RowOf(Test.FullyQualifiedName, shown) : null);
}

/// <summary>A file a test's result carries, by the name the test gave it, where its runner wrote it.</summary>
public sealed record TestAttachment(string Name, string Path)
{
    /// <summary>A picture the Unit Tests window shows as a frame of the result (a PNG of a window, as a rule).</summary>
    public bool IsPicture => PictureFiles.Is(Path);

    /// <summary>An attachment as a runner names it: a file URI or a path.</summary>
    public static TestAttachment Of(string name, string uriOrPath)
    {
        // Only a written URI is read as one: a path with % or # in it is a path.
        string path = uriOrPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(uriOrPath, UriKind.Absolute, out Uri? uri) ? uri.LocalPath : uriOrPath;
        return new TestAttachment(name.Length > 0 ? name : System.IO.Path.GetFileName(path), path);
    }
}

/// <summary>How a run ended: how long its tests took, and whether it was stopped or broke off.</summary>
public sealed record RunSummary(TimeSpan Elapsed, bool Canceled, bool Aborted, string? Error)
{
    /// <summary>Every outcome of the run, which the run's end holds to whatever order they were told in.</summary>
    public IReadOnlyList<TestOutcome> Outcomes { get; init; } = [];

    /// <summary>This run and the next as one: their times added, stopped or broken off if either was, every outcome.</summary>
    public RunSummary And(RunSummary next) => new(Elapsed + next.Elapsed, Canceled || next.Canceled, Aborted || next.Aborted, Error ?? next.Error)
    {
        Outcomes = [.. Outcomes, .. next.Outcomes],
    };
}
