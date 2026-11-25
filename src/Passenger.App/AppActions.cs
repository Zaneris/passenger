using Passenger.Editor;

namespace Passenger.App;

/// <summary>The ids of the IDE's own actions, beside the editor's (<see cref="EditorActions"/>), and the keymaps of each
/// platform that bind both by id, keys and mouse gestures: Rider's Visual Studio keymaps for these too, its VS Code
/// keymaps, IntelliJ IDEA's and its Emacs keymap. Each action is declared by its feature (<see cref="IdeAction"/>, in the window's <see
/// cref="ActionRegistry"/>).</summary>
[IdentifierHome]
public static class AppActions
{
    /// <summary>A solution or project to open as the workspace: Rider's Open Solution (RiderOpenSolution), Ctrl+Shift+O in
    /// every keymap but VS Code's on Windows, which gives it Ctrl+K, Ctrl+O.</summary>
    public const string OpenSolution = "app.openSolution";

    /// <summary>A folder to open: the system's panel takes files or folders, not both. Rider's keymaps have no keys for
    /// it.</summary>
    public const string OpenFolder = "app.openFolder";

    /// <summary>A file to open in the editor, never as the workspace: IntelliJ's Open (OpenFile), which Rider's Visual Studio
    /// and VS Code keymaps give Ctrl+O (Cmd+O on the Mac) and IntelliJ IDEA's none.</summary>
    public const string OpenFile = "app.openFile";

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

    // The indent widget's menu, which no keys open, and its conversions of a file's lines.
    public const string FileIndentation = "app.fileIndentation";
    public const string ConvertIndentationToTabs = "app.convertIndentation.tabs";
    public const string ConvertIndentationToSpaces = "app.convertIndentation.spaces";

    // Rider's ChangeTargetFramework (Target Framework), the status bar's context widget, which its keymaps give no keys.
    public const string TargetFramework = "app.code.targetFramework";
    public const string Save = "app.save";
    public const string SaveAll = "app.saveAll";
    public const string CloseTab = "app.closeTab";
    public const string NextTab = "app.nextTab";
    public const string PreviousTab = "app.previousTab";

    // Rider's Switcher: Ctrl+Tab, and Ctrl+Shift+Tab going the other way, in $default's and Mac OS X 10.5+'s own lists,
    // which every keymap here takes; Control on the Mac too.
    public const string Switcher = "app.switcher";
    public const string SwitcherBackward = "app.switcherBackward";
    public const string GoToFile = "app.goToFile";
    public const string GoToLine = "app.goToLine";
    public const string GoToSymbol = "app.goToSymbol";
    public const string GoToEverything = "app.goToEverything";
    public const string FileStructure = "app.fileStructure";

    // Rider's ShowNavBar (Jump to Navigation Bar): Ctrl+F2 in its Visual Studio keymap, Ctrl+Shift+; (Cmd+Shift+; on the
    // Mac) in its VS Code keymaps, Alt+Home in $default's and Mac OS X 10.5+'s. Its Visual Studio OSX keymap gives its
    // Cmd+F2 to Toggle Bookmark too, which keeps it here, and Mac OS X 10.5+'s Cmd+Up is the editor's Text Start here.
    public const string JumpToNavigationBar = "app.jumpToNavigationBar";

    // Rider's GotoRelated (Related Symbol), here a XAML or Razor file's code-behind and back: its Visual Studio keymaps'
    // Ctrl+Alt+F7 and Cmd+Shift+F7, its VS Code keymaps' (from their parents) Ctrl+Alt+Home and Ctrl+Cmd+Up.
    public const string GoToRelated = "app.goToRelated";
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

    // Get Memory Snapshot of the program being profiled: no key of Rider's.
    public const string MemorySnapshot = "app.profiler.memorySnapshot";

    // The Live Visual Tree of an app being debugged: no key of Rider's or Visual Studio's.
    public const string ShowLiveTree = "app.showLiveTree";
    public const string SelectElement = "app.liveTree.selectElement";
    public const string SelectIn3D = "app.liveTree.selectIn3D";

    // A Godot game's loop paused and stepped a frame from the Live Visual Tree, the debugger not breaking in: no key of Rider's.
    public const string PauseGameLoop = "app.godot.pauseLoop";
    public const string NextFrame = "app.godot.nextFrame";

    // The Game window over a Godot game drawn in Passenger, and the keys handed to the game drawn there and taken back
    // (heard by Passenger's helper in the game while it has them): no key of Rider's; the second Passenger's own.
    public const string ShowGame = "app.godot.showGame";
    public const string GameKeys = "app.godot.gameKeys";

    // A debugged Godot game's audio muted from the Debug window, kept with the folder: no key of Rider's.
    public const string MuteGameAudio = "app.godot.muteAudio";

    // Passenger made the script editor of the Godot found, in its editor settings: no key of Rider's.
    public const string GodotScriptEditor = "app.godot.scriptEditor";

    // C# script templates for Godot's Attach Script dialog written in the project's style: no key of Rider's.
    public const string GodotScriptTemplates = "app.godot.scriptTemplates";

    // Godot's editor on the scene at hand, at its node, through Passenger's addon where it runs: no key of Rider's.
    public const string GodotOpenInEditor = "app.godot.openInEditor";

    // Passenger's addon written into a Godot project, so Godot's Play builds and debugs here: no key of Rider's.
    public const string GodotAddon = "app.godot.addon";

    // A Godot project's page over its project.godot: no key of Rider's.
    public const string GodotProject = "app.godot.project";

    // The Godot found's export templates installed from a .tpz the person picks: no key of Rider's.
    public const string InstallGodotTemplates = "app.godot.installTemplates";

    // The open folder trusted, so its code runs: no key of Rider's.
    public const string TrustFolder = "app.trustFolder";

    // The .NET SDK installed into the person's own folder, and put on their shell's PATH: no key of Rider's.
    public const string InstallDotnetSdk = "app.dotnet.install";
    public const string AddDotnetToPath = "app.dotnet.addToPath";

    // The passenger-ide command on macOS, linked in ~/.local/bin and taken away again: no keys of Rider's.
    public const string AddCommand = "app.command.add";
    public const string RemoveCommand = "app.command.remove";

    // What a mobile project needs: the .NET workloads it lacks, and the Android SDK and JDK through the Android workload's installer: no keys of Rider's.
    public const string InstallWorkloads = "app.dotnet.installWorkloads";
    public const string InstallAndroidDependencies = "app.android.install";

    // The edits a mobile project's heads need to build here, made to its project and Info.plist: no key of Rider's.
    public const string MakeMobileEdits = "app.mobile.makeEdits";

    // A Mac Catalyst head's Debug build kept whole of .NET's own assemblies, for Passenger's inspector and update handlers' hook: no key of Rider's.
    public const string KeepAssembliesWhole = "app.mobile.keepAssembliesWhole";

    // The Endpoints window over the folder's .http files, and Test of the request chosen there: no key of Rider's.
    public const string ShowEndpoints = "app.showEndpoints";
    public const string TestEndpoint = "app.endpoints.test";

    /// <summary>Requests for a web project's .http file from its app's OpenAPI document: no key of Rider's.</summary>
    public const string GenerateOpenApiRequests = "app.endpoints.openApiRequests";

    /// <summary>Names a web project's OpenAPI document where its code names none: no key of Rider's.</summary>
    public const string NameOpenApiDocument = "app.endpoints.openApiDocument";

    // The Requests window over the running web app's live requests and their database commands: no key of Rider's.
    public const string ShowRequests = "app.showRequests";
    public const string RequestsCommandConsole = "app.requests.commandConsole";

    // The Migrations window over the folder's EF Core contexts, its commands, and the fix of what keeps dotnet ef from
    // working that it shows first: no key of Rider's.
    public const string ShowMigrations = "app.showMigrations";
    // The Database window: its connections, their explorer, a table's data and query consoles.
    public const string ShowDatabase = "app.showDatabase";
    public const string DatabaseRefresh = "app.database.refresh";
    public const string DatabaseConnect = "app.database.connect";
    public const string DatabaseAdd = "app.database.add";
    public const string DatabaseConnectionEdit = "app.database.connectionEdit";
    public const string DatabaseConnectionRename = "app.database.connectionRename";
    public const string DatabaseConnectionRemove = "app.database.connectionRemove";
    public const string DatabaseOpenTable = "app.database.openTable";
    public const string DatabaseCancel = "app.database.cancel";
    // An object's DDL: Show DDL on Go to Declaration's keys in the Database window, as Rider's Jdbc.OpenEditor.DDL takes them
    // (KeysIn); its scripts into a console, no key of Rider's.
    public const string DatabaseShowDdl = "app.database.showDdl";
    public const string DatabaseScriptCreate = "app.database.scriptCreate";
    public const string DatabaseScriptAlter = "app.database.scriptAlter";
    public const string DatabaseScriptDrop = "app.database.scriptDrop";

    // A console's Run Statement, Rider's Console.Execute.Multiline: Ctrl+Enter, the Mac's Cmd+Enter, which Visual Studio's and
    // VS Code's keymaps give the editor's Start New Line Before and Start New Line, so a SQL file takes theirs (KeysIn).
    public const string DatabaseConsole = "app.database.console";
    public const string DatabaseRun = "app.database.run";
    // Stop Statement, Rider's Console.Jdbc.Cancel, on Stop's keys in a console running a statement (KeysIn).
    public const string DatabaseRunStop = "app.database.runStop";
    // A SQL file run whole: no key of Rider's. The console's connection's history: Rider's Console.History.Browse, Ctrl+Alt+E.
    public const string DatabaseRunFile = "app.database.runFile";
    public const string DatabaseHistory = "app.database.history";
    // A console's statement explained as a plan's tree: Rider's Console.Jdbc.ExplainPlan, no key of its own.
    public const string DatabaseExplain = "app.database.explain";
    // A console's transaction in manual commit: Rider's Console.Transaction.Commit, $default's Ctrl+Alt+Shift+Enter.
    public const string DatabaseCommit = "app.database.commit";
    public const string DatabaseRollback = "app.database.rollback";
    // A table's changes not yet written: Submit on Run Statement's keys, Add Row on Generate's, Delete Rows on Delete Line's
    // and Revert on Rollback's, as Rider's data grid takes them (KeysIn).
    public const string DatabaseSubmit = "app.database.submit";
    public const string DatabaseRevert = "app.database.revert";
    public const string DatabaseAddRow = "app.database.addRow";
    public const string DatabaseDeleteRows = "app.database.deleteRows";
    // A Redis key's time to live set or taken away: no key of Rider's.
    public const string DatabaseExpire = "app.database.expire";
    // A connection's AWS profile signed in through IAM Identity Center: no key of Rider's.
    public const string DatabaseSignIn = "app.database.signIn";
    // A connection marked read-only or production: no key of Rider's.
    public const string DatabaseReadOnly = "app.database.readOnly";
    public const string DatabaseProduction = "app.database.production";
    // A table's or a result's rows written to a file, a file's read into a table, and a grid's chosen cells copied in a
    // format: no key of Rider's for any.
    public const string DatabaseExport = "app.database.export";
    public const string DatabaseImport = "app.database.import";
    // A schema's tables drawn as a diagram, and two schemas compared into a script: no key of Rider's for either.
    public const string DatabaseDiagram = "app.database.diagram";
    public const string DatabaseCompare = "app.database.compare";
    public const string CopyAsCsv = "app.table.copyAsCsv";
    public const string CopyAsTsv = "app.table.copyAsTsv";
    public const string CopyAsJson = "app.table.copyAsJson";
    public const string CopyAsInserts = "app.table.copyAsInserts";
    public const string MigrationsRefresh = "app.migrations.refresh";
    public const string AddMigration = "app.migrations.add";
    public const string RemoveLastMigration = "app.migrations.removeLast";
    public const string UpdateDatabase = "app.migrations.update";
    public const string UpdateDatabaseTo = "app.migrations.updateTo";
    public const string ScriptMigrations = "app.migrations.script";
    public const string BundleMigrations = "app.migrations.bundle";
    public const string MigrationsFix = "app.migrations.fix";
    public const string MigrationsEnvironment = "app.migrations.environment";
    public const string MigrationsDatabase = "app.migrations.database";

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
    public const string ScoutBaseImages = "app.containers.scoutBaseImages";
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
        ["app.open"] = OpenSolution,
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

    // The file shown chosen in the Explorer: Rider's LocateInSolutionView in its Visual Studio keymaps (its own descriptor's
    // Alt+Shift+L), SelectInProjectView in its VS Code keymaps (Ctrl+K, E; Cmd+K, E on macOS), no key in IntelliJ IDEA's.
    public const string SelectOpenedFile = "app.explorer.selectOpenedFile";

    // The Explorer switched between the solution's projects and the folder's files on disk: no key of Rider's.
    public const string SwitchExplorerView = "app.explorer.switchView";

    // The Explorer's own actions, on the row its menu opened on or the one chosen while it has the keys: NewFile, NewDir,
    // Class/Interface, the solution's own, Add Reference, Docker Support and Compose, Aspire Orchestration, NuGet, Manage
    // User Secrets (no keys of Rider's), Edit Project File, Unload and Reload Project, Compare Files (CompareTwoFiles, by
    // Duplicate's keys: KeysIn), Copy Path (CopyPaths), Reveal in Finder (RevealIn) and Open in Terminal. Rename and
    // Delete are Rename's and the editor's Delete, one action wherever they are pressed.
    public const string NewFile = "app.explorer.newFile";
    public const string NewFolder = "app.explorer.newFolder";
    public const string NewType = "app.explorer.newType";

    // New Item: dotnet new's item templates and Passenger's own, by Rider's New (NewElement) keys where Generate does not take
    // them (Visual Studio's keymaps; VS Code's give them all to Generate).
    public const string NewItem = "app.explorer.newItem";

    // A folder's .editorconfig, .gitignore and .gitattributes made, or opened where one is: no keys of Rider's.
    public const string NewEditorConfig = "app.explorer.newEditorConfig";
    public const string NewGitIgnore = "app.explorer.newGitIgnore";
    public const string NewGitAttributes = "app.explorer.newGitAttributes";
    public const string NewSolutionFolder = "app.explorer.newSolutionFolder";
    public const string AddExistingProject = "app.explorer.addExistingProject";
    public const string AddExistingItem = "app.explorer.addExistingItem";
    public const string AddReference = "app.explorer.addReference";
    public const string AddDockerSupport = "app.explorer.addDockerSupport";
    public const string AddDockerCompose = "app.explorer.addDockerCompose";
    public const string AddAspireOrchestration = "app.explorer.addAspireOrchestration";
    public const string ManageNuGet = "app.explorer.manageNuGet";
    public const string ManageNuGetForSolution = "app.explorer.manageNuGetForSolution";
    public const string ManageUserSecrets = "app.explorer.manageUserSecrets";
    public const string EditProjectFile = "app.explorer.editProjectFile";
    public const string UnloadProject = "app.explorer.unloadProject";
    public const string ReloadProject = "app.explorer.reloadProject";
    public const string IncludeInProject = "app.explorer.includeInProject";
    public const string ExcludeFromProject = "app.explorer.excludeFromProject";
    public const string CompareFiles = "app.explorer.compareFiles";
    public const string CopyPath = "app.explorer.copyPath";
    public const string RevealInFileManager = "app.explorer.reveal";
    public const string OpenInTerminal = "app.explorer.openInTerminal";

    // An HTML file opened in the person's browser (Rider's WebOpenInAction): its plugin's Alt+F2 for $default, which the
    // Mac keymaps and VS Code's take from it, and Ctrl+Alt+F2 for Visual Studio's.
    public const string OpenInBrowser = "app.explorer.openInBrowser";

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

    // Run of a Godot game from the scene open (or the scene a script open runs in): no key of Rider's.
    public const string RunCurrentScene = "app.run.currentScene";

    // One more copy of a Godot game run or debugged beside its copies running now: no key of Rider's.
    public const string RunAnotherCopy = "app.run.anotherCopy";
    public const string DebugAnotherCopy = "app.debug.anotherCopy";

    // Run, Debug or Profile again in a tab of the Terminal window, of a program whose output in the Run or Debug window said
    // it needs a terminal: no keys of Rider's.
    public const string RunInTerminal = "app.run.inTerminal";
    public const string DebugInTerminal = "app.debug.inTerminal";
    public const string ProfileInTerminal = "app.profile.inTerminal";

    // Run, Debug and Profile of the program at hand (a gutter icon's, an Explorer row's, the open file's): no keys, Rider's
    // RunClass key being Attach to Process's in its Visual Studio keymap.
    public const string RunHere = "app.run.here";
    public const string DebugHere = "app.debug.here";
    public const string ProfileHere = "app.profile.here";
    public const string Stop = "app.stop";
    public const string StopAll = "app.stopAll";
    public const string Rerun = "app.rerun";
    public const string EditConfigurations = "app.editConfigurations";

    // Where the chosen configuration's app runs (a device, an emulator or a simulator), the widget beside the configuration's.
    public const string ChooseRunTarget = "app.chooseRunTarget";

    // The build configuration and platform every build takes, the widget before the run configuration's: no keys of Rider's.
    public const string ChooseBuildConfiguration = "app.chooseBuildConfiguration";

    // One of the folder's own run configurations saved as a launch profile of its project: no keys of Rider's.
    public const string SaveAsLaunchProfile = "app.saveAsLaunchProfile";
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
    public const string AspireStats = "app.aspire.stats";
    public const string AspireGraph = "app.aspire.graph";
    public const string AspireLogs = "app.aspire.logs";
    public const string AspireTraces = "app.aspire.traces";
    public const string AspireAppHostConsole = "app.aspire.appHostConsole";
    public const string AspireOpenLink = "app.aspire.openLink";
    public const string AspireSource = "app.aspire.source";
    public const string AspireContainer = "app.aspire.container";
    public const string ShowProblems = "app.showProblems";
    public const string ShowFind = "app.showFind";
    public const string ToggleSolutionAnalysis = "app.toggleSolutionAnalysis";

    // The C# language server started again in place of the one running, or of one left stopped for ending too often: no
    // key of Rider's.
    public const string RestartCSharpServer = "app.restartCSharpServer";
    public const string ShowDebug = "app.showDebug";
    public const string HideToolWindow = "app.hideToolWindow";
    public const string CloseToolWindow = "app.closeToolWindow";
    public const string DockToolWindow = "app.dockToolWindow";
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

    // Rider's ShowUsages, its popup at the caret: Shift+Alt+F12 in its Visual Studio keymaps, Shift+F12 in VS Code's, $default's
    // Ctrl+Alt+F7 (Cmd+Alt+F7 on the Mac); none of them gives it a gesture.
    public const string ShowUsages = "app.code.showUsages";
    public const string HighlightUsages = "app.code.highlightUsages";
    public const string Reformat = "app.code.reformat";
    public const string CodeCleanup = "app.code.cleanup";

    // Passenger's own, for a GDScript: the style guide's Code order, kept out of Reformat Code; no keymap gives it keys.
    public const string OrderScriptParts = "app.code.orderScriptParts";

    // Reqnroll's Find Unused Step Definitions, which its Visual Studio extension gives no keys either.
    public const string FindUnusedStepDefinitions = "app.code.findUnusedStepDefinitions";

    // The Step Library window over the folder's step definitions, and Insert Step into Feature of the one chosen there: no
    // keys of Rider's.
    public const string ShowStepLibrary = "app.showStepLibrary";
    public const string InsertStep = "app.steps.insert";

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

    // Rider's MethodOverloadSwitchDown and MethodOverloadSwitchUp: Parameter Info's next and previous overload, on the keys
    // the editor's own action has there (KeysIn).
    public const string NextOverload = "app.code.nextOverload";
    public const string PreviousOverload = "app.code.previousOverload";

    // The completion list's filters by the kind of member, which no keymap of Rider's has: each on Alt and its badge's
    // letter, taken only while the list shows them (InEditor), the key typing as before anywhere else.
    public const string CompletionProperties = "app.code.completion.properties";
    public const string CompletionMethods = "app.code.completion.methods";
    public const string CompletionExtensionMethods = "app.code.completion.extensionMethods";
    public const string CompletionFields = "app.code.completion.fields";
    public const string CompletionEvents = "app.code.completion.events";

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

    // Rider's ContextHelp (F1, Cmd+Shift+F1 in Mac OS X 10.5+ and VSCode OSX, none in VSCode) with ExternalJavaDoc's
    // Shift+F1 (and Cmd+F1 in Visual Studio OSX): the caret's symbol's page in its documentation.
    public const string ContextHelp = "app.contextHelp";
    public const string NextError = "app.code.nextError";
    public const string PreviousError = "app.code.previousError";
    public const string FocusEditor = "app.focusEditor";

    // The keys to the window's next and previous region (the toolbar, each tool window shown, the editor, the status bar),
    // which no keymap of Rider's has an action for: Ctrl+Alt+` (Ctrl+Cmd+` on the Mac), Shift for back, in every keymap.
    public const string NextRegion = "app.nextRegion";
    public const string PreviousRegion = "app.previousRegion";

    // What the thing at hand says, as plain text to read (a card, the caret's problems, the terminal's last command, a
    // diff's changes, a test's result, the last message), which no keymap of Rider's has an action for: Ctrl+Alt+R
    // (Ctrl+Cmd+R on the Mac) in every keymap.
    public const string ReadAsText = "app.readAsText";

    // The debugger's, on the active editor and the session.
    public const string ToggleBreakpoint = "app.debug.toggleBreakpoint";

    // Rider's ToggleTemporaryLineBreakpoint, which every keymap takes from $default's Ctrl+Alt+Shift+F8 or Mac OS X 10.5+'s
    // Cmd+Alt+Shift+F8 and Cmd+Alt+Shift+\.
    public const string ToggleTemporaryBreakpoint = "app.debug.toggleTemporaryBreakpoint";

    // A method breakpoint on the method at the caret, which Rider's keymaps give no keys.
    public const string ToggleMethodBreakpoint = "app.debug.toggleMethodBreakpoint";

    // A snapshot point on the caret's line, capturing each hit as the program goes on, which Rider's keymaps give no keys.
    public const string ToggleSnapshotPoint = "app.debug.toggleSnapshotPoint";
    public const string ToggleBreakpointEnabled = "app.debug.toggleBreakpointEnabled";

    // Historical debugging, which Rider's keymaps give no keys: the programs debugged record their calls, and a stop steps
    // back and forward through what they recorded.
    public const string RecordCalls = "app.debug.recordCalls";
    public const string StepBack = "app.debug.stepBack";
    public const string StepForward = "app.debug.stepForward";
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

    // Rider's QuickEvaluateExpression: $default's Ctrl+Alt+F8 (Cmd+Alt+F8 on the Mac) and Alt-click, which its Visual Studio
    // keymaps take from it; VS Code's give the keys alone, their Alt-click adding a caret.
    public const string QuickEvaluate = "app.debug.quickEvaluate";

    // The expression at the caret pinned after its line, its value read again at each stop, which Rider's keymaps give no keys.
    public const string PinToLine = "app.debug.pinToLine";
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

    // Coverage, measured by a run of tests: Rider's Cover All Tests from Solution (its dotCover descriptor's keys, Ctrl+U, K in
    // its Visual Studio keymaps and Ctrl+;, K in IntelliJ's, which VS Code's take from their parents); the rest have none.
    public const string CoverTests = "app.tests.cover";
    public const string CoverAllTests = "app.tests.coverAll";
    public const string ClearCoverage = "app.tests.clearCoverage";
    public const string ExportCoverage = "app.tests.exportCoverage";
    public const string ShowCoverage = "app.showCoverage";

    // The Modules window, which Rider's keymaps give no keys.
    public const string ShowModules = "app.showModules";

    // Code clones, which Rider's keymaps give no keys: found across the solution or for the selection, two compared side by
    // side, and a group made to share one method.
    public const string FindClones = "app.code.findClones";
    public const string FindClonesOfSelection = "app.code.findClonesOfSelection";
    public const string CompareClones = "app.clones.compare";
    public const string ExtractClones = "app.clones.extractMethod";

    // Test doubles, which Rider's keymaps give no keys: a fake of the type at the caret written into a test project, and a
    // call's missing arguments filled with the doubles of the mocking library its project references.
    public const string CreateFake = "app.code.createFake";
    public const string FillArgumentsWithDoubles = "app.code.fillArgumentsWithDoubles";

    // The Dependency Map window at the caret, the Explorer's row or the solution, which Rider's keymaps give no keys.
    public const string ShowDependencyMap = "app.showDependencyMap";
    public const string ShowStructure = "app.showStructure";

    // Continuous testing, turned on and off for the folder's repository.
    public const string ContinuousTesting = "app.tests.continuous";

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

    /// <summary>The XAML file's project built for what its preview draws alone: no key of Rider's.</summary>
    public const string BuildForPreview = "app.editor.buildForPreview";

    // A Markdown file's preview beside its editor, under View and on the editor's tab strip: the Visual Studio keymaps and
    // their parents give IntelliJ's Editor and Preview layout (TextEditorWithPreview.Layout.EditorAndPreview) no keys; its
    // VSCode keymaps give it Ctrl+K, V (Cmd+K, V on macOS).
    public const string MarkdownPreview = "app.editor.markdownPreview";

    // A Markdown file's layouts, on its tab strip: IntelliJ's TextEditorWithPreview.Layout actions. Only the VSCode keymaps
    // give any keys, Preview Only's Ctrl+Shift+V (Cmd+Shift+V on macOS); Editor and Preview's are the toggle's above.
    public const string MarkdownEditorOnly = "app.editor.markdownEditorOnly";
    public const string MarkdownEditorAndPreview = "app.editor.markdownEditorAndPreview";
    public const string MarkdownPreviewOnly = "app.editor.markdownPreviewOnly";

    // A Windows Forms form's layouts, on its file's tab strip, View Designer under View too: Rider's OpenWinFormsCodeAction
    // and OpenWinFormsDesignerAction, F7 and Shift+F7 in its Visual Studio and Visual Studio OSX keymaps, where Shift+F7 is
    // Smart Step Into's too: a form's file shown takes it while the debugger has not stopped (KeysIn). Code and Design has
    // no key.
    public const string FormCodeOnly = "app.editor.formCodeOnly";
    public const string FormCodeAndDesign = "app.editor.formCodeAndDesign";
    public const string FormDesignOnly = "app.editor.formDesignOnly";

    // A form's design: its chosen components deleted (the Delete key's while the design has the keys), brought to the front
    // of what holds them or sent to its back, and the Toolbox of what can be added to it. No keymap of Rider's gives the
    // last three keys.
    public const string FormDelete = "app.design.delete";
    public const string FormBringToFront = "app.design.bringToFront";
    public const string FormSendToBack = "app.design.sendToBack";
    public const string ShowToolbox = "app.showToolbox";

    // An SVG file's picture beside its editor, under View and on the editor's tab strip: no keymap gives it keys.
    public const string SvgPreview = "app.editor.svgPreview";

    // An HTML file's page beside its editor, under View and on the editor's tab strip: no keymap gives it keys.
    public const string HtmlPreview = "app.editor.htmlPreview";

    // Rider's Close Solution (CloseProject) and Restore Default Layout (RiderRestoreDefaultLayout, in Window > Layout
    // Settings): no keys there either; both Visual Studio keymaps take IntelliJ's Shift+F12 off its layout action.
    public const string CloseSolution = "app.closeSolution";
    public const string RestoreDefaultLayout = "app.window.restoreDefaultLayout";

    // The terminal: a shell per tab in the open folder, and its New Session: no key of Rider's outside its terminal.
    public const string ShowTerminal = "app.showTerminal";
    public const string NewTerminalSession = "app.terminal.newSession";

    // The tool windows' frame, and a tree's Expand All and Collapse All in its header: no keys of Rider's there.
    public const string MaximizeToolWindow = "app.window.maximize";
    public const string HideAllToolWindows = "app.window.hideAll";
    public const string ExpandAll = "app.window.expandAll";
    public const string CollapseAll = "app.window.collapseAll";

    // The Refresh of the windows that read what they show again on asking: no keys of Rider's.
    public const string EndpointsRefresh = "app.endpoints.refresh";
    public const string GitLogRefresh = "app.vcs.refreshLog";

    // Commit Signing…, the Commit window's and the Git menu's: no keys of Rider's.
    public const string CommitSigning = "app.vcs.commitSigning";
    public const string CommitRefresh = "app.vcs.refreshChanges";

    // Rider's ChangesView.GroupBy.Directory, the Commit window's Group By › Directory.
    public const string GroupByDirectory = "app.vcs.groupByDirectory";

    // The Commit window's message history, Rider's Vcs.ShowMessageHistory: Ctrl+M in $default's and Mac OS X 10.5+'s, and
    // so VS Code's; Ctrl+H in its Visual Studio keymaps, Replace's there, as Ctrl+M is Match Brace's in the Mac's IntelliJ
    // IDEA, which it takes in the commit message (KeysIn); none in Emacs's.
    public const string CommitMessageHistory = "app.vcs.commitMessageHistory";
    public const string NuGetRefresh = "app.nuget.refresh";
    public const string NuGetUpdateAll = "app.nuget.updateAll";
    public const string NuGetUpdateEverywhere = "app.nuget.updateEverywhere";
    public const string NuGetUpdateSelected = "app.nuget.updateSelected";
    public const string LiveTreeRefresh = "app.liveTree.refresh";

    // The breakpoints' windows, the window's to show.
    public const string EditBreakpoint = "app.breakpoints.edit";
    public const string ViewBreakpoints = "app.breakpoints.view";

    // A folder's breakpoints written to a file, and a file's added, which Rider's keymaps give no keys.
    public const string ExportBreakpoints = "app.breakpoints.export";
    public const string ImportBreakpoints = "app.breakpoints.import";

    // Version control: Git over the open folder's working tree.
    public const string ShowCommit = "app.vcs.showCommit";
    public const string Commit = "app.vcs.commit";
    public const string Push = "app.vcs.push";
    public const string Branches = "app.vcs.branches";
    public const string NextChange = "app.vcs.nextChange";
    public const string PreviousChange = "app.vcs.previousChange";
    public const string RollbackLines = "app.vcs.rollbackLines";
    // Rider's Git.Stage.Add, which takes ChangesView.AddUnversioned's keys ($default's Ctrl+Alt+A); Unstage takes them
    // where what they act on is staged whole, as the change's own button turns.
    public const string Stage = "app.vcs.stage";
    public const string Unstage = "app.vcs.unstage";
    public const string ShowDiff = "app.vcs.showDiff";

    // Rider's Diff.ShowSettingsPopup, a diff's Settings: $default's Ctrl+Shift+D, none in its VS Code keymaps.
    public const string DiffSettings = "app.vcs.diffSettings";

    // Rider's EditSource in a diff, Jump to Source: F4 there, as in every list, no key of the keymap's (which would take F4
    // from the lists).
    public const string DiffJumpToSource = "app.vcs.diffJumpToSource";
    public const string ShowGit = "app.vcs.showGit";
    public const string Annotate = "app.vcs.annotate";
    public const string UpdateProject = "app.vcs.update";
    public const string Fetch = "app.vcs.fetch";
    public const string Stash = "app.vcs.stash";
    public const string Unstash = "app.vcs.unstash";
    public const string CloneRepository = "app.vcs.clone";

    // Rider's Git.Init, Create Git Repository: no keys in its Visual Studio keymaps or their parents.
    public const string CreateRepository = "app.vcs.createRepository";

    // Rider's Git.ResolveConflicts, Git.Rebase.Continue, Git.Rebase.Skip, Git.Rebase.Abort, Git.Merge.Abort,
    // Git.CherryPick.Abort and Git.Revert.Abort: no keys in its Visual Studio keymaps or their parents.
    public const string ResolveConflicts = "app.vcs.resolveConflicts";
    public const string ContinueRebase = "app.vcs.continueRebase";
    public const string SkipRebase = "app.vcs.skipRebase";
    public const string AbortRebase = "app.vcs.abortRebase";
    public const string AbortMerge = "app.vcs.abortMerge";
    public const string AbortCherryPick = "app.vcs.abortCherryPick";
    public const string AbortRevert = "app.vcs.abortRevert";

    // Rider's Git.Interactive.Rebase, the log's Interactively Rebase from Here: no keys in any of its keymaps.
    public const string InteractiveRebase = "app.vcs.interactiveRebase";

    // Rider's Git.CreateNewTag, New Tag…, and Git.CreateNewWorkingTree, New Worktree…: no keys in any of its keymaps.
    public const string NewTag = "app.vcs.newTag";
    public const string NewWorktree = "app.vcs.newWorktree";

    // Rider's Git.Configure.Remotes, Manage Remotes…, and Push to a remote other than the upstream (its Push dialog's
    // target): no keys in any of its keymaps.
    public const string ManageRemotes = "app.vcs.manageRemotes";
    public const string PushTo = "app.vcs.pushTo";

    // Rider's Vcs.ShowTabbedFileHistory, Vcs.ShowHistoryForBlock, Git.CompareWithBranch and Compare.Selected: no keys in
    // its Visual Studio keymaps or their parents.
    public const string ShowHistory = "app.vcs.showHistory";
    public const string ShowHistoryForSelection = "app.vcs.showHistoryForSelection";
    public const string CompareWithBranch = "app.vcs.compareWithBranch";
    public const string CompareWithRevision = "app.vcs.compareWithRevision";

    // Rider's Git.Get.From.Branch, its comparison's Get from Branch: no keys in any of its keymaps.
    public const string GetFromBranch = "app.vcs.getFromBranch";

    // A file or folder added to the repository's .gitignore, and the ignore file's line deciding whether git ignores one:
    // no keys of Rider's.
    public const string AddToGitIgnore = "app.vcs.addToGitIgnore";
    public const string GoToIgnoringLine = "app.vcs.goToIgnoringLine";

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

    /// <summary>The action whose keys a place takes for one of its own, where Rider's keymaps give a key two actions by place:
    /// Show Diff and Compare Files Duplicate's (none in VS Code's, nor Emacs's), Add to Watches Complete Statement's (VS Code's
    /// Start New Line Before's), Focus Editor a tool window's Escape; outside Visual Studio's, New Item Generate's and Edit
    /// Breakpoint View Breakpoints', IntelliJ IDEA's Rerun on the Mac Replace's; in Visual Studio's, Commit Message History
    /// Replace's (Match Brace's in the Mac's IntelliJ IDEA); a SQL file's and a table grid's as their ids say; the action's own once given some.</summary>
    public static string KeysIn(string id) => UserShortcuts.ContainsKey(id) ? id : id switch
    {
        ShowDiff or CompareFiles when Preset is not (KeymapPreset.VSCode or KeymapPreset.Emacs) => EditorActions.Duplicate,
        AddToWatches => Preset == KeymapPreset.VSCode ? EditorActions.StartNewLineBefore : CompleteStatement,
        FocusEditor => EditorActions.Escape,
        NewItem when Preset != KeymapPreset.VisualStudio => Generate,
        CommitMessageHistory when Preset == KeymapPreset.VisualStudio => Replace,
        CommitMessageHistory when Preset == KeymapPreset.IntelliJ && OperatingSystem.IsMacOS() => EditorActions.MatchBrace,
        EditBreakpoint when Preset != KeymapPreset.VisualStudio => ViewBreakpoints,
        DatabaseRun when Preset == KeymapPreset.VisualStudio => EditorActions.StartNewLineBefore,
        DatabaseRun when Preset == KeymapPreset.VSCode => EditorActions.StartNewLine,
        DatabaseSubmit => KeysIn(DatabaseRun),
        DatabaseAddRow => Generate,
        DatabaseDeleteRows => EditorActions.DeleteLine,
        DatabaseRevert => RollbackLines,
        DatabaseShowDdl => GoToDeclaration,
        DatabaseRunStop => Stop,
        Rerun when Preset == KeymapPreset.IntelliJ && OperatingSystem.IsMacOS() => Replace,
        NextOverload when Preset is not (KeymapPreset.VSCode or KeymapPreset.Emacs) => OverloadKeys(down: true),
        PreviousOverload when Preset is not (KeymapPreset.VSCode or KeymapPreset.Emacs) => OverloadKeys(down: false),
        FormDesignOnly when Preset == KeymapPreset.VisualStudio => SmartStepInto,
        _ => id,
    };

    /// <summary>The action whose keys step through Parameter Info's overloads, as Rider's keymaps give them Ctrl+Up and Down,
    /// or Alt's on the Mac: the editor's scroll by a line, its line move on the Mac, or IntelliJ IDEA's selection steps
    /// there. Emacs's own lists give Ctrl+Up and Down to its paragraph moves, so the overloads have none there.</summary>
    private static string OverloadKeys(bool down) => (Preset, OperatingSystem.IsMacOS()) switch
    {
        (KeymapPreset.IntelliJ, true) => down ? ShrinkSelection : ExtendSelection,
        (_, true) => down ? EditorActions.MoveLineDown : EditorActions.MoveLineUp,
        _ => down ? EditorActions.ScrollDown : EditorActions.ScrollUp,
    };

    /// <summary>Whether a key pressed is one a place takes for an action (<see cref="KeysIn"/>), in the keymap in force.</summary>
    public static bool Presses(string id, Avalonia.Input.KeyEventArgs e) =>
        Keymap().ShortcutsFor(KeysIn(id)).Any(s => s.Second is null && s.First == Keystroke.From(e));

    /// <summary>The Visual Studio keymap with these shortcuts over it.</summary>
    public static Keymap Keymap(IReadOnlyDictionary<string, string[]> overrides) => Keymap(KeymapPreset.VisualStudio, overrides);

    /// <summary>The keymap settings choose, with their shortcuts for it over it.</summary>
    public static Keymap Keymap(Settings.AppSettings settings) => Keymap(settings.Keymap, settings.OwnShortcuts());

    /// <summary>A keymap with these shortcuts over the platform's, each action's keys and mouse shortcuts exactly those its
    /// list names; one that does not read is passed over.</summary>
    public static Keymap Keymap(KeymapPreset preset, IReadOnlyDictionary<string, string[]> overrides)
    {
        // A chord no keymap here takes and a game is unlikely to want, as the game hears it while it has the keys.
        Keymap keymap = Base(preset).Bind(GameKeys, "control F11")
            .Bind(NextRegion, OperatingSystem.IsMacOS() ? "control meta BACK_QUOTE" : "control alt BACK_QUOTE")
            .Bind(PreviousRegion, OperatingSystem.IsMacOS() ? "control meta shift BACK_QUOTE" : "control alt shift BACK_QUOTE")
            .Bind(ReadAsText, OperatingSystem.IsMacOS() ? "control meta R" : "control alt R");
        // Rider's Console.History.Browse and Console.Transaction.Commit: $default's, in every keymap, the Mac's with Cmd for Ctrl.
        bool commandKey = OperatingSystem.IsMacOS() && preset != KeymapPreset.Emacs;
        keymap.Bind(DatabaseHistory, commandKey ? "meta alt E" : "control alt E").Bind(DatabaseCommit, commandKey ? "meta alt shift ENTER" : "control alt shift ENTER");
        // Rider's Vcs.ShowMessageHistory where its own key is free: VS Code's, and IntelliJ IDEA's off the Mac.
        if (preset == KeymapPreset.VSCode || (preset == KeymapPreset.IntelliJ && !OperatingSystem.IsMacOS()))
        {
            keymap.Bind(CommitMessageHistory, "control M");
        }
        if (preset != KeymapPreset.Emacs)
        {
            // The Switcher's keys are the same in every keymap of Rider's but Emacs's, whose own list gives it Ctrl+X, B.
            keymap.Bind(Switcher, "control TAB").Bind(SwitcherBackward, "control shift TAB");
        }
        foreach ((string filter, string key) in CompletionFilterKeys)
        {
            // Passenger's own, where the keymap leaves the key free: Emacs moves by Alt+F and Alt+M and finds actions by Alt+X.
            if (keymap.ActionFor(Shortcut.Parse(key)) is null)
            {
                keymap.Bind(filter, key);
            }
        }
        foreach ((string action, string[] shortcuts) in overrides)
        {
            List<Shortcut> keys = [];
            List<MouseShortcut> gestures = [];
            foreach (string text in shortcuts)
            {
                try
                {
                    if (MouseShortcut.IsMouse(text))
                    {
                        gestures.Add(MouseShortcut.Parse(text));
                    }
                    else
                    {
                        keys.Add(Shortcut.Parse(text));
                    }
                }
                catch (FormatException)
                {
                }
            }
            keymap.Set(Now(action), keys, gestures);
        }
        return keymap;
    }

    /// <summary>The completion list's filters by the kind of member, each on Alt and its badge's letter.</summary>
    private static readonly (string Filter, string Key)[] CompletionFilterKeys =
    [
        (CompletionProperties, "alt P"), (CompletionMethods, "alt M"), (CompletionExtensionMethods, "alt X"), (CompletionFields, "alt F"),
        (CompletionEvents, "alt E"),
    ];

    private static Keymap Base(KeymapPreset preset)
    {
        if (preset == KeymapPreset.VSCode)
        {
            return VSCode(DefaultKeymaps.For(preset));
        }
        if (preset == KeymapPreset.IntelliJ)
        {
            return IntelliJ(DefaultKeymaps.For(preset), OperatingSystem.IsMacOS());
        }
        if (preset == KeymapPreset.Emacs)
        {
            // $default's whole, the editor's and the IDE's, then Emacs's own lists over both.
            return Emacs(DefaultKeymaps.Emacs(IntelliJ(DefaultKeymaps.IntelliJ(DefaultKeymaps.NameOf(preset)), mac: false)));
        }
        Keymap keymap = DefaultKeymaps.ForPlatform();
        if (OperatingSystem.IsMacOS())
        {
            return keymap
                .Bind(OpenSolution, "control shift O")
                .Bind(OpenFile, "meta O")
                .Bind(NewProject, "meta shift N")
                .Bind(NewItem, "meta N", "alt INSERT")
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
                .Bind(GoToRelated, "meta shift F7")
                .Bind(Find, "meta F")
                .Bind(Replace, "control H")
                .Bind(FindNext, "F3", "meta alt PAGE_DOWN")
                .Bind(FindPrevious, "shift F3", "meta alt PAGE_UP")
                .Bind(FocusExplorer, "meta 1", "meta alt L")
                .Bind(SelectOpenedFile, "alt shift L")
                // CopyPaths: $default's Ctrl+Shift+C as Mac OS X 10.5+ takes it; RevealIn has none.
                .Bind(CopyPath, "meta shift C")
                .Bind(OpenInBrowser, "alt F2")
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
                .Bind(ToggleTemporaryBreakpoint, "meta alt shift F8", "meta alt shift BACK_SLASH")
                .Bind(ToggleBreakpointEnabled, "meta F9")
                .Bind(Resume, "F5")
                .Bind(Pause, "meta PAUSE", "shift meta P")
                .Bind(StepOver, "F10")
                .Bind(StepInto, "F11", "meta F11")
                .Bind(SmartStepInto, "shift F7")
                .Bind(FormCodeOnly, "F7")
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
                .Bind(QuickEvaluate, "meta alt F8", "alt button1")
                .Bind(ShowContextMenu, "shift F10")
                .Bind(EditBreakpoint, "alt F9")
                .Bind(ViewBreakpoints, "meta alt B")
                .Bind(MuteBreakpoints, "meta alt shift F10")
                .Bind(RunTests, "meta U , R", "meta U , meta R")
                .Bind(DebugTests, "meta U , D", "meta U , meta D")
                .Bind(RerunTests, "meta U , U", "meta U , meta U")
                .Bind(RerunFailedTests, "meta U , F", "meta U , meta F")
                .Bind(CoverAllTests, "meta U , K", "meta U , meta K")
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
                .Bind(Stage, "meta alt A")
                .Bind(DiffSettings, "meta shift D")
                .Bind(ShowGit, "meta 9")
                .Bind(ShowTerminal, "meta control 1")
                .Bind(UpdateProject, "meta alt W")
                .Bind(Back, "meta MINUS", "meta SUBTRACT", "button4")
                .Bind(Forward, "meta shift MINUS", "meta shift SUBTRACT", "button5")
                .Bind(Complete, "control SPACE", "control J")
                .Bind(QuickFix, "alt ENTER")
                .Bind(Rename, "meta R , R", "meta R , meta R")
                .Bind(GoToDeclaration, "F12", "meta button1")
                .Bind(GoToImplementation, "meta F12")
                .Bind(DerivedSymbols, "alt END", "meta alt button1")
                .Bind(GoToTypeDeclaration, "meta shift F11", "meta shift button1", "shift button2")
                .Bind(BaseSymbols, "control alt LEFT", "alt HOME")
                .Bind(FindUsages, "shift F12")
                .Bind(ShowUsages, "shift alt F12")
                .Bind(HighlightUsages, "shift alt F11")
                .Bind(Reformat, "meta alt ENTER", "meta K , F", "meta K , meta F", "meta K , D", "meta K , meta D")
                .Bind(ParameterInfo, "meta P")
                .Bind(CodeCleanup, "meta E , C", "meta E , meta C", "meta E , F", "meta E , meta F")
                .Bind(RefactorThis, "meta shift R")
                // Visual Studio OSX's own, which gives Cmd+N to New (NewElement).
                .Bind(Generate, "control meta N")
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
                .Bind(ContextHelp, "F1", "shift F1", "meta F1")
                .Bind(NextError, "alt PAGE_DOWN")
                .Bind(PreviousError, "alt PAGE_UP")
                .Bind(OpenInNewWindow, "shift F4")
                .Bind(SoftWrap, "meta E , W", "meta E , meta W")
                .Bind(ShowWhitespaces, "meta R , W", "meta R , meta W")
                .Bind(NextProjectWindow, "meta alt BACK_QUOTE")
                .Bind(PreviousProjectWindow, "meta shift alt BACK_QUOTE");
        }
        return keymap
            .Bind(OpenSolution, "control shift O")
            .Bind(OpenFile, "control O")
            .Bind(NewProject, "control shift N")
            .Bind(NewItem, "control N")
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
            .Bind(JumpToNavigationBar, "control F2")
            .Bind(GoToRelated, "control alt F7")
            .Bind(Find, "control F")
            .Bind(Replace, "control H")
            .Bind(FindNext, "F3", "control alt PAGE_DOWN")
            .Bind(FindPrevious, "shift F3", "control alt PAGE_UP")
            .Bind(FocusExplorer, "alt 1", "control alt L")
            .Bind(SelectOpenedFile, "alt shift L")
            .Bind(CopyPath, "control shift C")
            .Bind(OpenInBrowser, "control alt F2")
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
            .Bind(ToggleTemporaryBreakpoint, "control alt shift F8")
            .Bind(ToggleBreakpointEnabled, "control F9")
            .Bind(Resume, "F5")
            .Bind(Pause, "control PAUSE", "shift control P")
            .Bind(StepOver, "F10")
            .Bind(StepInto, "F11")
            .Bind(SmartStepInto, "shift F7")
            .Bind(FormCodeOnly, "F7")
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
            .Bind(QuickEvaluate, "control alt F8", "alt button1")
            .Bind(ShowContextMenu, "shift F10", "CONTEXT_MENU")
            .Bind(EditBreakpoint, "alt F9")
            .Bind(ViewBreakpoints, "control alt B")
            .Bind(MuteBreakpoints, "control alt shift F10")
            .Bind(RunTests, "control U , R", "control U , control R")
            .Bind(DebugTests, "control U , D", "control U , control D")
            .Bind(RerunTests, "control U , U", "control U , control U")
            .Bind(RerunFailedTests, "control U , F", "control U , control F")
            .Bind(CoverAllTests, "control U , K", "control U , control K")
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
            .Bind(Stage, "control alt A")
            .Bind(DiffSettings, "control shift D")
            .Bind(ShowGit, "alt 9")
            .Bind(ShowTerminal, "control alt 1")
            .Bind(UpdateProject, "control alt W")
            .Bind(Back, "control MINUS", "control SUBTRACT", "button4")
            .Bind(Forward, "control shift MINUS", "control shift SUBTRACT", "button5")
            .Bind(Complete, "control SPACE", "control J")
            .Bind(QuickFix, "alt ENTER", "control PERIOD")
            .Bind(Rename, "control R , R", "control R , control R")
            .Bind(GoToDeclaration, "F12", "control button1", "button2")
            .Bind(GoToImplementation, "control F12")
            .Bind(DerivedSymbols, "alt END")
            .Bind(GoToTypeDeclaration, "control shift F11", "control shift button1")
            .Bind(BaseSymbols, "alt HOME")
            .Bind(FindUsages, "shift F12")
            .Bind(ShowUsages, "shift alt F12")
            .Bind(HighlightUsages, "shift alt F11")
            .Bind(Reformat, "control alt ENTER", "control K , F", "control K , control F", "control K , D", "control K , control D")
            .Bind(ParameterInfo, "shift control SPACE")
            .Bind(CodeCleanup, "control E , C", "control E , control C", "control E , F", "control E , control F")
            .Bind(RefactorThis, "control shift R")
            // Ctrl+N is New's (NewElement), and Alt+Insert, which Rider gives both, is Generate's in the editor.
            .Bind(Generate, "alt INSERT")
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
            .Bind(ContextHelp, "F1", "shift F1")
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
                .Bind(OpenSolution, "control shift O")
                .Bind(OpenFile, "meta O")
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
                .Bind(JumpToNavigationBar, "meta shift SEMICOLON")
                .Bind(GoToRelated, "control meta UP")
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
                .Bind(SelectOpenedFile, "meta K , E")
                .Bind(CopyPath, "meta alt C", "meta K , P")
                .Bind(RevealInFileManager, "meta K , R", "meta alt R")
                .Bind(OpenInBrowser, "alt F2")
                .Bind(Build, "meta shift B")
                .Bind(RunProgram, "control F5")
                .Bind(Stop, "shift F5")
                .Bind(Rerun, "meta SEMICOLON , L", "meta shift F5")
                .Bind(ShowRun, "meta shift U")
                .Bind(ShowProblems, "meta shift M")
                .Bind(ShowFind, "meta 3")
                .Bind(ShowDebug, "meta shift D", "meta shift Y")
                .Bind(HideToolWindow, "meta J")
                .Bind(Back, "control MINUS", "button4")
                .Bind(Forward, "control shift MINUS", "button5")
                .Bind(Complete, "meta I", "alt ESCAPE", "control SPACE")
                .Bind(QuickFix, "meta PERIOD", "alt ENTER")
                .Bind(Rename, "F2")
                .Bind(GoToDeclaration, "F12", "meta button1", "button2")
                .Bind(GoToTypeDeclaration, "control shift B", "shift meta button1")
                .Bind(DerivedSymbols, "meta F12")
                .Bind(FindUsages, "alt shift F12")
                .Bind(ShowUsages, "shift F12")
                .Bind(QuickEvaluate, "meta alt F8")
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
                .Bind(QuickDocumentation, "meta K , meta I", "control button2")
                .Bind(ContextHelp, "meta shift F1", "shift F1")
                .Bind(NextError, "alt F8")
                .Bind(PreviousError, "shift F8", "alt shift F8")
                .Bind(ToggleBreakpoint, "F9")
                .Bind(ToggleTemporaryBreakpoint, "meta alt shift F8", "meta alt shift BACK_SLASH")
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
                .Bind(CoverAllTests, "meta SEMICOLON , K", "meta SEMICOLON , meta K")
                .Bind(ShowTests, "meta alt 8")
                .Bind(ShowStructure, "meta 7")
                .Bind(SplitRight, "meta BACK_SLASH")
                .Bind(MoveToOppositeGroup, "meta control RIGHT")
                .Bind(OpenInNewWindow, "meta K , O")
                .Bind(MarkdownPreview, "meta K , V")
                .Bind(MarkdownPreviewOnly, "meta shift V")
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
                .Bind(Stage, "meta alt A")
                .Bind(ShowGit, "control shift G")
                .Bind(RecentChanges, "alt shift C");
        }
        return keymap
            .Bind(OpenSolution, "control K , control O")
            .Bind(OpenFile, "control O")
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
            .Bind(JumpToNavigationBar, "control shift SEMICOLON")
            .Bind(GoToRelated, "control alt HOME")
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
            .Bind(SelectOpenedFile, "control K , E")
            .Bind(CopyPath, "shift alt C", "control K , P")
            .Bind(RevealInFileManager, "shift alt R", "control K , R")
            .Bind(OpenInBrowser, "alt F2")
            .Bind(Build, "control shift B")
            .Bind(RunProgram, "control F5")
            .Bind(Stop, "shift F5")
            .Bind(Rerun, "control SEMICOLON , L", "control shift F5")
            .Bind(ShowRun, "control shift U")
            .Bind(ShowProblems, "control shift M")
            .Bind(ShowFind, "alt 3")
            .Bind(ShowDebug, "control shift Y", "control shift D")
            .Bind(HideToolWindow, "control J")
            .Bind(Back, "alt LEFT", "button4")
            .Bind(Forward, "alt RIGHT", "button5")
            .Bind(Complete, "control I", "control SPACE")
            .Bind(QuickFix, "control PERIOD")
            .Bind(Rename, "F2")
            .Bind(GoToDeclaration, "F12", "control button1")
            .Bind(GoToTypeDeclaration, "shift control button1")
            .Bind(DerivedSymbols, "control F12")
            .Bind(FindUsages, "alt shift F12")
            .Bind(ShowUsages, "shift F12")
            .Bind(QuickEvaluate, "control alt F8")
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
            .Bind(ContextHelp, "shift F1")
            .Bind(NextError, "alt F8")
            .Bind(PreviousError, "alt shift F8", "shift F8")
            .Bind(ToggleBreakpoint, "F9")
            .Bind(ToggleTemporaryBreakpoint, "control alt shift F8")
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
            .Bind(CoverAllTests, "control SEMICOLON , K", "control SEMICOLON , control K")
            .Bind(ShowTests, "alt shift 8")
            .Bind(ShowStructure, "alt 7")
            .Bind(SplitRight, "control BACK_SLASH")
            .Bind(MoveToOppositeGroup, "control alt RIGHT")
            .Bind(OpenInNewWindow, "control K , O")
            .Bind(MarkdownPreview, "control K , V")
            .Bind(MarkdownPreviewOnly, "control shift V")
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
            .Bind(Stage, "control alt A")
            .Bind(ShowGit, "control shift G");
    }

    /// <summary>IntelliJ IDEA's $default and Mac OS X 10.5+ keymaps, with what Rider's descriptors add (its tests' Ctrl+;
    /// chords, Open Solution's Ctrl+Shift+O); Go to Symbol takes Go to Class's keys too, and Search Everywhere is a double
    /// Shift. A key IntelliJ gives two actions by place goes to one: Ctrl+Shift+F10 (macOS's Ctrl+Shift+R and D) to the
    /// tests, not Run and Debug Here; Ctrl+Shift+F8 to View Breakpoints; Alt+Insert (Cmd+N) to Generate, not New Item; the
    /// Mac's Cmd+R to Replace, not Rerun, and Cmd+T to Update Project, not the terminal's New Session.</summary>
    private static Keymap IntelliJ(Keymap keymap, bool mac)
    {
        if (mac)
        {
            // Cmd+Q, Exit's here, is the application menu's Quit, as in the other keymaps.
            return keymap
                .Bind(DatabaseRun, "meta ENTER")
                .Bind(OpenSolution, "control shift O")
                .Bind(NextProjectWindow, "meta alt BACK_QUOTE")
                .Bind(PreviousProjectWindow, "meta alt shift BACK_QUOTE")
                .Bind(SaveAll, "meta S")
                .Bind(CloseTab, "meta W")
                .Bind(NextTab, "meta shift CLOSE_BRACKET", "control RIGHT")
                .Bind(PreviousTab, "meta shift OPEN_BRACKET", "control LEFT")
                .Bind(GoToFile, "meta shift O")
                .Bind(GoToLine, "meta L")
                .Bind(GoToSymbol, "meta O", "meta alt O")
                .Bind(GoToEverything, "shift , shift")
                .Bind(FileStructure, "meta F12")
                .Bind(JumpToNavigationBar, "alt HOME")
                .Bind(GoToRelated, "control meta UP")
                .Bind(FindAction, "meta shift A")
                .Bind(FindInFiles, "meta shift F")
                .Bind(RecentFiles, "meta E")
                .Bind(RecentLocations, "meta shift E")
                .Bind(LastEditLocation, "meta shift BACK_SPACE")
                .Bind(ToggleBookmark, "F3")
                .Bind(ShowBookmarks, "meta F3", "meta 2")
                .Bind(ReplaceInFiles, "meta shift R")
                .Bind(ShowSettings, "meta COMMA")
                .Bind(ShowNuGet, "meta alt 7")
                .Bind(Find, "meta F")
                .Bind(Replace, "meta R")
                .Bind(FindNext, "meta G")
                .Bind(FindPrevious, "meta shift G")
                .Bind(FocusExplorer, "meta 1")
                .Bind(CopyPath, "meta shift C")
                .Bind(OpenInBrowser, "alt F2")
                .Bind(Build, "meta F9")
                .Bind(RunProgram, "control R")
                .Bind(Debug, "control D")
                .Bind(Stop, "meta F2")
                .Bind(ShowRun, "meta 4")
                .Bind(ShowFind, "meta 3")
                .Bind(ShowDebug, "meta 5")
                .Bind(ShowProblems, "meta 6")
                .Bind(ShowStructure, "meta 7")
                .Bind(ShowContainers, "meta 8")
                .Bind(ShowTests, "meta alt 8")
                .Bind(ShowGit, "meta 9")
                .Bind(ShowCommit, "meta 0")
                .Bind(ShowTerminal, "alt F12")
                .Bind(HideToolWindow, "shift ESCAPE")
                .Bind(HideAllToolWindows, "meta shift F12")
                .Bind(MaximizeToolWindow, "meta shift QUOTE")
                .Bind(Back, "meta OPEN_BRACKET", "meta alt LEFT", "button4")
                .Bind(Forward, "meta CLOSE_BRACKET", "meta alt RIGHT", "button5")
                .Bind(Complete, "control SPACE")
                .Bind(QuickFix, "alt ENTER")
                .Bind(Rename, "shift F6")
                .Bind(GoToDeclaration, "meta B", "meta button1", "button2")
                .Bind(GoToTypeDeclaration, "meta shift B", "control shift B", "meta shift button1")
                .Bind(BaseSymbols, "meta U")
                .Bind(DerivedSymbols, "meta alt B", "meta alt button1")
                .Bind(FindUsages, "alt F7")
                .Bind(ShowUsages, "meta alt F7")
                .Bind(HighlightUsages, "meta shift F7")
                .Bind(Reformat, "meta alt L")
                .Bind(ReformatWithPrettier, "meta alt shift P")
                .Bind(RefactorThis, "control T")
                .Bind(Generate, "meta N", "control ENTER")
                .Bind(ExtractMethod, "meta alt M")
                .Bind(IntroduceVariable, "meta alt V")
                .Bind(Inline, "meta alt N")
                .Bind(ParameterInfo, "meta P")
                .Bind(SurroundWith, "meta alt T", "meta alt J")
                .Bind(CompleteStatement, "meta shift ENTER")
                .Bind(OptimizeUsings, "control alt O")
                .Bind(SafeDelete, "meta DELETE")
                .Bind(IntroduceField, "meta alt F")
                .Bind(IntroduceParameter, "meta alt P")
                .Bind(ChangeSignature, "meta F6")
                .Bind(Move, "F6")
                .Bind(QuickDocumentation, "F1", "control J", "control button2")
                .Bind(ContextHelp, "meta shift F1", "shift F1")
                .Bind(NextError, "F2")
                .Bind(PreviousError, "shift F2")
                .Bind(ToggleBreakpoint, "meta F8")
                .Bind(ToggleTemporaryBreakpoint, "meta alt shift F8", "meta alt shift BACK_SLASH")
                .Bind(Resume, "meta alt R", "F9")
                .Bind(StepOver, "F8")
                .Bind(StepInto, "F7")
                .Bind(SmartStepInto, "shift F7")
                .Bind(StepOut, "shift F8")
                .Bind(RunToCursor, "alt F9")
                .Bind(SetNextStatement, "meta alt shift F9")
                .Bind(ExtendSelection, "alt UP")
                .Bind(ShrinkSelection, "alt DOWN")
                .Bind(CallHierarchy, "control alt H")
                .Bind(TypeHierarchy, "control H")
                .Bind(ShowExecutionPoint, "alt F10")
                .Bind(AttachToProcess, "alt shift F5")
                .Bind(ReattachToProcess, "meta alt shift F5")
                .Bind(Evaluate, "alt F8")
                .Bind(QuickEvaluate, "meta alt F8", "alt button1")
                .Bind(ViewBreakpoints, "meta shift F8")
                .Bind(RunTests, "meta SEMICOLON , R", "meta SEMICOLON , meta R", "control shift R")
                .Bind(DebugTests, "meta SEMICOLON , D", "meta SEMICOLON , meta D", "control shift D")
                .Bind(RunAllTests, "meta SEMICOLON , L", "meta SEMICOLON , meta L")
                .Bind(RunTestsUntilFailure, "meta SEMICOLON , W", "meta SEMICOLON , meta W")
                .Bind(RerunTests, "meta SEMICOLON , T", "meta SEMICOLON , meta T")
                .Bind(RerunFailedTests, "meta SEMICOLON , F", "meta SEMICOLON , meta F")
                .Bind(StopTests, "meta SEMICOLON , S", "meta SEMICOLON , meta S")
                .Bind(CoverAllTests, "meta SEMICOLON , K", "meta SEMICOLON , meta K")
                .Bind(OpenInNewWindow, "shift F4")
                .Bind(IncreaseFontSize, "control shift PERIOD")
                .Bind(DecreaseFontSize, "control shift COMMA")
                .Bind(RestoreDefaultLayout, "shift F12")
                .Bind(Commit, "meta K")
                .Bind(Push, "meta shift K")
                .Bind(UpdateProject, "meta T")
                .Bind(NextChange, "control alt shift DOWN")
                .Bind(PreviousChange, "control alt shift UP")
                .Bind(RollbackLines, "meta alt Z")
                .Bind(Stage, "meta alt A")
                .Bind(DiffSettings, "meta shift D")
                .Bind(RecentChanges, "alt shift C");
        }
        return keymap
            .Bind(OpenSolution, "control shift O")
            .Bind(NextProjectWindow, "control alt CLOSE_BRACKET")
            .Bind(PreviousProjectWindow, "control alt OPEN_BRACKET")
            .Bind(SaveAll, "control S")
            .Bind(CloseTab, "control F4")
            .Bind(NextTab, "alt RIGHT")
            .Bind(PreviousTab, "alt LEFT")
            .Bind(GoToFile, "control shift N")
            .Bind(GoToLine, "control G")
            .Bind(GoToSymbol, "control N", "control alt shift N")
            .Bind(GoToEverything, "shift , shift")
            .Bind(FileStructure, "control F12")
            .Bind(JumpToNavigationBar, "alt HOME")
            .Bind(GoToRelated, "control alt HOME")
            .Bind(FindAction, "control shift A")
            .Bind(FindInFiles, "control shift F")
            .Bind(RecentFiles, "control E")
            .Bind(RecentLocations, "control shift E")
            .Bind(LastEditLocation, "control shift BACK_SPACE")
            .Bind(ToggleBookmark, "F11")
            .Bind(ShowBookmarks, "shift F11", "alt 2")
            .Bind(ReplaceInFiles, "control shift R")
            .Bind(ShowSettings, "control alt S")
            .Bind(ShowNuGet, "alt shift 7")
            .Bind(Find, "control F", "alt F3")
            .Bind(Replace, "control R")
            .Bind(FindNext, "F3", "control L")
            .Bind(FindPrevious, "shift F3", "control shift L")
            .Bind(FocusExplorer, "alt 1")
            .Bind(CopyPath, "control shift C")
            .Bind(OpenInBrowser, "alt F2")
            .Bind(Build, "control F9")
            .Bind(RunProgram, "shift F10")
            .Bind(Debug, "shift F9")
            .Bind(Stop, "control F2")
            .Bind(Rerun, "control F5")
            .Bind(ShowRun, "alt 4")
            .Bind(ShowFind, "alt 3")
            .Bind(ShowDebug, "alt 5")
            .Bind(ShowProblems, "alt 6")
            .Bind(ShowStructure, "alt 7")
            .Bind(ShowContainers, "alt 8")
            .Bind(ShowTests, "alt shift 8")
            .Bind(ShowGit, "alt 9")
            .Bind(ShowCommit, "alt 0")
            .Bind(ShowTerminal, "alt F12")
            .Bind(HideToolWindow, "shift ESCAPE")
            .Bind(HideAllToolWindows, "control shift F12")
            .Bind(MaximizeToolWindow, "control shift QUOTE")
            .Bind(Back, "control alt LEFT", "button4")
            .Bind(Forward, "control alt RIGHT", "button5")
            .Bind(Complete, "control SPACE")
            .Bind(QuickFix, "alt ENTER")
            .Bind(Rename, "shift F6")
            .Bind(GoToDeclaration, "control B", "control button1")
            .Bind(GoToTypeDeclaration, "control shift B", "control shift button1")
            .Bind(BaseSymbols, "control U")
            .Bind(DerivedSymbols, "control alt B", "control alt button1")
            .Bind(FindUsages, "alt F7")
            .Bind(ShowUsages, "control alt F7")
            .Bind(HighlightUsages, "control shift F7")
            .Bind(Reformat, "control alt L")
            .Bind(ReformatWithPrettier, "control alt shift P")
            .Bind(RefactorThis, "control alt shift T")
            .Bind(Generate, "alt INSERT")
            .Bind(DatabaseRun, "control ENTER")
            .Bind(ExtractMethod, "control alt M")
            .Bind(IntroduceVariable, "control alt V")
            .Bind(Inline, "control alt N")
            .Bind(ParameterInfo, "control P")
            .Bind(SurroundWith, "control alt T", "control alt J")
            .Bind(CompleteStatement, "control shift ENTER")
            .Bind(OptimizeUsings, "control alt O")
            .Bind(SafeDelete, "alt DELETE")
            .Bind(IntroduceField, "control alt F")
            .Bind(IntroduceParameter, "control alt P")
            .Bind(ChangeSignature, "control F6")
            .Bind(Move, "F6")
            .Bind(QuickDocumentation, "control Q", "alt button2")
            .Bind(ContextHelp, "F1", "shift F1")
            .Bind(NextError, "F2")
            .Bind(PreviousError, "shift F2")
            .Bind(ToggleBreakpoint, "control F8")
            .Bind(ToggleTemporaryBreakpoint, "control alt shift F8")
            .Bind(Resume, "F9")
            .Bind(StepOver, "F8")
            .Bind(StepInto, "F7")
            .Bind(SmartStepInto, "shift F7")
            .Bind(StepOut, "shift F8")
            .Bind(RunToCursor, "alt F9")
            .Bind(SetNextStatement, "control alt shift F9")
            .Bind(ExtendSelection, "control W")
            .Bind(ShrinkSelection, "control shift W")
            .Bind(CallHierarchy, "control alt H")
            .Bind(TypeHierarchy, "control H")
            .Bind(ShowExecutionPoint, "alt F10")
            .Bind(AttachToProcess, "control alt F5")
            .Bind(ReattachToProcess, "control alt shift F5")
            .Bind(Evaluate, "alt F8")
            .Bind(QuickEvaluate, "control alt F8", "alt button1")
            .Bind(ShowContextMenu, "CONTEXT_MENU")
            .Bind(ViewBreakpoints, "control shift F8")
            .Bind(RunTests, "control SEMICOLON , R", "control SEMICOLON , control R", "control shift F10")
            .Bind(DebugTests, "control SEMICOLON , D", "control SEMICOLON , control D")
            .Bind(RunAllTests, "control SEMICOLON , L", "control SEMICOLON , control L")
            .Bind(RunTestsUntilFailure, "control SEMICOLON , W", "control SEMICOLON , control W")
            .Bind(RerunTests, "control SEMICOLON , T", "control SEMICOLON , control T")
            .Bind(RerunFailedTests, "control SEMICOLON , F", "control SEMICOLON , control F")
            .Bind(StopTests, "control SEMICOLON , S", "control SEMICOLON , control S")
            .Bind(CoverAllTests, "control SEMICOLON , K", "control SEMICOLON , control K")
            .Bind(OpenInNewWindow, "shift F4")
            .Bind(IncreaseFontSize, "alt shift PERIOD")
            .Bind(DecreaseFontSize, "alt shift COMMA")
            .Bind(RestoreDefaultLayout, "shift F12")
            .Bind(Commit, "control K")
            .Bind(Push, "control shift K")
            .Bind(UpdateProject, "control T")
            .Bind(Branches, "control shift BACK_QUOTE")
            .Bind(NextChange, "control alt shift DOWN")
            .Bind(PreviousChange, "control alt shift UP")
            .Bind(RollbackLines, "control alt Z")
            .Bind(Stage, "control alt A")
            .Bind(DiffSettings, "control shift D")
            .Bind(RecentChanges, "alt shift C");
    }

    /// <summary>Rider's Emacs keymap's own lists of the IDE's actions over $default's, each in place of what $default gives
    /// it and taken from any action that had a key of it, as the keymap's own list goes before its parent's: Find's
    /// Ctrl+Alt+S from Settings, Ctrl+K (Cut to Line End) from Commit. Its Ctrl+X and Escape chords (Ctrl+X, Ctrl+S to Save
    /// All, Esc, X to Find Action) are chords as any; Reformat Code, Safe Delete, Introduce Field, Recent Files, Derived
    /// Symbols, Previous Project Window and the font size in every editor have none.</summary>
    private static Keymap Emacs(Keymap keymap)
    {
        Keymap emacs = EmacsOwn(keymap);
        // The Mac's Cmd+S, W and , which Rider's Emacs leaves out (Cmd+Q is the application menu's Quit in every keymap).
        return !OperatingSystem.IsMacOS() ? emacs : emacs
            .Bind(Save, "meta S")
            .Bind(CloseTab, "meta W")
            .Bind(ShowSettings, "meta COMMA");
    }

    private static Keymap EmacsOwn(Keymap keymap) => keymap
        .Own(FindAction, "alt X", "ESCAPE , X")
        .Own(Find, "control alt S", "alt F3", "control S")
        .Own(FindNext, "F3", "alt S")
        .Own(FindPrevious, "shift F3", "alt R", "control R")
        .Own(Replace, "shift alt 5")
        .Own(FindUsages, "alt F7", "shift alt S")
        .Own(GoToDeclaration, "alt PERIOD", "control alt G", "ESCAPE , PERIOD", "control button1")
        .Own(GoToSymbol, "shift alt G", "control alt shift N")
        .Own(GoToFile, "control X , control F")
        .Own(GoToLine, "alt G")
        .Own(Switcher, "control X , B")
        .Own(SwitcherBackward)
        .Own(NextError, "F2", "control X , BACK_QUOTE")
        .Own(PreviousError, "shift F2", "shift control X , BACK_QUOTE")
        .Own(Complete, "alt SLASH")
        .Own(ParameterInfo, "shift alt P")
        .Own(ExtendSelection, "control alt W")
        .Own(SaveAll, "control X , control S")
        .Own(NextTab, "alt RIGHT", "control X , N")
        .Own(PreviousTab, "alt LEFT", "control X , P")
        .Own(CloseTab, "control F4", "control X , K", "control X , 0")
        .Own(CloseAllTabs, "control X , control C")
        .Own(SplitRight, "control X , 3")
        .Own(SplitDown, "control X , 2")
        .Own(UnsplitAll, "control X , 1")
        .Own(NextProjectWindow, "control X , control O")
        .Own(PreviousProjectWindow)
        .Own(Reformat)
        .Own(SafeDelete)
        .Own(IntroduceField)
        .Own(RecentFiles)
        .Own(DerivedSymbols)
        .Own(IncreaseFontSize)
        .Own(DecreaseFontSize);
}
