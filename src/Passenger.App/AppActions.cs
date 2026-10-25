using Passenger.Editor;

namespace Passenger.App;

/// <summary>The ids of the IDE's own actions, beside the editor's (<see cref="EditorActions"/>), and the keymaps of each
/// platform that bind both by id: Rider's Visual Studio keymaps for these too, and its VS Code keymaps. Each action is
/// declared by its feature (<see cref="IdeAction"/>, in the window's <see cref="ActionRegistry"/>).</summary>
[IdentifierHome]
public static class AppActions
{
    public const string Open = "app.open";

    /// <summary>A folder to open: the system's panel takes files or folders, not both, so Open takes the solution or
    /// project files and this the folder. Rider's keymaps have no keys for it.</summary>
    public const string OpenFolder = "app.openFolder";

    /// <summary>Rider's New Solution (RiderNewSolution), which its keymaps give no keys.</summary>
    public const string NewSolution = "app.newSolution";

    /// <summary>Rider's New Project (NewRiderProject), a project added to the open solution.</summary>
    public const string NewProject = "app.newProject";
    public const string Exit = "app.exit";

    // Rider's NextProjectWindow and PreviousProjectWindow (Next and Previous Solution Window), whose keys its Visual Studio
    // keymaps take from their parents: Mac OS X 10.5+'s and $default's.
    public const string NextProjectWindow = "app.nextProjectWindow";
    public const string PreviousProjectWindow = "app.previousProjectWindow";

    // Rider's File Encoding (ChangeFileEncodingAction) and Line Separators (ConvertTo*LineSeparators), which its keymaps
    // give no keys.
    public const string FileEncoding = "app.fileEncoding";
    public const string LineSeparatorsCrlf = "app.lineSeparators.crlf";
    public const string LineSeparatorsLf = "app.lineSeparators.lf";
    public const string LineSeparatorsCr = "app.lineSeparators.cr";

    // Rider's ChangeTargetFramework (Target Framework), the editor's context switcher, which its keymaps give no keys.
    public const string TargetFramework = "app.code.targetFramework";
    public const string Save = "app.save";
    public const string SaveAll = "app.saveAll";
    public const string CloseTab = "app.closeTab";
    public const string NextTab = "app.nextTab";
    public const string PreviousTab = "app.previousTab";
    public const string GoToFile = "app.goToFile";
    public const string GoToLine = "app.goToLine";
    public const string GoToSymbol = "app.goToSymbol";
    public const string GoToEverything = "app.goToEverything";
    public const string FileStructure = "app.fileStructure";
    public const string FindAction = "app.findAction";
    public const string FindInFiles = "app.findInFiles";
    public const string RecentFiles = "app.recentFiles";

    // Rider's RecentChangedFiles (Recently Changed Files), RecentLocations, JumpToLastChange (Last Edit Location) and
    // JumpToNextChange (Next Edit Location); the last has no keys in its Visual Studio keymaps, Recent Locations none in
    // the macOS one.
    public const string RecentChangedFiles = "app.recentChangedFiles";
    public const string RecentLocations = "app.recentLocations";
    public const string LastEditLocation = "app.lastEditLocation";
    public const string NextEditLocation = "app.nextEditLocation";

    // Bookmarks: Rider's, on the Visual Studio keymaps' Ctrl+K chords.
    public const string ToggleBookmark = "app.bookmarks.toggle";
    public const string NextBookmark = "app.bookmarks.next";
    public const string PreviousBookmark = "app.bookmarks.previous";
    public const string ShowBookmarks = "app.bookmarks.show";
    public const string ReplaceInFiles = "app.replaceInFiles";
    public const string ShowSettings = "app.settings";
    public const string ShowLog = "app.showLog";
    public const string About = "app.about";
    public const string CheckForUpdates = "app.checkForUpdates";
    public const string RestartToUpdate = "app.restartToUpdate";

    // The running version's release notes: no key of Rider's.
    public const string WhatsNew = "app.whatsNew";
    public const string ShowNuGet = "app.showNuGet";
    public const string Profile = "app.profile";
    public const string ShowProfiler = "app.showProfiler";

    // Attach the profiler to the program Run or Debug started: no key of Rider's.
    public const string AttachProfiler = "app.profiler.attach";

    // The Live Visual Tree of an app being debugged: no key of Rider's or Visual Studio's.
    public const string ShowLiveTree = "app.showLiveTree";
    public const string SelectElement = "app.liveTree.selectElement";

    // The Endpoints window over the folder's .http files, and Test of the request chosen there: no key of Rider's.
    public const string ShowEndpoints = "app.showEndpoints";
    public const string TestEndpoint = "app.endpoints.test";

    // The Requests window over the running web app's live requests and their database commands: no key of Rider's.
    public const string ShowRequests = "app.showRequests";

    // The Migrations window over the folder's EF Core contexts, its commands, and the fix of what keeps dotnet ef from
    // working that it shows first: no key of Rider's.
    public const string ShowMigrations = "app.showMigrations";
    public const string MigrationsRefresh = "app.migrations.refresh";
    public const string AddMigration = "app.migrations.add";
    public const string RemoveLastMigration = "app.migrations.removeLast";
    public const string UpdateDatabase = "app.migrations.update";
    public const string UpdateDatabaseTo = "app.migrations.updateTo";
    public const string ScriptMigrations = "app.migrations.script";
    public const string BundleMigrations = "app.migrations.bundle";
    public const string MigrationsFix = "app.migrations.fix";

    // The Containers window over the person's container engine and its actions on the row chosen there: no keys of Rider's,
    // whose Services window has none in its Visual Studio keymaps.
    public const string ShowContainers = "app.showContainers";
    public const string ContainersRefresh = "app.containers.refresh";
    public const string ContainerStart = "app.containers.start";
    public const string ContainerStop = "app.containers.stop";
    public const string ContainerRestart = "app.containers.restart";
    public const string ContainerPause = "app.containers.pause";
    public const string ContainerRemove = "app.containers.remove";
    public const string ContainerScale = "app.containers.scale";
    public const string ContainerPull = "app.containers.pull";
    public const string ContainerPrune = "app.containers.prune";
    public const string ContainerInspect = "app.containers.inspect";
    public const string ContainerShell = "app.containers.shell";

    // The Containers window's tabs, its log's Find, and its Telemetry's log trace and span logs, as Aspire's are.
    public const string ContainersLog = "app.containers.log";
    public const string ContainersStats = "app.containers.stats";
    public const string ContainersFiles = "app.containers.files";
    public const string ContainersDetails = "app.containers.details";
    public const string ContainersTelemetry = "app.containers.telemetry";
    public const string ContainersLogFind = "app.containers.logFind";
    public const string ContainersShowTrace = "app.containers.showTrace";
    public const string ContainersShowRelatedLogs = "app.containers.showRelatedLogs";

    // A .NET service of a run of services under way (a container run's, an AppHost's), chosen in the Containers or Aspire
    // window or by its gutter icon, the run left up: debugged from its start, attached to as it runs, let go by the debugger,
    // or profiled. No keys of Rider's. The ids each window had for them before they were one set still name them (Formerly).
    public const string ServiceDebug = "app.service.debug";
    public const string ServiceAttach = "app.service.attach";
    public const string ServiceRun = "app.service.runWithoutDebugger";
    public const string ServiceProfile = "app.service.profile";

    /// <summary>The ids actions had before, by the id each has now: a person's shortcuts kept under one (settings.json's
    /// <c>shortcuts</c>, read as the new id's) and an agent's <c>ide_action</c> naming one reach the action it is now.</summary>
    public static IReadOnlyDictionary<string, string> Formerly { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["app.containers.service.debug"] = ServiceDebug,
        ["app.containers.service.attach"] = ServiceAttach,
        ["app.containers.service.run"] = ServiceRun,
        ["app.containers.service.profile"] = ServiceProfile,
        ["app.aspire.debug"] = ServiceDebug,
        ["app.aspire.attach"] = ServiceAttach,
        ["app.aspire.runWithoutDebugger"] = ServiceRun,
        ["app.aspire.profile"] = ServiceProfile,
    };

    /// <summary>An action's id now, for one it had before (<see cref="Formerly"/>), else the id itself.</summary>
    public static string Now(string id) => Formerly.GetValueOrDefault(id, id);

    // Avalonia's renderer debug overlays in an app Run, Debug or Profile launched (Avalonia's own names): no key of Rider's.
    public const string OverlayFps = "app.avalonia.overlay.Fps";
    public const string OverlayDirtyRects = "app.avalonia.overlay.DirtyRects";
    public const string OverlayLayoutTimeGraph = "app.avalonia.overlay.LayoutTimeGraph";
    public const string OverlayRenderTimeGraph = "app.avalonia.overlay.RenderTimeGraph";
    public const string Find = "app.find";
    public const string Replace = "app.replace";
    public const string FindNext = "app.findNext";
    public const string FindPrevious = "app.findPrevious";
    public const string FocusExplorer = "app.focusExplorer";

    // The Explorer's own actions, on the row its menu opened on or the one chosen while it has the keys: NewFile, NewDir,
    // Class/Interface, the solution's own, Add Reference, Docker Support and Compose, Aspire Orchestration, NuGet, Manage
    // User Secrets (no keys of Rider's), Edit Project File, Unload and Reload Project, Compare Files (CompareTwoFiles, off
    // macOS sharing the editor's Duplicate keys), Copy Path (CopyPaths), Reveal in Finder (RevealIn) and Open in Terminal.
    // Rename and Delete are Rename's and the editor's Delete, one action wherever they are pressed.
    public const string NewFile = "app.explorer.newFile";
    public const string NewFolder = "app.explorer.newFolder";
    public const string NewType = "app.explorer.newType";
    public const string NewSolutionFolder = "app.explorer.newSolutionFolder";
    public const string AddExistingProject = "app.explorer.addExistingProject";
    public const string AddExistingItem = "app.explorer.addExistingItem";
    public const string AddReference = "app.explorer.addReference";
    public const string AddDockerSupport = "app.explorer.addDockerSupport";
    public const string AddDockerCompose = "app.explorer.addDockerCompose";
    public const string AddAspireOrchestration = "app.explorer.addAspireOrchestration";
    public const string ManageNuGet = "app.explorer.manageNuGet";
    public const string ManageUserSecrets = "app.explorer.manageUserSecrets";
    public const string EditProjectFile = "app.explorer.editProjectFile";
    public const string UnloadProject = "app.explorer.unloadProject";
    public const string ReloadProject = "app.explorer.reloadProject";
    public const string CompareFiles = "app.explorer.compareFiles";
    public const string CopyPath = "app.explorer.copyPath";
    public const string RevealInFileManager = "app.explorer.reveal";
    public const string OpenInTerminal = "app.explorer.openInTerminal";

    public const string Build = "app.build";
    public const string RebuildSolution = "app.build.rebuildSolution";
    public const string CleanSolution = "app.build.cleanSolution";
    public const string BuildSelected = "app.build.selected";
    public const string RebuildSelected = "app.build.rebuildSelected";
    public const string CleanSelected = "app.build.cleanSelected";

    // Publish of the chosen project to a folder through a profile, and again with the profile it last published with: no
    // keys of Rider's.
    public const string Publish = "app.build.publish";
    public const string PublishAgain = "app.build.publishAgain";
    public const string RunProgram = "app.run";
    public const string Debug = "app.debug";
    public const string Stop = "app.stop";
    public const string StopAll = "app.stopAll";
    public const string Rerun = "app.rerun";
    public const string EditConfigurations = "app.editConfigurations";
    public const string ShowBuild = "app.showBuild";
    public const string ShowRun = "app.showRun";
    public const string ShowAspire = "app.showAspire";
    public const string AspireRefresh = "app.aspire.refresh";
    public const string AspireDiscover = "app.aspire.discover";
    public const string AspireCommand = "app.aspire.command";
    public const string AspireLogFind = "app.aspire.logFind";
    public const string AspireShowTrace = "app.aspire.showTrace";
    public const string AspireShowRelatedLogs = "app.aspire.showRelatedLogs";
    public const string AspireResources = "app.aspire.resources";
    public const string AspireConsole = "app.aspire.console";
    public const string AspireTelemetry = "app.aspire.telemetry";
    public const string AspireLogs = "app.aspire.logs";
    public const string AspireTraces = "app.aspire.traces";
    public const string AspireAppHostConsole = "app.aspire.appHostConsole";
    public const string AspireOpenLink = "app.aspire.openLink";
    public const string AspireSource = "app.aspire.source";
    public const string AspireContainer = "app.aspire.container";
    public const string ShowProblems = "app.showProblems";
    public const string ShowFind = "app.showFind";
    public const string ToggleSolutionAnalysis = "app.toggleSolutionAnalysis";
    public const string ShowDebug = "app.showDebug";
    public const string HideToolWindow = "app.hideToolWindow";
    public const string Back = "app.back";
    public const string Forward = "app.forward";

    // Code actions, on the active editor.
    public const string Complete = "app.code.complete";
    public const string QuickFix = "app.code.quickFix";
    public const string Rename = "app.code.rename";
    public const string GoToDeclaration = "app.code.goToDeclaration";
    public const string GoToImplementation = "app.code.goToImplementation";

    // Rider's GotoTypeDeclaration, and GotoSuperMethod and GotoImplementation, which it names Base Symbols and Derived
    // Symbols; its Go to Implementation is ReSharperGotoImplementation.
    public const string GoToTypeDeclaration = "app.code.goToTypeDeclaration";
    public const string BaseSymbols = "app.code.baseSymbols";
    public const string DerivedSymbols = "app.code.derivedSymbols";

    public const string FindUsages = "app.code.findUsages";
    public const string HighlightUsages = "app.code.highlightUsages";
    public const string Reformat = "app.code.reformat";
    public const string CodeCleanup = "app.code.cleanup";

    // Rider's ReformatWithPrettierAction (its Visual Studio keymaps give its keys to VcsShowPrevChangeMarker) and
    // Javascript.Linters.EsLint.Fix, which it names Fix ESLint Problems; both in the editor's menu.
    public const string ReformatWithPrettier = "app.code.reformatWithPrettier";
    public const string FixEslintProblems = "app.code.fixEslintProblems";
    public const string RefactorThis = "app.code.refactorThis";
    public const string Generate = "app.code.generate";
    public const string ExtractMethod = "app.code.extractMethod";
    public const string IntroduceVariable = "app.code.introduceVariable";
    public const string Inline = "app.code.inline";
    public const string ParameterInfo = "app.code.parameterInfo";

    // Rider's SurroundWith (its Visual Studio keymaps clear its own keys and give theirs to SurroundWithLiveTemplate, which
    // it is here), EditorCompleteStatement, OptimizeImports (named Optimize Imports in C# too; its id says usings, which
    // Find Action reads), SafeDelete, IntroduceField, IntroduceParameter and RiderBackendAction-EncapsulateField.
    public const string SurroundWith = "app.code.surroundWith";
    public const string CompleteStatement = "app.code.completeStatement";
    public const string OptimizeUsings = "app.code.optimizeUsings";
    public const string SafeDelete = "app.code.safeDelete";
    public const string IntroduceField = "app.code.introduceField";
    public const string IntroduceParameter = "app.code.introduceParameter";
    public const string EncapsulateField = "app.code.encapsulateField";

    // Rider's ChangeSignature, ExtractInterface, Move, MembersPullUp and MemberPushDown, the refactorings that ask
    // their options in a dialog.
    public const string ChangeSignature = "app.code.changeSignature";
    public const string ExtractInterface = "app.code.extractInterface";
    public const string Move = "app.code.move";
    public const string PullMembersUp = "app.code.pullMembersUp";
    public const string PushMembersDown = "app.code.pushMembersDown";

    // Rider's XAML refactorings (XamlExtractResource, XamlExtractStyle), which its keymaps give no keys.
    public const string ExtractResource = "app.code.extractResource";
    public const string ExtractStyle = "app.code.extractStyle";
    public const string QuickDocumentation = "app.code.quickDocumentation";
    public const string NextError = "app.code.nextError";
    public const string PreviousError = "app.code.previousError";
    public const string FocusEditor = "app.focusEditor";

    // The debugger's, on the active editor and the session.
    public const string ToggleBreakpoint = "app.debug.toggleBreakpoint";
    public const string ToggleBreakpointEnabled = "app.debug.toggleBreakpointEnabled";
    public const string MuteBreakpoints = "app.debug.muteBreakpoints";
    public const string Resume = "app.debug.resume";
    public const string Pause = "app.debug.pause";
    public const string StepOver = "app.debug.stepOver";
    public const string StepInto = "app.debug.stepInto";
    public const string SmartStepInto = "app.debug.smartStepInto";
    public const string StepOut = "app.debug.stepOut";
    public const string RunToCursor = "app.debug.runToCursor";
    public const string SetNextStatement = "app.debug.setNextStatement";
    public const string ExtendSelection = "app.editor.extendSelection";
    public const string CallHierarchy = "app.hierarchy.calls";
    public const string TypeHierarchy = "app.hierarchy.types";
    public const string ShrinkSelection = "app.editor.shrinkSelection";
    public const string ShowExecutionPoint = "app.debug.showExecutionPoint";

    // Rider's XDebugger.AttachToProcess and RiderReattach, on its Visual Studio keymaps' Ctrl+Alt+P (Cmd+Alt+P) and
    // Alt+Shift+P, and RiderDetachDebuggerAction, after Stop, which no keymap gives keys.
    public const string AttachToProcess = "app.debug.attachToProcess";
    public const string ReattachToProcess = "app.debug.reattachToProcess";
    public const string Detach = "app.debug.detach";

    // Rider's RiderDebuggerApplyEncChagnes, "Apply Hot Reload Changes", on Alt+F10 in both its Visual Studio keymaps.
    public const string ApplyHotReloadChanges = "app.debug.applyHotReloadChanges";
    public const string Evaluate = "app.debug.evaluate";
    public const string AddToWatches = "app.debug.addToWatches";
    public const string ShowContextMenu = "app.contextMenu";

    // Unit tests, run where the caret or the Unit Tests window's selection is.
    public const string RunTests = "app.tests.run";
    public const string DebugTests = "app.tests.debug";
    public const string RunAllTests = "app.tests.runAll";
    public const string RunTestsUntilFailure = "app.tests.runUntilFailure";
    public const string RerunTests = "app.tests.rerun";
    public const string RerunFailedTests = "app.tests.rerunFailed";
    public const string StopTests = "app.tests.stop";
    public const string ShowTests = "app.showTests";
    public const string ShowStructure = "app.showStructure";

    // Editor splits: Rider's, from the tab's menu (no keys in its Visual Studio keymaps).
    public const string SplitRight = "app.editor.splitRight";
    public const string SplitDown = "app.editor.splitDown";
    public const string MoveToOppositeGroup = "app.editor.moveToOppositeGroup";

    // Rider's Open Tab in New Window (EditSourceInNewWindow): Shift+F4 in both of its Visual Studio keymaps, from their parents.
    public const string OpenInNewWindow = "app.editor.openInNewWindow";

    // Rider's Window > Editor Tabs (ReopenClosedTab, PinActiveEditorTab, CloseAllEditorsButActive, CloseAllEditors,
    // CloseAllUnpinnedEditors, CloseAllToTheLeft and -Right, Unsplit, UnsplitAll): no keys in its Visual Studio keymaps
    // or their parents.
    public const string ReopenClosedTab = "app.editor.reopenClosedTab";
    public const string PinTab = "app.editor.pinTab";
    public const string CloseOtherTabs = "app.editor.closeOthers";
    public const string CloseAllTabs = "app.editor.closeAll";
    public const string CloseUnpinned = "app.editor.closeUnpinned";
    public const string CloseTabsLeft = "app.editor.closeLeft";
    public const string CloseTabsRight = "app.editor.closeRight";
    public const string Unsplit = "app.editor.unsplit";
    public const string UnsplitAll = "app.editor.unsplitAll";

    // Rider's View > Active Editor: Soft-Wrap (EditorToggleUseSoftWraps) and Show Whitespaces (EditorToggleShowWhitespaces)
    // on its Visual Studio keymaps' Ctrl+E, W and Ctrl+R, W chords (Cmd on macOS), and Increase, Decrease and Reset Font
    // Size (EditorIncreaseFontSize, EditorDecreaseFontSize, EditorResetFontSize), which they and their parents give no keys.
    public const string SoftWrap = "app.editor.softWrap";
    public const string ShowWhitespaces = "app.editor.showWhitespaces";
    public const string IncreaseFontSize = "app.editor.increaseFontSize";
    public const string DecreaseFontSize = "app.editor.decreaseFontSize";
    public const string ResetFontSize = "app.editor.resetFontSize";

    // The XAML previewer beside the editor, under View: no action of Rider's keymaps.
    public const string XamlPreview = "app.editor.xamlPreview";

    // A Markdown file's preview beside its editor, under View and on the editor's tab strip: the Visual Studio keymaps and
    // their parents give IntelliJ's Editor and Preview layout (TextEditorWithPreview.Layout.EditorAndPreview) no keys; its
    // VSCode keymaps give it Ctrl+K, V (Cmd+K, V on macOS).
    public const string MarkdownPreview = "app.editor.markdownPreview";

    // Rider's Close Solution (CloseProject) and Restore Default Layout (RiderRestoreDefaultLayout, in Window > Layout
    // Settings): no keys there either; both Visual Studio keymaps take IntelliJ's Shift+F12 off its layout action.
    public const string CloseSolution = "app.closeSolution";
    public const string RestoreDefaultLayout = "app.window.restoreDefaultLayout";

    // The terminal: a shell per tab in the open folder.
    public const string ShowTerminal = "app.showTerminal";

    // The tool windows' frame.
    public const string MaximizeToolWindow = "app.window.maximize";
    public const string HideAllToolWindows = "app.window.hideAll";

    // The breakpoints' windows, the window's to show.
    public const string EditBreakpoint = "app.breakpoints.edit";
    public const string ViewBreakpoints = "app.breakpoints.view";

    // Version control: Git over the open folder's working tree.
    public const string ShowCommit = "app.vcs.showCommit";
    public const string Commit = "app.vcs.commit";
    public const string Push = "app.vcs.push";
    public const string Branches = "app.vcs.branches";
    public const string NextChange = "app.vcs.nextChange";
    public const string PreviousChange = "app.vcs.previousChange";
    public const string RollbackLines = "app.vcs.rollbackLines";
    public const string ShowDiff = "app.vcs.showDiff";

    // Rider's Diff.ShowSettingsPopup, a diff's Settings: $default's Ctrl+Shift+D, none in its VS Code keymaps.
    public const string DiffSettings = "app.vcs.diffSettings";
    public const string ShowGit = "app.vcs.showGit";
    public const string Annotate = "app.vcs.annotate";
    public const string UpdateProject = "app.vcs.update";
    public const string Fetch = "app.vcs.fetch";
    public const string Stash = "app.vcs.stash";
    public const string Unstash = "app.vcs.unstash";
    public const string CloneRepository = "app.vcs.clone";

    // Rider's Git.ResolveConflicts, Git.Rebase.Continue, Git.Rebase.Abort and Git.Merge.Abort: no keys in its Visual
    // Studio keymaps or their parents.
    public const string ResolveConflicts = "app.vcs.resolveConflicts";
    public const string ContinueRebase = "app.vcs.continueRebase";
    public const string AbortRebase = "app.vcs.abortRebase";
    public const string AbortMerge = "app.vcs.abortMerge";

    // Rider's Vcs.ShowTabbedFileHistory, Vcs.ShowHistoryForBlock, Git.CompareWithBranch and Compare.Selected: no keys in
    // its Visual Studio keymaps or their parents.
    public const string ShowHistory = "app.vcs.showHistory";
    public const string ShowHistoryForSelection = "app.vcs.showHistoryForSelection";
    public const string CompareWithBranch = "app.vcs.compareWithBranch";
    public const string CompareWithRevision = "app.vcs.compareWithRevision";

    // Rider's Local History (LocalHistory.ShowHistory, LocalHistory.ShowProjectHistory, RecentChanges and
    // LocalHistory.PutLabel): its Visual Studio keymaps give none of them keys, and take Recent Changes' Alt+Shift+C off.
    public const string ShowLocalHistory = "app.localHistory.show";
    public const string ShowProjectHistory = "app.localHistory.showProject";
    public const string RecentChanges = "app.localHistory.recentChanges";
    public const string PutLabel = "app.localHistory.putLabel";

    /// <summary>The keymap the person chose (<see cref="KeymapPreset"/>), the platform's.</summary>
    public static KeymapPreset Preset { get; private set; }

    /// <summary>The person's own shortcuts over the chosen keymap's, by action (an empty list unbinds it), as Settings
    /// keeps them for that keymap. The process's: every window has the one keymap, which the settings' store puts in
    /// force (<see cref="Settings.SettingsStore"/>).</summary>
    public static IReadOnlyDictionary<string, string[]> UserShortcuts { get; private set; } = new Dictionary<string, string[]>();

    private static Keymap? inForce;

    /// <summary>Puts a keymap and the person's shortcuts over it in force.</summary>
    public static void Choose(KeymapPreset preset, IReadOnlyDictionary<string, string[]> shortcuts)
    {
        Preset = preset;
        UserShortcuts = shortcuts;
        inForce = null;
    }

    /// <summary>The keymap in force: the chosen keymap with the person's shortcuts over it, made once until those
    /// change, since every tip and menu reads it; a caller reads it and never binds into it.</summary>
    public static Keymap Keymap() => inForce ??= Keymap(Preset, UserShortcuts);

    /// <summary>The action whose keys a place takes for one of its own, where Rider's keymaps give a key two actions told
    /// apart by where it is pressed and the keymap gives it to the other: Show Diff in the Commit window and the Git log
    /// takes Compare Files' keys in Visual Studio's keymap (Duplicate's off macOS; none in VS Code's), and the Debug
    /// window's evaluate box adds a watch by Complete Statement's keys (Start New Line Before's in VS Code's). The
    /// action's own once the person gives it some.</summary>
    public static string KeysIn(string id) => UserShortcuts.ContainsKey(id) ? id : id switch
    {
        ShowDiff when Preset == KeymapPreset.VisualStudio => OperatingSystem.IsMacOS() ? CompareFiles : EditorActions.Duplicate,
        AddToWatches => Preset == KeymapPreset.VisualStudio ? CompleteStatement : EditorActions.StartNewLineBefore,
        _ => id,
    };

    /// <summary>Whether a key pressed is one a place takes for an action (<see cref="KeysIn"/>), in the keymap in force.</summary>
    public static bool Presses(string id, Avalonia.Input.KeyEventArgs e) =>
        Keymap().ShortcutsFor(KeysIn(id)).Any(s => s.Second is null && s.First == Keystroke.From(e));

    /// <summary>The Visual Studio keymap with these shortcuts over it.</summary>
    public static Keymap Keymap(IReadOnlyDictionary<string, string[]> overrides) => Keymap(KeymapPreset.VisualStudio, overrides);

    /// <summary>The keymap settings choose, with their shortcuts for it over it.</summary>
    public static Keymap Keymap(Settings.AppSettings settings) => Keymap(settings.Keymap, settings.OwnShortcuts());

    /// <summary>A keymap with these shortcuts over the platform's; one that does not read is passed over.</summary>
    public static Keymap Keymap(KeymapPreset preset, IReadOnlyDictionary<string, string[]> overrides)
    {
        Keymap keymap = Base(preset);
        foreach ((string action, string[] shortcuts) in overrides)
        {
            List<Shortcut> parsed = [];
            foreach (string text in shortcuts)
            {
                try
                {
                    parsed.Add(Shortcut.Parse(text));
                }
                catch (FormatException)
                {
                }
            }
            keymap.Set(Now(action), parsed);
        }
        return keymap;
    }

    private static Keymap Base(KeymapPreset preset)
    {
        if (preset == KeymapPreset.VSCode)
        {
            return VSCode(DefaultKeymaps.For(preset));
        }
        Keymap keymap = DefaultKeymaps.ForPlatform();
        if (OperatingSystem.IsMacOS())
        {
            return keymap
                .Bind(Open, "meta O")
                .Bind(NewProject, "meta shift N")
                .Bind(Save, "meta S")
                .Bind(SaveAll, "meta shift S")
                .Bind(CloseTab, "meta W", "meta F4")
                .Bind(NextTab, "meta shift CLOSE_BRACKET", "control RIGHT")
                .Bind(PreviousTab, "meta shift OPEN_BRACKET", "control LEFT")
                .Bind(GoToFile, "meta shift T")
                .Bind(GoToLine, "meta G")
                .Bind(GoToSymbol, "meta shift alt T")
                .Bind(GoToEverything, "meta T")
                .Bind(FindAction, "meta shift A")
                .Bind(FindInFiles, "meta shift F")
                .Bind(RecentFiles, "control COMMA")
                .Bind(RecentChangedFiles, "meta shift COMMA")
                .Bind(LastEditLocation, "shift meta BACK_SPACE")
                .Bind(ToggleBookmark, "meta K , K", "meta K , meta K", "meta F2")
                .Bind(NextBookmark, "meta K , N", "meta K , meta N")
                .Bind(PreviousBookmark, "meta K , P", "meta K , meta P")
                .Bind(ShowBookmarks, "meta K , W", "meta K , meta W", "control BACK_QUOTE")
                .Bind(ReplaceInFiles, "meta shift H")
                .Bind(ShowSettings, "meta COMMA")
                .Bind(ShowNuGet, "meta 7", "meta shift 7")
                .Bind(FileStructure, "alt BACK_SLASH")
                .Bind(Find, "meta F")
                .Bind(Replace, "control H")
                .Bind(FindNext, "F3", "meta alt PAGE_DOWN")
                .Bind(FindPrevious, "shift F3", "meta alt PAGE_UP")
                .Bind(FocusExplorer, "meta 1", "meta alt L")
                // CopyPaths and CompareTwoFiles: $default's Ctrl+Shift+C and Ctrl+D as Mac OS X 10.5+ takes them; RevealIn has none.
                .Bind(CopyPath, "meta shift C")
                .Bind(CompareFiles, "meta D")
                .Bind(Build, "meta shift B")
                .Bind(RunProgram, "control F5")
                .Bind(Debug, "alt F5")
                .Bind(Stop, "shift F5")
                .Bind(Rerun, "alt shift F5")
                .Bind(ShowBuild, "meta 0")
                .Bind(ShowRun, "meta 4")
                .Bind(ShowFind, "meta 3")
                .Bind(ShowDebug, "meta 5")
                .Bind(ShowProblems, "meta 6")
                .Bind(ToggleBreakpoint, "F9")
                .Bind(ToggleBreakpointEnabled, "meta F9")
                .Bind(Resume, "F5")
                .Bind(Pause, "meta PAUSE", "shift meta P")
                .Bind(StepOver, "F10")
                .Bind(StepInto, "F11", "meta F11")
                .Bind(SmartStepInto, "shift F7")
                .Bind(StepOut, "shift F11")
                .Bind(RunToCursor, "meta F10")
                .Bind(SetNextStatement, "meta shift F10")
                .Bind(ApplyHotReloadChanges, "alt F10")
                .Bind(ExtendSelection, "meta alt RIGHT")
                .Bind(CallHierarchy, "control alt H")
                .Bind(TypeHierarchy, "meta E , H", "meta E , meta H")
                .Bind(ShrinkSelection, "meta alt LEFT")
                .Bind(ShowExecutionPoint, "alt MULTIPLY")
                .Bind(AttachToProcess, "meta alt P")
                .Bind(ReattachToProcess, "alt shift P")
                .Bind(Evaluate, "shift F9")
                .Bind(ShowContextMenu, "shift F10")
                .Bind(EditBreakpoint, "alt F9")
                .Bind(ViewBreakpoints, "meta alt B")
                .Bind(MuteBreakpoints, "meta alt shift F10")
                .Bind(RunTests, "meta U , R", "meta U , meta R")
                .Bind(DebugTests, "meta U , D", "meta U , meta D")
                .Bind(RerunTests, "meta U , U", "meta U , meta U")
                .Bind(RerunFailedTests, "meta U , F", "meta U , meta F")
                .Bind(ShowTests, "meta 8")
                .Bind(ShowStructure, "meta alt F", "control alt F")
                .Bind(MaximizeToolWindow, "meta shift QUOTE")
                .Bind(HideAllToolWindows, "meta shift F12")
                .Bind(HideToolWindow, "shift ESCAPE")
                .Bind(Commit, "meta alt shift K")
                .Bind(Push, "meta shift K")
                .Bind(NextChange, "shift meta alt N")
                .Bind(PreviousChange, "shift meta alt P")
                .Bind(RollbackLines, "meta alt Z")
                .Bind(DiffSettings, "meta shift D")
                .Bind(ShowGit, "meta 9")
                .Bind(ShowTerminal, "meta control 1")
                .Bind(UpdateProject, "meta alt W")
                .Bind(Back, "meta MINUS", "meta SUBTRACT")
                .Bind(Forward, "meta shift MINUS", "meta shift SUBTRACT")
                .Bind(Complete, "control SPACE", "control J")
                .Bind(QuickFix, "alt ENTER")
                .Bind(Rename, "meta R , R", "meta R , meta R")
                .Bind(GoToDeclaration, "F12")
                .Bind(GoToImplementation, "meta F12")
                .Bind(DerivedSymbols, "alt END")
                .Bind(GoToTypeDeclaration, "meta shift F11")
                .Bind(BaseSymbols, "control alt LEFT", "alt HOME")
                .Bind(FindUsages, "shift F12")
                .Bind(HighlightUsages, "shift alt F11")
                .Bind(Reformat, "meta alt ENTER", "meta K , F", "meta K , meta F", "meta K , D", "meta K , meta D")
                .Bind(ParameterInfo, "control P")
                .Bind(CodeCleanup, "meta E , C", "meta E , meta C", "meta E , F", "meta E , meta F")
                .Bind(RefactorThis, "meta shift R")
                .Bind(Generate, "meta N")
                .Bind(ExtractMethod, "meta R , M", "meta R , meta M")
                .Bind(IntroduceVariable, "meta R , V", "meta R , meta V")
                .Bind(Inline, "meta R , I", "meta R , meta I")
                .Bind(SurroundWith, "meta E , U", "meta E , meta U")
                // From Mac OS X 10.5+, which takes $default's Ctrl as Cmd.
                .Bind(CompleteStatement, "meta shift ENTER")
                .Bind(OptimizeUsings, "control alt O")
                // The keymap's third, Alt+Delete, is the editor's Delete to Word End there, which its parent gives it.
                .Bind(SafeDelete, "meta R , D", "meta R , meta D")
                .Bind(IntroduceField, "meta R , F", "meta R , meta F")
                .Bind(IntroduceParameter, "meta R , P", "meta R , meta P")
                .Bind(EncapsulateField, "meta R , E", "meta R , meta E")
                .Bind(ChangeSignature, "meta R , S", "meta R , meta S")
                .Bind(Move, "meta R , O", "meta R , meta O")
                .Bind(QuickDocumentation, "meta K , I", "meta K , meta I", "control shift F1")
                .Bind(NextError, "alt PAGE_DOWN")
                .Bind(PreviousError, "alt PAGE_UP")
                .Bind(OpenInNewWindow, "shift F4")
                .Bind(SoftWrap, "meta E , W", "meta E , meta W")
                .Bind(ShowWhitespaces, "meta R , W", "meta R , meta W")
                .Bind(NextProjectWindow, "meta alt BACK_QUOTE")
                .Bind(PreviousProjectWindow, "meta shift alt BACK_QUOTE");
        }
        return keymap
            .Bind(Open, "control O")
            .Bind(NewProject, "control shift N")
            .Bind(Save, "control S")
            .Bind(SaveAll, "control shift S")
            .Bind(CloseTab, "control F4")
            .Bind(NextTab, "alt RIGHT")
            .Bind(PreviousTab, "alt LEFT")
            .Bind(GoToFile, "control shift T")
            .Bind(GoToLine, "control G")
            .Bind(GoToSymbol, "control shift alt T")
            .Bind(GoToEverything, "control T")
            .Bind(FindAction, "control shift A")
            .Bind(FindInFiles, "control shift F")
            .Bind(RecentFiles, "control COMMA")
            .Bind(RecentChangedFiles, "control shift COMMA")
            .Bind(RecentLocations, "control shift E")
            .Bind(LastEditLocation, "shift control BACK_SPACE")
            .Bind(ToggleBookmark, "control K , K", "control K , control K")
            .Bind(NextBookmark, "control K , N", "control K , control N")
            .Bind(PreviousBookmark, "control K , P", "control K , control P")
            .Bind(ShowBookmarks, "control K , W", "control K , control W", "control BACK_QUOTE")
            .Bind(ReplaceInFiles, "control shift H")
            .Bind(ShowSettings, "control alt S")
            .Bind(ShowNuGet, "alt 7", "alt shift 7")
            .Bind(FileStructure, "alt BACK_SLASH")
            .Bind(Find, "control F")
            .Bind(Replace, "control H")
            .Bind(FindNext, "F3", "control alt PAGE_DOWN")
            .Bind(FindPrevious, "shift F3", "control alt PAGE_UP")
            .Bind(FocusExplorer, "alt 1", "control alt L")
            .Bind(CopyPath, "control shift C")
            .Bind(Build, "control shift B")
            .Bind(RunProgram, "control F5")
            .Bind(Debug, "alt F5")
            .Bind(Stop, "shift F5")
            .Bind(Rerun, "control shift F5")
            .Bind(ShowBuild, "alt 0")
            .Bind(ShowRun, "alt 4")
            .Bind(ShowFind, "alt 3")
            .Bind(ShowDebug, "alt 5")
            .Bind(ShowProblems, "alt 6")
            .Bind(ToggleBreakpoint, "F9")
            .Bind(ToggleBreakpointEnabled, "control F9")
            .Bind(Resume, "F5")
            .Bind(Pause, "control PAUSE", "shift control P")
            .Bind(StepOver, "F10")
            .Bind(StepInto, "F11")
            .Bind(SmartStepInto, "shift F7")
            .Bind(StepOut, "shift F11")
            .Bind(RunToCursor, "control F10")
            .Bind(SetNextStatement, "control shift F10")
            .Bind(ApplyHotReloadChanges, "alt F10")
            .Bind(ExtendSelection, "control W", "shift alt EQUALS", "control alt RIGHT")
            .Bind(CallHierarchy, "control alt H")
            .Bind(TypeHierarchy, "control E , H", "control E , control H")
            .Bind(ShrinkSelection, "control shift W", "shift alt MINUS", "control alt LEFT")
            .Bind(ShowExecutionPoint, "alt MULTIPLY")
            .Bind(AttachToProcess, "control alt P")
            .Bind(ReattachToProcess, "alt shift P")
            .Bind(Evaluate, "shift F9")
            .Bind(ShowContextMenu, "shift F10", "CONTEXT_MENU")
            .Bind(EditBreakpoint, "alt F9")
            .Bind(ViewBreakpoints, "control alt B")
            .Bind(MuteBreakpoints, "control alt shift F10")
            .Bind(RunTests, "control U , R", "control U , control R")
            .Bind(DebugTests, "control U , D", "control U , control D")
            .Bind(RerunTests, "control U , U", "control U , control U")
            .Bind(RerunFailedTests, "control U , F", "control U , control F")
            .Bind(ShowTests, "alt 8")
            .Bind(ShowStructure, "control alt F")
            .Bind(MaximizeToolWindow, "control shift QUOTE")
            .Bind(HideAllToolWindows, "control shift F12")
            .Bind(HideToolWindow, "shift ESCAPE")
            .Bind(Commit, "control alt K")
            .Bind(Push, "control shift K")
            .Bind(NextChange, "shift control alt N")
            .Bind(PreviousChange, "shift control alt P")
            .Bind(RollbackLines, "control alt Z")
            .Bind(DiffSettings, "control shift D")
            .Bind(ShowGit, "alt 9")
            .Bind(ShowTerminal, "control alt 1")
            .Bind(UpdateProject, "control alt W")
            .Bind(Back, "control MINUS", "control SUBTRACT")
            .Bind(Forward, "control shift MINUS", "control shift SUBTRACT")
            .Bind(Complete, "control SPACE", "control J")
            .Bind(QuickFix, "alt ENTER", "control PERIOD")
            .Bind(Rename, "control R , R", "control R , control R")
            .Bind(GoToDeclaration, "F12")
            .Bind(GoToImplementation, "control F12")
            .Bind(DerivedSymbols, "alt END")
            .Bind(GoToTypeDeclaration, "control shift F11")
            .Bind(BaseSymbols, "alt HOME")
            .Bind(FindUsages, "shift F12")
            .Bind(HighlightUsages, "shift alt F11")
            .Bind(Reformat, "control alt ENTER", "control K , F", "control K , control F", "control K , D", "control K , control D")
            .Bind(ParameterInfo, "shift control SPACE")
            .Bind(CodeCleanup, "control E , C", "control E , control C", "control E , F", "control E , control F")
            .Bind(RefactorThis, "control shift R")
            .Bind(Generate, "alt INSERT", "control N")
            .Bind(ExtractMethod, "control R , M", "control R , control M")
            .Bind(IntroduceVariable, "control R , V", "control R , control V")
            .Bind(Inline, "control R , I", "control R , control I")
            .Bind(SurroundWith, "control E , U", "control E , control U", "control K , S", "control K , control S")
            .Bind(CompleteStatement, "control shift ENTER")
            .Bind(OptimizeUsings, "control R , G", "control R , control G", "control alt O")
            .Bind(SafeDelete, "control R , D", "control R , control D", "alt DELETE")
            .Bind(IntroduceField, "control R , F", "control R , control F")
            .Bind(IntroduceParameter, "control R , P", "control R , control P")
            .Bind(EncapsulateField, "control R , E", "control R , control E")
            .Bind(ChangeSignature, "control R , S", "control R , control S")
            .Bind(Move, "control R , O", "control R , control O")
            .Bind(QuickDocumentation, "control K , I", "control K , control I", "control shift F1")
            .Bind(NextError, "alt PAGE_DOWN")
            .Bind(PreviousError, "alt PAGE_UP")
            .Bind(OpenInNewWindow, "shift F4")
            .Bind(SoftWrap, "control E , W", "control E , control W")
            .Bind(ShowWhitespaces, "control R , W", "control R , control W")
            .Bind(NextProjectWindow, "control alt CLOSE_BRACKET")
            .Bind(PreviousProjectWindow, "control alt OPEN_BRACKET");
    }

    /// <summary>Rider's VSCode and VSCode OSX keymaps, with what they take from $default and Mac OS X 10.5+. A key IntelliJ
    /// gives two actions, told apart by where it is pressed, goes to one: F5 to Resume (Debug with nothing debugged), Ctrl+D
    /// to Select Next Occurrence (not Show Diff), Ctrl+Shift+F8 to View Breakpoints; else the keymap's own over the one it
    /// inherits: Ctrl+; L to Rerun, Ctrl+Shift+W to Close Solution, macOS's Ctrl+Shift+R to Refactor This and Alt+Shift+F5
    /// to Previous Change (not Run All Tests, Shrink Selection, Run Tests and Attach to Process).</summary>
    private static Keymap VSCode(Keymap keymap)
    {
        if (OperatingSystem.IsMacOS())
        {
            return keymap
                .Bind(Open, "meta O")
                // VS Code's Ctrl+W, Switch Window; Cmd+Q is the application menu's Quit, as in the Visual Studio keymap.
                .Bind(NextProjectWindow, "control W")
                .Bind(PreviousProjectWindow, "meta alt shift BACK_QUOTE")
                .Bind(Save, "meta S")
                .Bind(SaveAll, "meta alt S")
                .Bind(CloseTab, "meta W")
                .Bind(NextTab, "meta shift CLOSE_BRACKET", "meta alt RIGHT")
                .Bind(PreviousTab, "meta shift OPEN_BRACKET", "meta alt LEFT")
                .Bind(GoToFile, "meta P")
                .Bind(GoToLine, "control G")
                .Bind(GoToSymbol, "meta T")
                .Bind(FileStructure, "meta alt O", "meta shift O")
                .Bind(FindAction, "F1", "meta shift P", "meta shift A")
                .Bind(FindInFiles, "meta shift F")
                .Bind(RecentFiles, "meta E")
                .Bind(LastEditLocation, "meta K , meta Q")
                .Bind(ReplaceInFiles, "meta shift H")
                .Bind(ShowSettings, "meta COMMA")
                .Bind(ShowNuGet, "meta alt 7")
                .Bind(Find, "meta F")
                .Bind(Replace, "meta alt F")
                .Bind(FindNext, "meta G", "F3", "meta K , meta D")
                // Its third, Cmd+Shift+F5, is VS Code's Restart, Rerun here.
                .Bind(FindPrevious, "meta shift G", "shift F3")
                .Bind(FocusExplorer, "meta shift E")
                .Bind(CopyPath, "meta alt C", "meta K , P")
                .Bind(RevealInFileManager, "meta K , R", "meta alt R")
                .Bind(Build, "meta shift B")
                .Bind(RunProgram, "control F5")
                .Bind(Stop, "shift F5")
                .Bind(Rerun, "meta SEMICOLON , L", "meta shift F5")
                .Bind(ShowRun, "meta shift U")
                .Bind(ShowProblems, "meta shift M")
                .Bind(ShowFind, "meta 3")
                .Bind(ShowDebug, "meta shift D", "meta shift Y")
                .Bind(HideToolWindow, "meta J")
                .Bind(Back, "control MINUS")
                .Bind(Forward, "control shift MINUS")
                .Bind(Complete, "meta I", "alt ESCAPE", "control SPACE")
                .Bind(QuickFix, "meta PERIOD", "alt ENTER")
                .Bind(Rename, "F2")
                .Bind(GoToDeclaration, "F12")
                .Bind(GoToTypeDeclaration, "control shift B")
                .Bind(DerivedSymbols, "meta F12")
                .Bind(FindUsages, "alt shift F12")
                .Bind(HighlightUsages, "meta shift F7")
                .Bind(Reformat, "alt shift F")
                .Bind(ReformatWithPrettier, "meta alt shift P")
                .Bind(RefactorThis, "control shift R")
                .Bind(Generate, "control ENTER")
                .Bind(ExtractMethod, "meta alt M")
                .Bind(IntroduceVariable, "meta alt V")
                .Bind(Inline, "meta alt N")
                .Bind(ParameterInfo, "meta shift SPACE")
                .Bind(SurroundWith, "meta alt J")
                .Bind(OptimizeUsings, "alt shift O")
                .Bind(IntroduceParameter, "meta alt P")
                .Bind(ChangeSignature, "meta F6")
                .Bind(QuickDocumentation, "meta K , meta I")
                .Bind(NextError, "alt F8")
                .Bind(PreviousError, "shift F8", "alt shift F8")
                .Bind(ToggleBreakpoint, "F9")
                .Bind(Resume, "F5")
                .Bind(Pause, "F6")
                .Bind(StepOver, "F10")
                .Bind(StepInto, "F11", "F7")
                .Bind(SmartStepInto, "shift F7")
                .Bind(StepOut, "shift F11")
                .Bind(RunToCursor, "alt F9")
                .Bind(SetNextStatement, "meta alt shift F9")
                .Bind(ExtendSelection, "meta control shift RIGHT", "control shift RIGHT")
                .Bind(CallHierarchy, "alt shift H")
                .Bind(TypeHierarchy, "control H")
                .Bind(ShrinkSelection, "meta control shift LEFT")
                .Bind(ShowExecutionPoint, "alt F10")
                .Bind(ReattachToProcess, "meta alt shift F5")
                .Bind(ShowContextMenu, "shift F10")
                .Bind(RunTests, "meta SEMICOLON , R", "meta SEMICOLON , meta R")
                .Bind(DebugTests, "meta SEMICOLON , D", "meta SEMICOLON , meta D", "control shift D")
                .Bind(RunAllTests, "meta SEMICOLON , meta L")
                .Bind(RunTestsUntilFailure, "meta SEMICOLON , W", "meta SEMICOLON , meta W")
                .Bind(RerunTests, "meta SEMICOLON , T", "meta SEMICOLON , meta T")
                .Bind(RerunFailedTests, "meta SEMICOLON , F", "meta SEMICOLON , meta F")
                .Bind(StopTests, "meta SEMICOLON , S", "meta SEMICOLON , meta S")
                .Bind(ShowTests, "meta alt 8")
                .Bind(ShowStructure, "meta 7")
                .Bind(SplitRight, "meta BACK_SLASH")
                .Bind(MoveToOppositeGroup, "meta control RIGHT")
                .Bind(OpenInNewWindow, "meta K , O")
                .Bind(MarkdownPreview, "meta K , V")
                .Bind(ReopenClosedTab, "meta shift T")
                .Bind(PinTab, "meta K , shift ENTER")
                .Bind(CloseOtherTabs, "meta alt T", "meta K , U")
                .Bind(CloseAllTabs, "meta K , meta W")
                .Bind(SoftWrap, "alt Z")
                .Bind(IncreaseFontSize, "meta EQUALS")
                .Bind(DecreaseFontSize, "meta MINUS")
                .Bind(CloseSolution, "meta K , F", "meta shift W")
                .Bind(ShowTerminal, "control BACK_QUOTE")
                .Bind(MaximizeToolWindow, "meta shift QUOTE")
                .Bind(ViewBreakpoints, "meta shift F8")
                .Bind(ShowCommit, "meta 0")
                .Bind(NextChange, "alt F3", "alt F5")
                .Bind(PreviousChange, "alt shift F3", "alt shift F5")
                .Bind(RollbackLines, "meta alt Z")
                .Bind(ShowGit, "control shift G")
                .Bind(RecentChanges, "alt shift C");
        }
        return keymap
            .Bind(Open, "control O")
            // VS Code's Ctrl+W closes the editor here, its Switch Window macOS's Ctrl+W; Next Project Window has no key.
            .Bind(Save, "control S")
            .Bind(SaveAll, "control K , S")
            .Bind(CloseTab, "control W")
            .Bind(NextTab, "control PAGE_DOWN")
            .Bind(PreviousTab, "control PAGE_UP")
            .Bind(GoToFile, "control P")
            .Bind(GoToLine, "control G")
            .Bind(GoToSymbol, "control T")
            .Bind(GoToEverything, "F1", "control shift A")
            .Bind(FileStructure, "control shift O")
            .Bind(FindAction, "control shift P")
            .Bind(FindInFiles, "control shift F")
            .Bind(RecentFiles, "control E")
            .Bind(LastEditLocation, "control K , control Q")
            .Bind(ReplaceInFiles, "control shift H")
            .Bind(ShowSettings, "control COMMA")
            .Bind(ShowNuGet, "alt shift 7")
            .Bind(Find, "control F")
            .Bind(Replace, "control H")
            .Bind(FindNext, "F3", "control K , control D")
            .Bind(FindPrevious, "shift F3", "control shift F3")
            .Bind(FocusExplorer, "control shift E")
            .Bind(CopyPath, "shift alt C", "control K , P")
            .Bind(RevealInFileManager, "shift alt R", "control K , R")
            .Bind(Build, "control shift B")
            .Bind(RunProgram, "control F5")
            .Bind(Stop, "shift F5")
            .Bind(Rerun, "control SEMICOLON , L", "control shift F5")
            .Bind(ShowRun, "control shift U")
            .Bind(ShowProblems, "control shift M")
            .Bind(ShowFind, "alt 3")
            .Bind(ShowDebug, "control shift Y", "control shift D")
            .Bind(HideToolWindow, "control J")
            .Bind(Back, "alt LEFT")
            .Bind(Forward, "alt RIGHT")
            .Bind(Complete, "control I", "control SPACE")
            .Bind(QuickFix, "control PERIOD")
            .Bind(Rename, "F2")
            .Bind(GoToDeclaration, "F12")
            .Bind(DerivedSymbols, "control F12")
            .Bind(FindUsages, "alt shift F12")
            .Bind(HighlightUsages, "control shift F7")
            .Bind(Reformat, "alt shift F")
            .Bind(ReformatWithPrettier, "control alt shift P")
            .Bind(RefactorThis, "control shift R")
            .Bind(Generate, "alt INSERT")
            .Bind(ExtractMethod, "control alt M")
            .Bind(IntroduceVariable, "control alt V")
            .Bind(Inline, "control alt N")
            .Bind(ParameterInfo, "control shift SPACE")
            .Bind(SurroundWith, "control alt J")
            .Bind(OptimizeUsings, "alt shift O")
            .Bind(SafeDelete, "alt DELETE")
            .Bind(IntroduceField, "control alt F")
            .Bind(IntroduceParameter, "control alt P")
            .Bind(ChangeSignature, "control F6")
            .Bind(QuickDocumentation, "control K , control I")
            .Bind(NextError, "alt F8")
            .Bind(PreviousError, "alt shift F8", "shift F8")
            .Bind(ToggleBreakpoint, "F9")
            .Bind(Resume, "F5")
            .Bind(Pause, "F6")
            .Bind(StepOver, "F10")
            .Bind(StepInto, "F11")
            .Bind(SmartStepInto, "shift F7")
            .Bind(StepOut, "shift F11")
            .Bind(RunToCursor, "alt F9")
            .Bind(SetNextStatement, "control alt shift F9")
            .Bind(ExtendSelection, "alt shift RIGHT")
            .Bind(CallHierarchy, "alt shift H")
            .Bind(ShowExecutionPoint, "alt F10")
            .Bind(AttachToProcess, "control alt F5")
            .Bind(ReattachToProcess, "control alt shift F5")
            .Bind(ShowContextMenu, "shift F10", "CONTEXT_MENU")
            .Bind(RunTests, "control SEMICOLON , R", "control SEMICOLON , control R", "control shift F10")
            .Bind(DebugTests, "control SEMICOLON , D", "control SEMICOLON , control D")
            .Bind(RunAllTests, "control SEMICOLON , control L")
            .Bind(RunTestsUntilFailure, "control SEMICOLON , W", "control SEMICOLON , control W")
            .Bind(RerunTests, "control SEMICOLON , T", "control SEMICOLON , control T")
            .Bind(RerunFailedTests, "control SEMICOLON , F", "control SEMICOLON , control F")
            .Bind(StopTests, "control SEMICOLON , S", "control SEMICOLON , control S")
            .Bind(ShowTests, "alt shift 8")
            .Bind(ShowStructure, "alt 7")
            .Bind(SplitRight, "control BACK_SLASH")
            .Bind(MoveToOppositeGroup, "control alt RIGHT")
            .Bind(OpenInNewWindow, "control K , O")
            .Bind(MarkdownPreview, "control K , V")
            .Bind(ReopenClosedTab, "control shift T")
            .Bind(PinTab, "control K , shift ENTER")
            .Bind(CloseOtherTabs, "control K , U")
            .Bind(CloseAllTabs, "control K , control W")
            .Bind(SoftWrap, "alt Z")
            .Bind(IncreaseFontSize, "control EQUALS")
            .Bind(DecreaseFontSize, "control MINUS")
            .Bind(CloseSolution, "control K , F", "control shift W")
            .Bind(ShowTerminal, "control BACK_QUOTE")
            .Bind(MaximizeToolWindow, "control shift QUOTE")
            .Bind(ViewBreakpoints, "control shift F8")
            .Bind(ShowCommit, "alt 0")
            .Bind(Branches, "control shift BACK_QUOTE")
            .Bind(NextChange, "alt F5")
            .Bind(PreviousChange, "alt shift F5")
            .Bind(RollbackLines, "control alt Z")
            .Bind(ShowGit, "control shift G");
    }
}
