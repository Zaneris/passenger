using Avalonia.Controls;
using Passenger.App.Settings;
using Passenger.App.Testing;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>Continuous testing in the window: turned on and off for the folder's repository (its first-time question, asked at
/// the person's first run of tests there, its row in Settings, and Tests › Continuous Testing), paused while the person's
/// build, tests, debugging or profiling needs the machine, its map kept in the folder's state, and each open file's lines
/// marked with how the tests running them last ended, a press on a mark listing those tests.</summary>
public partial class MainWindow
{
    /// <summary>The open folder's continuous testing.</summary>
    public ContinuousTests Continuous => session.Continuous;

    private void AddContinuousTesting()
    {
        actions!.Add(new IdeAction(A.ContinuousTesting, "Continuous Testing", "Unit Tests",
            _ => Questions.Set(Declared.ContinuousTesting, !Questions.IsOn(Declared.ContinuousTesting)))
        {
            WhyNot = NeedsTestBuild,
            IsChecked = _ => Questions.IsOn(Declared.ContinuousTesting),
            Menus = [new MenuPlace("Tests", 1, 5)],
            Words = ["Test Continuously", "Rerun Tests on Save"],
        });
        Editors.Opened += (_, document) => MarkReach(session, document);
        Editors.ForEachGutter(gutter => gutter.Coverage.Asked = (line, mark) => ShowReachMenu(gutter.Editor, mark, line));
        // Work begun or ended (a build, a run of tests, a debugger's or profiler's start) may free the machine or need it.
        work.Changed += (_, _) => session.Continuous.MachineChanged();
    }

    /// <summary>A session's continuous testing: its folder's repository's answer read and followed, its map kept in the
    /// folder's state, the open files marked from it while it is the window's.</summary>
    private void BindContinuous(ProjectSession next)
    {
        next.Continuous.Paused = () => MachineTaken(next);
        next.Continuous.MapChanged += (_, _) =>
        {
            next.State?.TestReach.Set(next.Continuous.Map);
            if (session == next)
            {
                MarkReach(next);
            }
        };
        next.Continuous.Turned += (_, _) =>
        {
            if (session == next)
            {
                MarkReach(next);
            }
        };
        next.Tests.BusyChanged += (_, _) => next.Continuous.MachineChanged();
        next.Debug.StateChanged += (_, _) => next.Continuous.MachineChanged();
        next.ProfiledChanged += (_, _) => next.Continuous.MachineChanged();
        buildRun.BuildFinished += (_, _) =>
        {
            if (session == next)
            {
                next.Continuous.MachineChanged();
            }
        };
        next.Continuous.Open(next.Folder, next.State?.TestReach.Value ?? new TestReachState());
        IdeErrors.Observe(FollowRepositoryAsync(next));
    }

    /// <summary>The folder's repository's answers read, continuous testing turned as its answer stands.</summary>
    private async Task FollowRepositoryAsync(ProjectSession next)
    {
        await Questions.OpenAsync(next.Folder);
        if (session == next && !next.IsEnded)
        {
            next.Continuous.Turn(next.Folder is not null && Questions.IsOn(Declared.ContinuousTesting));
        }
    }

    /// <summary>Why the person's work has the machine now, which continuous testing waits out; null while it is free.</summary>
    private string? MachineTaken(ProjectSession of) =>
        IsBuilding ? Refusals.Building
        : of.Tests.IsBusy ? "the tests are running"
        : of.Debug.IsDebugging ? "a program is debugged"
        : of.Profiled is not null || of.ProfileStarting is not null ? "a program is profiled"
        : null;

    private void MarkReach(ProjectSession of)
    {
        foreach (OpenDocument document in Editors.Documents)
        {
            MarkReach(of, document);
        }
    }

    /// <summary>A file's lines marked with how the tests running them last ended, as the session's map has them where continuous
    /// testing is on; none where it is off or maps nothing of the file.</summary>
    private static void MarkReach(ProjectSession of, OpenDocument document) =>
        document.Reach = of.Continuous.IsOn && document.HoldsText && of.Continuous.Map.Files.ContainsKey(document.Path)
            && of.Continuous.Map.Marks(document.Path, TestReachState.LinesOf(document.Text.Snapshot.ToString())) is { Count: > 0 } marks
            ? new ReachedLines(document.Text, marks) : null;

    /// <summary>The menu on a line's mark: the tests that run the line, each with how it last ended, going to its code, and
    /// Run These Tests.</summary>
    private bool ShowReachMenu(CodeEditor editor, ReachedLine mark, int line)
    {
        MenuFlyout menu = new();
        List<TestNode> nodes = [.. mark.Tests.Select(MethodOf).OfType<TestNode>()];
        if (mark.Tests.Length == 0)
        {
            menu.Items.Add(ToolWindows.ListMenus.Notice($"No test runs line {line + 1}"));
        }
        else
        {
            menu.Items.Add(ToolWindows.ListMenus.Heading($"Line {line + 1}, run by {Plural.Of(mark.Tests.Length, "test")}"));
            foreach (string identity in mark.Tests)
            {
                TestNode? node = MethodOf(identity);
                ReachedTest? reached = session.Continuous.Map.Tests.GetValueOrDefault(identity);
                string name = node?.Name ?? ContinuousTests.Named(identity).Method.Split('.')[^1];
                string said = reached?.State switch
                {
                    TestState.Passed => "passed",
                    TestState.Failed => "failed",
                    TestState.Skipped => "skipped",
                    _ => "not run",
                };
                menu.Items.Add(ToolWindows.ListMenus.Item($"{name} ({said})", () =>
                {
                    if (node?.Path is { } path && File.Exists(path))
                    {
                        NavigateTo(PathNames.Canonical(path), new TextPosition(Math.Max(0, node.Line - 1), 0));
                    }
                }));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(ToolWindows.ListMenus.Item(nodes.Count == 1 ? "Run This Test" : "Run These Tests", () => RunTests(() => session.Tests.RunAsync(Folder!, nodes)),
                session.Tests.IsBusy ? "Tests are already running" : nodes.Count == 0 ? "None of these tests is in the Unit Tests window" : null));
        }
        ReachMenu = menu;
        menu.Placement = PlacementMode.Pointer;
        menu.ShowAt(editor, showAtPointer: true);
        return true;
    }

    /// <summary>The tests menu of a line's mark last opened.</summary>
    public MenuFlyout? ReachMenu { get; private set; }

    /// <summary>A test method of the tree by the identity continuous testing maps it by.</summary>
    private TestNode? MethodOf(string identity)
    {
        (string project, string method) = ContinuousTests.Named(identity);
        return session.Tests.Tree.Methods.FirstOrDefault(m => m.FullName == method && m.ProjectPath is { } its && PathNames.Same(its, project));
    }
}
