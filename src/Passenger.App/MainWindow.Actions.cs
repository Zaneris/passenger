using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Editor;
using E = Passenger.Editor.EditorActions;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>The window's actions: the registry every feature adds its own to, where each runs (<see cref="Context"/>),
/// and the window's own (files, editing, navigation, building and running, tests, the editor tabs' and the tool
/// windows' frame). However an action is asked for, it runs through the registry, so one that cannot run says why.</summary>
public partial class MainWindow
{
    private ActionRegistry? actions;

    /// <summary>What the window's actions are doing that takes a while, which the toolbar's buttons and the status bar show:
    /// made with the window, before any session's controllers are given it.</summary>
    private readonly ActionWork work = new();

    /// <summary>The window's actions and its keymap.</summary>
    public ActionRegistry Actions => actions!;

    /// <summary>Runs an action by id, as its shortcut would: the IDE's wherever the keys are, an editor's where they are
    /// (the active editor, else the text box with them). False for an id no action has, or one that cannot run here.</summary>
    public bool Perform(string action) => actions!.Run(action) is null;

    /// <summary>Runs an action a person asked for (its keys, a menu item, Find Action, a tool window's button), in the
    /// context made as they asked: one that cannot run here says why in the status bar.</summary>
    private bool Request(string action, ActionContext? context = null)
    {
        if (actions!.Run(action, context ?? actions.Context()) is not { } why)
        {
            return true;
        }
        status.Text = why;
        return false;
    }

    /// <summary>The tool windows' buttons that run actions show those actions' work, as the toolbar's do.</summary>
    private void ShowActionWork()
    {
        foreach (Docking.ToolWindow tool in Dock.Windows)
        {
            if (tool.IsBuilt)
            {
                ActionButtons.ShowMarked(tool.Content, actions!);
            }
            else
            {
                tool.Built += (_, _) => ActionButtons.ShowMarked(tool.Content, actions!);
            }
        }
    }

    /// <summary>Runs an action's work held under its id: begun at once with its first stage, ended with the task; asked for
    /// again while it works, it says what is under way.</summary>
    private async Task WorkingAsync(string id, string stage, Func<Task> run, bool stoppable = false)
    {
        if (work.Begin(id, stage, stoppable) is not { } working)
        {
            status.Text = ActionWork.Busy(work.Of(id)!);
            return;
        }
        using (working)
        {
            await run();
        }
    }

    /// <summary>Where an action asked for now runs: the file shown and its caret, what has the keys in the window its keys
    /// were pressed in, else the window in front (an editor window, a diff, a dialog, a floating tool window, this one), and
    /// the tool window it is in, and the debugger's state.</summary>
    private ActionContext Context()
    {
        TopLevel front = windowKeys.Pressing ?? Editors.InFront?.Window ?? OwnedWindows.LastOrDefault(w => w.IsActive) ?? this;
        object? focused = front.FocusManager?.GetFocusedElement();
        Docking.ToolWindow? tool = focused is Visual visual
            ? Dock.Windows.FirstOrDefault(w => w.IsBuilt && (w.Content == visual || w.Content.IsVisualAncestorOf(visual)))
            : null;
        return new ActionContext(Editors.Active, Editors.CodeEditor, focused, tool, session.Debug.IsDebugging ? session.Debug.Session?.State : null);
    }

    private string? NeedsFolder(ActionContext _) => Folder is null ? Refusals.NoFolder : null;

    /// <summary>Why Expand All or Collapse All cannot run: no tool window with rows to open or close has the keys.</summary>
    private static string? NoTree(ActionContext context, Action? does, string verb) =>
        does is not null ? null : context.ToolWindow is { } window ? $"{window.Title} has no rows to {verb}" : $"No tool window with rows to {verb} has the keys";

    /// <summary>Find over the console of the tool window with the keys: the Run and Build windows', the Containers' and
    /// Aspire's logs'.</summary>
    private Editors.FindBar? ConsoleFind(ActionContext context) => context.ToolWindow?.Id switch
    {
        ToolWindowIds.Run => Bottom.Run.Find,
        ToolWindowIds.Build => Bottom.Build.Find,
        ToolWindowIds.Containers => ContainersWindow.Log.Find,
        ToolWindowIds.Aspire => aspireView?.Find,
        _ => null,
    };

    /// <summary>The scope of the rows chosen in the Explorer while it has the keys; null elsewhere, for the scope
    /// kept.</summary>
    private Workspace.SearchScope? ExplorerScope(ActionContext context) =>
        context.ToolWindow?.Id == ToolWindowIds.Explorer && Explorer.SelectedNodes is { Count: > 0 } rows ? ScopeOf(rows) : null;

    /// <summary>The window's own actions; each feature's partial or controller adds its own beside them.</summary>
    private void AddActions()
    {
        actions = new ActionRegistry(Context, () => keys.Keymap, work);
        Editors.Commands.Work = work;
        Dock.Actions = actions;
        Dock.Request = Request;
        foreach ((Button button, string id) in (ReadOnlySpan<(Button, string)>)[(BuildButton, A.Build), (RunButton, A.RunProgram), (DebugButton, A.Debug),
            (ProfileButton, A.Profile), (HotReloadButton, A.ApplyHotReloadChanges), (StopButton, A.Stop), (SearchButton, A.GoToEverything), (SettingsButton, A.ShowSettings)])
        {
            ActionButtons.Bind(button, id, actions, action => Request(action));
        }
        work.Changed += (_, change) =>
        {
            status.ShowWork(change, work.Latest);
            ShowStop();
        };
        AddEditing();
        Add(A.Open, "Open…", "IDE", context => IdeErrors.Observe(PickWorkspace()), menu: ("File", 0, 0));
        actions.Add(new IdeAction(A.RecentFiles, "Recent Files", "IDE", _ => ShowRecentFiles()) { Menus = [new("File", 0, 4), new("View", 2, 0)] });
        Add(A.Save, "Save", "IDE", context => IdeErrors.Observe(SaveExplicitly(all: false)), Refusals.NeedsUnsaved, ("File", 1, 0));
        Add(A.SaveAll, "Save All", "IDE", context => IdeErrors.Observe(SaveExplicitly(all: true)), _ => Editors.Documents.Any(d => d.IsModified) ? null : "Every file is saved", ("File", 1, 1));
        // Quit is the application menu's on macOS; either closes every window.
        Add(A.Exit, "Exit", "IDE", _ => PassengerApp.Quit(), menu: OperatingSystem.IsMacOS() ? null : ("File", 4, 0));
        Add(A.FindAction, "Find Action…", "IDE", _ => ShowFindAction(), menu: ("Help", 0, 0));

        // The Unit Tests window takes Find's keys for its search while it has them, a console's window for its text.
        Add(A.Find, "Find", "IDE", c =>
        {
            if (c.ToolWindow?.Id == ToolWindowIds.Tests)
            {
                Bottom.Tests.FocusSearch();
            }
            else if (c.ToolWindow?.Id == ToolWindowIds.Aspire)
            {
                actions!.Run(A.AspireLogFind, c);
            }
            else if (ConsoleFind(c) is { } console)
            {
                console.Open(replace: false);
            }
            else
            {
                Editors.ShowFind(replace: false);
            }
        }, c => c.ToolWindow?.Id == ToolWindowIds.Tests || ConsoleFind(c) is not null ? null : Refusals.NeedsFile(c), ("Edit/Find", 0, 0));
        Add(A.Replace, "Replace", "IDE", _ => Editors.ShowFind(replace: true), Refusals.NeedsFile, ("Edit/Find", 0, 1));
        Add(A.FindNext, "Find Next", "IDE", c => (ConsoleFind(c) ?? Editors.FindBar).Next(), c => ConsoleFind(c) is not null ? null : Refusals.NeedsFile(c), ("Edit/Find", 0, 2));
        Add(A.FindPrevious, "Find Previous", "IDE", c => (ConsoleFind(c) ?? Editors.FindBar).Previous(), c => ConsoleFind(c) is not null ? null : Refusals.NeedsFile(c),
            ("Edit/Find", 0, 3));
        Add(A.FindInFiles, "Find in Files…", "IDE", c => ShowFindInFiles(replace: false, ExplorerScope(c)), NeedsFolder, ("Edit/Find", 1, 0));
        Add(A.ReplaceInFiles, "Replace in Files…", "IDE", c => ShowFindInFiles(replace: true, ExplorerScope(c)), NeedsFolder, ("Edit/Find", 1, 1));
        Add(A.ExtendSelection, "Extend Selection", "Editor", _ => ExtendSelection(), Refusals.NeedsFile, ("Edit", 2, 1));
        Add(A.ShrinkSelection, "Shrink Selection", "Editor", _ => ShrinkSelection(), Refusals.NeedsFile, ("Edit", 2, 2));

        Add(A.Back, "Back", "Navigate", _ => navigator.StepHistory(-1), menu: ("Navigate", 0, 0));
        Add(A.Forward, "Forward", "Navigate", _ => navigator.StepHistory(1), menu: ("Navigate", 0, 1));
        Add(A.GoToEverything, "Search Everywhere", "Navigate", _ => navigator.ShowGoToEverything(), NeedsFolder, ("Navigate", 1, 0));
        Add(A.GoToFile, "Go to File", "Navigate", _ => navigator.ShowGoToFile(), NeedsFolder, ("Navigate", 1, 1));
        Add(A.GoToSymbol, "Go to Symbol", "Navigate", _ => navigator.ShowGoToSymbol(), NeedsFolder, ("Navigate", 1, 2));
        Add(A.GoToLine, "Go to Line:Column", "Navigate", _ => navigator.ShowGoToLine(), Refusals.NeedsLines, ("Navigate", 1, 3));
        Add(A.FileStructure, "File Structure", "Navigate", _ => navigator.ShowFileStructure(), Refusals.NeedsFile, ("Navigate", 1, 4));
        Add(A.GoToRelated, "Go to Related File", "Navigate", c => OpenFile(Navigation.CodeBehind.PartnerOf(c.File!.Path)!),
            c => c.File is not { } file ? Refusals.NoFile : Navigation.CodeBehind.PartnerOf(file.Path) is null ? $"{file.Name} has no code-behind or markup beside it" : null,
            ("Navigate", 1, 5), words: ["Code-Behind", "View Code", "View Markup"]);

        AddBuilding();
        AddPublishing();
        AddAspireActions();
        AddHotReload();
        AddAttaching();
        AddTests();

        AddTabActions();

        // The tool windows' frame, run from the terminal too.
        actions.Add(new IdeAction(A.MaximizeToolWindow, "Maximize Tool Window", "Tool Windows", _ => Dock.ToggleMaximize()) { Menus = [new("View", 3, 0)], FromTerminal = true });
        actions.Add(new IdeAction(A.HideAllToolWindows, "Hide All Tool Windows", "Tool Windows", _ => Dock.ToggleHideAll()) { Menus = [new("View", 3, 1)], FromTerminal = true });
        actions.Add(new IdeAction(A.HideToolWindow, "Hide Active Tool Window", "Tool Windows", _ => HideToolWindow()) { Menus = [new("View", 3, 2)], FromTerminal = true });
        actions.Add(new IdeAction(A.ExpandAll, "Expand All", "Tool Windows", c => c.ToolWindow!.ExpandAll!()) { WhyNot = c => NoTree(c, c.ToolWindow?.ExpandAll, "open") });
        actions.Add(new IdeAction(A.CollapseAll, "Collapse All", "Tool Windows", c => c.ToolWindow!.CollapseAll!()) { WhyNot = c => NoTree(c, c.ToolWindow?.CollapseAll, "close") });
        // Pressed in a tool window by the editor's Escape (KeysIn), where nothing of the window's own takes that key.
        actions.Add(new IdeAction(A.FocusEditor, "Focus Editor", "IDE", _ => FocusEditor())
        {
            WhyNot = Refusals.NeedsFile,
            KeysOf = () => AppActions.KeysIn(A.FocusEditor) is var keys && keys != A.FocusEditor ? keys : null,
        });
        Add(A.ToggleSolutionAnalysis, "Toggle Solution-Wide Analysis", "IDE", _ => ToggleSolutionAnalysis(),
            _ => session.CSharp is null ? Refusals.NoCSharp : null, ("Code", 4, 0));
        Add(A.RestartCSharpServer, "Restart C# Language Server", "IDE", _ =>
        {
            session.CSharp!.Restart();
            status.Text = "Starting the C# language server again";
        }, _ => session.CSharp is null ? Refusals.NoCSharp : null, ("Code", 4, 1), words: ["Roslyn"]);
    }

    /// <summary>The editor's actions, run where the keys are: a text box's own editing there, another editor (a diff's)
    /// when it has them, the Explorer's Cut, Copy and Paste of files while its rows have them, else the active editor.</summary>
    private void AddEditing()
    {
        Dictionary<string, MenuPlace[]> menus = new()
        {
            [E.Undo] = [new("Edit", 0, 0)],
            [E.Redo] = [new("Edit", 0, 1)],
            [E.Cut] = [new("Edit", 1, 0)],
            [E.Copy] = [new("Edit", 1, 1)],
            [E.Paste] = [new("Edit", 1, 2)],
            [E.Duplicate] = [new("Edit", 1, 3)],
            [E.DeleteLine] = [new("Edit", 1, 4)],
            [E.SelectAll] = [new("Edit", 2, 0)],
            [E.SelectNextOccurrence] = [new("Edit", 2, 3)],
            [E.SelectAllOccurrences] = [new("Edit", 2, 4)],
            [E.ToggleLineComment] = [new("Edit", 4, 0), new("Code", 2, 0)],
            [E.ToggleBlockComment] = [new("Code", 2, 1)],
            [E.MoveLineUp] = [new("Edit", 4, 1)],
            [E.MoveLineDown] = [new("Edit", 4, 2)],
            [E.Unfold] = [new("Code/Folding", 0, 0)],
            [E.UnfoldAll] = [new("Code/Folding", 0, 1)],
            [E.Fold] = [new("Code/Folding", 1, 0)],
            [E.FoldToDefinitions] = [new("Code/Folding", 1, 1)],
            [E.FoldAll] = [new("Code/Folding", 1, 2)],
            [E.UnfoldDocComments] = [new("Code/Folding", 2, 0)],
            [E.FoldDocComments] = [new("Code/Folding", 2, 1)],
            [E.ToggleFold] = [new("Code/Folding", 3, 0)],
        };
        foreach ((string id, string title) in E.All)
        {
            actions!.Add(new IdeAction(id, title, "Editor", context => Edit(id, context))
            {
                InEditor = true,
                WhyNot = context => ExplorerEdits(id, context) ? WhyNotOnRow(id, context)
                    : context.File is null && context.Focused is not (TextBox or CodeEditor) ? Refusals.NoFile : null,
                TitleIn = id == E.Delete ? context => DeleteTitle(context) ?? title : null,
                Menus = menus.GetValueOrDefault(id, []),
            });
        }
    }

    private void Edit(string action, ActionContext context)
    {
        if (ExplorerEdits(action, context))
        {
            ExplorerEdit(action, context);
            return;
        }
        switch (context.Focused)
        {
            case TextBox box when Edit(box, action):
                return;
            case Tables.CellGrid table when table.Execute(action):
                return;
            case CodeEditor editor when editor != Editors.CodeEditor:
                editor.TryExecute(action);
                return;
        }
        if (action is E.Undo or E.Redo && UndoCommand(action == E.Redo, Editors.Active))
        {
            return;
        }
        Editors.CodeEditor.TryExecute(action);
    }

    /// <summary>Build, Run and Debug: the solution's or the chosen projects' builds, the run configuration's run, and Edit
    /// Configurations.</summary>
    private void AddBuilding()
    {
        Add(A.Build, "Build Solution", "Run", _ => BuildSolution(), Unbuildable, ("Build", 0, 0));
        // One build at a time, whichever began it: while one works, the others say so.
        Add(A.RebuildSolution, "Rebuild Solution", "Build", _ => Build(BuildKind.Rebuild), Unbuildable, ("Build", 0, 1), works: A.Build);
        Add(A.CleanSolution, "Clean Solution", "Build", _ => Build(BuildKind.Clean), Unbuildable, ("Build", 0, 2), works: A.Build);
        Add(A.BuildSelected, "Build Selected Projects", "Build", _ => Build(BuildKind.Build, SelectedProjects()), NoneSelected, ("Build", 1, 0), works: A.Build);
        Add(A.RebuildSelected, "Rebuild Selected Projects", "Build", _ => Build(BuildKind.Rebuild, SelectedProjects()), NoneSelected, ("Build", 1, 1), works: A.Build);
        Add(A.CleanSelected, "Clean Selected Projects", "Build", _ => Build(BuildKind.Clean, SelectedProjects()), NoneSelected, ("Build", 1, 2), works: A.Build);

        Add(A.RunProgram, "Run", "Run", _ => RunSelected(), _ => Unconfigured("run"), ("Run", 0, 0));
        Add(A.Debug, "Debug", "Run", _ => DebugSelected(), _ => Unconfigured("debug"), ("Run", 0, 1));
        // No key in Rider's Visual Studio keymaps.
        Add(A.EditConfigurations, "Edit Configurations…", "Run", _ => ShowRunConfigurations(), NeedsFolder, ("Run", 0, 5));
        // Edit Configurations' button, on the configuration chosen there; elsewhere, on the toolbar's.
        Add(A.SaveAsLaunchProfile, "Save as Launch Profile", "Run", _ => IdeErrors.Observe(ShareChosenAsync()), _ => WhyNotShare());
        // Rerun runs the last session again, as it ran; one still running is stopped first.
        Add(A.Rerun, "Rerun", "Run", c =>
        {
            if (session.Debug.Session is { } debugged && debugged == attachSession)
            {
                actions!.Run(A.ReattachToProcess, c);
            }
            else if (session.Debug.IsDebugging)
            {
                DebugSelected();
            }
            else
            {
                IdeErrors.Observe(RerunAsync());
            }
        }, _ => session.Debug.Session is { } debugged && debugged == attachSession ? null : Unconfigured("run"), ("Run", 0, 6));
        Add(A.Stop, "Stop", "Run", _ => buildRun.Stop(), _ => buildRun.WhyNotStop(), ("Run", 0, 7));
        // Every program debugged, where Stop ends the one chosen in the Debug window.
        Add(A.StopAll, "Stop All", "Run", context => IdeErrors.Observe(buildRun.StopAllAsync()), _ => buildRun.WhyNotStop(), ("Run", 0, 7));
        Add(A.EditBreakpoint, "Edit Breakpoint…", "Debug", c =>
        {
            int line = c.Caret.Line;
            string? attribute = Debugging.DebugController.AttributeAtCaret(c);
            Debugging.LambdaBreakpoints.Lambda? lambda = attribute is null ? Debugging.DebugController.LambdaAtCaret(c) : null;
            IReadOnlyList<Debugging.LineBreakpoint> here = session.Debug.Breakpoints.In(c.File!.Path);
            Debugging.LineBreakpoint? own = lambda is not null ? session.Debug.OnLambda(c.File, lambda.Value)
                : here.FirstOrDefault(b => b.Line == line && b.Attribute == attribute && b.Column is null);
            // In a lambda, its own, set if it has none: the line's is edited from outside the lambda.
            if ((own ?? (lambda is null ? here.FirstOrDefault(b => b.Line == line) : null)) is not { } editing)
            {
                // Edit Breakpoint on a line with none sets one to edit: on an XAML attribute or in a lambda, its own.
                if (attribute is not null)
                {
                    session.Debug.Breakpoints.ToggleAttribute(c.File.Path, line, attribute);
                }
                else if (lambda is { } inside)
                {
                    session.Debug.ToggleLambda(c.File, inside);
                }
                else
                {
                    session.Debug.Breakpoints.Toggle(c.File.Path, line);
                }
                editing = lambda is not null ? session.Debug.OnLambda(c.File, lambda.Value)!
                    : session.Debug.Breakpoints.In(c.File.Path).First(b => b.Line == line && b.Attribute == attribute);
            }
            ShowBreakpointPopup(editing);
        }, Refusals.NeedsFile, ("Run", 2, 2));
        Add(A.ViewBreakpoints, "View Breakpoints and Exceptions…", "Debug", _ => ShowBreakpoints(null), menu: ("Run", 2, 3));

        string? Unbuildable(ActionContext _) => Folder is null ? "Nothing to build: no folder is open" : IsBuilding ? "Another build is running" : null;

        string? NoneSelected(ActionContext context) => Unbuildable(context)
            ?? (SelectedProjects().Count == 0 ? "No project is chosen in the Explorer or open in the editor" : null);

        string? Unconfigured(string verb) => Configuration is null ? session.NothingTo(verb) : null;
    }

    /// <summary>The unit tests' actions: in context (the Unit Tests window's chosen rows while it has the keys, else the
    /// test at the caret), all of them, again, and Stop.</summary>
    private void AddTests()
    {
        Add(A.RunTests, "Run Unit Tests", "Unit Tests", c => RunTestsInContext(c, debug: false), NoTestHere, ("Tests", 0, 0));
        Add(A.DebugTests, "Debug Unit Tests", "Unit Tests", c => RunTestsInContext(c, debug: true), NoTestHere, ("Tests", 0, 1), works: A.RunTests);
        Add(A.RunTestsUntilFailure, "Run Unit Tests Until Failure", "Unit Tests", c => RunTestsInContext(c, debug: false, untilFailure: true), NoTestHere, ("Tests", 0, 2), works: A.RunTests);
        Add(A.RunAllTests, "Run All Tests", "Unit Tests", _ => RunTests(() => session.Tests.RunAsync(Folder!)), NeedsFolder, ("Tests", 0, 3), works: A.RunTests);
        Add(A.RerunTests, "Rerun Unit Tests", "Unit Tests", _ => RunTests(() => session.Tests.RerunAsync(Folder!)), NeedsFolder, ("Tests", 1, 0));
        Add(A.RerunFailedTests, "Rerun Failed Tests", "Unit Tests", _ => RunTests(() => session.Tests.RerunFailedAsync(Folder!)), NeedsFolder, ("Tests", 1, 1));
        Add(A.StopTests, "Stop Unit Tests", "Unit Tests", _ => session.Tests.Stop(), NothingRunning, ("Tests", 1, 2));

        // Where the Explorer has the keys, its rows say what to run, never the editor's caret.
        string? NoTestHere(ActionContext context) => NeedsFolder(context)
            ?? (ChosenTests(context) is not null || ChosenProjects(context) is not null ? null
                : ExplorerHasKeys(context) ? "No project chosen in the Explorer holds tests"
                : TestAtCaret(context) is null ? "No test is at the caret" : null);

        // Stop is disabled with no run to end, its toolbar's button and its menu item alike.
        string? NothingRunning(ActionContext context) => NeedsFolder(context) ?? (session.Tests.IsBusy ? null : "No unit tests are running");
    }

    /// <summary>The projects holding tests chosen in the Explorer, while it has the keys (<see cref="TestProjectsOf"/>).</summary>
    private IReadOnlyList<string>? ChosenProjects(ActionContext context) =>
        context.ToolWindow?.Id == ToolWindowIds.Explorer && context.Row is IReadOnlyList<Explorer.ExplorerNode> rows
            && rows.SelectMany(TestProjectsOf).Distinct().ToList() is { Count: > 0 } projects ? projects : null;

    /// <summary>The projects a row stands for that hold tests: a project, those under a solution folder, the solution's
    /// loaded ones.</summary>
    private IEnumerable<string> TestProjectsOf(Explorer.ExplorerNode row)
    {
        HashSet<string> testing = new(session.Tests.Finder.Projects.Select(p => p.Path), Core.PathNames.Comparer);
        testing.UnionWith(session.Tests.Tree.Methods.Select(m => m.ProjectPath).OfType<string>());
        IEnumerable<string> projects = row.Kind == Passenger.App.Explorer.NodeKind.Solution
            ? session.Tests.Finder.Projects.Select(p => p.Path).Where(p => !Explorer.Tree.Unloaded.Contains(p))
            : Explorer.Tree.ProjectsOf(row);
        return projects.Where(testing.Contains);
    }

    /// <summary>The Unit Tests window's chosen rows, while it has the keys.</summary>
    private static IReadOnlyList<Testing.TestNode>? ChosenTests(ActionContext context) =>
        context.ToolWindow?.Id == ToolWindowIds.Tests && context.Row is IReadOnlyList<Testing.TestNode> { Count: > 0 } chosen ? chosen : null;

    private (string Type, string? Method)? TestAtCaret(ActionContext context) => context.File is { } file ? TestAt(file, context.Caret.Line) : null;

    /// <summary>The test a file's line is in, as its code declares it or a runner found it; null where there is none.</summary>
    private (string Type, string? Method)? TestAt(OpenDocument file, int line) =>
        file.Syntax is { } syntax ? Testing.TestsAtCaret.At(syntax.Current, line, testGutter.Knows) : null;

    /// <summary>Adds one of the window's actions, listed in one place of the menus if given.</summary>
    private void Add(string id, string title, string group, Action<ActionContext> run, Func<ActionContext, string?>? whyNot = null, (string Path, int Group, int Order)? menu = null,
        IReadOnlyList<string>? words = null, string? works = null) =>
        actions!.Add(new IdeAction(id, title, group, run)
        {
            WhyNot = whyNot ?? (_ => null),
            Menus = menu is { } at ? [new MenuPlace(at.Path, at.Group, at.Order)] : [],
            Words = words ?? [],
            Works = works,
        });
}
