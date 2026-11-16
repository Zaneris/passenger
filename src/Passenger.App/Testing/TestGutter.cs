using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Workspace.Gherkin;

namespace Passenger.App.Testing;

/// <summary>The run icons beside the tests and test classes each open C# file declares, and beside the methods and types
/// a runner found tests of (by their names, or a base type's by the file and line it gave them), coloured by how each last
/// ran. A file's tests are read off the UI thread from its own parse as each lands, none made for them, so typing in one
/// file reads no other; the Unit Tests window's tree takes the same read (<see cref="Read"/>). An open GDScript file of a
/// Godot project carrying GUT is read for GUT's tests as its typing pauses (<see cref="GutScripts"/>), and an open .feature
/// file of a test project for its feature, rules, scenarios and example rows, each marked from the start and run by the tests
/// the runner or the code-behind's <c>#line</c> directives say are on its lines, a row by its outline's test's row. The icons
/// follow the Unit Tests window at once when tests are found or a run begins or ends, and at most every 150 ms while its
/// results come in (a pause would never come in a long run); with no test known, and none known before, nothing follows.</summary>
public sealed class TestGutter
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(150);

    /// <summary>How long a GDScript file's typing pauses before its GUT tests are read again.</summary>
    private static readonly TimeSpan ScriptPause = TimeSpan.FromMilliseconds(200);

    private readonly TestTree tree;

    /// <summary>The icons coloured a moment after the first result since they last were, while it is due.</summary>
    private readonly Debounce colouring;
    private bool due;
    private readonly Dictionary<OpenDocument, Declared> files = [];

    /// <summary>The tree's methods by their full names (nested types' '+' as '.'), and by every type they are under.</summary>
    private readonly Dictionary<string, HashSet<TestNode>> byMethod = [];
    private readonly Dictionary<string, HashSet<TestNode>> byType = [];

    /// <summary>The tree's methods by the file a runner found them in, each with the lines it gave their tests.</summary>
    private readonly Dictionary<string, List<(int Line, TestNode Method)>> byFile = new(PathNames.Comparer);
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
        Declared declared = new();
        if (!files.TryAdd(document, declared))
        {
            return;
        }
        if (IsFeature(document))
        {
            declared.Reading = new Debounce(ScriptPause, () => ReadFeatureAsync(document, declared, ++declared.Asked, document.Text.Snapshot));
            document.Text.Changed += (_, _) => declared.Reading.Restart();
            declared.Reading.Restart();
            return;
        }
        if (IsScript(document))
        {
            declared.Reading = new Debounce(ScriptPause, () => ReadScriptAsync(document, declared, ++declared.Asked, document.Text.Snapshot));
            document.Text.Changed += (_, _) => declared.Reading.Restart();
            declared.Reading.Restart();
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

    /// <summary>The test attributes a file's project derives from a framework's, which its methods are tests by too (a GUT
    /// project's, the test classes its scripts derive).</summary>
    public Func<string, IReadOnlySet<string>>? OwnAttributes { get; set; }

    /// <summary>The test projects a file is one of (<see cref="TestFinder.ProjectsOf"/>), whose tests alone its names mark.</summary>
    public Func<string, IReadOnlySet<string>>? ProjectsOf { get; set; }

    private static readonly IReadOnlySet<string> NoProjects = new HashSet<string>();

    /// <summary>Reads every open file's tests again: the attributes their projects derive changed.</summary>
    public void ReadAgain()
    {
        UiThread.Check();
        foreach (OpenDocument document in files.Keys.ToList())
        {
            if (files[document].Reading is { } reading)
            {
                reading.Restart();
            }
            else if (document.Syntax?.Newest is { } now)
            {
                ReadParse(document, now);
            }
        }
    }

    /// <summary>Lets go of a closed file.</summary>
    public void Forget(OpenDocument document)
    {
        UiThread.Check();
        if (files.Remove(document, out Declared? file))
        {
            file.Reading?.Stop();
        }
    }

    /// <summary>How many times a file's tests have been read from its syntax, for a test to count.</summary>
    public int Reads(OpenDocument document) => files.TryGetValue(document, out Declared? file) ? file.Reads : 0;

    /// <summary>The test method, else the test class, whose icon is on a line, as the file was last read and marked.</summary>
    public DeclaredPlace? At(OpenDocument document, int line)
    {
        UiThread.Check();
        if (!files.TryGetValue(document, out Declared? file) || !document.TestMarks.Any(m => m.Line == line))
        {
            return null;
        }
        return file.Places.Methods.FirstOrDefault(m => m.Line == line) ?? file.Places.Types.FirstOrDefault(t => t.Line == line);
    }

    /// <summary>Whether the Unit Tests window knows a test of a type, or the test a method is: one its code declares, or one
    /// a runner found (an attribute of the project's own, a test its base type declares).</summary>
    public bool Knows(string type, string? method)
    {
        UiThread.Check();
        Index();
        return byType.TryGetValue(type.Replace('+', '.'), out HashSet<TestNode>? tests)
            && (method is null || tests.Any(t => t.FullName is { } name && TestsAtCaret.Matches(name, type, method)));
    }

    /// <summary>The GUT test function, else the GUT test class, a GDScript file's line is in, as the file was last read; null
    /// in any other file, or outside them.</summary>
    public DeclaredPlace? ScriptTestAt(OpenDocument document, int line)
    {
        UiThread.Check();
        if (!files.TryGetValue(document, out Declared? file) || file.Reading is null)
        {
            return null;
        }
        if (file.Places.Methods.FirstOrDefault(m => m.First <= line && line <= m.Last) is { } method
            && file.Tests.Tests.Any(t => t.Type == method.Type && t.Method == method.Method))
        {
            return method;
        }
        return file.Places.Types.Where(t => t.First <= line && line <= t.Last && file.Tests.Types.Any(held => held.Type == t.Type)).MaxBy(t => t.First);
    }

    /// <summary>Reads again each open GDScript file last read before its project was known to carry GUT, and each .feature
    /// file last read before its test project was known: the test projects were found again.</summary>
    public void ReadUnplaced()
    {
        UiThread.Check();
        foreach (Declared file in files.Values.Where(f => f.Unplaced))
        {
            file.Unplaced = false;
            file.Reading?.Restart();
        }
    }

    private static bool IsScript(OpenDocument document) => Path.GetExtension(document.Path).Equals(".gd", StringComparison.OrdinalIgnoreCase);

    private static bool IsFeature(OpenDocument document) => Bdd.FeatureChecks.IsFeature(document.Path);

    /// <summary>A line a compiler gives a test (from one, in its counting) as the editor's, from one.</summary>
    public static int EditorLine(Passenger.Lsp.ServerLines lines, int line) =>
        line <= 0 ? line : CSharp.ServerPlaces.PositionOf(lines, new Passenger.Lsp.LspPosition(line - 1, 0)).Line + 1;

    /// <summary>The example row on a .feature file's line, with the outline it is a row of, as the file was last read; null on
    /// any other line.</summary>
    public (DeclaredPlace Outline, ExampleRow Row)? RowAt(OpenDocument document, int line)
    {
        UiThread.Check();
        if (!files.TryGetValue(document, out Declared? file) || file.Rows.FirstOrDefault(r => r.Row.Line == line) is not { } row
            || file.Places.Methods.FirstOrDefault(m => m.Line == row.Outline.Line) is not { } outline)
        {
            return null;
        }
        return (outline, row);
    }

    /// <summary>Whether a data row of an outline's test ran an example row, by the arguments its name gives, the rows read with it.</summary>
    public static bool Ran(TestNode node, ExampleRow row, IReadOnlyList<ExampleRow> rows) =>
        node.Kind == TestNodeKind.Row && ReferenceEquals(ExampleRows.RowOf(rows, row.Outline, node.Name), row);

    /// <summary>The example rows a .feature file was last read to hold.</summary>
    public IReadOnlyList<ExampleRow> RowsOf(OpenDocument document) => files.TryGetValue(document, out Declared? file) ? file.Rows : [];

    /// <summary>The scenario, else the rule, else the feature a .feature file's line is in, as the file was last read; null in
    /// any other file, or outside them.</summary>
    public DeclaredPlace? FeatureTestAt(OpenDocument document, int line)
    {
        UiThread.Check();
        if (!IsFeature(document) || !files.TryGetValue(document, out Declared? file))
        {
            return null;
        }
        return file.Places.Methods.FirstOrDefault(m => m.First <= line && line <= m.Last)
            ?? file.Places.Types.Where(t => t.First <= line && line <= t.Last).MinBy(t => t.Last - t.First);
    }

    /// <summary>A .feature file's feature, rules and scenarios read off the UI thread from its text as it stood, in its
    /// project's language: a place each, marked whether or not its tests are found yet (a build makes them), the feature and
    /// its rules as types holding the scenarios on their lines; none in a file of no test project.</summary>
    private async Task ReadFeatureAsync(OpenDocument document, Declared file, int asked, Passenger.Text.TextSnapshot text)
    {
        string? project = ProjectsOf?.Invoke(document.Path).FirstOrDefault();
        (FilePlaces places, IReadOnlyList<ExampleRow> rows) = project is null ? (FilePlaces.None, []) : await BackgroundWork.Run(() =>
        {
            Passenger.Workspace.Gherkin.FeatureReading reading = Passenger.Workspace.Gherkin.FeatureReading.Read(text.ToString(),
                Passenger.Workspace.Gherkin.FeatureLanguage.Of(Path.GetDirectoryName(project)));
            List<DeclaredPlace> types = [];
            if (reading.Feature is { } feature)
            {
                types.Add(new DeclaredPlace($"{feature.Keyword}: {feature.Title}", null, feature.Line, feature.Line, feature.EndLine));
            }
            types.AddRange(reading.Rules.Select(r => new DeclaredPlace($"{r.Keyword}: {r.Title}", null, r.Line, r.Line, r.EndLine)));
            List<DeclaredPlace> methods = [.. reading.Scenarios.Where(s => s.Kind != Passenger.Workspace.Gherkin.ScenarioKind.Background)
                .Select(s => new DeclaredPlace(document.Path, s.Title.Length > 0 ? s.Title : s.Keyword, s.Line, s.Line, s.EndLine))];
            return (new FilePlaces(types, methods), ExampleRows.Of(reading));
        });
        if (asked != file.Asked || !ReferenceEquals(document.Text.Snapshot, text) || !files.ContainsKey(document))
        {
            return;
        }
        file.Reads++;
        file.Tests = FileTests.None;
        file.Places = places;
        file.Rows = rows;
        file.Unplaced = project is null;
        file.Marked = places.Types.Concat(places.Methods).Select(p => p.Line).ToHashSet();
        Index();
        Colour(document, file);
    }

    /// <summary>A GDScript file's GUT tests read off the UI thread from its text as it stood, as the test class its path in
    /// its Godot project names; none in a file of no project known to carry GUT, which tells the tree nothing, so what is
    /// on disk stands for it until it is read again (<see cref="ReadUnplaced"/>).</summary>
    private async Task ReadScriptAsync(OpenDocument document, Declared file, int asked, Passenger.Text.TextSnapshot text)
    {
        string? folder = ProjectsOf?.Invoke(document.Path).Where(p => Path.GetFileName(p) == Passenger.Workspace.GodotProject.FileName).Select(Path.GetDirectoryName).FirstOrDefault();
        IReadOnlySet<string>? bases = OwnAttributes?.Invoke(document.Path);
        (FileTests tests, FilePlaces places) = folder is null ? (FileTests.None, FilePlaces.None)
            : await BackgroundWork.Run(() => GutScripts.Read(text.ToString(), folder, document.Path, bases, GutScripts.ConfigOf(folder)));
        if (asked != file.Asked || !ReferenceEquals(document.Text.Snapshot, text) || !files.ContainsKey(document))
        {
            return;
        }
        file.Reads++;
        file.Tests = tests;
        file.Places = places;
        file.Unplaced = folder is null;
        if (folder is not null)
        {
            Read?.Invoke(this, (document, tests));
        }
        Index();
        Colour(document, file);
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
        IReadOnlySet<string>? own = OwnAttributes?.Invoke(document.Path);
        (FileTests tests, FilePlaces places) = await BackgroundWork.Run(() => (TestsAtCaret.Read(parsed, own), TestsAtCaret.Places(parsed)));
        // A later parse's read is under way, the text has moved on to one that will land, or the file was closed.
        if (asked != file.Asked || !ReferenceEquals(document.Text.Snapshot, parsed.Text) || !files.ContainsKey(document))
        {
            return;
        }
        file.Reads++;
        file.Tests = tests;
        file.Places = places;
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

    /// <summary>A mark beside each type and method the file declares a test of, or a runner found tests of: by the type's or
    /// method's name in the file's projects, or by the file and line the runner gave them (a base type's tests, run as each
    /// derived type's).</summary>
    private void Colour(OpenDocument document, Declared file)
    {
        if (document.Syntax is null && file.Reading is null)
        {
            file.Tests = FileTests.None;
            file.Places = FilePlaces.None;
        }
        HashSet<int> testLines = new(file.Tests.Tests.Count + file.Tests.Types.Count);
        foreach (DeclaredTest test in file.Tests.Tests)
        {
            testLines.Add(test.Line);
        }
        foreach ((_, int line) in file.Tests.Types)
        {
            testLines.Add(line);
        }
        List<(int Line, TestNode Method)> found = byFile.GetValueOrDefault(document.Path) ?? NoneFound;
        // A feature's tests are placed as its compiler counts its lines (at a lone '\r' too): taken to the editor's.
        if (IsFeature(document) && CSharp.ServerPlaces.Compiled(document.Path, document.Text.Snapshot) is { } compiled)
        {
            found = [.. found.Select(f => (EditorLine(compiled, f.Line), f.Method))];
        }
        List<TestMark> marks = new(file.Tests.Types.Count + file.Tests.Tests.Count);
        IReadOnlySet<string>? projects = null;
        foreach (DeclaredPlace place in file.Places.Types)
        {
            Mark(place);
        }
        foreach (DeclaredPlace place in file.Places.Methods)
        {
            Mark(place);
        }
        foreach (ExampleRow row in file.Rows)
        {
            HashSet<TestNode>? ran = null;
            if (file.Places.Methods.FirstOrDefault(m => m.Line == row.Outline.Line) is { } outline)
            {
                foreach (TestNode method in MethodsOf(outline) ?? [])
                {
                    foreach (TestNode each in method.Children.Where(c => Ran(c, row, file.Rows)))
                    {
                        (ran ??= new(ReferenceEqualityComparer.Instance)).Add(each);
                    }
                }
            }
            marks.Add(new TestMark(row.Row.Line, MarkOf(ran)));
        }
        if (!marks.SequenceEqual(document.TestMarks))
        {
            document.TestMarks = marks;
        }

        void Mark(DeclaredPlace place)
        {
            HashSet<TestNode>? methods = MethodsOf(place);
            if (methods is not null || testLines.Contains(place.Line) || file.Marked.Contains(place.Line))
            {
                marks.Add(new TestMark(place.Line, MarkOf(methods)));
            }
        }

        // A place no test is known by costs nothing: a parse of a file of thousands of methods colours them on every key.
        HashSet<TestNode>? MethodsOf(DeclaredPlace place)
        {
            HashSet<TestNode>? methods = null;
            Dictionary<string, HashSet<TestNode>> index = place.Method is null ? byType : byMethod;
            if (index.Count > 0 && index.TryGetValue(place.Method is null ? place.Type.Replace('+', '.') : $"{place.Type.Replace('+', '.')}.{place.Method}",
                out HashSet<TestNode>? named))
            {
                projects ??= ProjectsOf?.Invoke(document.Path) ?? NoProjects;
                foreach (TestNode each in named)
                {
                    if (TestFinder.IsOf(each, projects))
                    {
                        (methods ??= new(ReferenceEqualityComparer.Instance)).Add(each);
                    }
                }
            }
            foreach ((int line, TestNode method) in found)
            {
                if (place.Holds(line))
                {
                    (methods ??= new(ReferenceEqualityComparer.Instance)).Add(method);
                }
            }
            return methods;
        }
    }

    /// <summary>The methods a file without tests is known to hold: none, never added to.</summary>
    private static readonly List<(int Line, TestNode Method)> NoneFound = [];

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
        byFile.Clear();
        foreach (TestNode method in tree.Methods)
        {
            foreach ((string path, int line) in method.Places)
            {
                if (!byFile.TryGetValue(path, out List<(int Line, TestNode Method)>? its))
                {
                    byFile[path] = its = [];
                }
                its.Add((line, method));
            }
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
        public FilePlaces Places { get; set; } = FilePlaces.None;

        /// <summary>A GDScript file's read as its typing pauses; null for a C# file, read from its parses.</summary>
        public Debounce? Reading { get; set; }

        /// <summary>Whether a GDScript file was last read with no project carrying GUT known to hold it, or a .feature file with
        /// no test project.</summary>
        public bool Unplaced { get; set; }

        /// <summary>The lines marked whether or not a test is known there: a feature's and its scenarios'.</summary>
        public IReadOnlySet<int> Marked { get; set; } = new HashSet<int>();

        /// <summary>A feature's outlines' example rows, each marked by the row of its outline's test that ran it.</summary>
        public IReadOnlyList<ExampleRow> Rows { get; set; } = [];
    }
}
