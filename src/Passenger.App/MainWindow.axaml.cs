using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.App.CSharp;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.Run;
using Passenger.App.Settings;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App;

public partial class MainWindow : Window
{
    /// <summary>The window's least width and height (its MinWidth and MinHeight), which a screenshot's <c>--size</c> is
    /// held to: Avalonia would widen a smaller window to them without a word.</summary>
    public const double SmallestWidth = 640;

    public const double SmallestHeight = 500;

    private readonly KeymapDispatcher keys = new(AppActions.Keymap());

    /// <summary>The popups over this window and its editor windows.</summary>
    private readonly PopupHost popups;

    /// <summary>What the last action came to, and the file shown's place and properties.</summary>
    private readonly StatusBar status;

    /// <summary>Going to places in the code, and back.</summary>
    private readonly Navigation.Navigator navigator;

    private readonly CodeAssist assist;
    private readonly UsageHighlighting usages;
    private readonly Testing.TestGutter testGutter;
    private readonly Passenger.App.Explorer.ExplorerView Explorer = new();
    private readonly ToolWindows.ToolViews Bottom = new();
    private readonly ToolWindows.StructureView Structure = new();

    /// <summary>The folder open and what it has and runs, or the window's nothing before one opens.</summary>
    private ProjectSession session;

    /// <summary>The session's builds and runs as this window shows them, made for each session it binds.</summary>
    private BuildRunController buildRun;

    public MainWindow()
    {
        UiThread.Own();
        InitializeComponent();
        session = ProjectSession.None(Setup());
        status = new StatusBar(StatusText, StatusIcon, CaretText, IndentText, LanguageText, LineSeparatorButton, LineEndingText, EncodingButton, EncodingText);
        popups = new PopupHost(Overlay, OverlayHost, Editors, () => session.Debug.Breakpoints);
        navigator = new Navigation.Navigator(new Navigation.NavigatorParts(Editors, () => session, popups, status, path => OpenFile(path), TextOf));
        // One keymap for the whole window, seen before any control: IDE actions run from anywhere,
        // editor actions go to the editor when it has the focus.
        AddHandler(KeyDownEvent, OnKeyDownFirst, RoutingStrategies.Tunnel);
        AddActions();
        AddTitleBar();
        AddWelcome();
        AddChords();
        AddToolWindows();
        AddVcs();
        AddTerminal();
        AddSettings();
        AddActiveEditor();
        AddFolding();
        AddPackages();
        AddProfiler();
        AddLiveTree();
        AddBreadcrumbs();
        AddBookmarks();
        AddPlaces();
        AddHierarchy();
        AddFileOperations();
        AcceptDrops(this);
        AddFileProperties();
        AddTargetFramework();
        AddAutosave();
        AddConflicts();
        AddErrors();
        AddAbout();
        AddUpdates();
        AddAgents();
        AddDiscord();
        AddShutdown();
        AddEditorWindows();
        AddPreview();
        Explorer.FileOpened += (_, path) => OpenFile(path);
        Editors.ActiveChanged += (_, _) => FollowInExplorer();
        Structure.Follow(() => Editors.Active is { } shown
            ? (shown.Path, session.CSharp?.For(shown)?.Lsp, Editors.CodeEditor.CaretPosition.Line)
            : null);
        Structure.GoToRequested += (_, row) => NavigateTo(row.Path, row.Position);
        Editors.ActiveChanged += (_, _) => Structure.Invalidate();
        Editors.CaretMoved += (_, _) => Structure.FollowCaret();
        Editors.Opened += (_, document) => document.Text.Changed += (_, _) =>
        {
            if (Editors.Active == document)
            {
                Structure.Invalidate();
            }
        };
        Editors.ActiveChanged += (_, _) => UpdateStatus();
        Editors.ActiveChanged += (_, _) => session.CSharp?.FollowIndent(Editors.Active);
        // The indent widget and the line break follow a file's .editorconfig changed, or its save.
        Editors.Opened += (_, document) => document.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(OpenDocument.Indentation) or nameof(OpenDocument.LineEnding) && Editors.Active == document)
            {
                UpdateStatus();
                session.CSharp?.FollowIndent(document);
            }
        };
        Editors.CaretMoved += (_, _) => UpdateCaret();
        Editors.SaveFailed += (_, problem) => status.Text = problem;
        Editors.Opened += (_, document) => session.Attach(document);
        Editors.Closed += (_, document) => session.Detach(document);
        Editors.ForEachEditor(editor => editor.ProblemsRequested += (_, _) => ShowToolWindow("problems"));
        // IntelliJ's words for its caret limit.
        Editors.ForEachEditor(editor => editor.CaretLimitReached += (_, _) =>
            status.Text = string.Create(CultureInfo.InvariantCulture, $"Cannot create more than {SelectionSet.MaxCount:N0} carets"));
        Editors.ForEachEditor(editor => editor.ExecutionPointDragged += (_, line) =>
        {
            if (Editors.Active is { } active && session.Debug.Session is { State: DebugState.Paused } paused)
            {
                _ = session.Debug.SetNextStatementAsync(paused, active.Path, line);
            }
        });
        Editors.ForEachEditor(editor => editor.GutterClicked += (_, click) =>
        {
            if (Editors.Active is not { } active)
            {
                return;
            }
            if (!click.Context && editor.IsInMarkColumn(click.Position) && active.TestMarks.Any(m => m.Line == click.Line))
            {
                // A test's run icon: Rider's menu of Run and Debug for it.
                ShowTestMenu(editor, active, click.Line);
                return;
            }
            if (PickColour(editor, active, click))
            {
                return;
            }
            if (!click.Context && editor.IsInAnnotationColumn(click.Position))
            {
                _ = ShowBlamedCommitAsync(active, click.Line);
                return;
            }
            if (!click.Context && editor.IsInChangeColumn(click.Position) && ChangeAt(active, click.Line) is { } change)
            {
                _ = ShowChangePopupAsync(editor, active, change);
                return;
            }
            if (click.Context)
            {
                // A right-click on a breakpoint opens its properties, as in Rider.
                if (session.Debug.Breakpoints.In(active.Path).FirstOrDefault(b => b.Line == click.Line) is { } breakpoint)
                {
                    ShowBreakpointPopup(breakpoint);
                }
                else
                {
                    ShowGutterMenu(editor, active);
                }
            }
            else if (click.Modifiers.HasFlag(KeyModifiers.Shift))
            {
                session.Debug.Breakpoints.ToggleTracepoint(active.Path, click.Line);
            }
            else
            {
                session.Debug.Breakpoints.Toggle(active.Path, click.Line);
            }
        });
        assist = new CodeAssist(Editors, Editors.AssistLayer, () => session.CSharp) { Xaml = () => session.Xaml, MSBuild = () => session.MSBuild, Web = () => session.Web };
        assist.Navigate += (_, target) => NavigateTo(target.Path, target.Position);
        assist.Message += (_, message) => status.Text = message;
        assist.Browse += (_, page) => Browse(page);
        assist.FilesChanged += (_, _) => FilesChanged();
        usages = new UsageHighlighting(Editors, () => session.CSharp);
        usages.Message += (_, message) => status.Text = message;
        Bottom.Problems.Chosen += (_, problem) =>
        {
            if (problem.Path is { } path)
            {
                NavigateTo(path, problem.Position);
            }
        };
        testGutter = new Testing.TestGutter(Bottom.TestTree);
        Bind(session);
        Bottom.Build.RerunClicked += (_, _) => buildRun.Rebuild();
        Bottom.Build.StopClicked += (_, _) => session.Builder.Stop();
        Bottom.Run.RerunClicked += (_, _) => Request(AppActions.RunProgram);
        Bottom.Run.StopClicked += (_, _) => session.Runner.Stop();
        Bottom.Run.HotReloadClicked += (_, _) => Request(AppActions.ApplyHotReloadChanges);
        AddUnitTests();
        AddLinks();
        AddLocalHistory();
        Bottom.Problems.FixRequested += (_, problem) =>
        {
            NavigateTo(problem.Path!, problem.Position);
            Request(AppActions.QuickFix);
        };
        Structure.UsagesRequested += (_, row) =>
        {
            NavigateTo(row.Path, row.Position);
            Request(AppActions.FindUsages);
        };
        assist.DebuggerValue = async (offset, cancel) => Editors.Active is { } shown && Debugging.XamlBreakpoints.Compiled(shown.Path)
            ? bindingValues!.HoverAt(shown, offset)
            : await session.Debug.ValueAt(offset, cancel) is { } value ? new DebuggerHover(value) : null;
        // A running app's failed binding is offered its source's nearest members, at the member its XAML names.
        assist.EditorFixes = (file, offset) => Passenger.App.LiveTree.BindingErrors.Fixes(file, offset, session.FailedBindings.Select(b => b.Failure));
        assist.AddActions(actions!);
        usages.AddActions(actions!);
        DebugController.AddActions(actions!, () => session.Debug);
        AddFind();
        AddProblems();
        AddContextMenus();
        AddProjectWindows();
        AddMainMenu();
        UpdateStatus();
        // Last, so the process hears of its closing once what it started is ending (Ended).
        PassengerApp.Add(this);
    }

    /// <summary>The C# service for the open folder, or null when it has no projects.</summary>
    public CSharpService? CSharpService => session.CSharp;

    /// <summary>The open folder's XAML: each open XAML file's share of its project's types and files.</summary>
    public Xaml.XamlService XamlService => session.Xaml;

    public EditorArea EditorArea => Editors;

    public Explorer.ExplorerView ExplorerView => Explorer;

    /// <summary>Every file of the open folder, as Go to File searches them.</summary>
    public FileIndex FileIndex => session.Index;

    /// <summary>Opens what the command line named: a folder, solution or project, files in it, a caret position. Named
    /// nothing, what the system handed Passenger as it started (a solution double-clicked in the Finder), else the
    /// window that keeps state reopens what was open as Passenger last quit, or shows the Welcome screen.</summary>
    public void Start(LaunchOptions options)
    {
        started = true;
        IReadOnlyList<string> handed = PassengerApp.TakePending();
        if ((options.Workspace ?? options.Folder) is { } path)
        {
            PassengerApp.OpenProject(path, OpenTarget.ThisWindow, this);
        }
        else if (handed.Count > 0)
        {
            foreach (string named in handed)
            {
                Open(named);
            }
        }
        else if (options.Files.Count == 0)
        {
            ReopenLast();
        }
        foreach (string file in options.Files)
        {
            if (file.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase))
            {
                // A profiler's trace opens as a snapshot, as a dotTrace snapshot opens in Rider.
                _ = OpenSnapshotAsync(Path.GetFullPath(file));
                continue;
            }
            OpenFile(file);
        }
        if (options.Caret is { } caret && Editors.Active is not null)
        {
            Editors.CodeEditor.GoTo(caret);
        }
        if (session.Folder is null && Editors.Documents.Count == 0)
        {
            welcome.FocusSearch();
        }
        PreloadTemplates();
    }

    private string? followed;

    /// <summary>Chooses the file shown in the Explorer as another is shown, a tab switched to as well as a file opened, as
    /// Rider's Always Select Opened File; a row chosen there meanwhile stays chosen until then.</summary>
    private void FollowInExplorer()
    {
        string? shown = Editors.Active?.Path;
        if (shown is not null && shown != followed)
        {
            Explorer.Reveal(shown);
        }
        followed = shown;
    }

    /// <summary>Opens a file in the editor; null once it is open, else why not, which the status bar says too.</summary>
    public string? OpenFile(string path)
    {
        string full = PathNames.Canonical(Path.IsPathRooted(path) || session.Folder is not { } open ? path : Path.Combine(open, path));
        if (Editors.Open(full) is { } problem)
        {
            status.Text = problem;
            return problem;
        }
        session.Opened(full);
        Explorer.Reveal(full);
        return null;
    }

    /// <summary>A key's action that throws is reported as an IDE error, never the IDE's end. The window it was pressed in
    /// (this one or an editor window) is the one the person works in.</summary>
    private void OnKeyDownFirst(object? sender, KeyEventArgs e)
    {
        try
        {
            Editors.Front(sender as TopLevel);
            OnKey(e);
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            e.Handled = true;
        }
    }

    private void OnKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && popups.IsOpen)
        {
            // Escape closes what is over the window before anything under it hears it.
            popups.Dismiss();
            e.Handled = true;
            return;
        }
        if (TerminalTakes(e) || DiffTakes(e))
        {
            return;
        }
        string? action = keys.Press(e, out bool consumed, out bool shifted);
        if (action is null)
        {
            e.Handled |= consumed;
            return;
        }
        if (actions![action] is { InEditor: false })
        {
            if (!shifted)
            {
                if (!BranchesTake(action) && !Bottom.Debug.TakesAction(action))
                {
                    Request(action);
                }
                e.Handled = true;
            }
            return;
        }
        CodeEditor editor = Editors.CodeEditor;
        if (editor.IsFocused && !shifted && action is EditorActions.Undo or EditorActions.Redo && UndoCommand(action == EditorActions.Redo, Editors.Active))
        {
            e.Handled = true;
        }
        else if (editor.IsFocused && !shifted && assist.TakeKey(action))
        {
            e.Handled = true;
        }
        else if (editor.IsFocused && (!shifted || EditorActions.IsMovement(action)) && editor.TryExecute(action, shifted))
        {
            e.Handled = true;
        }
        else if (action == EditorActions.Escape && editor.IsFocused && Editors.FindBar.IsVisible)
        {
            // Escape with nothing of the editor's own to undo closes what sits over it, as in Rider.
            Editors.FindBar.Close();
            e.Handled = true;
        }
    }

    /// <summary>A diff in the window (Local History's) takes F7 and Shift+F7, Rider's next and previous difference there,
    /// ahead of Smart Step Into's Shift+F7 everywhere else.</summary>
    private bool DiffTakes(KeyEventArgs e) =>
        e.Key == Key.F7 && FocusManager?.GetFocusedElement() is Visual focused && focused.FindAncestorOfType<Vcs.DiffView>(includeSelf: true) is not null;

    public bool IsPopupOpen => popups.IsOpen;

    /// <summary>What the open popup is (Go to File, a breakpoint's properties, the Breakpoints window), or null.</summary>
    public Control? Popup => popups.Open;

    public CodeAssist Assist => assist;

    /// <summary>The tool windows' views: Build, Run, Debug, Unit Tests, Problems.</summary>
    public ToolWindows.ToolViews BottomPanel => Bottom;

    public DebugController Debugger => session.Debug;

    /// <summary>The open folder, canonical, or null before one is opened.</summary>
    public string? Folder => session.Folder;

    /// <summary>The program the Run window shows, while it runs or since it last did.</summary>
    public ProcessRunner Runner => session.Runner;

    /// <summary>The build, rebuild or clean the Build window shows, while it runs or since it last did.</summary>
    public ProcessRunner Builder => session.Builder;

    /// <summary>The open folder's builds and runs as the window shows them.</summary>
    public BuildRunController Builds => buildRun;

    /// <summary>Builds as Build does, and says how it ended once it has: built, failed, stopped, refused or unable to start.
    /// Canceled, the build is stopped as the Build window's Stop stops it.</summary>
    public Task<BuildOutcome> BuildAsync(CancellationToken cancel = default) => buildRun.BuildAsync(BuildCause.Explicit, cancel);

    /// <summary>The status bar's text, which says what the last action came to, for the person. What an operation came to
    /// is its own answer (a build's outcome, a commit's), never this, which anything may have replaced since.</summary>
    public string? Status => status.Text;

    /// <summary>What the status bar's language widget says: the file's language, and what its language server is waiting
    /// on or runs (C# loading, no Node.js, the TypeScript in use).</summary>
    public string? LanguageShown => LanguageText.Text;

    /// <summary>Builds the solution (or, with none, the folder's projects), as Build does; <paramref name="then"/> hears how
    /// it ended.</summary>
    public void BuildSolution(Action<BuildOutcome>? then = null) => buildRun.Build(BuildKind.Build, null, then);

    /// <summary>Builds, rebuilds or cleans the solution, or the projects given, as the open folder's
    /// <see cref="BuildRunController.Build"/> does.</summary>
    public void Build(BuildKind kind, IReadOnlyList<string>? projects = null, Action<BuildOutcome>? then = null, string? framework = null,
        BuildCause cause = BuildCause.Explicit) => buildRun.Build(kind, projects, then, framework, cause);

    /// <summary>Whether a build, rebuild or clean is under way, from its start until its callers have heard how it ended.</summary>
    public bool IsBuilding => buildRun.IsBuilding;

    /// <summary>The projects Build Selected Projects means: those chosen in the Explorer (a solution folder's, those under
    /// it), else the active file's.</summary>
    public IReadOnlyList<string> SelectedProjects()
    {
        List<string> chosen = [.. Explorer.SelectedNodes.SelectMany(Explorer.Tree.ProjectsOf).Distinct()];
        if (chosen.Count == 0 && Editors.Active is { } file && ProjectOf(file.Path) is { } owner)
        {
            chosen.Add(owner.Path);
        }
        return chosen;
    }

    /// <summary>Shows where the debugger stopped: the file and line, and the Debug window's frames.</summary>
    private void ShowStop(string path, TextPosition position)
    {
        NavigateTo(path, position);
        ShowToolWindow("debug");
        Bottom.Debug.ShowTab(console: false);
    }

    private void OnDebugStateChanged()
    {
        StopButton.IsVisible = session.Debug.IsDebugging || buildRun.IsRunning || buildRun.IsLaunching;
        ShowHotReloadButtons();
    }

    /// <summary>Opens a tool window in its place ("debug", "build", "tests"...), as its stripe button does.</summary>
    public void ShowToolWindow(string id) => Dock.Show(id);

    /// <summary>Shift+Escape: the tool window open last closes, and the editor has the keys again.</summary>
    private void HideToolWindow()
    {
        Dock.HideActive();
        FocusEditor();
    }

    /// <summary>Rider's Focus Editor: the file shown has the keys again, the tool windows left as they are.</summary>
    private void FocusEditor()
    {
        if (Editors.Active is not null)
        {
            Editors.CodeEditor.Focus();
        }
    }

    /// <summary>The tool windows, each in the place IntelliJ gives it at first.</summary>
    private void AddToolWindows()
    {
        Dock.FocusCenterRequested += (_, _) =>
        {
            if (Editors.Active is not null)
            {
                FocusEditor();
            }
            else
            {
                welcome.FocusSearch();
            }
        };
        Dock.Add(new Docking.ToolWindow("explorer", "Explorer", "Icon.Explorer", Explorer, Docking.ToolAnchor.LeftTop)
        {
            Action = AppActions.FocusExplorer,
            HeaderTools = Explorer.TakeHeaderTools(),
            Options = Explorer.Options,
            Chosen = () => Explorer.SelectedNodes,
            Activate = () =>
            {
                Dock.Show("explorer");
                Explorer.FocusRows();
            },
            OpenAtFirst = true,
        });
        Dock.Add(new Docking.ToolWindow("structure", "Structure", "Icon.Structure", Structure, Docking.ToolAnchor.LeftBottom)
        {
            Action = AppActions.ShowStructure,
            Activate = () =>
            {
                ShowToolWindow("structure");
                _ = Structure.Refresh();
            },
        });
        Dock.Add(new Docking.ToolWindow("run", "Run", "Icon.RunOutline", Bottom.Run, Docking.ToolAnchor.BottomLeft) { Action = AppActions.ShowRun, ActionTitle = "Run Tool Window" });
        Dock.Add(new Docking.ToolWindow("debug", "Debug", "Icon.Debug", Bottom.Debug, Docking.ToolAnchor.BottomLeft) { Action = AppActions.ShowDebug, ActionTitle = "Debug Tool Window" });
        Dock.Add(new Docking.ToolWindow("tests", "Unit Tests", "Icon.Tests", Bottom.Tests, Docking.ToolAnchor.BottomLeft)
        {
            Action = AppActions.ShowTests,
            Menus = [new MenuPlace("Tests", 2, 0)],
            Chosen = () => Bottom.Tests.Selected,
        });
        Dock.Add(new Docking.ToolWindow("build", "Build", "Icon.Build", Bottom.Build, Docking.ToolAnchor.BottomLeft) { Action = AppActions.ShowBuild, ActionTitle = "Build Tool Window" });
        Dock.Add(new Docking.ToolWindow("problems", "Problems", "Icon.Problems", Bottom.Problems, Docking.ToolAnchor.BottomLeft) { Action = AppActions.ShowProblems, ActionTitle = "Problems" });
    }

    /// <summary>The window a person uses keeps its layout: it opens on the one kept, and keeps each change; and each
    /// folder's editors, recent files, layout, breakpoints and bookmarks, which come back when the folder opens again.</summary>
    public void KeepLayout()
    {
        State.StateStore store = Store();
        if (store.Layout.IsKept)
        {
            Dock.Apply(store.Layout.Value);
        }
        Dock.LayoutChanged += (_, _) => KeepDock();
        findSettings = store.Find.Value;
        assist.RenameSettings = store.Refactorings.Value;
        assist.RenameSettingsChanged += (_, _) => state?.Refactorings.Set(assist.RenameSettings);
        keepsWorkspace = true;
        RestoreWorkspace();
        ShowRecent();
    }

    /// <summary>The folder this window keeps its state and its folders' snapshots in: the store's own unless one is named,
    /// as a test names a fresh one without changing the process's environment, which the programs it starts inherit.</summary>
    public string StateFolder { get; init; } = State.StateStore.DefaultRoot;

    /// <summary>Where the window leaves its language server running as it closes, and takes one up from as it opens the
    /// same solution, rather than end it and start another: a test class's windows on one project share a warm server
    /// this way. None in the IDE, whose windows end their server with them.</summary>
    public WarmServer? WarmServer { get; init; }

    /// <summary>The store of the window a person uses, the process's for its state folder, taken the first time it keeps
    /// something; null in a headless one.</summary>
    private State.StateStore? state;

    private bool keepsWorkspace;

    /// <summary>Whether the window keeps its layout and its folders' state (the person's windows), which a window opened
    /// from it does too.</summary>
    public bool KeepsLayout => keepsWorkspace;

    /// <summary>Whether the window follows and keeps the settings in its state folder (the person's windows).</summary>
    public bool KeepsSettings => keepsSettings;

    private State.StateStore Store()
    {
        if (state is null)
        {
            state = State.StateStore.Open(StateFolder);
            // The last changes are written as the window closes, since the process will not wait for their timers.
            Closing += (_, _) =>
            {
                if (keepsWorkspace)
                {
                    KeepDock();
                    KeepWorkspace();
                }
                state.Flush();
            };
            Closed += (_, _) => state.Release();
        }
        return state;
    }

    /// <summary>The layout as it stands, kept with the folder open and as the one a folder without its own opens on.</summary>
    private void KeepDock()
    {
        Docking.DockLayout now = Dock.Capture();
        state!.Layout.Set(now);
        session.State?.Layout.Set(now);
    }

    private void KeepWorkspace() => session.State?.Workspace.Set(new WorkspaceState
    {
        Editors = Editors.Capture(),
        EditorWindows = Editors.CaptureWindows(),
        RecentFiles = [.. session.RecentFiles],
        UnloadedProjects = [.. session.Unloaded],
        Folds = session.Folds.Capture(Editors.Documents),
    });

    /// <summary>The open folder's state as the store kept it, once the window keeps state: its layout (else the one
    /// changed last in any window), breakpoints, bookmarks, the Unit Tests window's view and the log's favourite branches,
    /// the session's own (<see cref="ProjectSession.Keep"/>), and its editors, the one shown the latest recent file.</summary>
    private void RestoreWorkspace()
    {
        if (!keepsWorkspace || session.Folder is not { } folder)
        {
            return;
        }
        State.ProjectState opening = state!.Project(folder);
        // The folder's own layout, else the one changed last in any window, which a folder new to Passenger opens on.
        if (opening.Layout.IsKept)
        {
            Dock.Apply(opening.Layout.Value);
        }
        else if (state.Layout.IsKept)
        {
            Dock.Apply(state.Layout.Value);
        }
        // Before the folder's documents are this window's, so what restoring them changes is not written back.
        RestoreUnitTests(opening.UnitTests.Value);
        GitBranches.Favorites = opening.GitBranches.Value;
        session.Keep(opening);
        WorkspaceState kept = opening.Workspace.Value;
        if (kept.UnloadedProjects.Count > 0)
        {
            Explorer.Refresh();
        }
        Editors.Restore(kept.Editors, kept.EditorWindows);
        if (Editors.Active is { } shown)
        {
            session.Opened(shown.Path);
        }
    }

    /// <summary>The Structure tool window.</summary>
    public ToolWindows.StructureView StructureView => Structure;

    /// <summary>The tool windows' host, for the layout and the tests.</summary>
    public Docking.DockHost DockHost => Dock;

    private void OnBuildClicked(object? sender, RoutedEventArgs e) => BuildSolution();

    private void OnRunClicked(object? sender, RoutedEventArgs e) => Request(AppActions.RunProgram);

    private void OnDebugClicked(object? sender, RoutedEventArgs e) => Request(AppActions.Debug);

    private void OnStopClicked(object? sender, RoutedEventArgs e) => Request(AppActions.Stop);

    /// <summary>Goes to a file and position (go to definition, a usage), remembering where it came from for Back.</summary>
    public void NavigateTo(string path, TextPosition position) => navigator.NavigateTo(path, position);

    /// <summary>The actions run from Find Action, the latest first, which it lists before anything is typed.</summary>
    private readonly List<string> recentActions = [];

    /// <summary>Find Action: every action by name, those that cannot run where it was opened greyed; the one chosen runs
    /// there as its shortcut would, or says why it cannot.</summary>
    public void ShowFindAction()
    {
        ActionContext context = actions!.Context();
        Navigation.ActionPopup popup = new(actions, context, recentActions);
        popup.Done += (_, chosen) =>
        {
            popups.Close();
            if (chosen is null || chosen.Id == AppActions.FindAction || !Request(chosen.Id, context))
            {
                return;
            }
            recentActions.Remove(chosen.Id);
            recentActions.Insert(0, chosen.Id);
            if (recentActions.Count > 10)
            {
                recentActions.RemoveAt(recentActions.Count - 1);
            }
        };
        popups.Show(popup);
    }

    /// <summary>Rider's Unload Project: the project shows dimmed with nothing under it, and leaves the run configurations,
    /// the solution's builds and the test runner until it is reloaded; the folder keeps the choice.</summary>
    public void SetUnloaded(string project, bool unloaded)
    {
        if (unloaded ? !session.Unloaded.Add(project) : !session.Unloaded.Remove(project))
        {
            return;
        }
        Explorer.Refresh();
        session.Configurations.Configure(Projects);
        status.Text = $"{Path.GetFileNameWithoutExtension(project)} {(unloaded ? "unloaded" : "reloaded")}";
        FindTests();
        KeepWorkspace();
    }

    /// <summary>A breakpoint's properties beside its line in the gutter, its condition ready to type.</summary>
    public void ShowBreakpointPopup(Debugging.LineBreakpoint breakpoint)
    {
        CodeEditor editor = Editors.CodeEditor;
        Point at = popups.PointIn(editor, editor.GutterPoint(breakpoint.Line)) ?? new Point(100, 100);
        Debugging.BreakpointPopup popup = new(breakpoint, session.Debug.Breakpoints, actions!.Shortcut(AppActions.ViewBreakpoints));
        popup.Closed += (_, more) =>
        {
            popups.Close();
            if (more)
            {
                ShowBreakpoints(breakpoint);
            }
        };
        popups.Show(popup, at);
        Dispatcher.UIThread.Post(popup.Properties.FocusCondition, DispatcherPriority.Loaded);
    }

    /// <summary>The Breakpoints window, a breakpoint chosen in it.</summary>
    public void ShowBreakpoints(Debugging.Breakpoint? chosen)
    {
        Debugging.BreakpointsWindow window = new(session.Debug.Breakpoints, chosen, ExceptionTypesAsync);
        window.Closed += (_, show) =>
        {
            popups.Close();
            if (show is not null)
            {
                NavigateTo(show.Path, new TextPosition(show.Line, 0));
            }
        };
        popups.Show(window);
    }

    /// <summary>The exception types Add CLR Exception Breakpoints offers: the solution's own, as the language server finds
    /// them, then the runtime's.</summary>
    private async Task<IReadOnlyList<string>> ExceptionTypesAsync()
    {
        IReadOnlyList<string> runtime = await BackgroundWork.Run(Debugging.ExceptionTypes.Runtime);
        IReadOnlyList<string> solution = [];
        if (session.CSharp?.Session is { IsLoaded: true } server)
        {
            try
            {
                solution = Debugging.ExceptionTypes.FromSymbols(await server.SymbolsAsync("Exception"));
            }
            catch (Lsp.LanguageServerFailed)
            {
                // The runtime's still are.
            }
        }
        return [.. solution.Concat(runtime).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The status bar and the title follow the file shown.</summary>
    private void UpdateStatus()
    {
        status.ShowFile(Editors.Active, Folder, Editors.CodeEditor, LanguageLabel());
        ShowTitle();
    }

    /// <summary>The window's title, as Rider's: the solution (or the project opened alone, or the folder) and the file its
    /// own editors show, an editor window in front or not; with nothing open, the Welcome window's.</summary>
    private void ShowTitle()
    {
        string? named = Folder is not { } folder ? null : session.Workspace?.Name ?? Path.GetFileName(folder);
        OpenDocument? shown = Editors.ShownInMain;
        Title = shown is null ? named ?? "Welcome to Passenger" : $"{(named is null ? "" : named + " – ")}{shown.Name}";
        FollowInDiscord();
    }

    /// <summary>The file's language, with the language server's state beside it while it is not ready, or the work it
    /// reports under way (its projects loading, and how far) whenever there is some.</summary>
    private string LanguageLabel()
    {
        string language = Editors.Active?.Language.Name ?? string.Empty;
        if (Editors.Active is { } active && Web.WebLanguages.LanguageOf(active.Path) is not null)
        {
            // A web file's server, not C#'s, is what it waits on.
            return (session.Web?.StateOf(active) ?? session.Web?.DetailOf(active)) is { Length: > 0 } said ? $"{language} · {said}" : language;
        }
        CSharpService? csharp = session.CSharp;
        string? shown = csharp?.Progress ?? (csharp is { IsReady: false, State: { Length: > 0 } state } ? state : null);
        if (shown is not null)
        {
            return language.Length > 0 ? $"{language} · {shown}" : shown;
        }
        return language;
    }

    private void UpdateCaret() => status.ShowCaret(Editors.Active is null ? null : Editors.CodeEditor);

    private void OnProjectClicked(object? sender, RoutedEventArgs e) => ShowProjectMenu();

    /// <summary>The project badge's two letters, as Rider draws them: the first two words' initials,
    /// or a lone word's first two letters.</summary>
    internal static string Initials(string name)
    {
        string[] words = name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        string letters = words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)],
            _ => $"{words[0][0]}{words[1][0]}",
        };
        return letters.ToUpperInvariant();
    }
}
