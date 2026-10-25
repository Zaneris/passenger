using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;

namespace Passenger.App.Testing;

/// <summary>The run icons beside the tests and test classes each open C# file declares,
/// coloured by how each last ran. A file's tests are read off the UI thread from its own parse as each lands, none made
/// for them, so typing in one file reads no other; the Unit Tests window's tree takes the same read (<see cref="Read"/>).
/// The icons follow the Unit Tests window at once when tests are found or a run begins or ends, and at most every 150 ms
/// while its results come in (a pause would never come in a long run); with no test known, and none known before, there
/// is nothing to follow.</summary>
public sealed class TestGutter
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(150);

    private readonly TestTree tree;

    /// <summary>The icons coloured a moment after the first result since they last were, while it is due.</summary>
    private readonly Debounce colouring;
    private bool due;
    private readonly Dictionary<OpenDocument, Declared> files = [];

    /// <summary>The tree's methods by their full names (nested types' '+' as '.'), and by every type they are under.</summary>
    private readonly Dictionary<string, HashSet<TestNode>> byMethod = [];
    private readonly Dictionary<string, HashSet<TestNode>> byType = [];
    private IReadOnlyList<TestNode>? indexed;
    private bool colouredWithTests;

    public TestGutter(TestTree tree)
    {
        this.tree = tree;
        colouring = new Debounce(Throttle, () =>
        {
            due = false;
            ColourAll();
            return Task.CompletedTask;
        });
        tree.Updated += (_, _) =>
        {
            colouring.Stop();
            due = false;
            ColourAll();
        };
        // Never put off by the next result, as a pause would be: the colours follow a long run at least this often.
        tree.ResultApplied += (_, _) =>
        {
            if (!due)
            {
                due = true;
                colouring.Restart();
            }
        };
    }

    /// <summary>Gives an open file its icons, read now if its parse is of the text as it stands, and again after each
    /// parse of it that lands.</summary>
    public void Watch(OpenDocument document)
    {
        UiThread.Check();
        if (!files.TryAdd(document, new Declared()))
        {
            return;
        }
        document.Parsed += (_, parsed) => ReadParse(document, parsed);
        if (document.Syntax?.Newest is { } now)
        {
            ReadParse(document, now);
        }
    }

    /// <summary>An open file's tests were read from a parse of its text as it stands.</summary>
    public event EventHandler<(OpenDocument Document, FileTests Tests)>? Read;

    /// <summary>Lets go of a closed file.</summary>
    public void Forget(OpenDocument document)
    {
        UiThread.Check();
        files.Remove(document);
    }

    /// <summary>How many times a file's tests have been read from its syntax, for a test to count.</summary>
    public int Reads(OpenDocument document) => files.TryGetValue(document, out Declared? file) ? file.Reads : 0;

    /// <summary>The test method, else the test class, whose icon is on a line, as the file was last read.</summary>
    public (string Type, string? Method)? At(OpenDocument document, int line)
    {
        UiThread.Check();
        if (!files.TryGetValue(document, out Declared? file))
        {
            return null;
        }
        foreach (DeclaredTest test in file.Tests.Tests)
        {
            if (test.Line == line)
            {
                return (test.Type, test.Method);
            }
        }
        foreach ((string type, int at) in file.Tests.Types)
        {
            if (at == line)
            {
                return (type, null);
            }
        }
        return null;
    }

    private void ReadParse(OpenDocument document, ParsedText parsed)
    {
        if (files.TryGetValue(document, out Declared? file))
        {
            IdeErrors.Observe(ReadAsync(document, file, ++file.Asked, parsed));
        }
    }

    private async Task ReadAsync(OpenDocument document, Declared file, int asked, ParsedText parsed)
    {
        FileTests tests = await BackgroundWork.Run(() => TestsAtCaret.Read(parsed));
        // A later parse's read is under way, the text has moved on to one that will land, or the file was closed.
        if (asked != file.Asked || !ReferenceEquals(document.Text.Snapshot, parsed.Text) || !files.ContainsKey(document))
        {
            return;
        }
        file.Reads++;
        file.Tests = tests;
        Read?.Invoke(this, (document, tests));
        Index();
        Colour(document, file);
    }

    private void ColourAll()
    {
        Index();
        bool known = byMethod.Count > 0;
        if (!known && !colouredWithTests)
        {
            return;
        }
        colouredWithTests = known;
        foreach ((OpenDocument document, Declared file) in files)
        {
            Colour(document, file);
        }
    }

    private void Colour(OpenDocument document, Declared file)
    {
        if (document.Syntax is null)
        {
            file.Tests = FileTests.None;
        }
        List<TestMark> marks = new(file.Tests.Types.Count + file.Tests.Tests.Count);
        foreach ((string type, int line) in file.Tests.Types)
        {
            marks.Add(new TestMark(line, MarkOf(byType.GetValueOrDefault(type.Replace('+', '.')))));
        }
        foreach (DeclaredTest test in file.Tests.Tests)
        {
            marks.Add(new TestMark(test.Line, MarkOf(byMethod.GetValueOrDefault($"{test.Type.Replace('+', '.')}.{test.Method}"))));
        }
        if (!marks.SequenceEqual(document.TestMarks))
        {
            document.TestMarks = marks;
        }
    }

    /// <summary>Indexes the tree's methods once for each set of tests found, as <see cref="TestsAtCaret.Matches"/> would
    /// match them: a method by its whole name (its data rows in it), a type by every name it starts.</summary>
    private void Index()
    {
        if (ReferenceEquals(indexed, tree.Methods))
        {
            return;
        }
        indexed = tree.Methods;
        byMethod.Clear();
        byType.Clear();
        foreach (TestNode method in tree.Methods)
        {
            if (method.FullName?.Replace('+', '.') is not { } name)
            {
                continue;
            }
            Add(byMethod, name, method);
            for (int dot = name.IndexOf('.', StringComparison.Ordinal); dot > 0; dot = name.IndexOf('.', dot + 1))
            {
                Add(byType, name[..dot], method);
            }
        }

        static void Add(Dictionary<string, HashSet<TestNode>> index, string key, TestNode method)
        {
            if (!index.TryGetValue(key, out HashSet<TestNode>? methods))
            {
                index[key] = methods = new HashSet<TestNode>(ReferenceEqualityComparer.Instance);
            }
            methods.Add(method);
        }
    }

    private static TestMarkKind MarkOf(HashSet<TestNode>? methods) =>
        methods is null || methods.Count == 0 ? TestMarkKind.NotRun
        : methods.Any(m => m.State == TestState.Running) ? TestMarkKind.Running
        : methods.Any(m => m.State == TestState.Failed) ? TestMarkKind.Failed
        : methods.All(m => m.State is TestState.Passed or TestState.Skipped) && methods.Any(m => m.State == TestState.Passed) ? TestMarkKind.Passed
        : TestMarkKind.NotRun;

    /// <summary>What a file was last read to declare, and how many reads were asked for and made.</summary>
    private sealed class Declared
    {
        public int Asked { get; set; }
        public int Reads { get; set; }
        public FileTests Tests { get; set; } = FileTests.None;
    }
}
