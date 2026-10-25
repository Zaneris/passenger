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
    /// held to: Avalonia would widen a smaller window to them without a word. Narrow enough to tile beside another on
    /// half a 1280-wide screen (Windows' snap halves, GNOME's Super+Left): the toolbar's names give way, a stripe puts
    /// what it has no room for behind More, and the side tool windows keep the editor its share.</summary>
    public const double SmallestWidth = 640;

    public const double SmallestHeight = 500;

    private readonly KeymapDispatcher keys = new(AppActions.Keymap());

    /// <summary>The popups over this window and the others it opens.</summary>
    private readonly PopupHost popups;

    /// <summary>The keymap's keys in this window and every other it opens but a dialog.</summary>
    private readonly IdeKeys windowKeys;

    /// <summary>What the last action came to, and the file shown's place and properties.</summary>
    private readonly StatusBar status;

    /// <summary>Going to places in the code, and back.</summary>
    private readonly Navigation.Navigator navigator;

    private readonly CodeAssist assist;
    private readonly UsageHighlighting usages;
    private readonly Testing.TestGutter testGutter;
    private readonly Passenger.App.Explorer.ExplorerView Explorer = new();
    private readonly ToolWindows.ToolViews Bottom = new();
    private ToolWindows.StructureView? structure;
    private ToolWindows.DebugView? debugView;

    /// <summary>The folder open and what it has and runs, or the window's nothing before one opens.</summary>
    private ProjectSession session;

    /// <summary>The session's builds and runs as this window shows them, made for each session it binds.</summary>
    private BuildRunController buildRun;

    public MainWindow()
    {
        UiThread.Own();
        InitializeComponent();
        session = ProjectSession.None(Setup());
        status = new StatusBar(StatusMessage, StatusText, StatusIcon, StatusWorking, CaretText, IndentText, LanguageText, LineSeparatorButton, LineEndingText, EncodingButton, EncodingText);
        popups = new PopupHost(Overlay, OverlayHost, Editors, () => session.Debug.Breakpoints);
        navigator = new Navigation.Navigator(new Navigation.NavigatorParts(Editors, () => session, popups, status, path => OpenFile(path), Editors.TextOf));
        // One keymap for the whole window, seen before any control: editor actions go to the editor with the focus.
        windowKeys = new IdeKeys(this, keys, popups, OnKey, OnActivity, OnLeft);
        windowKeys.Attach(this);
        AddActions();
        AddTitleBar();
        AddWelcome();
        AddChords();
        AddToolWindows();
        ShowActionWork();
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
        AddEndpoints();
        AddRoutes();
        AddRequests();
        AddMigrations();
        AddContainers();
        AddRunMarks();
        AddPlaces();
        AddHierarchy();
        AddFileOperations();
        AcceptDrops(this);
        AddFileProperties();
        AddTargetFramework();
        AddAutosave();
        AddReadOnly();
        AddConflicts();
        AddErrors();
        AddAbout();
        AddUpdates();
        AddWhatsNew();
        AddAgents();
        AddDiscord();
        AddShutdown();
        AddOwnWindows();
        AddPreview();
        AddMarkdownPreview();
        Explorer.FileOpened += (_, path) => OpenFile(path);
        Editors.ActiveChanged += (_, _) => FollowInExplorer();
        // An unbuilt Structure window asks for the file itself; a C# file's outline waits out the pause.
        Editors.ActiveChanged += (_, _) =>
        {
            if (Editors.Active is { } shown && session.CSharp?.For(shown) is not null)
            {
                structure?.Invalidate();
            }
            else
            {
                structure?.FollowActive();
            }
        };
        Editors.CaretMoved += (_, _) => structure?.FollowCaret();
        Editors.Opened += (_, document) => document.Text.Changed += (_, _) =>
        {
            if (Editors.Active == document)
            {
                structure?.Invalidate();
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
        Editors.OpenFailed += (_, problem) => status.Text = problem;
        Editors.Opened += (_, document) => session.Attach(document);
        Editors.Closed += (_, document) => session.Detach(document);
        Editors.ForEachEditor(editor => editor.ProblemsRequested += (_, _) => ShowToolWindow(ToolWindowIds.Problems));
        Editors.ForEachEditor(editor => editor.CaretLimitReached += (_, _) =>
            status.Text = string.Create(CultureInfo.InvariantCulture, $"Cannot create more than {SelectionSet.MaxCount:N0} carets"));
        Editors.ForEachEditor(editor => editor.ExecutionPointDragged += (_, line) =>
        {
            if (Editors.Active is { } active && session.Debug.Session is { State: DebugState.Paused } paused)
            {
                IdeErrors.Observe(session.Debug.SetNextStatementAsync(paused, active.Path, line));
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
                ShowTestMenu(editor, active, click.Line);
                return;
            }
            if (!click.Context && editor.IsInMarkColumn(click.Position) && active.RouteMarks.Any(m => m.Line == click.Line))
            {
                ShowRouteMarkMenu(editor, active, click.Line);
                return;
            }
            if (!click.Context && editor.IsInMarkColumn(click.Position) && active.RunMarks.Any(m => m.Line == click.Line))
            {
                ShowRunMarkMenu(editor, active, click.Line);
                return;
            }
            if (PickColour(editor, active, click))
            {
                return;
            }
            if (!click.Context && editor.IsInAnnotationColumn(click.Position))
            {
                IdeErrors.Observe(ShowBlamedCommitAsync(active, click.Line));
                return;
            }
            if (!click.Context && editor.IsInChangeColumn(click.Position) && ChangeAt(active, click.Line) is { } change)
            {
                IdeErrors.Observe(ShowChangePopupAsync(editor, active, change));
                return;
            }
            if (click.Context)
            {
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
        assist = new CodeAssist(Editors, Editors.AssistLayer, () => session.CSharp)
        {
            Xaml = () => session.Xaml, MSBuild = () => session.MSBuild, Web = () => session.Web, Projects = () => session.Workspace?.Projects ?? [],
        };
        assist.Navigate += (_, target) => NavigateTo(target.Path, target.Position);
        assist.Message += (_, message) => status.Text = message;
        assist.Browse += (_, page) => Browse(page);
        assist.FilesChanged += (_, _) => FilesChanged();
        assist.HttpRouteTargets = HttpRouteTargets;
        assist.RouteCallers = RouteCallers;
        usages =new UsageHighlighting(Editors, () => session.CSharp);
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
        Bottom.Run.RerunClicked += (_, _) => IdeErrors.Observe(RerunAsync());
        Bottom.Run.StopClicked += (_, _) => session.Runner.Stop();
        Bottom.Run.HotReloadClicked += (_, _) => Request(AppActions.ApplyHotReloadChanges);
        Bottom.Run.NameKeys(AppActions.Rerun, AppActions.Stop, AppActions.ApplyHotReloadChanges);
        AddUnitTests();
        AddLinks();
        AddLocalHistory();
        Bottom.Problems.FixRequested += (_, problem) =>
        {
            NavigateTo(problem.Path!, problem.Position);
            Request(AppActions.QuickFix);
        };
        assist.DebuggerValue = async (offset, cancel) => Editors.Active is { } shown && Debugging.XamlBreakpoints.Compiled(shown.Path)
            ? bindingValues!.HoverAt(shown, offset)
            : await session.Debug.HoverAt(offset, cancel);
        // Passenger's own fixes: bindings, compose names, Aspire, configuration and localized keys.
        assist.EditorFixes = (file, offset) => [.. Passenger.App.LiveTree.BindingErrors.Fixes(file, offset, session.FailedBindings.Select(b => b.Failure)),
            .. session.Web?.Checks.Fixes(file, offset) ?? [], .. assist.AspireFixes(file, offset), .. assist.ComposeReadFixes(file, offset), .. MigrationFixes(file, offset),
            .. assist.ConfigurationFixes(file, offset), .. assist.LocalizationFixes(file, offset)];
        assist.AskValue = AskValueAsync;
        // The session as each call finds it.
        assist.ConfigurationOf = path => session.ConfigurationOf(path);
        assist.ConfiguredBy = path => session.ConfiguredBy(path);
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
                IdeErrors.Observe(OpenSnapshotAsync(Path.GetFullPath(file)));
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
        IdeErrors.Observe(ForgetUnmaskedCopiesAsync());
    }

    private string? followed;

    /// <summary>Chooses the file shown in the Explorer as another is shown, a tab switched to as well as a file opened;
    /// a row chosen there meanwhile stays chosen until then.</summary>
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

    /// <summary>A key the keymap has in one of the IDE's windows (this one, an editor window, a diff, a floating tool
    /// window): the window it was pressed in is the one the person works in, an IDE action runs there and an editor action
    /// goes to that window's editor with the keys.</summary>
    private void OnKey(TopLevel window, object? focused, KeyEventArgs e)
    {
        Editors.Front(window);
        if (TerminalTakes(focused, e))
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
                if (!BranchesTake(action))
                {
                    Request(action);
                }
                e.Handled = true;
            }
            return;
        }
        if (EditorWithKeys(window, focused) is not { } editor)
        {
            if (!shifted && (ExplorerTakes(action) || (focused as Visual)?.FindAncestorOfType<ReleaseNotesView>(includeSelf: true)?.Move(action) == true))
            {
                e.Handled = true;
            }
            return;
        }
        bool own = editor == Editors.CodeEditor;
        if (own && !shifted && action is EditorActions.Undo or EditorActions.Redo && UndoCommand(action == EditorActions.Redo, Editors.Active))
        {
            e.Handled = true;
        }
        else if (own && !shifted && assist.TakeKey(action))
        {
            e.Handled = true;
        }
        else if ((!shifted || EditorActions.IsMovement(action)) && editor.TryExecute(action, shifted))
        {
            e.Handled = true;
        }
        else if (own && action == EditorActions.Escape && Editors.FindBar.IsVisible)
        {
            // Escape with nothing of the editor's own to undo closes what sits over it.
            Editors.FindBar.Close();
            e.Handled = true;
        }
    }

    /// <summary>The editor a window's editor actions go to while it has the keys: a side of a diff or a merge, or the
    /// shown file's (the active group's, in this window or an editor window); none in a window whose keys are elsewhere.</summary>
    private CodeEditor? EditorWithKeys(TopLevel window, object? focused)
    {
        if (focused is CodeEditor side && side != Editors.CodeEditor && (side.FindAncestorOfType<Vcs.DiffView>() is not null || side.FindAncestorOfType<Vcs.MergeView>() is not null))
        {
            return side;
        }
        CodeEditor shown = Editors.CodeEditor;
        return shown.IsFocused && TopLevel.GetTopLevel(shown) == window ? shown : null;
    }

    /// <summary>A key or a click in any of the IDE's windows: the person at work, which the idle save and Discord wait out.</summary>
    private void OnActivity()
    {
        RestartIdleSave();
        Stir();
    }

    /// <summary>One of the IDE's windows left: Passenger left, unless another of its own came to the front.</summary>
    private void OnLeft()
    {
        if (!OwnWindowActive())
        {
            SaveOnLeaving();
        }
    }

    public bool IsPopupOpen => popups.IsOpen;

    /// <summary>What the open popup is (Go to File, a breakpoint's properties, the Breakpoints window), or null.</summary>
    public Control? Popup => popups.Open;

    public CodeAssist Assist => assist;

    /// <summary>The tool windows' views: Build, Run, Debug, Unit Tests, Problems.</summary>
    public ToolWindows.ToolViews BottomPanel => Bottom;

    public DebugController Debugger => session.Debug;

    /// <summary>The Debug window's view, built as it is asked for; what it shows is <see cref="ToolWindows.ToolViews.Debug"/>,
    /// kept while it is not.</summary>
    public ToolWindows.DebugView DebugWindow => debugView ?? (ToolWindows.DebugView)Dock[ToolWindowIds.Debug].Content;

    /// <summary>The open folder, canonical, or null before one is opened.</summary>
    public string? Folder => session.Folder;

    /// <summary>The folder open, else the one opening while what the last left ends and its documents are read: what
    /// the window holds as the process opens another (<see cref="PassengerApp.OpenProject"/>).</summary>
    internal string? FolderHeld => session.Folder ?? (opens == openingAt ? opening : null);

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
        ShowToolWindow(ToolWindowIds.Debug);
        Bottom.Debug.ShowTab(console: false);
    }

    /// <summary>Stop can be clicked while there is something to stop: a debug session, a program Run or Profile
    /// started, or a start or build under way. Otherwise it stays in its place, greyed, so Run and Debug never move
    /// from under the pointer.</summary>
    private void ShowStop()
    {
        StopButton.IsEnabled = buildRun.WhyNotStop() is null;
        // A configuration said to be running follows its run as Stop does.
        ShowChosenConfiguration(session.Configurations);
    }

    private void OnDebugStateChanged()
    {
        ShowStop();
        ShowHotReloadButtons();
    }

    /// <summary>Opens a tool window in its place (one of <see cref="ToolWindowIds"/>).</summary>
    public void ShowToolWindow(string id) => Dock.Show(id);

    /// <summary>Shift+Escape: the tool window open last closes, and the editor has the keys again.</summary>
    private void HideToolWindow()
    {
        Dock.HideActive();
        FocusEditor();
    }

    /// <summary>Focus Editor: the file shown has the keys again, the tool windows left as they are.</summary>
    private void FocusEditor()
    {
        if (Editors.Active is not null)
        {
            Editors.CodeEditor.Focus();
        }
    }

    /// <summary>The tool windows, each in its first place.</summary>
    private void AddToolWindows()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Explorer, "Explorer", IconKeys.Explorer, Explorer, Docking.ToolAnchor.LeftTop)
        {
            Action = AppActions.FocusExplorer,
            HeaderTools = Explorer.TakeHeaderTools(),
            Options = Explorer.Options,
            Chosen = () => Explorer.SelectedNodes,
            Activate = () =>
            {
                Dock.Show(ToolWindowIds.Explorer);
                Explorer.FocusRows();
            },
            OpenAtFirst = true,
        });
        // Shown however it is (its action, its stripe button, a layout), it asks for the file shown then itself.
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Structure, "Structure", IconKeys.Structure, MakeStructure, Docking.ToolAnchor.LeftBottom) { Action = AppActions.ShowStructure });
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Run, "Run", IconKeys.RunOutline, Bottom.Run, Docking.ToolAnchor.BottomLeft) { Action = AppActions.ShowRun, ActionTitle = "Run Tool Window" });
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Debug, "Debug", IconKeys.Debug, () => debugView = new ToolWindows.DebugView(Bottom.Debug), Docking.ToolAnchor.BottomLeft)
        {
            Action = AppActions.ShowDebug,
            ActionTitle = "Debug Tool Window",
            Tools = () => debugView?.HeaderTools,
        });
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Aspire, "Aspire", IconKeys.Aspire, MakeAspire, Docking.ToolAnchor.BottomLeft) { Action = AppActions.ShowAspire, ActionTitle = "Aspire Tool Window" });
        Closed += (_, _) =>
        {
            Bottom.Aspire.Dispose();
            aspireView?.Dispose();
        };
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Tests, "Unit Tests", IconKeys.Tests, Bottom.Tests, Docking.ToolAnchor.BottomLeft)
        {
            Action = AppActions.ShowTests,
            Menus = [new MenuPlace("Tests", 2, 0)],
            Chosen = () => Bottom.Tests.Selected,
        });
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Build, "Build", IconKeys.Build, Bottom.Build, Docking.ToolAnchor.BottomLeft) { Action = AppActions.ShowBuild, ActionTitle = "Build Tool Window" });
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Problems, "Problems", IconKeys.Problems, () => new ToolWindows.ProblemsView(Bottom.Problems), Docking.ToolAnchor.BottomLeft)
        {
            Action = AppActions.ShowProblems,
            ActionTitle = "Problems",
        });
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
        Editors.ShowsMarkdownPreview = store.Previews.Value.Markdown;
        assist.RenameSettings = store.Refactorings.Value;
        assist.RenameSettingsChanged += (_, _) => state?.Refactorings.Set(assist.RenameSettings);
        keepsWorkspace = true;
        RestoreWorkspace();
        ShowRecent();
    }

    /// <summary>The folder this window keeps its state and its folders' snapshots in: the store's own unless one is
    /// named, which leaves the environment the programs it starts inherit as it was.</summary>
    public string StateFolder { get; init; } = State.StateStore.DefaultRoot;

    /// <summary>Where the window leaves its language server running as it closes, and takes one up from as it opens the
    /// same solution, rather than end it and start another. None in the IDE, whose windows end their server with
    /// them.</summary>
    public WarmServer? WarmServer { get; init; }

    /// <summary>Whether the window starts the C# language server on a folder with a solution or project: always in the
    /// IDE; off for a window that asks nothing of C#, an <c>--mcp</c> Passenger's through
    /// <see cref="Mcp.McpHost.LanguageServerVariable"/>.</summary>
    public bool StartsLanguageServer { get; init; } = true;

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
            Action setAside = () => UiThread.Post(SayWhatWasSetAside);
            State.StateFile.SetAsideNow += setAside;
            Closed += (_, _) =>
            {
                State.StateFile.SetAsideNow -= setAside;
                state.Release();
            };
            SayWhatWasSetAside();
        }
        return state;
    }

    /// <summary>A file of the window's state that did not read and was set aside (its settings, a folder's breakpoints)
    /// is said in the status bar, so the person knows where what they kept went.</summary>
    private void SayWhatWasSetAside()
    {
        if (State.StateFile.TakeUnsaid(StateFolder) is { Count: > 0 } said)
        {
            Say(string.Join("; ", said.Select(s => s.Said)), SetAsidePlace(said[0].Kept));
        }
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
        branchFavorites = opening.GitBranches.Value;
        if (gitView?.Log.Branches is { } branches)
        {
            branches.Favorites = branchFavorites;
        }
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

    /// <summary>The Structure tool window, built as it is first shown or asked for.</summary>
    public ToolWindows.StructureView StructureView => (ToolWindows.StructureView)Dock[ToolWindowIds.Structure].Content;

    /// <summary>The Structure window's view, built as it is first shown (<see cref="StructureView"/>) and wired then.</summary>
    private ToolWindows.StructureView MakeStructure()
    {
        structure = new ToolWindows.StructureView();
        structure.Follow(() => Editors.Active is { } shown
            ? (shown.Path, session.CSharp?.For(shown)?.Lsp ?? session.Web?.For(shown)?.Lsp, Editors.CodeEditor.CaretPosition.Line)
            : null);
        structure.GoToRequested += (_, row) => NavigateTo(row.Path, row.Position);
        structure.UsagesRequested += (_, row) =>
        {
            NavigateTo(row.Path, row.Position);
            Request(AppActions.FindUsages);
        };
        return structure;
    }

    /// <summary>The tool windows' host.</summary>
    public Docking.DockHost DockHost => Dock;

    /// <summary>Goes to a file and position (go to definition, a usage), remembering where it came from for Back.</summary>
    public void NavigateTo(string path, TextPosition position) => navigator.NavigateTo(path, position);

    /// <summary>Goes to a place a language server names, in its lines (<see cref="Lsp.ServerLines"/>).</summary>
    public void NavigateTo(string path, Lsp.LspPosition at) => navigator.NavigateTo(path, ServerPlaces.PositionOf(Editors, path, at));

    /// <summary>The actions run from Find Action, the latest first, which it lists before anything is typed.</summary>
    private readonly List<string> recentActions = [];

    /// <summary>Find Action: every action by name, those that cannot run where it was opened greyed; the one chosen runs
    /// there as its shortcut would, or says why it cannot.</summary>
    public void ShowFindAction()
    {
        ActionContext context = actions!.Context();
        Navigation.ActionPopup popup = new(actions, context, recentActions);
        popups.Show(popup, chosen =>
        {
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
        });
    }

    /// <summary>Unload Project: the project shows dimmed with nothing under it, and leaves the run configurations, the
    /// solution's builds and the test runner until it is reloaded; the folder keeps the choice.</summary>
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
        ShowSolutionProblems();
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

    /// <summary>The window's title: the solution (or the project opened alone, or the folder) and the file its own
    /// editors show, an editor window in front or not; with nothing open, the Welcome window's.</summary>
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

    private void UpdateCaret() => status.ShowCaret(Editors.Active is { HoldsText: true } ? Editors.CodeEditor : null);

    private void OnProjectClicked(object? sender, RoutedEventArgs e) => ShowProjectMenu();

    /// <summary>The project badge's two letters: the first two words' initials, or a lone word's first two
    /// letters.</summary>
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
