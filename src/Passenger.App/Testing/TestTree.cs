using Avalonia;
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

public enum TestNodeKind
{
    Project,

    /// <summary>One of a multi-targeted project's target frameworks, under its project.</summary>
    Framework,
    Namespace,
    Class,
    Method,

    /// <summary>One of a theory's data rows (or NUnit's cases), found by a runner or made by its run.</summary>
    Row,

    /// <summary>A folder of a project's files that a test's file is in, as the File System grouping nests them.</summary>
    Folder,

    /// <summary>A category or trait the tests under it have.</summary>
    Category,

    /// <summary>How long the tests under it took, by grade.</summary>
    Duration,
}

/// <summary>A row of the Unit Tests window: a group (a project, a target framework, a folder, a namespace, a category or a
/// duration), a class, a test method or one of its data rows. A group's state is its methods' (failed over running over
/// not run over passed), its duration their sum, both kept up to date by each method's change rather than summed again;
/// a method's state is all its results', a data row's its own. A test under two categories is a method row under each,
/// both given every result.</summary>
public sealed class TestNode : Observable, ITreeRow
{
    private readonly List<TestOutcome> results = [];

    /// <summary>A group's methods by state, or a method's results by state, indexed by <see cref="TestState"/>.</summary>
    private readonly int[] counts = new int[5];
    private TestState state;
    private long ticks;
    private bool isExpanded;

    /// <summary>How many outcomes the method's run is waiting for.</summary>
    private int expected;

    public TestNode(TestNodeKind kind, string name, TestNode? parent)
    {
        Kind = kind;
        Name = name;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
        isExpanded = kind is not (TestNodeKind.Method or TestNodeKind.Row);
    }

    /// <summary>A test the window counts and filters: a data row, or a method with none.</summary>
    internal bool IsLeaf => Kind == TestNodeKind.Row || (Kind == TestNodeKind.Method && Children.Count == 0);

    public TestNodeKind Kind { get; }
    public string Name { get; }
    public TestNode? Parent { get; private set; }
    public List<TestNode> Children { get; } = [];
    public int Depth { get; private set; }
    public Thickness Indent => new(Depth * 16, 0, 0, 0);

    /// <summary>Where the tree has it: its groups' names and its own, by which a load or a regrouping finds it again.</summary>
    internal string Key { get; set; } = string.Empty;

    /// <summary>A class's, method's or data row's test, whatever groups it is under: by it the same row is found again
    /// when the grouping changes, keeping its results and whether it is open.</summary>
    internal string? Identity { get; set; }

    /// <summary>A method's target framework when its project is built for several, which its full name does not say.</summary>
    public string? Framework { get; internal set; }

    /// <summary>The project file of a method's tests.</summary>
    public string? ProjectPath => FromCode?.Project ?? Items.FirstOrDefault()?.Project;

    /// <summary>A method's tests as a runner found them, which a run runs: the method, or each of its data rows; none
    /// until a runner has looked.</summary>
    public List<TestItem> Items { get; } = [];

    /// <summary>A method as its code declares it now: where it is, while it is typed.</summary>
    public TestItem? FromCode { get; internal set; }

    /// <summary>A data row's test; null for a row its method's run made.</summary>
    public TestItem? Item { get; internal set; }

    /// <summary>A project row's project file, as its tests' <see cref="TestItem.Project"/> names it.</summary>
    public string? Project { get; internal set; }

    /// <summary>Taken out of the tree by a load: the method was renamed or removed.</summary>
    public bool Gone { get; private set; }

    /// <summary>The results of a method's last run, one per test or data row; a data row's own.</summary>
    public IReadOnlyList<TestOutcome> Results => results;

    /// <summary>A method's full name, Namespace.Class.Method, nested types after a '+'.</summary>
    public string? FullName => FromCode?.Method ?? Items.FirstOrDefault()?.Method ?? Item?.Method;

    /// <summary>Where it is: a data row the runner found no file for is where its method is, if anything says so.</summary>
    public string? Path => Located?.Path ?? (Kind == TestNodeKind.Row ? Parent?.Located?.Path : Children.Select(c => c.Path).FirstOrDefault(p => p is not null));

    public int Line => Located?.Line ?? (Kind == TestNodeKind.Row ? Parent?.Located?.Line ?? 0 : Children.Select(c => c.Line).FirstOrDefault(l => l > 0));

    public TestState State
    {
        get => state;
        private set
        {
            TestState was = state;
            if (Set(ref state, value) && Kind == TestNodeKind.Method)
            {
                for (TestNode? group = Parent; group is not null; group = group.Parent)
                {
                    group.Shift(was, value);
                }
            }
        }
    }

    public TimeSpan Duration => TimeSpan.FromTicks(ticks);

    public bool IsExpanded
    {
        get => isExpanded;
        internal set => Set(ref isExpanded, value);
    }

    public bool HasChildren => Children.Count > 0;

    bool ITreeRow.Expands => HasChildren;

    /// <summary>The methods under this row, or the method itself; none under a data row.</summary>
    public IEnumerable<TestNode> Methods => Kind switch
    {
        TestNodeKind.Method => [this],
        TestNodeKind.Row => [],
        _ => Children.SelectMany(c => c.Methods),
    };

    /// <summary>What the details pane shows: each result's outcome, message, stack, output and the files it carries.</summary>
    public string Details => string.Join("\n\n", results.Select(r =>
    {
        string header = results.Count > 1 ? $"{ResultName(r)}: {r.State}" : r.State.ToString();
        string? attachments = r.Attachments.Count > 0 ? $"Attachments:\n{string.Join('\n', r.Attachments.Select(a => $"  {a.Name}: {a.Path}"))}" : null;
        return string.Join('\n', new[] { header, r.Message, r.StackTrace, r.Output is { } output ? $"Output:\n{output.TrimEnd()}" : null, attachments }.Where(t => !string.IsNullOrWhiteSpace(t)));
    }));

    /// <summary>The pictures its results carry, the frames of a failing headless test's windows among them, in its results'
    /// order, each with the result it is of.</summary>
    public IReadOnlyList<(TestOutcome Result, TestAttachment Frame)> Frames =>
        [.. results.SelectMany(r => r.Attachments.Where(a => a.IsPicture).Select(a => (r, a)))];

    /// <summary>A result's name among a method's several: its data row's.</summary>
    internal static string ResultName(TestOutcome result) => result.Row ?? result.DisplayName ?? result.Test.DisplayName;

    /// <summary>Where the code or a runner says it is: the code's, which follows typing, over the last build's.</summary>
    private TestItem? Located => FromCode ?? Item ?? Items.FirstOrDefault(i => i.Path is not null);

    /// <summary>A method or row about to run: its results go, and it is running until as many outcomes as it runs
    /// tests have come.</summary>
    internal void Begin(int tests)
    {
        results.Clear();
        Array.Clear(counts);
        AddTicks(-ticks);
        expected = tests;
        State = TestState.Running;
        Raise(nameof(Details));
    }

    internal void Add(TestOutcome outcome)
    {
        results.Add(outcome);
        counts[(int)outcome.State]++;
        AddTicks(outcome.Duration.Ticks);
        State = Combine(counts, running: results.Count < expected);
        Raise(nameof(Details));
    }

    /// <summary>A method or row still running when its run ended was never run: it goes back to not run.</summary>
    internal void End()
    {
        if (State == TestState.Running)
        {
            State = Combine(counts, running: false);
        }
    }

    internal void Leave() => Gone = true;

    internal void RaiseShape() => Raise(nameof(HasChildren));

    /// <summary>Moves it under another row, as a regrouping does.</summary>
    internal void Reparent(TestNode? parent)
    {
        Parent = parent;
        int depth = parent is null ? 0 : parent.Depth + 1;
        if (depth != Depth)
        {
            Depth = depth;
            Raise(nameof(Depth));
        }
    }

    /// <summary>Takes another row's results as its own: the same test shown again under another group.</summary>
    internal void CopyFrom(TestNode source)
    {
        results.Clear();
        results.AddRange(source.results);
        Array.Copy(source.counts, counts, counts.Length);
        expected = source.expected;
        AddTicks(source.ticks - ticks);
        // Not through State, whose change a method's groups take: they are counted once the tree is made.
        if (state != source.state)
        {
            state = source.state;
            Raise(nameof(State));
        }
        Raise(nameof(Details));
    }

    /// <summary>A group's counts and duration made again from its methods, once, when the tree is loaded (a method found
    /// again may have more or fewer tests).</summary>
    internal void Recount()
    {
        if (Kind is TestNodeKind.Method or TestNodeKind.Row)
        {
            return;
        }
        Array.Clear(counts);
        long sum = 0;
        foreach (TestNode child in Children)
        {
            child.Recount();
            sum += child.ticks;
            if (child.Kind == TestNodeKind.Method)
            {
                counts[(int)child.state]++;
            }
            else
            {
                for (int each = 0; each < counts.Length; each++)
                {
                    counts[each] += child.counts[each];
                }
            }
        }
        if (sum != ticks)
        {
            ticks = sum;
            Raise(nameof(Duration));
        }
        State = Combine(counts, running: false);
    }

    /// <summary>One of the group's methods went from one state to another.</summary>
    private void Shift(TestState from, TestState to)
    {
        counts[(int)from]--;
        counts[(int)to]++;
        State = Combine(counts, running: false);
    }

    /// <summary>A method's time is its groups' too; a data row's is already its method's.</summary>
    private void AddTicks(long delta)
    {
        if (delta == 0)
        {
            return;
        }
        for (TestNode? node = this; node is not null; node = Kind == TestNodeKind.Row ? null : node.Parent)
        {
            node.ticks += delta;
            node.Raise(nameof(Duration));
        }
    }

    /// <summary>Failed over running over not run over passed; skipped only when all were.</summary>
    private static TestState Combine(int[] counts, bool running) =>
        counts[(int)TestState.Failed] > 0 ? TestState.Failed
        : running || counts[(int)TestState.Running] > 0 ? TestState.Running
        : counts[(int)TestState.NotRun] > 0 ? TestState.NotRun
        : counts[(int)TestState.Passed] > 0 ? TestState.Passed
        : counts[(int)TestState.Skipped] > 0 ? TestState.Skipped
        : TestState.NotRun;
}

/// <summary>The tests a runner found in one of a project's assemblies, built for one target framework.</summary>
public sealed record TestSource(string Framework, IReadOnlyList<TestItem> Tests);

/// <summary>A test project's tests: those its code declares, and those a runner found in each of its assemblies. The same
/// parts make an equal value, which the tree takes for a project unchanged.</summary>
public sealed record ProjectTests(string Project, string Name, CodeTests Code, IReadOnlyList<TestSource> Discovered)
{
    /// <summary>The one target framework the project file names, which groups its tests by target framework before a
    /// runner has found them in its assembly; null when it names several or one MSBuild has to work out.</summary>
    public string? DeclaredFramework { get; init; }
}

/// <summary>The Unit Tests window's tree: the tests grouped (by project and namespace unless another grouping is
/// chosen), then by class, method and data row, flattened to the rows that are open and pass the outcomes and search
/// chosen, so a virtualizing list draws only what is on screen. The code's tests and a runner's merge on their method's
/// id, so a run's results land on the rows the code showed, and a test the code declares stays whatever a run reported;
/// a regrouping moves each row, its results and whether it is open with it.</summary>
public sealed class TestTree
{
    /// <summary>The projects' tests the tree was last made from, and the grouping it was made with.</summary>
    private IReadOnlyList<ProjectTests> inputs = [];
    private IReadOnlyList<TestGroupLevel> builtWith = TestGroupings.Default;

    /// <summary>The rows that show each test a runner found, by its key: its data row's, else its method's, under each
    /// group it is in.</summary>
    private Dictionary<string, List<TestNode>> byKey = new(StringComparer.Ordinal);

    /// <summary>The methods shown from the code's tests, by the file the code declares them in: those a move of lines moves.</summary>
    private Dictionary<string, List<TestNode>> byFile = new(StringComparer.Ordinal);

    /// <summary>The methods and rows the run under way began, by identity, which its end ends wherever they are by then.</summary>
    private HashSet<string> running = new(StringComparer.Ordinal);

    private HashSet<TestState> outcomes = [];
    private string emptyText = string.Empty;

    public TreeRows<TestNode> Rows { get; } = [];

    /// <summary>Tests were found, or a run began or ended: what shows their state follows.</summary>
    public event EventHandler? Updated;

    /// <summary>Results came in during a run, of which there may be thousands: worth following a moment later.</summary>
    public event EventHandler? ResultApplied;

    /// <summary>The grouping, the outcomes shown or the search changed.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>The tree's top rows: its projects in the default grouping, else the first level's groups.</summary>
    public IReadOnlyList<TestNode> Roots { get; private set; } = [];

    /// <summary>Every test method once, in the tree's order, whatever it is grouped by.</summary>
    public IReadOnlyList<TestNode> Methods { get; private set; } = [];

    /// <summary>What the tests are grouped by above their classes, outermost first.</summary>
    public IReadOnlyList<TestGroupLevel> Grouping { get; private set; } = TestGroupings.Default;

    /// <summary>The outcomes whose tests are shown; every test is, while there are none.</summary>
    public IReadOnlySet<TestState> Outcomes => outcomes;

    /// <summary>What the tests shown have in their names, or the names of a row above them.</summary>
    public string Search { get; private set; } = string.Empty;

    /// <summary>What the window says while the tree has no rows: why there are none yet.</summary>
    public string EmptyText
    {
        get => emptyText;
        set
        {
            if (emptyText != value)
            {
                emptyText = value;
                EmptyTextChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? EmptyTextChanged;

    /// <summary>The tree for the test projects' tests, made again only when they changed. A row with the same place and
    /// name is the same row, so what was open or closed stays so, a chosen row stays chosen, and a test keeps its last
    /// results until it runs again.</summary>
    public void Load(IReadOnlyList<ProjectTests> tests)
    {
        List<ProjectTests> projects = [.. tests.DistinctBy(p => p.Project, StringComparer.Ordinal)];
        if (!projects.SequenceEqual(inputs) || !builtWith.SequenceEqual(Grouping))
        {
            inputs = projects;
            Build();
        }
        ShowRows();
        Updated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Groups the tests by these levels: each row moves with its results, and whether it is open, to where the
    /// levels put it.</summary>
    public void Group(IEnumerable<TestGroupLevel> levels)
    {
        IReadOnlyList<TestGroupLevel> ordered = TestGroupings.Ordered(levels);
        if (ordered.SequenceEqual(Grouping))
        {
            return;
        }
        Grouping = ordered;
        Build();
        ShowRows();
        Updated?.Invoke(this, EventArgs.Empty);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows only the tests with these outcomes (all of them for none), the rows above them opened.</summary>
    public void ShowOutcomes(IEnumerable<TestState> shown)
    {
        HashSet<TestState> next = [.. shown];
        if (next.SetEquals(outcomes))
        {
            return;
        }
        outcomes = next;
        OpenPassing();
        ShowRows();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows only the tests whose names, or the names of a row above them, have this text in them, ignoring case;
    /// the rows above them are opened.</summary>
    public void SearchFor(string text)
    {
        string trimmed = text.Trim();
        if (trimmed == Search)
        {
            return;
        }
        Search = trimmed;
        OpenPassing();
        ShowRows();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>How many tests are in each state, indexed by <see cref="TestState"/>: each data row of a method that has
    /// them, else each method, counted once whatever it is grouped by.</summary>
    public int[] Counts()
    {
        int[] counts = new int[Enum.GetValues<TestState>().Length];
        foreach (TestNode method in Methods)
        {
            if (method.Children.Count == 0)
            {
                counts[(int)method.State]++;
                continue;
            }
            foreach (TestNode row in method.Children)
            {
                counts[(int)row.State]++;
            }
        }
        return counts;
    }

    /// <summary>Opens every row that has rows under it.</summary>
    public void ExpandAll()
    {
        foreach (TestNode node in Every())
        {
            node.IsExpanded |= node.HasChildren;
        }
        ShowRows();
    }

    /// <summary>Closes every row, leaving the top ones.</summary>
    public void CollapseAll()
    {
        foreach (TestNode node in Every())
        {
            node.IsExpanded = false;
        }
        ShowRows();
    }

    /// <summary>The code's tests of a file moved to other lines, nothing else of them changed: each row showing one takes it
    /// on its new line (what Go to Source and the details read), the tree made again nowhere. The tests loaded hold them so
    /// already (<see cref="TestFinder.Moved"/>).</summary>
    public void Move(string path, IReadOnlyDictionary<TestItem, TestItem> moved)
    {
        if (!byFile.TryGetValue(path, out List<TestNode>? methods))
        {
            return;
        }
        foreach (TestNode method in methods)
        {
            if (method.FromCode is { } was && moved.TryGetValue(was, out TestItem? now))
            {
                method.FromCode = now;
            }
        }
    }

    /// <summary>The row a test is shown by: its data row's, else its method's (the first, when it is under two groups).</summary>
    public TestNode? Node(TestItem test) => byKey.TryGetValue(test.Key, out List<TestNode>? nodes) ? nodes[0] : null;

    /// <summary>The tests about to run go to running, each method waiting for as many outcomes as it runs tests; the
    /// rows a method's last run made wait to be made again.</summary>
    public void Begin(IReadOnlyList<TestItem> tests)
    {
        Dictionary<TestNode, int> methods = new(ReferenceEqualityComparer.Instance);
        List<TestNode> rows = [];
        foreach (TestItem test in tests)
        {
            foreach (TestNode node in byKey.GetValueOrDefault(test.Key) ?? [])
            {
                TestNode method = node.Kind == TestNodeKind.Row ? node.Parent! : node;
                methods[method] = methods.GetValueOrDefault(method) + 1;
                rows.AddRange(node.Kind == TestNodeKind.Row ? [node] : node.Children.Where(c => c.Item is null));
            }
        }
        foreach ((TestNode method, int count) in methods)
        {
            method.Begin(count);
        }
        foreach (TestNode row in rows)
        {
            row.Begin(1);
        }
        running = [.. methods.Keys.Concat(rows).Select(n => n.Identity).OfType<string>()];
        if (outcomes.Count > 0)
        {
            ShowRows();
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Outcomes as they come, each changing its rows, its methods and the groups above them, nothing else. A
    /// result naming a data row its test does not have (a theory whose rows its run makes) is that row's, made now.</summary>
    public void Apply(IEnumerable<TestOutcome> outcomes)
    {
        bool any = false;
        bool reshaped = false;
        foreach (TestOutcome outcome in outcomes)
        {
            foreach (TestNode node in byKey.GetValueOrDefault(outcome.Test.Key) ?? [])
            {
                TestNode method = node.Kind == TestNodeKind.Row ? node.Parent! : node;
                TestNode? row = node.Kind == TestNodeKind.Row ? node : outcome.Row is { } name ? MadeRow(method, name, ref reshaped) : null;
                row?.Add(outcome);
                method.Add(outcome);
                any = true;
            }
        }
        // Shown by outcome, a test that has one now may come or go.
        if (reshaped || (any && this.outcomes.Count > 0))
        {
            ShowRows();
        }
        if (any)
        {
            ResultApplied?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The run is over: what it began and never heard of goes back to not run, and a row a method's run made
    /// before and did not make again goes. Grouped by duration, each test moves to its time's group.</summary>
    public void End()
    {
        bool reshaped = false;
        foreach (TestNode node in Every().Where(n => n.Identity is { } identity && running.Contains(identity)).ToList())
        {
            if (node is { Kind: TestNodeKind.Row, Item: null, Results.Count: 0 } stale && stale.Parent is { Results.Count: > 0 } method)
            {
                method.Children.Remove(stale);
                stale.Leave();
                method.RaiseShape();
                reshaped |= method.IsExpanded;
            }
            node.End();
        }
        running = new(StringComparer.Ordinal);
        if (Grouping.Contains(TestGroupLevel.Duration))
        {
            Build();
            reshaped = true;
        }
        if (reshaped || outcomes.Count > 0)
        {
            ShowRows();
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Opens or closes a row: only its rows below come or go, so the rows chosen stay chosen.</summary>
    public void Toggle(TestNode node)
    {
        if (!node.HasChildren)
        {
            return;
        }
        node.IsExpanded = !node.IsExpanded;
        if (node.IsExpanded)
        {
            Rows.Open(node, Visible(node, includeSelf: false, Passing()));
        }
        else
        {
            Rows.Close(node);
        }
    }

    /// <summary>Opens the rows that hold a node, so it is one of the rows shown.</summary>
    public void Reveal(TestNode node)
    {
        Stack<TestNode> closed = new();
        for (TestNode? above = node.Parent; above is not null; above = above.Parent)
        {
            if (!above.IsExpanded)
            {
                closed.Push(above);
            }
        }
        while (closed.TryPop(out TestNode? opening))
        {
            Toggle(opening);
        }
    }

    /// <summary>Every row of the tree, shown or not.</summary>
    private IEnumerable<TestNode> Every()
    {
        Stack<TestNode> left = new(Roots.Reverse());
        while (left.TryPop(out TestNode? node))
        {
            yield return node;
            for (int child = node.Children.Count - 1; child >= 0; child--)
            {
                left.Push(node.Children[child]);
            }
        }
    }

    private void ShowRows()
    {
        HashSet<TestNode>? passing = Passing();
        Rows.Show([.. Roots.SelectMany(r => Visible(r, includeSelf: true, passing))]);
    }

    /// <summary>The rows the outcomes and search chosen let through: each test with one of the outcomes whose name, or
    /// the name of a row above it, has the text, and every row above such a test; null while every row is.</summary>
    private HashSet<TestNode>? Passing()
    {
        if (outcomes.Count == 0 && Search.Length == 0)
        {
            return null;
        }
        HashSet<TestNode> passing = new(ReferenceEqualityComparer.Instance);
        foreach (TestNode root in Roots)
        {
            Walk(root, named: false);
        }
        return passing;

        bool Walk(TestNode node, bool named)
        {
            named |= node.Name.Contains(Search, StringComparison.OrdinalIgnoreCase);
            bool shown = false;
            if (node.IsLeaf)
            {
                shown = named && (outcomes.Count == 0 || outcomes.Contains(node.State));
            }
            else
            {
                foreach (TestNode child in node.Children)
                {
                    shown |= Walk(child, named);
                }
            }
            if (shown)
            {
                passing.Add(node);
            }
            return shown;
        }
    }

    /// <summary>Opens the rows above every test the outcomes and search chosen let through, as a filtered tree shows them.</summary>
    private void OpenPassing()
    {
        foreach (TestNode leaf in Passing()?.Where(n => n.IsLeaf) ?? [])
        {
            for (TestNode? above = leaf.Parent; above is not null && !above.IsExpanded; above = above.Parent)
            {
                above.IsExpanded = true;
            }
        }
    }

    /// <summary>The tree from the tests as last loaded, grouped by the levels chosen, the rows of the tree before taken
    /// again where they are the same: by their place, else (a class, method or data row moved by a regrouping) by their
    /// test. A test a runner found in a file whose code no longer declares its method was renamed or removed since the
    /// build, and is left out; one in a file the code was not read from (an inherited test's base, a test of an attribute
    /// the code's reading does not know) stays.</summary>
    private void Build()
    {
        builtWith = Grouping;
        Dictionary<string, TestNode> before = new(StringComparer.Ordinal);
        Dictionary<string, TestNode> earlier = new(StringComparer.Ordinal);
        foreach (TestNode old in Every())
        {
            before[old.Key] = old;
            if (old.Identity is { } identity)
            {
                earlier.TryAdd(identity, old);
            }
        }
        List<Placed> placed = [];
        foreach (Entry entry in Entries())
        {
            foreach (List<Segment> path in Paths(entry, earlier))
            {
                placed.Add(new Placed(entry, path, Class(entry)));
            }
        }
        placed.Sort(Compare);

        Dictionary<string, TestNode> made = new(StringComparer.Ordinal);
        HashSet<TestNode> taken = new(ReferenceEqualityComparer.Instance);
        HashSet<TestNode> hadChildren = new(ReferenceEqualityComparer.Instance);
        Dictionary<TestNode, List<TestNode>> runRows = new(ReferenceEqualityComparer.Instance);
        Dictionary<string, TestNode> first = new(StringComparer.Ordinal);
        List<(TestNode Copy, TestNode Of)> copies = [];
        Dictionary<string, List<TestNode>> keyed = new(StringComparer.Ordinal);
        Dictionary<string, List<TestNode>> files = new(StringComparer.Ordinal);
        List<TestNode> roots = [];
        List<TestNode> methods = [];
        foreach ((Entry entry, List<Segment> path, (string Label, string Full) type) in placed)
        {
            TestNode? parent = null;
            string key = string.Empty;
            foreach (Segment segment in path)
            {
                key += $"/{(int)segment.Kind}:{segment.Id ?? segment.Name}";
                parent = Node(parent, segment.Kind, segment.Name, key, null);
                if (segment.Kind == TestNodeKind.Project)
                {
                    parent.Project = entry.Project.Project;
                }
            }
            string owner = $"{entry.IdentityFramework}|{entry.Project.Project}::";
            key += $"/{(int)TestNodeKind.Class}:{owner}{type.Full}";
            TestNode @class = Node(parent, TestNodeKind.Class, type.Label, key, $"class:{owner}{type.Full}");
            key += $"/{(int)TestNodeKind.Method}:{entry.Name}";
            bool reused = false;
            TestNode method = Node(@class, TestNodeKind.Method, entry.Name, key, entry.Identity, _ => reused = true);
            method.Framework = entry.Several ? entry.Framework : null;
            method.FromCode = entry.FromCode;
            if (entry.FromCode?.Path is { } file)
            {
                if (!files.TryGetValue(file, out List<TestNode>? declared))
                {
                    files[file] = declared = [];
                }
                declared.Add(method);
            }
            if (first.TryAdd(entry.Identity, method))
            {
                methods.Add(method);
            }
            else if (!reused)
            {
                copies.Add((method, first[entry.Identity]));
            }
            foreach (TestItem test in entry.Items.OrderBy(t => t.Row ?? entry.Named.GetValueOrDefault(t), StringComparer.Ordinal))
            {
                method.Items.Add(test);
                TestNode shows = method;
                if ((test.Row ?? entry.Named.GetValueOrDefault(test)) is { } row)
                {
                    shows = Node(method, TestNodeKind.Row, row, $"{key}/{(int)TestNodeKind.Row}:{row}", $"{entry.Identity}::{row}");
                    shows.Item = test;
                }
                if (!keyed.TryGetValue(test.Key, out List<TestNode>? nodes))
                {
                    keyed[test.Key] = nodes = [];
                }
                nodes.Add(shows);
            }
        }
        // The rows a run made stay under their method until it runs again, unless a runner has found the row since.
        HashSet<TestNode> restored = new(ReferenceEqualityComparer.Instance);
        foreach ((TestNode method, List<TestNode> rows) in runRows)
        {
            foreach (TestNode row in rows.Where(r => r.Item is null && !method.Children.Contains(r)))
            {
                method.Children.Add(row);
                row.Reparent(method);
                row.Key = $"{method.Key}/{(int)TestNodeKind.Row}:{row.Name}";
                restored.Add(row);
            }
        }
        // A test shown again under another group has its results there too, and the rows its run made.
        foreach ((TestNode copy, TestNode of) in copies)
        {
            copy.CopyFrom(of);
            foreach (TestNode row in of.Children)
            {
                if (copy.Children.FirstOrDefault(c => c.Name == row.Name) is { } same)
                {
                    same.CopyFrom(row);
                }
                else
                {
                    TestNode copied = new(TestNodeKind.Row, row.Name, copy) { Key = $"{copy.Key}/{(int)TestNodeKind.Row}:{row.Name}", Identity = row.Identity };
                    copied.CopyFrom(row);
                    copy.Children.Add(copied);
                }
            }
        }
        foreach (TestNode left in before.Values.Where(n => !taken.Contains(n) && !restored.Contains(n)))
        {
            left.Leave();
        }
        foreach (TestNode node in made.Values.Where(n => n.HasChildren != hadChildren.Contains(n)))
        {
            node.RaiseShape();
        }
        foreach (TestNode root in roots)
        {
            root.Recount();
        }
        Roots = roots;
        Methods = methods;
        byKey = keyed;
        byFile = files;

        // The key's node: this build's, else the one before there, else the same test's elsewhere (emptied), else a new one.
        TestNode Node(TestNode? parent, TestNodeKind kind, string name, string key, string? identity, Action<TestNode>? reusing = null)
        {
            if (made.TryGetValue(key, out TestNode? node))
            {
                return node;
            }
            node = before.TryGetValue(key, out TestNode? there) && Fits(there) ? there
                : identity is not null && earlier.TryGetValue(identity, out TestNode? elsewhere) && Fits(elsewhere) ? elsewhere
                : null;
            if (node is null)
            {
                node = new TestNode(kind, name, parent) { Identity = identity };
            }
            else
            {
                taken.Add(node);
                reusing?.Invoke(node);
                if (node.HasChildren)
                {
                    hadChildren.Add(node);
                    if (kind == TestNodeKind.Method && node.Children.Any(c => c.Item is null))
                    {
                        runRows[node] = [.. node.Children.Where(c => c.Item is null)];
                    }
                }
                node.Reparent(parent);
            }
            node.Key = key;
            node.Children.Clear();
            node.Items.Clear();
            node.FromCode = null;
            node.Item = null;
            made[key] = node;
            if (parent is null)
            {
                roots.Add(node);
            }
            else
            {
                parent.Children.Add(node);
            }
            return node;

            bool Fits(TestNode candidate) => !taken.Contains(candidate) && candidate.Kind == kind && candidate.Name == name;
        }
    }

    /// <summary>Each project's test methods, a multi-targeted project's once for each framework: the code's tests and the
    /// runner's merged on their method. A runner's test named by a case name the code gives a method (NUnit's TestName) is
    /// a row of that method, named as the runner shows it.</summary>
    private List<Entry> Entries()
    {
        List<Entry> entries = [];
        Dictionary<string, Entry> byIdentity = new(StringComparer.Ordinal);
        Dictionary<string, int> frameworks = new(StringComparer.Ordinal);
        for (int order = 0; order < inputs.Count; order++)
        {
            ProjectTests project = inputs[order];
            Dictionary<string, TestItem> cases = new(StringComparer.Ordinal);
            foreach (TestItem declared in project.Code.Tests.Where(t => t.Cases.Count > 0))
            {
                string type = declared.Method[..(declared.Method.LastIndexOf('.') + 1)];
                foreach (string name in declared.Cases)
                {
                    cases.TryAdd(type + name, declared);
                }
            }
            bool several = project.Discovered.Count > 1;
            IEnumerable<(string? Framework, IReadOnlyList<TestItem> Found)> tops = several
                ? project.Discovered.Select(s => ((string?)s.Framework, s.Tests))
                : [(project.Discovered.Count == 1 ? project.Discovered[0].Framework : project.DeclaredFramework, project.Discovered.Count == 1 ? project.Discovered[0].Tests : [])];
            foreach ((string? framework, IReadOnlyList<TestItem> found) in tops)
            {
                if (framework is not null)
                {
                    frameworks.TryAdd(framework, frameworks.Count);
                }
                IEnumerable<TestItem> kept = found.Where(t => t.Path is null || !project.Code.MethodsByFile.TryGetValue(t.Path, out IReadOnlySet<string>? declared)
                    || declared.Contains(t.Method[(t.Method.LastIndexOf('.') + 1)..]) || cases.ContainsKey(t.Method));
                foreach (TestItem test in kept.Concat(project.Code.Tests))
                {
                    TestItem? named = test.Framework is not null && test.Row is null && cases.TryGetValue(test.Method, out TestItem? owner) ? owner : null;
                    string method = named?.Method ?? test.Method;
                    string identity = $"{(several ? framework : null)}|{project.Project}::{method}";
                    if (!byIdentity.TryGetValue(identity, out Entry? entry))
                    {
                        entry = new Entry(project, order, framework, several, method, identity, entries.Count, frameworks);
                        byIdentity[identity] = entry;
                        entries.Add(entry);
                    }
                    if (test.Framework is null)
                    {
                        entry.FromCode ??= test;
                    }
                    else
                    {
                        entry.Items.Add(test);
                        if (named is not null)
                        {
                            entry.Named[test] = TestItem.Shown(test.Method, test.DisplayName);
                        }
                    }
                }
            }
        }
        return entries;
    }

    /// <summary>The groups a method is under, outermost first, one list for each place it is in: as many as its categories,
    /// grouped by them. Its framework, when its project has several, is under its project unless it is a level itself.</summary>
    private List<List<Segment>> Paths(Entry entry, Dictionary<string, TestNode> earlier)
    {
        List<List<Segment>> paths = [[]];
        foreach (TestGroupLevel level in Grouping)
        {
            switch (level)
            {
                case TestGroupLevel.TargetFramework when entry.Framework is { } framework:
                    Add(new Segment(TestNodeKind.Framework, framework, entry.FrameworkRank));
                    break;
                case TestGroupLevel.Project:
                    Add(new Segment(TestNodeKind.Project, entry.Project.Name, entry.Order, entry.Project.Project));
                    if (entry.Several && !Grouping.Contains(TestGroupLevel.TargetFramework))
                    {
                        Add(new Segment(TestNodeKind.Framework, entry.Framework!, entry.FrameworkRank));
                    }
                    break;
                case TestGroupLevel.FileSystem:
                    foreach (string folder in Folders(entry))
                    {
                        Add(new Segment(TestNodeKind.Folder, folder, 0));
                    }
                    break;
                case TestGroupLevel.Namespace when Split(entry.Method).Namespace is { Length: > 0 } space:
                    Add(new Segment(TestNodeKind.Namespace, space, 0));
                    break;
                case TestGroupLevel.Category:
                    List<string> categories = [.. TestGroupings.Categories(entry.FromCode?.Traits ?? entry.Items.SelectMany(i => i.Traits)).Distinct(StringComparer.Ordinal)];
                    paths = categories.Count == 0
                        ? [.. paths.Select(p => (List<Segment>)[.. p, new Segment(TestNodeKind.Category, TestGroupings.Uncategorized, 1)])]
                        : [.. paths.SelectMany(p => categories.Select(c => (List<Segment>)[.. p, new Segment(TestNodeKind.Category, c, 0)]))];
                    break;
                case TestGroupLevel.Duration:
                    TestNode? ran = earlier.GetValueOrDefault(entry.Identity);
                    (string grade, int rank) = TestGroupings.Grade(ran is { Results.Count: > 0 } ? ran.Duration : (TimeSpan?)null);
                    Add(new Segment(TestNodeKind.Duration, grade, rank));
                    break;
            }
        }
        return paths;

        void Add(Segment segment)
        {
            foreach (List<Segment> path in paths)
            {
                path.Add(segment);
            }
        }
    }

    /// <summary>The folders from a method's project to its file, as the File System grouping nests them.</summary>
    private static IEnumerable<string> Folders(Entry entry)
    {
        string? file = entry.FromCode?.Path ?? entry.Items.FirstOrDefault(i => i.Path is not null)?.Path;
        string root = Path.GetDirectoryName(entry.Project.Project)!;
        if (file is null || Path.GetDirectoryName(file) is not { } folder || !PathNames.IsUnder(folder, root))
        {
            return [];
        }
        return Path.GetRelativePath(root, folder).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>A method's class as its row names it and its full name: the namespace a level above says it, else the
    /// name has it; a multi-targeted project's framework, when no level above says it, after it.</summary>
    private (string Label, string Full) Class(Entry entry)
    {
        (string space, string type, _) = Split(entry.Method);
        string full = space.Length == 0 ? type : $"{space}.{type}";
        string label = Grouping.Contains(TestGroupLevel.Namespace) ? type : full;
        bool frameworkAbove = Grouping.Contains(TestGroupLevel.TargetFramework) || Grouping.Contains(TestGroupLevel.Project);
        return (entry.Several && !frameworkAbove ? $"{label} ({entry.Framework})" : label, full);
    }

    /// <summary>Groups before what is not grouped there, each kind of group by its rank and then its name; then classes by
    /// name, a nested type after its outer type's methods ('/' sorts after '.', where '+' sorts before), and methods.</summary>
    private static int Compare(Placed a, Placed b)
    {
        for (int at = 0; at < Math.Min(a.Path.Count, b.Path.Count); at++)
        {
            (Segment x, Segment y) = (a.Path[at], b.Path[at]);
            int order = x.Kind != y.Kind ? x.Kind.CompareTo(y.Kind)
                : x.Rank != y.Rank ? x.Rank.CompareTo(y.Rank)
                : string.CompareOrdinal(x.Name, y.Name);
            if (order != 0)
            {
                return order;
            }
        }
        if (a.Path.Count != b.Path.Count)
        {
            return b.Path.Count.CompareTo(a.Path.Count);
        }
        int byClass = string.CompareOrdinal(a.Class.Label.Replace('+', '/'), b.Class.Label.Replace('+', '/'));
        int byName = byClass != 0 ? byClass : string.CompareOrdinal(a.Entry.Name, b.Entry.Name);
        return byName != 0 ? byName : a.Entry.Index.CompareTo(b.Entry.Index);
    }

    /// <summary>A row made by a method's run: the one made before by that name, else a new one, under the method.</summary>
    private static TestNode MadeRow(TestNode method, string name, ref bool reshaped)
    {
        foreach (TestNode child in method.Children)
        {
            if (child.Name == name)
            {
                return child;
            }
        }
        TestNode row = new(TestNodeKind.Row, name, method) { Key = $"{method.Key}/{(int)TestNodeKind.Row}:{name}", Identity = $"{method.Identity}::{name}" };
        method.Children.Add(row);
        method.RaiseShape();
        reshaped |= method.IsExpanded;
        return row;
    }

    private static IEnumerable<TestNode> Visible(TestNode node, bool includeSelf, HashSet<TestNode>? passing)
    {
        if (passing is not null && !passing.Contains(node))
        {
            yield break;
        }
        if (includeSelf)
        {
            yield return node;
        }
        if (!node.IsExpanded)
        {
            yield break;
        }
        foreach (TestNode child in node.Children)
        {
            foreach (TestNode shown in Visible(child, includeSelf: true, passing))
            {
                yield return shown;
            }
        }
    }

    /// <summary>"A.B.Class.Method" as its namespace, class and method.</summary>
    internal static (string Namespace, string Class, string Method) Split(string method)
    {
        int last = method.LastIndexOf('.');
        if (last < 0)
        {
            return (string.Empty, string.Empty, method);
        }
        string type = method[..last];
        int owner = type.LastIndexOf('.');
        return owner < 0 ? (string.Empty, type, method[(last + 1)..]) : (type[..owner], type[(owner + 1)..], method[(last + 1)..]);
    }

    /// <summary>A test method of a project, under one of its target frameworks when it has several: what the code
    /// declares and what a runner found of it.</summary>
    private sealed class Entry(ProjectTests project, int order, string? framework, bool several, string method, string identity, int index, Dictionary<string, int> frameworks)
    {
        public ProjectTests Project { get; } = project;

        /// <summary>The project's place among the test projects.</summary>
        public int Order { get; } = order;

        /// <summary>The target framework it was found under, or the one its project names; null when neither is known.</summary>
        public string? Framework { get; } = framework;

        /// <summary>Whether its project is built for several frameworks, each with its own tests.</summary>
        public bool Several { get; } = several;

        public string? IdentityFramework => Several ? Framework : null;

        public int FrameworkRank => Framework is null ? 0 : frameworks[Framework];

        public string Method { get; } = method;

        public string Name => Split(Method).Method;

        public string Identity { get; } = identity;

        /// <summary>Its place among the methods as found, the last word in their order.</summary>
        public int Index { get; } = index;

        public TestItem? FromCode { get; set; }

        public List<TestItem> Items { get; } = [];

        /// <summary>The rows of its tests a runner names by a case name its code gives (<see cref="TestItem.Cases"/>).</summary>
        public Dictionary<TestItem, string> Named { get; } = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>A group a method is under: its kind, its name, its place among its kind's (a project's among the test
    /// projects), and what tells it apart where its name does not (a project's file).</summary>
    private readonly record struct Segment(TestNodeKind Kind, string Name, int Rank, string? Id = null);

    private sealed record Placed(Entry Entry, List<Segment> Path, (string Label, string Full) Class);
}
