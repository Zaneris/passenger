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
        // An open GDScript file read before its Godot project was known to carry GUT is read again once the projects are listed.
        tests.Finder.Changed += (_, _) =>
        {
            if (session == next)
            {
                testGutter.ReadUnplaced();
            }
        };
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
        tests.AttachDebugger = (processId, reload) => AttachTestHost(next, processId, reload, testHosts);
        // A debugged test host takes Hot Reload, with its update handlers, as Settings has it while debugging.
        tests.ReloadFor = (assembly, environment) => next.Debug.HotReloadOn()
            && Debugging.UpdateHandlerHook.ForTestHost(assembly, environment) is ({ } hook, { } variables) ? new Testing.TestHostReload(variables, hook) : null;
        tests.DebugGame = (start, folder, leading, csharp, heard) => DebugTestGame(next, start, folder, leading, csharp, testHosts, heard);
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
        // The results a run told, kept in the folder's state for its next opening.
        tests.ResultsToKeep += (_, runs) =>
        {
            if (next.State is { } state)
            {
                state.UnitTestResults.Set(state.UnitTestResults.Value.With(runs));
            }
        };
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
        // A walk's landing asks too, before it ends, and one look once it has ended answers both.
        if (!read.IsCompleted && !ReferenceEquals(read, testsAwaitRead))
        {
            testsAwaitRead = read;
            IdeErrors.Observe(FindTestsOnceRead(read, session));
        }
    }

    /// <summary>The read of the folder's projects a look for tests waits for.</summary>
    private Task? testsAwaitRead;

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
    /// its environment has the test project's launch profile's), on the framework the runner names, which Hot Reload's update
    /// handlers' hook asks; its process for the runner to talk to.</summary>
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
            RunsOn = start.Framework,
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
    private async Task<bool> AttachTestHost(ProjectSession running, int processId, Testing.TestHostReload? reload, HashSet<DebugTab> testHosts)
    {
        CheckTestSession(running);
        CancellationToken cancel = running.Tests.RunCancellation;
        ShowToolWindow(ToolWindowIds.Debug);
        Debugging.DebugLaunch? attached = null;
        try
        {
            attached = await running.Debug.AttachAdditionalAsync(processId, "Tests", cancel, tab => testHosts.Add(tab), reload?.Hook);
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

    /// <summary>A Godot run of a project's tests (GUT's, GoDotTest's) started under the debugger as Debug starts its game,
    /// Passenger listening for it as its remote debugger, the breakpoints in its scripts set from its start: a game of
    /// GDScript alone under the GDScript debugger, which launches it; a C# game (<paramref name="csharp"/>) under .NET's,
    /// its GDScript debugged beside it. In Debug tabs ended with the test run; done once its session is open, with the run's
    /// end.</summary>
    private async Task<Task> DebugTestGame(ProjectSession running, Testing.TestHostStart start, string folder, int leading, bool csharp, HashSet<DebugTab> testHosts,
        Action<string>? heard)
    {
        CheckTestSession(running);
        string? game = Passenger.Workspace.GodotProject.Read(folder)?.Name;
        LaunchPlan plan = new()
        {
            Name = csharp && game is not null ? $"{game} Tests" : "GUT",
            Program = start.FileName,
            Arguments = start.Arguments,
            Leading = leading,
            WorkingDirectory = folder,
            Environment = new Dictionary<string, string>(start.Environment),
            Godot = folder,
            GodotUnseen = Unseen,
            ScriptsAlone = !csharp,
            GameName = game,
        };
        ShowToolWindow(ToolWindowIds.Debug);
        (LaunchPlan listened, Godot.GodotGame? listening) = running.Listened(plan, running.Debug.ScriptStart(folder));
        if (listening is null)
        {
            throw new InvalidOperationException("The tests' run is not a Godot game the debugger can listen for");
        }
        Debugging.DebugSession? debugged;
        if (csharp)
        {
            Debugging.DebugLaunch launched;
            try
            {
                launched = await running.Debug.StartAdditionalAsync(listened, running.Tests.RunCancellation, tab => testHosts.Add(tab));
            }
            catch
            {
                listening.LetScriptsGo();
                listening.Dispose();
                throw;
            }
            debugged = launched.Tab.Session;
            if (heard is not null && debugged is not null)
            {
                debugged.Heard += Debugging.DebugSession.Lines(heard);
            }
            listening.Debugged = debugged;
            Run.BuildRunController.EndsWithGame(listening);
            if (await running.Debug.DebugScriptsAsync(listening, listened.Name) is null
                && running.Debug.SessionNamed(Debugging.DebugController.ScriptsTab(listened.Name)) is { } scripts && running.Debug.TabOf(scripts) is { } scriptsTab)
            {
                testHosts.Add(scriptsTab);
            }
            else
            {
                listening.LetScriptsGo();
            }
        }
        else
        {
            if (await running.Debug.LaunchScriptsAsync(listened, listening) is { } refused)
            {
                listening.Dispose();
                throw new InvalidOperationException(refused);
            }
            debugged = running.Debug.SessionNamed(listened.Name);
            listening.Debugged = debugged;
            if (debugged is not null && running.Debug.TabOf(debugged) is { } tab)
            {
                testHosts.Add(tab);
            }
        }
        Task ended = SessionEnd(debugged);
        IdeErrors.Observe(DisposedOnceAsync(listening, ended));
        return ended;

        static async Task DisposedOnceAsync(Godot.GodotGame game, Task ended)
        {
            await ended;
            game.Dispose();
        }
    }

    /// <summary>A debug session's end; done at once for none, or one ended.</summary>
    private static Task SessionEnd(Debugging.DebugSession? session)
    {
        if (session is not { State: not Debugging.DebugState.Ended } running)
        {
            return Task.CompletedTask;
        }
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        running.StateChanged += (_, _) =>
        {
            if (running.State == Debugging.DebugState.Ended)
            {
                ended.TrySetResult();
            }
        };
        return ended.Task;
    }

    private void CheckTestSession(ProjectSession running)
    {
        if (session != running || running.IsEnded)
        {
            throw new InvalidOperationException(Refusals.SolutionClosed);
        }
    }
}
