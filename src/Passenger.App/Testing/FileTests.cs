namespace Passenger.App.Testing;

/// <summary>A test method a file declares: its type's full name (nested types after a '+'), its name, the editor's line
/// (from zero) its name is on, and the traits its attributes and its type's give it. Abstract and generic types' tests
/// run only through the types deriving from them.</summary>
public sealed record DeclaredTest(string Type, string Method, int Line, IReadOnlyList<KeyValuePair<string, string>> Traits, bool Abstract)
{
    /// <summary>The names its cases are given as string literals (NUnit's TestCase TestName), each a runner's test name.</summary>
    public IReadOnlyList<string> Cases { get; init; } = [];

    /// <summary>Its code, attributes to body, as a number that changes with what it says (<see cref="TestCode"/>): whether a
    /// result is of the code there now. A read that differs in it alone is the same (<see cref="SameAs"/>).</summary>
    public ulong Code { get; init; }

    /// <summary>The file of the script declaring a test the file inherits, which holds its code and lines
    /// (<see cref="FileTests.PassedOn"/>); null for a test the file declares itself.</summary>
    public string? DeclaredIn { get; init; }

    public bool SameAs(DeclaredTest other) => Line == other.Line && SameTest(other);

    /// <summary>The same test, on whatever line.</summary>
    public bool SameTest(DeclaredTest other) =>
        Type == other.Type && Method == other.Method && Abstract == other.Abstract && DeclaredIn == other.DeclaredIn && Traits.SequenceEqual(other.Traits) && Cases.SequenceEqual(other.Cases);
}

/// <summary>What a file declares for testing: the types that hold tests and the tests, with the lines their names are on,
/// the name of every method it declares, test or not, and every test it tells, by which a test a runner found there and
/// since removed is known to be gone, and the test attributes it declares (classes deriving from one, named as written,
/// less "Attribute").</summary>
public sealed record FileTests(IReadOnlyList<(string Type, int Line)> Types, IReadOnlyList<DeclaredTest> Tests, IReadOnlySet<string> Methods)
{
    public static readonly FileTests None = new([], [], new HashSet<string>(StringComparer.Ordinal));

    public IReadOnlySet<string> Attributes { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The code of each test it declares for those extending it, by the test's name (<see cref="DeclaredTest.Code"/>),
    /// whether or not it tells that test, and outside <see cref="SameAs"/> as a test's own code is: a change of it reads no other
    /// file again.</summary>
    public IReadOnlyDictionary<string, ulong> PassedOn { get; init; } = new Dictionary<string, ulong>(StringComparer.Ordinal);

    /// <summary>Whether a later read says the same: typing inside a method's body changes nothing of it.</summary>
    public bool SameAs(FileTests other) =>
        Types.SequenceEqual(other.Types) && Tests.Count == other.Tests.Count && Tests.Zip(other.Tests).All(p => p.First.SameAs(p.Second)) && Methods.SetEquals(other.Methods)
        && Attributes.SetEquals(other.Attributes);

    /// <summary>Whether a later read says the same but for the lines the types and tests are on: lines put in or taken out
    /// above or among them, which move their rows rather than make the tree again.</summary>
    public bool OnlyMoved(FileTests other)
    {
        if (Types.Count != other.Types.Count || Tests.Count != other.Tests.Count || !Methods.SetEquals(other.Methods) || !Attributes.SetEquals(other.Attributes))
        {
            return false;
        }
        for (int i = 0; i < Types.Count; i++)
        {
            if (Types[i].Type != other.Types[i].Type)
            {
                return false;
            }
        }
        for (int i = 0; i < Tests.Count; i++)
        {
            if (!Tests[i].SameTest(other.Tests[i]))
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>A type or a method a file declares: its type's full name (nested types after a '+'), the method's name (null
/// for the type), the editor's line (from zero) its name is on, and the lines it spans from its attributes to its end as
/// the compiler counts them (from zero), where a runner says the tests it declares are.</summary>
public sealed record DeclaredPlace(string Type, string? Method, int Line, int First, int Last)
{
    /// <summary>Whether a line a runner gives a test (from one, as the compiler counts) is inside it.</summary>
    public bool Holds(int line) => line - 1 >= First && line - 1 <= Last;
}

/// <summary>Every type and method a file declares, with where each is (<see cref="DeclaredPlace"/>), outer types first.</summary>
public sealed record FilePlaces(IReadOnlyList<DeclaredPlace> Types, IReadOnlyList<DeclaredPlace> Methods)
{
    public static readonly FilePlaces None = new([], []);
}
