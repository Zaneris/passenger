using Avalonia.Controls;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App;

/// <summary>Unit testing in the window: the Unit Tests window's tree from the moment a folder opens (the test projects'
/// code read for their tests, open files as they are typed, files changed on disk), its runs, the gutter's run icons and
/// their menu, and a test debugged under the IDE's own debugger.</summary>
public partial class MainWindow
{
    /// <summary>The unit test runner, its tree and its runs.</summary>
    public Testing.TestController Tests => session.Tests;

    /// <summary>The run icons beside the tests of each open file.</summary>
    public Testing.TestGutter TestGutter => testGutter;

    /// <summary>Wires the Unit Tests window, the gutter's icons and the tests found from code to the window.</summary>
    private void AddUnitTests()
    {
        // The gutter reads each open file's tests from its parse; the tree takes the same read.
        testGutter.Read += (_, read) => session.Tests.Finder.OpenRead(read.Document.Path, read.Tests);
        testGutter.OwnAttributes = path => session.Tests.Finder.AttributesOf(path);
        testGutter.ProjectsOf = path => session.Tests.Finder.ProjectsOf(path);
        Editors.Opened += (_, document) => testGutter.Watch(document);
        Editors.Closed += (_, document) =>
        {
            testGutter.Forget(document);
            session.Tests.Finder.Closed(document.Path);
        };
        // The grouping and outcomes chosen are the folder's.
        Bottom.TestTree.ViewChanged += (_, _) => session.State?.UnitTests.Set(new Testing.UnitTestsState
        {
            Grouping = [.. Bottom.TestTree.Grouping],
            Outcomes = [.. Bottom.TestTree.Outcomes.Order()],
        });
        Bottom.Tests.ActionRequested += (_, action) => Request(action);
        Bottom.Tests.RunRequested += (_, debugging) =>
        {
            IReadOnlyList<Testing.TestNode> chosen = Bottom.Tests.Selected;
            RunTests(() => session.Tests.RunAsync(Folder!, chosen, debugging));
        };
        Bottom.Tests.UntilFailureRequested += (_, _) => RunUntilFailure(Bottom.Tests.Selected);
        Bottom.Tests.GoToRequested += (_, node) =>
        {
            if (node.Path is { } path && File.Exists(path))
            {
                NavigateTo(PathNames.Canonical(path), new TextPosition(Math.Max(0, node.Line - 1), 0));
            }
        };
    }

    /// <summary>A session's test runner says what it does in the status bar and the Unit Tests window while it is the
    /// window's, debugs a test under the window's debugger, and labels its runs in the folder's Local History.</summary>
    private void BindTests(ProjectSession next)
    {
        Testing.TestController tests = next.Tests;
        tests.Message += (_, message) =>
        {
            if (session == next)
            {
                // Said in the Unit Tests window too, over the run's results, which is where to see more.
                Say(message, StatusPlace.Window(Dock, ToolWindowIds.Tests));
                Bottom.Tests.Status = message;
            }
        };
        // The Unit Tests window's Stop has something to end only while a run (or a look for the tests) goes on.
        tests.BusyChanged += (_, _) =>
        {
            if (session == next)
            {
                Bottom.Tests.IsRunning = tests.IsBusy;
            }
        };
        Bottom.Tests.IsRunning = tests.IsBusy;
        // The open files' tests are read again by the test attributes their projects derive, once those are known.
        tests.Finder.AttributesChanged += (_, _) =>
        {
            if (session == next)
            {
                testGutter.ReadAgain();
            }
        };
        HashSet<DebugTab> testHosts = [];
        tests.BeginAspireTest = (project, debug, cancel) => next.Aspire.BeginTestAsync(project, debug, cancel);
        tests.LaunchUnderDebugger = start => LaunchTestHost(next, start, testHosts);
        tests.AttachDebugger = processId => AttachTestHost(next, processId, testHosts);
        tests.EndDebugging = () => Task.WhenAll(testHosts.ToArray().Select(EndTestHost));
        async Task EndTestHost(DebugTab tab)
        {
            try
            {
                await next.Debug.StopAsync(tab);
            }
            finally
            {
                testHosts.Remove(tab);
            }
        }
        // A test run's end, a label in Local History: passed, failed, or broken off (its test host ended mid-run).
        tests.Ran += (_, ended) =>
        {
            if (session == next && !next.IsEnded)
            {
                string label = ended.Aborted ? "Tests Broken Off" : ended.Outcomes.Any(o => o.State == Testing.TestState.Failed) ? "Tests Failed" : "Tests Passed";
                next.LocalHistory?.Label(label, system: true);
            }
        };
    }

    /// <summary>The Unit Tests window's grouping and outcomes as the folder kept them, or the defaults.</summary>
    private void RestoreUnitTests(Testing.UnitTestsState kept)
    {
        session.Tests.Tree.Group(kept.Grouping);
        session.Tests.Tree.ShowOutcomes(kept.Outcomes);
    }

    /// <summary>The Unit Tests window follows the folder's projects: their tests are found from code once the folder's
    /// projects are known, which for a folder without a solution is once the walk for them has landed.</summary>
    private void FindTests()
    {
        Task read = WorkspaceRead;
        session.Tests.Open(Folder, projectsRead: read.IsCompleted);
        if (!read.IsCompleted)
        {
            IdeErrors.Observe(FindTestsOnceRead(read, session));
        }
    }

    private async Task FindTestsOnceRead(Task read, ProjectSession opening)
    {
        // Held from the walk's end to the look it starts, so a screenshot or a test never finds the window between them.
        using IDisposable held = BackgroundWork.Scope();
        await read;
        if (session == opening && !opening.IsEnded)
        {
            session.Tests.Open(Folder);
        }
    }

    /// <summary>A run of tests shown in the Unit Tests window; what stopped it, if anything, in the status bar.</summary>
    private void RunTests(Func<Task<string?>> run)
    {
        if (Folder is null)
        {
            return;
        }
        ShowToolWindow(ToolWindowIds.Tests);
        ProjectSession running = session;
        IdeErrors.Observe(Go());

        async Task Go()
        {
            string? problem;
            try
            {
                problem = await run();
            }
            catch (Exception error)
            {
                // Nobody awaits the run: a failure no runner foresaw is an IDE error, said now rather than at a collection.
                IdeErrors.Report(error);
                problem = $"The tests could not run: {error.Message}";
            }
            if (session == running && !running.IsEnded && problem is not null)
            {
                Say(problem, StatusPlace.Window(Dock, ToolWindowIds.Tests));
                Bottom.Tests.Status = problem;
            }
        }
    }

    /// <summary>The menu on a test's run icon: run or debug that test, or all of its class's.</summary>
    private void ShowTestMenu(CodeEditor editor, OpenDocument document, int line)
    {
        if (testGutter.At(document, line) is not { } chosen)
        {
            return;
        }
        string name = chosen.Method ?? chosen.Type[(chosen.Type.LastIndexOfAny(['.', '+']) + 1)..];
        MenuFlyout menu = new();
        MenuItem run = new() { Header = $"Run '{name}'" };
        run.Click += (_, _) => RunTestsAt(chosen, document.Path, debug: false);
        MenuItem debugIt = new() { Header = $"Debug '{name}'" };
        debugIt.Click += (_, _) => RunTestsAt(chosen, document.Path, debug: true);
        MenuItem untilFailure = new() { Header = $"Run '{name}' Until Failure" };
        untilFailure.Click += (_, _) => RunTestsAt(chosen, document.Path, debug: false, untilFailure: true);
        menu.Items.Add(run);
        menu.Items.Add(debugIt);
        menu.Items.Add(untilFailure);
        TestMarkMenu = menu;
        menu.Placement = Avalonia.Controls.PlacementMode.Pointer;
        menu.ShowAt(editor, showAtPointer: true);
    }

    /// <summary>A test's run icon's menu last opened.</summary>
    public MenuFlyout? TestMarkMenu { get; private set; }

    /// <summary>Run (or Debug) Unit Tests in context: the Unit Tests window's chosen rows when it has the keys, the
    /// projects chosen in the Explorer (a solution folder's, those under it) when it has them, else the test method or
    /// class at the editor's caret.</summary>
    private void RunTestsInContext(ActionContext context, bool debug, bool untilFailure = false)
    {
        if (!untilFailure && ChosenProjects(context) is { } projects)
        {
            RunProjectTests(projects, Explorer.Selected?.Name ?? "the projects chosen", debug);
            return;
        }
        if (ChosenTests(context) is { } chosen)
        {
            if (untilFailure)
            {
                RunUntilFailure(chosen);
                return;
            }
            RunTests(() => session.Tests.RunAsync(Folder!, chosen, debug));
            return;
        }
        if (TestAtCaret(context) is { } test && context.File is { } file)
        {
            RunTestsAt(test, file.Path, debug, untilFailure);
        }
    }

    /// <summary>Run Until Failure: runs tests until one fails, and shows the failed one's output.</summary>
    private void RunUntilFailure(IReadOnlyList<Testing.TestNode> chosen)
    {
        if (Folder is null || chosen.Count == 0)
        {
            return;
        }
        ProjectSession running = session;
        string workspace = Folder;
        RunTests(async () =>
        {
            string? problem = await running.Tests.RunUntilFailureAsync(workspace, chosen);
            if (session == running && !running.IsEnded && running.Tests.FirstFailed(chosen) is { } failed)
            {
                Bottom.Tests.Choose(failed);
            }
            return problem;
        });
    }

    /// <summary>Runs (or debugs) a test method, or every test of a type, of the projects the file is in, as the tree has
    /// them once the run's build has ended and its tests are found: a test only a runner finds (an attribute of the
    /// project's own) runs with them, and a base type's tests as every derived type's, which the runner says are on its
    /// lines of the file.</summary>
    private void RunTestsAt(Testing.DeclaredPlace place, string path, bool debug, bool untilFailure = false)
    {
        (string type, string? method) = (place.Type, place.Method);
        if (Folder is null)
        {
            return;
        }
        ProjectSession running = session;
        string workspace = Folder;
        string none = $"No test of {method ?? type} was found; is the project a test project?";
        IReadOnlySet<string>? projects = null;
        RunTests(async () =>
        {
            if (!untilFailure)
            {
                return await running.Tests.RunMatchingAsync(workspace, Matches, none, debug);
            }
            string? stopped = await running.Tests.RunUntilFailureAsync(workspace, Matches, none);
            if (session == running && !running.IsEnded && running.Tests.FirstFailed(running.Tests.RunMethods) is { } failed)
            {
                Bottom.Tests.Choose(failed);
            }
            return stopped;
        });

        bool Matches(Testing.TestNode test) =>
            (test.FullName is { } name && Testing.TestsAtCaret.Matches(name, type, method)
                && Testing.TestFinder.IsOf(test, projects ??= running.Tests.Finder.ProjectsOf(path)))
            || test.Items.Any(item => item is { Path: { } at, Line: > 0 } && PathNames.Same(at, path) && place.Holds(item.Line));
    }

    /// <summary>A test host a runner would start, started under the debugger as a plan of its own (the runner built it, and
    /// its environment has the test project's launch profile's); its process for the runner to talk to.</summary>
    private async Task<int> LaunchTestHost(ProjectSession running, Testing.TestHostStart start, HashSet<DebugTab> testHosts)
    {
        CheckTestSession(running);
        CancellationToken cancel = running.Tests.RunCancellation;
        LaunchPlan plan = new()
        {
            Name = "Tests",
            Program = start.FileName,
            Arguments = start.Arguments,
            WorkingDirectory = start.WorkingDirectory ?? running.Folder ?? Environment.CurrentDirectory,
            Environment = new Dictionary<string, string>(start.Environment),
        };
        ShowToolWindow(ToolWindowIds.Debug);
        Debugging.DebugLaunch launched = await running.Debug.StartAdditionalAsync(plan, cancel, tab => testHosts.Add(tab));
        try
        {
            CheckTestSession(running);
            cancel.ThrowIfCancellationRequested();
            return launched.ProcessId;
        }
        catch
        {
            await running.Debug.StopAsync(launched.Tab);
            throw;
        }
    }

    /// <summary>A test host a runner started itself, attached to: true once the debugger has hold of it.</summary>
    private async Task<bool> AttachTestHost(ProjectSession running, int processId, HashSet<DebugTab> testHosts)
    {
        CheckTestSession(running);
        CancellationToken cancel = running.Tests.RunCancellation;
        ShowToolWindow(ToolWindowIds.Debug);
        Debugging.DebugLaunch? attached = null;
        try
        {
            attached = await running.Debug.AttachAdditionalAsync(processId, "Tests", cancel, tab => testHosts.Add(tab));
            CheckTestSession(running);
            cancel.ThrowIfCancellationRequested();
            return attached.ProcessId == processId;
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException)
        {
            if (attached is not null)
            {
                await running.Debug.StopAsync(attached.Tab);
            }
            return false;
        }
    }

    private void CheckTestSession(ProjectSession running)
    {
        if (session != running || running.IsEnded)
        {
            throw new InvalidOperationException("The test run's solution was closed");
        }
    }
}
