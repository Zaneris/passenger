using Passenger.App.Testing;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Languages.CSharp;
using Passenger.Text;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>Test sessions from the keys and the Unit Tests window's menu: a new session of the tests chosen or at the
/// caret, those added to the current one, the current one run, the window shown on every test or on the current session
/// with the keys; the tests' quick list; and Go to Test, from a type to the test classes named after it and back.</summary>
public partial class MainWindow
{
    private void AddTestSessions()
    {
        TestTree tree = Bottom.TestTree;
        Add(A.NewTestSession, ActionTitles.NewTestSession, "Unit Tests", NewTestSession, c => NeedsFolder(c) ?? NoTestsFor(c), ("Tests", 3, 0),
            words: ["Create New Session", "Unit Test Session"]);
        Add(A.AppendToTestSession, ActionTitles.AppendToTestSession, "Unit Tests", AppendToTestSession,
            c => NeedsFolder(c) ?? (tree.Current is null ? Refusals.NoTestSession : NoTestsFor(c)), ("Tests", 3, 1), words: ["Add to Session"]);
        Add(A.RunTestSession, ActionTitles.RunTestSession, "Unit Tests", _ => RunTestSession(),
            c => NeedsTestBuild(c) ?? (tree.Current is null ? Refusals.NoTestSession : null), ("Tests", 3, 2), works: A.RunTests);
        Add(A.ShowAllTests, ActionTitles.ShowAllTests, "Unit Tests", _ => ShowTestsOf(null), NeedsFolder, ("Tests", 3, 3), words: ["Unit Test Explorer"]);
        Add(A.ShowTestSession, ActionTitles.ShowTestSession, "Unit Tests", _ => ShowTestsOf(tree.Current),
            c => NeedsFolder(c) ?? (tree.Current is null ? Refusals.NoTestSession : null), ("Tests", 3, 4));
        Add(A.TestsQuickList, ActionTitles.TestsQuickList, "Unit Tests", c => ShowTestOperations(c), NeedsFolder, words: ["Quick List", "Unit Test Quick List"]);
        Add(A.GoToTest, ActionTitles.GoToTest, "Navigate", c => IdeErrors.Observe(GoToTestAsync(c)), c => TypeAtCaret(c) is null ? Refusals.NoTypeForTest : null,
            ("Navigate", 1, 8), words: ["Go to Tested Type", "Linked Types", "Test Class"]);

        string? NoTestsFor(ActionContext context) => (SessionTestsOf(context).Tests.Count == 0 ? Refusals.NoTestsForSession : null)?.Shown;
    }

    /// <summary>The tests a session takes from where it was asked, and a name for a session of them: the Unit Tests window's
    /// chosen rows while it has the keys, the test projects chosen in the Explorer while it has them, else the test method or
    /// class at the caret, as Run Unit Tests takes them.</summary>
    private (List<SessionTest> Tests, string Name) SessionTestsOf(ActionContext context)
    {
        if (ChosenTests(context) is { } chosen)
        {
            return ([.. chosen.SelectMany(TestSession.Of).Distinct()], Named(chosen[0].Name, chosen.Count));
        }
        if (ChosenProjects(context) is { } projects)
        {
            return ([.. projects.Select(p => new SessionTest { Project = p })], Named(Path.GetFileNameWithoutExtension(projects[0]), projects.Count));
        }
        if (context.File is { } file && TestAtCaret(context) is { } place)
        {
            return (TestsAt(place, file.Path), place.Method ?? TestLinks.Short(place.Type));
        }
        return ([], string.Empty);

        static string Named(string first, int count) => count == 1 ? first : $"{first} and {Numbers.Said(count - 1)} more";
    }

    /// <summary>The tests of a method or class a file declares, as the tree has them: by their names in the file's projects, or
    /// where a runner places them (a base class's tests as each class deriving from it); by its name where the tree has none
    /// yet.</summary>
    private List<SessionTest> TestsAt(DeclaredPlace place, string path)
    {
        IReadOnlySet<string> projects = session.Tests.Finder.ProjectsOf(path);
        List<TestNode> found = [.. session.Tests.Tree.Methods.Where(test =>
            (test.FullName is { } name && TestsAtCaret.Matches(name, place.Type, place.Method) && TestFinder.IsOf(test, projects))
            || test.Places.Any(at => PathNames.Same(at.Path, path) && place.Holds(at.Line)))];
        string named = place.Method is null ? place.Type.Replace('+', '.') : $"{place.Type}.{place.Method}";
        return found.Count > 0
            ? [.. found.Where(m => m.ProjectPath is not null).Select(m => new SessionTest { Project = m.ProjectPath!, Name = place.Method is null ? TestSession.ClassOf(m.FullName!) : m.FullName }).Distinct()]
            : [.. projects.Select(p => new SessionTest { Project = p, Name = named })];
    }

    private void NewTestSession(ActionContext context)
    {
        (List<SessionTest> tests, string name) = SessionTestsOf(context);
        if (tests.Count == 0)
        {
            return;
        }
        TestSession made = Bottom.TestTree.NewSession(name, tests);
        ShowToolWindow(ToolWindowIds.Tests);
        Say($"New unit test session: {made.Name}", StatusPlace.Window(Dock, ToolWindowIds.Tests));
    }

    private void AppendToTestSession(ActionContext context)
    {
        (List<SessionTest> tests, _) = SessionTestsOf(context);
        if (Bottom.TestTree.Current is not { } current || tests.Count == 0)
        {
            return;
        }
        bool added = Bottom.TestTree.AddTo(current, tests);
        Bottom.TestTree.ShowSession(current);
        ShowToolWindow(ToolWindowIds.Tests);
        Say(added ? $"Appended to {current.Name}" : $"{current.Name} holds them already", StatusPlace.Window(Dock, ToolWindowIds.Tests));
    }

    /// <summary>Runs the current session's tests, shown in the Unit Tests window as they run: each method it holds whole, and
    /// the data rows it holds of the others, as the tree has them once the run's build has ended and its tests are found.</summary>
    private void RunTestSession()
    {
        if (Bottom.TestTree.Current is not { } current || Folder is null)
        {
            return;
        }
        Bottom.TestTree.ShowSession(current);
        ProjectSession running = session;
        string workspace = Folder;
        RunTests(() => running.Tests.RunPickedAsync(workspace, methods => running.Tests.Tree.RunsOf(current, methods), _ => Refusals.SessionHoldsNone(current.Name).Shown));
    }

    /// <summary>The Unit Tests window on a session's tests (every test for none), its tree given the keys.</summary>
    private void ShowTestsOf(TestSession? shown)
    {
        Dock.Activate(ToolWindowIds.Tests);
        if (AgentAsks.Beside)
        {
            Bottom.TestTree.ShowSession(shown);
        }
        else
        {
            Bottom.Tests.FocusSession(shown);
        }
    }

    /// <summary>Unit Test Operations: the Tests menu's actions that can run where it was asked, in the menu's order with
    /// their keys, in a popup narrowed as words are typed; the one chosen runs there.</summary>
    public Vcs.ChoicePopup ShowTestOperations(ActionContext context)
    {
        ActionRegistry registry = actions!;
        Vcs.Choice[] runnable = [.. MainMenu.ActionsIn(MainMenu.Of(registry.All, "Tests"))
            .Where(id => registry.WhyNot(id, context) is null)
            .Select(id => new Vcs.Choice(registry[id]!.TitleWhere(context).Shown, registry.ShortcutsShown(id) ?? string.Empty, id))];
        Vcs.ChoicePopup popup = new(TitleOf(A.TestsQuickList), "Search unit test actions", runnable, Refusals.NoTestOperations.Shown);
        popups.Show(popup, chosen =>
        {
            if (chosen?.Value is string id)
            {
                Request(id, context);
            }
        });
        return popup;
    }

    /// <summary>The type the caret is in, in a C# file; null elsewhere.</summary>
    private static DeclaredPlace? TypeAtCaret(ActionContext context) =>
        context.File?.Syntax?.Current is { } syntax ? TestsAtCaret.TypeAt(syntax, context.Caret.Line) : null;

    /// <summary>Go to Test: from a test class, to the type its name names (in a file named after it, else as the language
    /// server finds it by name); from any other type, to the test classes named after it. Straight to one, else a list at
    /// the caret to choose from.</summary>
    private async Task GoToTestAsync(ActionContext context)
    {
        if (context.File is not { } file || TypeAtCaret(context) is not { } type)
        {
            return;
        }
        ProjectSession asked = session;
        string name = TestLinks.Short(type.Type);
        List<TestNode> methods = [.. asked.Tests.Tree.Methods];
        bool isTest = methods.Any(m => m.FullName is { } test && TestsAtCaret.Matches(test, type.Type, null));
        List<(string Path, string Type)> targets;
        string none;
        if (isTest && TestLinks.Tested(name) is { } tested)
        {
            none = Refusals.NoTestedType(name, tested).Shown;
            targets = await TestedTypesAsync(asked, tested, file.Path);
        }
        else if (isTest)
        {
            Say(Refusals.NoTestedType(name, name));
            return;
        }
        else
        {
            none = Refusals.NoTestOfType(name).Shown;
            targets = [.. methods.Where(m => m.FullName is { } test && m.Path is not null && TestLinks.NamedAfter(TestLinks.Short(TestSession.ClassOf(test)), name))
                .GroupBy(m => TestSession.ClassOf(m.FullName!), StringComparer.Ordinal)
                .Select(g => (g.First().Path!, g.Key))];
        }
        Func<string, string?> textOf = path => Editors.Find(path)?.Text.Snapshot.ToString();
        List<(string Path, string Type, string? Open)> reading = [.. targets.Select(t => (t.Path, t.Type, textOf(t.Path)))];
        List<(string Path, TextSnapshot Text, int Offset, string Name)> places = await BackgroundWork.Run(() =>
            reading.Select(t => Placed(t.Path, t.Type, t.Open)).OfType<(string, TextSnapshot, int, string)>().ToList());
        if (session == asked)
        {
            Assist.ShowPlaces(TitleOf(A.GoToTest), places, none);
        }

        static (string Path, TextSnapshot Text, int Offset, string Name)? Placed(string path, string type, string? open)
        {
            if ((open ?? Passenger.App.Editors.EditorArea.ReadText(path)) is not { } text)
            {
                return null;
            }
            TextSnapshot snapshot = TextSnapshot.From(text);
            ParsedText parsed = new(CSharpParsing.Parse(text), snapshot);
            DeclaredPlace? declared = TestsAtCaret.Places(parsed).Types.FirstOrDefault(t => t.Type == type || TestLinks.Short(t.Type) == type);
            return declared is null ? null : (path, snapshot, snapshot.GetLineStart(declared.Line), TestLinks.Short(declared.Type));
        }
    }

    /// <summary>The C# files a type of this name may be declared in, apart from the test's own: those named after it under
    /// the folder, else those the language server finds the type in, where it has loaded the solution.</summary>
    private static async Task<List<(string Path, string Type)>> TestedTypesAsync(ProjectSession asked, string tested, string from)
    {
        string? root = asked.Index.Root;
        List<(string Path, string Type)> named = root is null ? [] : [.. asked.Index.Files
            .Where(f => Path.GetExtension(f).Equals(".cs", StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(f) == tested)
            .Select(f => PathNames.Canonical(Path.Combine(root, f)))
            .Where(p => !PathNames.Same(p, from))
            .Select(p => (p, tested))];
        if (named.Count > 0 || asked.CSharp?.Session is not { IsLoaded: true } server)
        {
            return named;
        }
        IReadOnlyList<Lsp.SymbolInformation> found = await server.SymbolsAsync(tested);
        return [.. Lsp.WorkspaceSymbols.Kept(found)
            .Where(s => s.Name == tested && Lsp.DocumentUris.GeneratedName(s.Location.Uri) is null && Lsp.SymbolKinds.Name(s.Kind) is "class" or "struct" or "interface")
            .Select(s => Lsp.DocumentUris.ToPath(s.Location.Uri))
            .Where(p => !PathNames.Same(p, from))
            .Distinct(PathNames.Comparer)
            .Select(p => (p, tested))];
    }
}
