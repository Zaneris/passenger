using System.Globalization;
using Passenger.App.Mobile;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Why an action cannot run for want of what it acts on, named once, so the same want reads the same wherever an
/// action, a tool or a status line says it: the status bar, a greyed menu item's tip and an agent's ide_action alike.</summary>
public static class Refusals
{
    /// <summary>No tool window is open, for an action on the one open last.</summary>
    public const string NoToolWindowOpen = "No tool window is open";

    /// <summary>No tool window's view has had the keys yet, for Jump to Last Tool Window.</summary>
    public const string NoToolWindowHadKeys = "No tool window has had the keys yet";

    /// <summary>What has the keys holds no tabs, for Close Tool Window Tab.</summary>
    public const string NoToolTabs = "No tool window with tabs has the keys";

    /// <summary>The tab shown has no ×, for Close Tool Window Tab.</summary>
    public const string ToolTabStays = "The tab shown has no close: it stays open";

    /// <summary>The window's scale is at the end of Settings' Display scale, for Zoom In and Zoom Out.</summary>
    public static string ZoomedAsFarAsItGoes(bool up) => up ? "The window is as large as it goes" : "The window is as small as it goes";

    /// <summary>Closed tool windows keep their stripe icons or tabs, so Close is Hide.</summary>
    public static readonly string ClosedKeepIcons =
        $"Closed tool windows keep their icons or tabs: Hide closes them ({SettingsDialog.PlaceOfPage(SettingsDialog.Appearance)} | Closed tool windows)";

    /// <summary>No file is shown in the editor.</summary>
    public const string NoFile = "No file is open";

    /// <summary>A regex stopped at its time limit, wherever that cuts an answer short (<see cref="TextSearch.RegexStopped"/>).</summary>
    public const string RegexStopped = TextSearch.RegexStopped;

    /// <summary><see cref="RegexStopped"/> naming where it stopped: a file, or what it was matching.</summary>
    public static string RegexStoppedIn(string where) => TextSearch.RegexStoppedIn(where);

    /// <summary>No tab is open and no tool window serves the folder, for the Switcher.</summary>
    public const string NothingToSwitchTo = "No tab or tool window is open to switch to";

    /// <summary>The file shown is no Godot scene, nor a script a scene runs, of a Godot project the folder builds a game of.</summary>
    public const string NoGodotScene = "No scene of a Godot game is open: open a .tscn, or a script one of its nodes runs";

    /// <summary>A form's layout asked for of a file that is no Windows Forms form's.</summary>
    public const string NoForm = "No form is open: open a Windows Forms form's or user control's code or its .Designer.cs";

    /// <summary>A form's design asked for off Windows, where Windows Forms draws nothing.</summary>
    public const string FormsDrawOnWindows = "Windows Forms draws only on Windows, where its designer runs";

    /// <summary>A design's action asked for while no form's design has the keys, or the Toolbox while none is shown.</summary>
    public const string NoFormDesign = "No form's design is shown: open a form's code or its .Designer.cs and show its design";

    /// <summary>A design's action on its chosen components while none is chosen.</summary>
    public const string NoComponentChosen = "No component of the form is chosen: click one in its design";

    /// <summary>Delete, Bring to Front or Send to Back asked of the form itself.</summary>
    public const string FormItselfStays = "The form itself is neither deleted nor put in front of or behind anything";

    /// <summary>Move Up or Move Down asked of a component that is no item of a list.</summary>
    public const string NotInList = "Only an item of a list (a menu's, a strip's, a list's columns, a tab control's pages) moves up or down in it";

    /// <summary>Move Up asked of the first item of its list.</summary>
    public const string FirstInList = "It is first in its list already";

    /// <summary>Move Down asked of the last item of its list.</summary>
    public const string LastInList = "It is last in its list already";

    /// <summary>Bring to Front or Send to Back asked of an item a control draws (a menu's, a list's columns).</summary>
    public const string ItemHasNoOrder = "An item is drawn by what holds it, in its list's order, which Move Up and Move Down change";

    /// <summary>Bring to Front or Send to Back asked of a component in the tray.</summary>
    public const string TrayHasNoOrder = "A component in the tray is drawn nowhere, so it stands in front of or behind nothing";

    /// <summary>An edit of a design asked for while the designer makes the last one.</summary>
    public const string DesignerBusy = "The designer is still making the last change";

    /// <summary>The form's code changed (or the designer started again) while the designer made an edit, which it then wrote nowhere.</summary>
    public const string DesignChangedMeanwhile = "The form's code changed while the designer worked: make the change again";

    /// <summary>An edit of a design that the form's code says nothing of (a control moved to where it stood).</summary>
    public const string DesignChangesNothing = "That changes nothing the form's code says";

    /// <summary>Reset asked of a property the form's code sets nothing of.</summary>
    public const string PropertyAtDefault = "The form's code sets nothing of it: it stands at its default";

    /// <summary>Remove Handler asked of an event no handler is hooked to.</summary>
    public const string NoHandler = "No handler is hooked to it";

    /// <summary>(Name) given to the form itself, whose name is its class's.</summary>
    public const string FormNameIsItsClass = "The form's name is its class's: rename the class in its code";

    /// <summary>Undo or Redo in a form's design with nothing of its file's to take back or make again.</summary>
    public const string NothingToUndo = "Nothing of the form's code to undo";

    public const string NothingToRedo = "Nothing of the form's code to redo";

    /// <summary>The Toolbox asked for in a folder with no Windows Forms project.</summary>
    public const string NoFormsProject = "No Windows Forms project is open";

    /// <summary>The folder open is not trusted, so Passenger runs none of its code (<see cref="WorkspaceTrust"/>): no build,
    /// run, debug, test, profile, restore, Hot Reload, C# language server or previewer of its.</summary>
    public const string Untrusted = WorkspaceTrust.Untrusted;

    /// <summary>The folder open is not trusted, so an agent's package search asks none of the feeds its nuget.config names,
    /// which may be any server.</summary>
    public const string UntrustedFeeds = $"The folder is not trusted: an agent's {Mcp.McpTools.NugetSearch} asks none of the feeds its nuget.config names until the person trusts it";

    /// <summary>What trusting a folder lets Passenger run, as the trust question and the banner say it.</summary>
    public const string TrustLets = "Trusted, Passenger runs its code as it works: design-time builds and restore as it opens, the analyzers and "
        + "source generators its projects name, builds, runs, tests and debugging, its own TypeScript, ESLint, Prettier, Oxlint, Oxfmt and Biome, and the programs its "
        + "Git configuration names.";

    /// <summary>What the banner over an untrusted folder says.</summary>
    public const string UntrustedSaid = "This folder is not trusted: none of its code runs until you trust it";

    /// <summary>What the banner over an untrusted folder below the top of a Git repository says: its Trust Folder trusts the
    /// repository (<see cref="WorkspaceTrust.FolderToTrust"/>).</summary>
    public static string UntrustedInRepository(string repository) =>
        $"This folder is not trusted: none of its code runs until you trust the Git repository it is in, {Path.GetFileName(repository)}";

    /// <summary>What the banner over a folder no longer trusted says while what the person started there runs on.</summary>
    public static string UntrustedRunsOn(IReadOnlyList<string> running) =>
        $"This folder is no longer trusted: {Plural.Listed(running)} {Plural.Agreeing(running.Count, "runs", "run")} on until {Plural.Agreeing(running.Count, "it ends", "they end")} or this window closes";

    /// <summary>A project template that restores what it makes, with no option to skip that, refused in a folder not trusted.</summary>
    public static string RestoresUntrusted(string template) =>
        $"{template} restores the project it makes and cannot be told not to, which would run this folder's MSBuild files: trust the folder first";

    /// <summary>An agent asked to trust a folder, which only the person does: through an action, or by its input into
    /// Passenger's windows.</summary>
    public const string TrustIsThePersons = "Only the person trusts a folder, with Trust Folder over the editors";

    /// <summary>An agent's input reaching the question whether to trust a folder, which only the person answers.</summary>
    public const string TrustAnsweredByThePerson = "Only the person answers whether to trust a folder: no agent's input reaches the question";

    /// <summary>The folder open is trusted already.</summary>
    public const string TrustedAlready = "The folder is trusted already";

    /// <summary>The window has no folder open (a solution, a project or a folder of files).</summary>
    public const string NoFolder = "No folder is open";

    /// <summary>A completion list's filter by the kind of member asked for where none is offered.</summary>
    public const string NoMembersListed = "No completion list of a type's members of that kind is open";

    /// <summary>Parameter Info's next or previous overload asked for where it shows no call of several.</summary>
    public const string NoOverloadsShown = "Parameter Info shows no call of several overloads";

    /// <summary>The folder open has no solution (a project opened alone, or a folder of files).</summary>
    public const string NoSolution = "No solution is open";

    /// <summary>The folder open has neither a solution nor a project, so the Explorer has only its files to show.</summary>
    public const string NoSolutionView = "The open folder has no solution or project: the Explorer shows its files alone";

    /// <summary>The folder open is in no Git working tree.</summary>
    public const string NoRepository = "The folder is not in a Git repository";

    /// <summary>The folder open is in a Git working tree already, which Create Git Repository would make.</summary>
    public const string InRepository = "The folder is in a Git repository already";

    /// <summary>Git has files in conflict, which no commit takes as they are.</summary>
    public const string CommitWithConflicts = "Resolve conflicts in order to commit";

    /// <summary>The Commit window has no file chosen, and no merge to finish or commit to amend.</summary>
    public const string NoFilesChosenToCommit = "No files are chosen to commit";

    /// <summary>The Commit window's message is empty or blank.</summary>
    public const string EmptyCommitMessage = "The commit message is empty";

    /// <summary>Commit Message History in a repository the Commit window has committed nothing in yet.</summary>
    public const string NoCommitMessages = "No message has been committed from the Commit window in this repository yet";

    /// <summary>The Git log's branches panel has HEAD, a group or no row chosen, where a branch or tag is acted on.</summary>
    public const string NoRefChosen = "No branch or tag is chosen";

    /// <summary>The branches panel has no branch chosen, which alone is marked as a favourite.</summary>
    public const string NoBranchChosen = "No branch is chosen";

    /// <summary>The branches panel has no local branch chosen, which alone Update brings up to date.</summary>
    public const string NoLocalBranchChosen = "No local branch is chosen";

    /// <summary>A local branch to update that tracks no remote's branch to bring it up to.</summary>
    public static string Untracked(string branch) => $"{branch} tracks no remote's branch to update from";

    /// <summary>A remote's branch, which a fetch brings up to date where Update brings a local branch.</summary>
    public static string FetchedNotUpdated(string branch) => $"{branch} is a remote's branch, which Git › Fetch brings up to date";

    /// <summary>The branch checked out, which git deletes only once another is.</summary>
    public static string CheckedOut(string branch) => $"{branch} is checked out";

    /// <summary>A branch checked out in another working tree of the repository, which git checks out in one at a time and
    /// deletes only once none has it.</summary>
    public static string InWorktree(string branch, string folder) => $"{branch} is checked out in the worktree {PathNames.Shown(folder, null)}: work on it there";

    /// <summary>The folder has no C# language server: it has neither a solution nor a project, or its window starts none.</summary>
    public const string NoCSharp = "No C# language server runs for this folder";

    /// <summary>What is asked works only in a C# file, and the file shown is another language's.</summary>
    public const string NotCSharp = "The file is not C#";

    /// <summary>Next or Previous Usage where the editor highlights no usage of the symbol at the caret.</summary>
    public const string NoUsagesHighlighted = "No usage of the symbol at the caret is highlighted";

    /// <summary>Quick Implementations where the server knows nothing the caret's symbol is implemented by or declared at.</summary>
    public const string NothingImplements = "Nothing at the caret has an implementation or a declaration to show";

    /// <summary>Error Description where the caret's line has no problem.</summary>
    public const string NoProblemHere = "No problem is marked on the caret's line";

    /// <summary>Expression Type Info where no expression with a type stands at the caret.</summary>
    public const string NoExpressionType = "No expression with a type is at the caret";

    /// <summary>Collapse Selection with nothing selected.</summary>
    public const string NothingSelectedToFold = "Select what to fold first";

    /// <summary>Collapse Block where no block of braces is around the caret.</summary>
    public const string NoBlockHere = "No block of braces is around the caret";

    /// <summary>Cancel Build with no build running.</summary>
    public const string NoBuildRunning = "No build is running";

    /// <summary>Remove All Breakpoints with none in the code (on a line, a lambda, an attribute or a method).</summary>
    public const string NoBreakpointsInCode = "No breakpoint is set in the code";

    /// <summary>VCS Operations where none of the Git menu's actions can run.</summary>
    public const string NoVcsOperations = "No Git action can run here";

    /// <summary>Paste from History before anything was copied, here or on the clipboard.</summary>
    public const string NothingCopied = "Nothing has been copied yet";

    /// <summary>Smart Type Completion where C# expects no type at the caret.</summary>
    public const string NoTypeExpected = "No type is expected at the caret";

    /// <summary>Smart Type Completion where nothing in reach of the caret fits the type expected there.</summary>
    public static string NothingFits(string type) => $"Nothing in reach fits {type} here";

    /// <summary>Go to Region in a C# file with no #region.</summary>
    public static string NoRegions(string file) => $"{file} has no #region";

    /// <summary>The C# language server ended too often and was left stopped, until a person starts it again.</summary>
    public const string CSharpStopped = "The C# language server ended too often and was left stopped until it is started again";

    /// <summary>The C# language server could not start, why in the words of what stopped it, until a person starts it again.</summary>
    public static string CSharpUnavailable(string why) =>
        $"The C# language server could not start, so it has none of the folder's code to answer from until it is started again: {why}";

    /// <summary>The folder's C# language server asked of has not started yet, or has ended: a clause, after what it stopped
    /// or as <see cref="Passenger.Lsp.LanguageServerFailed"/>'s reason, or a sentence through <see cref="Sentence"/>.</summary>
    public const string CSharpNotRunning = "the C# language server is not running";

    /// <summary>A C# file nested past what Passenger reads, which the C# language server is never given.</summary>
    public static string TooDeep(string file) => $"{file} is nested too deeply for Passenger to read, so the C# language server is not given it";

    /// <summary>What the Game window shows while it holds no game: which game draws there, and the keys that take the keys
    /// back from a game that has them.</summary>
    public static string NoGameInWindow(string keys) =>
        "A Godot game whose run configuration starts it in Passenger's Game window draws here (Run › Edit Configurations, Starts). "
        + $"While the game has the keys, {keys} or a click anywhere in Passenger takes them back";

    /// <summary>No game draws in the Game window for the keys to be handed to.</summary>
    public const string NoGameInPane = "No Godot game draws in the Game window: run one whose configuration starts it there";

    /// <summary>A game that runs headless, as one nobody sees does, has no picture for the Game window.</summary>
    public static string GameHeadless(string game) => $"{game} runs headless, with no picture to draw in the Game window";

    /// <summary>A Godot before 4.4 has no embedding of a game in another program's window.</summary>
    public static string GameEmbedsFrom(string? version) =>
        $"Godot {version ?? "of no version said"} draws a game only in a window of its own: the Game window takes Godot 4.4 or newer";

    /// <summary>The Game window holds a game on Windows, on macOS and under X11 alone.</summary>
    public const string GameNotEmbeddedHere = "Passenger draws a game in its Game window on Windows, on macOS and on Linux under X11 (XWayland too); "
        + "here a game draws in a window of its own";

    /// <summary>On macOS a game draws in the Game window through Godot's embedded display, from Godot 4.5.</summary>
    public static string GameEmbedsOnMacFrom(string? version) =>
        $"Godot {version ?? "of no version said"} draws a game only in a window of its own on macOS: the Game window takes Godot 4.5 or newer there, "
        + "whose embedded display shares the game's picture";

    /// <summary>A macOS whose QuartzCore has no CALayerHost cannot show a game's shared picture.</summary>
    public const string GameNoLayerHost = "this macOS has no CALayerHost, through which the Game window shows a game's picture, "
        + "so a game draws in a window of its own";

    /// <summary>The Game window holds one game at a time: another copy, or a game started meanwhile, draws in its own.</summary>
    public static string GameWindowHolds(string game) => $"the Game window holds {game}";

    /// <summary>A game's window never came to the Game window (it ended first, or none came in time), with Godot's last
    /// errors where its console has any.</summary>
    public static string GameWindowNeverCame(string game, string? godotSaid) =>
        $"{game}'s window never came to the Game window" + (godotSaid is null ? "" : $". Godot said: {godotSaid}");

    /// <summary>A C# file come under a project that the language server has not loaded into it (<see
    /// cref="CSharp.CSharpService.UnplacedAsync"/>), whose answers about the solution would leave it out.</summary>
    public static string NotTakenIn(string file, string project) =>
        $"{file} is not loaded into {project} yet: the C# language server has not taken it in after {Durations.Said(CSharp.Patience.Limit)}, "
        + "and would answer without it; try again once it has";

    /// <summary>A command (<paramref name="what"/>) asked of a file the C# language server is ready but has not taken in
    /// yet: one come under a project (<paramref name="project"/>) whose load of it has not ended, else one it is still to open.</summary>
    public static string NotInServerYet(string what, string file, string? project) => project is null
        ? $"{what} needs {file} in the C# language server, which has not taken it in yet: try again in a moment"
        : $"{what} needs {file} in the C# language server, which has not loaded it into {project} yet: try again once it has";

    /// <summary>A project the C# language server was asked to load again (its file or an import changed) that it has not
    /// loaded since (<see cref="CSharp.CSharpService.UnplacedAsync"/>), whose answers would be of the project as it was.</summary>
    public static string NotLoadedAgain(string project) =>
        $"{project} is not loaded again yet: the C# language server has not loaded it as changed after {Durations.Said(CSharp.Patience.Limit)}, "
        + "and would answer from it as it was; try again once it has";

    /// <summary>What an answer given before the C# language server took C# files come under its projects into them went
    /// without (<see cref="CSharp.CSharpService.UntakenAsync"/>), the files named as shown.</summary>
    public static string Untaken(IReadOnlyList<string> files) =>
        $"Without {FirstNamed(files)}, which the C# language server has not loaded into {Plural.Agreeing(files.Count, "its project", "their projects")} yet";

    /// <summary>What an answer of the solution's whole code went without once the wait for the C# language server ran out:
    /// the files come under its projects it has not taken in (<see cref="Untaken"/>), and the projects it answered from as
    /// they were before they changed, not loaded again yet.</summary>
    public static string WentWithout(IReadOnlyList<string> files, IReadOnlyList<string> projects) => string.Join(". ", new[]
    {
        files.Count > 0 ? Untaken(files) : null,
        projects.Count > 0 ? $"From {FirstNamed(projects)} as {Plural.Agreeing(projects.Count, "it was", "they were")} before "
            + $"{Plural.Agreeing(projects.Count, "it", "they")} changed, which the C# language server has not loaded again yet" : null,
    }.OfType<string>());

    /// <summary>The first three names, and how many more.</summary>
    private static string FirstNamed(IReadOnlyList<string> names) =>
        $"{string.Join(", ", names.Take(3))}{(names.Count > 3 ? $" and {Numbers.Said(names.Count - 3)} more" : string.Empty)}";

    /// <summary>The folder's projects have no .NET SDK to load, build and run them with: the one its global.json asks
    /// for (named from <paramref name="folder"/>) is not installed, none is, or dotnet found none for another reason it
    /// gives.</summary>
    public static string NoSdk(MissingSdk missing, string? folder) => missing switch
    {
        { Asked: { } asked } => $"The .NET SDK {asked} that {(missing.GlobalJson is { } file ? PathNames.Shown(PathNames.Canonical(file), folder) : "global.json")} "
            + "asks for is not installed: install it, or have global.json name one that is",
        { NoHost: true } => NoDotnet,
        { ForOtherProcessor: true } => OnlyNativeSdk,
        { NoneInstalled: true } => $"{RuntimeAlone(null)}: {NeedsTheSdk}",
        _ => $"No .NET SDK was found for this folder: {ChildResult.FirstLineOf(missing.Said)}",
    };

    /// <summary>A C# code tool asked while the folder's projects have no .NET SDK to load with, as <paramref name="lacking"/>
    /// says it: the language server holds none of their code to answer from.</summary>
    public static string ProjectsUnloadable(string lacking) =>
        $"The folder's projects are not loaded, so the C# language server has none of their code to search or read: {lacking}";

    /// <summary>A C# code tool asked about a file of a project the language server met an error loading and holds no code
    /// of, with what it met.</summary>
    public static string ProjectNotLoaded(string project, string error) =>
        $"{project} did not load into the C# language server, which holds none of its code to answer from: {error.TrimEnd('.')}. Mend that and it loads again; "
        + "diagnostics with no path lists what each project met loading";

    /// <summary>A C# code tool's answer, empty, while projects the language server met an error loading may hold what it
    /// looked for.</summary>
    public static string NoneWhileUnloaded(string none, IReadOnlyList<(string Project, string Error)> failed) =>
        $"{none}, but the C# language server met errors loading {Plural.Listed([.. failed.Select(f => f.Project)])}, whose code it may not hold: "
        + $"{string.Join("; ", failed.Select(f => $"{f.Project}: {f.Error}"))}";

    /// <summary>A C# code tool asked while the language server is still loading the folder's projects after the wait.</summary>
    public static string StillLoading(TimeSpan waited, string state) =>
        $"The C# language server is still loading the folder's projects after {Durations.Said(waited)} ({(state.Length > 0 ? state : "starting")}), "
        + "so it has nothing of them to answer from yet: ask again once workspace says languageServer is ready";

    /// <summary>A code tool asked of a file whose language server (<paramref name="server"/>, "YAML") is still starting
    /// after the wait.</summary>
    public static string StillStarting(string server, TimeSpan waited) =>
        $"The {server} language server is still starting after {Durations.Said(waited)}, so it has nothing to answer from yet: ask again in a moment";

    /// <summary>A C# code tool asked while what a restore brought, or a load again, is still to reach the language server
    /// after the wait: <paramref name="coming"/> says what, as the agent was told it waited for.</summary>
    public static string StillComing(TimeSpan waited, string coming) =>
        $"Passenger waited {Durations.Said(waited)} for {coming}, which has not come, so the answer would be of the code before it: ask again in a moment";

    /// <summary>An x64 Passenger the system translates found only a .NET SDK of the system's own processor, whose MSBuild it
    /// cannot load (<see cref="MissingSdk.ForOtherProcessor"/>).</summary>
    public static string OnlyNativeSdk => $"This {TranslatedBuild}, loads projects only with {X64Sdk}, and {ThisComputer}'s is {SystemsProcessor}'s: "
        + $"{SystemsOwnPassenger}, or install the x64 .NET SDK beside it";

    /// <summary>An x64 Passenger the system translates with no .NET SDK at all: no .NET, or a runtime alone, in
    /// <paramref name="runtime"/>.</summary>
    public static string TranslatedWithoutSdk(string? runtime) =>
        $"{(runtime is null ? NoneOfDotnet : RuntimeAlone(runtime))}, and this {TranslatedBuild}, loads projects "
        + $"only with {X64Sdk}: {SystemsOwnPassenger} and installs the .NET SDK for it, or install the x64 .NET SDK yourself";

    /// <summary>Install .NET SDK in an x64 Passenger the system translates: the SDK it installs, the system's own processor's,
    /// is one this Passenger cannot load.</summary>
    public static string InstallsOnlyNative => $"Install .NET SDK installs only {ThisComputer}'s own .NET SDK, {SystemsProcessor}'s, which this {TranslatedBuild}, cannot load: "
        + $"{SystemsOwnPassenger}, or install the x64 .NET SDK yourself";

    /// <summary>An x64 build of Passenger, as the system translating it runs it.</summary>
    private static string TranslatedBuild => OperatingSystem.IsMacOS() ? "Intel build of Passenger, run by Rosetta" : "x64 build of Passenger, run by emulation";

    private static string X64Sdk => OperatingSystem.IsMacOS() ? "an Intel (x64) .NET SDK" : "an x64 .NET SDK";

    private static string ThisComputer => OperatingSystem.IsMacOS() ? "this Mac" : "this computer";

    private static string SystemsProcessor => OperatingSystem.IsMacOS() ? "Apple Silicon" : "Arm64";

    /// <summary>The way on for an x64 Passenger the system translates: the build for the system's own processor.</summary>
    private static string SystemsOwnPassenger => $"download Passenger for {SystemsProcessor}, which suits {ThisComputer}";

    /// <summary>No dotnet at all, runtime or SDK, was found.</summary>
    public const string NoDotnet = $"{NoneOfDotnet}: files open and edit, and Git and the terminal work, but {NeedsTheSdk}";

    private const string NoneOfDotnet = "No .NET is installed";

    /// <summary>A dotnet with runtimes alone, in <paramref name="root"/> where it is known.</summary>
    private static string RuntimeAlone(string? root) => $"Only the .NET runtime is installed{(root is { } at ? $", in {PathNames.Shown(at, null)}" : string.Empty)}";

    private const string NeedsTheSdk = "projects need the .NET SDK to load, build, run and test";

    /// <summary>What .NET the machine or the folder lacks, as the banner, an action's refusal and an agent say it; null with
    /// nothing lacking.</summary>
    public static string? Dotnet(DotnetWant want, string? folder) => want.Lack switch
    {
        DotnetLack.NoDotnet => NoDotnet,
        DotnetLack.RuntimeOnly => $"{RuntimeAlone(want.Root)}: {NeedsTheSdk}",
        DotnetLack.OtherProcessor => want.SystemsOwn is null ? TranslatedWithoutSdk(want.Root) : OnlyNativeSdk,
        DotnetLack.GlobalJsonSdk or DotnetLack.NoSdk => NoSdk(want.Missing!, folder),
        DotnetLack.OlderThanProject when want.ChosenBy is { } asked => $"{want.Newer!.Value.Project} targets .NET {want.Newer.Value.Framework}, which the .NET SDK {want.Sdk} "
            + $"does not build: {PathNames.Shown(asked.Path, folder)} chose it, asking for {asked.Version}{(asked.RollForward is { } roll ? $" with rollForward {roll}" : string.Empty)}; "
            + (want.Builders.Count > 0
                ? $"the {Plural.Noun(want.Builders.Count, ".NET SDK")} {Plural.Listed(want.Builders)} installed {Plural.Agreeing(want.Builders.Count, "builds", "build")} it: have global.json ask for {Plural.Agreeing(want.Builders.Count, "it", "one of them")}"
                : $"no .NET SDK installed builds .NET {want.Newer.Value.Framework}: install the .NET {want.Newer.Value.Framework} SDK, and have global.json ask for it"),
        DotnetLack.OlderThanProject => $"{want.Newer!.Value.Project} targets .NET {want.Newer.Value.Framework}, which the .NET SDK {want.Sdk} does not build: "
            + $"it needs the .NET {want.Newer.Value.Framework} SDK",
        DotnetLack.OffPath => $"Passenger uses the .NET SDK {want.Sdk} in {PathNames.Shown(want.Root!, null)}, which your shell's PATH does not name, "
            + "so a terminal outside Passenger finds no dotnet",
        _ => null,
    };

    /// <summary>What an agent's ide_action of Install .NET SDK or Add .NET to the Shell's PATH is refused with.</summary>
    public const string AgentInstalls = "An agent's own Passenger, which nobody is at, never installs software: ask the person, who does it with the banner over "
        + "Passenger's window, or Tools, Install .NET SDK…";

    /// <summary>An agent's input into Passenger's window asking to install the .NET SDK, or answering the person's question
    /// whether to: only the person installs it there, an agent asking them through ide_action.</summary>
    public const string InstallIsThePersons = "Only the person installs the .NET SDK by Passenger's window, never an agent's input: "
        + "an agent's ide_action of Install .NET SDK… asks the person at their window";

    /// <summary>An agent's Add .NET to the Shell's PATH: the person's shell is theirs to change.</summary>
    public const string AgentChangesPath = "An agent never changes the person's shell PATH: ask the person, who does it with the banner over Passenger's window, "
        + "or Tools, Add .NET to the Shell's PATH…";

    /// <summary>An agent's Add or Remove the passenger-ide Command, or its input answering the person's question of it: the
    /// person's shell is theirs to change.</summary>
    public static readonly string CommandIsThePersons = $"An agent never adds or removes the person's {PassengerCommand.Name} command: ask the person, who does it "
        + $"with Tools, Add the {PassengerCommand.Name} Command… or Remove the {PassengerCommand.Name} Command…";

    /// <summary>Add or Remove the passenger-ide Command where Passenger's installer makes the command (Windows) or
    /// install.sh does (Linux); null on macOS, where the person does.</summary>
    public static string? CommandMadeElsewhere =>
        OperatingSystem.IsWindows() ? $"On Windows, Passenger's installer puts the {PassengerCommand.Name} command on your Path, and uninstalling takes it off"
        : OperatingSystem.IsMacOS() ? null
        : $"On Linux, install.sh links the {PassengerCommand.Name} command, and install.sh --uninstall removes it";

    /// <summary>The passenger-ide command while the window has not yet read what stands at its place.</summary>
    public static readonly string CommandStillRead = $"Passenger is still reading where the {PassengerCommand.Name} command stands";

    /// <summary>The passenger-ide command of a Passenger in no app bundle, as a build from source is not.</summary>
    public static readonly string CommandOfNoBundle = $"This Passenger is in no app bundle, as a build from source is not, so it carries no {PassengerCommand.Name} command";

    /// <summary>The passenger-ide command of a Passenger macOS runs from a copy of its own, which goes as it ends.</summary>
    public static string CommandOfTranslocated(string bundle) =>
        $"macOS runs this Passenger from a copy of its own ({bundle}), which goes as it ends: once Passenger is in Applications, its {PassengerCommand.Name} command can be added";

    /// <summary>The passenger-ide command of a Passenger on a disk image, which goes as the image is ejected.</summary>
    public static string CommandOfDiskImage(string bundle) =>
        $"This Passenger runs from {bundle}, on a disk image or a disk it cannot write: drag it onto Applications, then add its {PassengerCommand.Name} command";

    /// <summary>The passenger-ide command where the app's bundle carries no launcher for it.</summary>
    public static string CommandNotCarried(string launcher) => $"This Passenger carries no {PassengerCommand.Name} command at {launcher}";

    /// <summary>The passenger-ide command's place held by another program, a folder, or a link to one: the person's own.</summary>
    public static string CommandOthers(string link) => $"{PathNames.Shown(link, null)} is another program's, not Passenger's: move it away, then add the command again";

    /// <summary>Add the passenger-ide Command where it is there, leading to this Passenger, its folder on the PATH.</summary>
    public static string CommandThere(string link) => $"{PathNames.Shown(link, null)} is there already, leading to this Passenger";

    /// <summary>Remove the passenger-ide Command where nothing is at its place.</summary>
    public static string NoCommand(string link) => $"There is no {PassengerCommand.Name} command at {PathNames.Shown(link, null)} to remove";

    /// <summary>The banner's word of a C# file of no project shown while the machine has no .NET SDK: what Roslyn's server
    /// cannot give it, and why.</summary>
    public static string LooseWithoutSdk(string file) =>
        $"{file} is a C# file of no project, which knows no framework types until a .NET SDK is installed: Roslyn reads a loose file's framework only from a project the SDK's MSBuild builds";

    /// <summary>Install .NET SDK with nothing it installs mending the want: the newest SDK found, and where; or, for a
    /// Passenger the system translates, which loads none of the system's own SDKs, that it installs none it loads.</summary>
    public static string NothingToInstall(DotnetMachine? machine, DotnetWant want) =>
        want.Lack == DotnetLack.OtherProcessor || want is { Translated: true, Install: not null } ? InstallsOnlyNative
        : machine?.NewestSdk is { } sdk && machine.Found is { } found
            ? $"The .NET SDK {want.Sdk ?? sdk} is installed, in {PathNames.Shown(found.Root, null)}: nothing is missing"
            : "Passenger is still looking for .NET";

    /// <summary>Add .NET to the Shell's PATH where the shell's PATH has it already.</summary>
    public static string OnPathAlready(string root) => $"{PathNames.Shown(root, null)} is on your shell's PATH already";

    /// <summary>No run configuration to run, debug or profile (the <paramref name="verb"/>): the folder has no SDK to
    /// load its projects with (<see cref="NoSdk"/>), or no executable project.</summary>
    public static string NothingTo(string verb, MissingSdk? missing, string? folder) =>
        missing is null ? $"Nothing to {verb}: no executable project" : NoSdk(missing, folder);

    /// <summary>A build, publish or run (the <paramref name="verb"/>) asked for with no folder open, or once its folder has
    /// closed.</summary>
    public static string NoFolderTo(string verb) => $"Nothing to {verb}: no folder is open";

    /// <summary>Why no build configuration can be chosen: the folder's solution or projects have not been read yet.</summary>
    public const string NoBuildConfigurations = "The folder's build configurations are not read yet: its solution or projects are still being read";

    /// <summary>An action on a Godot project asked for in a folder that holds none.</summary>
    public const string NoGodotProject = "The open folder holds no Godot project (project.godot)";

    /// <summary>The Aspire window, in a folder holding no AppHost: no project Aspire's SDK makes one, no single-file AppHost.</summary>
    public const string NoAppHost = "The open folder holds no Aspire AppHost";

    /// <summary>The Requests window, in a folder holding no project a web SDK builds.</summary>
    public const string NoWebApp = "The open folder holds no ASP.NET Core app";

    /// <summary>The Endpoints window, in a folder holding no web app's routes and no .http or .rest file's requests.</summary>
    public const string NoWebRequests = "The open folder holds no ASP.NET Core app and no .http file";

    /// <summary>The Step Library, in a folder holding no project that takes Reqnroll's or SpecFlow's packages.</summary>
    public const string NoStepDefinitions = "The open folder holds no Reqnroll or SpecFlow project";

    /// <summary>Live Visual Tree, in a folder holding no app Passenger's inspector looks into, with none inspected now.</summary>
    public const string NoInspectedApp = "The open folder holds no Avalonia, WPF, Windows Forms, .NET MAUI or Godot app, and no app is inspected";

    /// <summary>Set Next Statement asked of a program whose debugger cannot move where a paused thread goes on (a Godot game's
    /// GDScript).</summary>
    public const string NoNextStatement = "The paused program's debugger cannot move where it goes on, so there is no Set Next Statement there";

    /// <summary>A build asked of a folder with no solution or project file MSBuild could build, which it would refuse in its own
    /// words (MSB1003); short enough to read whole in the smallest window's status bar.</summary>
    public const string NothingToBuild = "The open folder has no solution or project to build";

    /// <summary>A program whose output said it needs a terminal for a call (<see cref="Run.TerminalNeeds"/>) where the Run
    /// or Debug window's console gave it pipes.</summary>
    public static string NeedsTerminal(string program, string call, string window) =>
        $"{program} needs a terminal for {call}: the {window} window gives it pipes";

    /// <summary>Run or Debug in the Terminal asked for while no program the window's console holds said it needs one.</summary>
    public static string NoTerminalNeed(string window) => $"No program in the {window} window said it needs a terminal";

    /// <summary>Debug or Profile asked of a configuration running a Node package's script, which Run alone starts.</summary>
    public static string ScriptOnlyRuns(string name) =>
        $"{name} runs a package.json script with its package manager, which Run starts: Passenger debugs and profiles .NET programs";

    /// <summary>A build asked of a folder whose one kind of project is a Godot project of GDScript alone, which builds nothing.</summary>
    public const string NothingToBuildOfScripts = "The open folder's Godot project is GDScript alone, which Godot runs as it is: there is nothing to build (Run, Debug and Profile start its game)";

    /// <summary>A Godot game's GoDotTest tests asked to run where no scene of the game calls GoTest.RunTests, so a run would
    /// start Godot and run none until stopped: said before it starts.</summary>
    public static string GoDotTestNeverRuns(string game) =>
        $"{game} never runs its GoDotTest tests: no scene of it calls GoTest.RunTests, neither its main scene nor a test scene (GoDotTest's README makes one in {Testing.GoDotTests.TestFolder}), so Godot is not started for them";

    /// <summary>A built test assembly whose tests its runner could not look through, with what the runner said: a clause, after
    /// what found no test or a run's tally.</summary>
    public static string TestsNotFound(string assembly, string framework, string why) => $"the tests in {assembly} ({framework}) could not be found: {why}";

    /// <summary>Run Tests at the caret took no test of the method or class there once the tests were looked for: why, where
    /// its project's tests could not be found (<see cref="TestsNotFound"/>), else what is likeliest.</summary>
    public static string NoTestOf(string name, string? unfound) => unfound is null
        ? $"No test of {name} was found; is the project a test project?"
        : $"No test of {name} was found: {unfound}";

    /// <summary>A test chosen of one of the classes GoDotTest cannot tell apart (<see cref="Testing.GoDotTests.Alike"/>).</summary>
    public static string GoDotTestAlike(string suite, IReadOnlyList<string> classes) =>
        $"Not run: GoDotTest knows {Plural.Listed(classes)} by one name ({suite}), so it can run neither alone. Rename one, or choose all of their tests together";

    /// <summary>A game's GoDotTest tests asked to run where they cannot start (<see cref="Testing.GoDotTests.Start"/>): no
    /// scene runs them, or the scene its run settings name is not the game's or runs none; null where they can.</summary>
    public static string? GoDotTestStart(string game, Testing.GoDotTests.Start start)
    {
        string named = $"{(start.Settings is { } file ? PathNames.Shown(file, null) : "The run settings file")} names {start.Scene} for {game}'s GoDotTest tests (its {Testing.GoDotTests.SceneParameter} parameter)";
        return start.Problem switch
        {
            Testing.GoDotTests.StartProblem.NoScene => GoDotTestNeverRuns(game),
            Testing.GoDotTests.StartProblem.ChosenMissing => $"{named}, but the game has no such scene, so Godot is not started for them",
            Testing.GoDotTests.StartProblem.ChosenRunsNone => $"{named}, but no script of that scene calls GoTest.RunTests, so Godot is not started for them",
            _ => null,
        };
    }

    /// <summary>A build or publish asked for while another of the window's runs: one builds at a time.</summary>
    public const string Building = "Another build is running";

    /// <summary>What a build whose projects' assemblies hold Avalonia XAML never compiled says (<see cref="Passenger.Xaml.Metadata.UncompiledXaml"/>),
    /// and a preview whose previewer Avalonia's runtime ended for it: the build that last compiled their C# stopped before
    /// Avalonia's XAML compiler finished, and each build since took that as up to date. A rebuild runs both compilers again.</summary>
    public static string XamlUncompiled(IReadOnlyList<string> projects) =>
        $"{(projects.Count == 1 ? $"{projects[0]}'s build has" : $"The builds of {Plural.Listed(projects)} have")} no compiled XAML: Avalonia's XAML compiler "
        + $"did not finish in the build that last compiled {Plural.Agreeing(projects.Count, "its", "their")} C#, and the builds since took that as up to date. "
        + "Rebuild to compile it";

    /// <summary>An AppHost the folder's session no longer runs, asked of by an agent's call or an Aspire tool: it stopped,
    /// or the folder was left.</summary>
    public const string AppHostNotOwned = "The AppHost is no longer owned by the workspace";

    /// <summary>An AppHost's folder left, or the AppHost stopped or started again, while what was asked of it waited.</summary>
    public const string AppHostChanged = "The workspace or owned AppHost changed";

    /// <summary>Why a file shown has no outline for Structure and File Structure: they list what its language server
    /// outlines, and none has it open.</summary>
    public static string NoOutline(string file) => $"{file} has no outline: no language server has it open";

    /// <summary>Why Jump to Navigation Bar opens nothing in a file shown: its outline is read, and holds no declaration,
    /// heading or key.</summary>
    public static string DeclaresNothing(string file) => $"{file} declares nothing to go to";

    /// <summary>Why Context Help has nothing to name in a file shown: neither C#'s server nor GDScript's has it, and it is no shader.</summary>
    public static string NoContextHelp(string file) => $"{file} has no context help: Context Help opens Godot's reference from C#, GDScript and shaders";

    /// <summary>Context Help with the caret on nothing of Godot's engine: the project's own code, or .NET's.</summary>
    public const string NoGodotDocumentation = "The caret is on no Godot class or member, whose page in Godot's class reference Context Help opens";

    /// <summary>Context Help in a shader with the caret on nothing of Godot's shading language: the shader's own names.</summary>
    public const string NoShaderDocumentation = "The caret is on no built-in, mode, hint or function of Godot's shading language, whose place in its reference Context Help opens";

    /// <summary>An action whose whole work is to move the person's keys (Focus Editor), asked for by an agent, which never
    /// moves them.</summary>
    public const string PersonsKeys = "It moves the person's keys, which only the person does";

    /// <summary>An action that asks the person in a popup or dialog (Go to File, Rename, Settings), asked for by an agent,
    /// whose own tools do what those ask for.</summary>
    public const string AsksThePerson = "It asks the person in a popup or dialog, which would take their keys: the agent's own tools do its work";

    /// <summary>An agent's call whose question the window took away as it left the folder it was asked in (<paramref
    /// name="left"/>), for another or none, before the person answered: an answer would have allowed it where it no longer is.</summary>
    public static string FolderChanged(string? left) =>
        $"The folder changed while the person was asked: the Passenger window left {left ?? "the folder it had open"} before they answered, so nothing "
        + "was done. A later call is asked about the folder open now";

    /// <summary>Neither a selection nor a name is at the caret for the debugger to evaluate.</summary>
    public const string NoExpression = "No expression is at the caret";

    /// <summary>A debugger's action asked for with no program debugged, as the debugger's own answer says it.</summary>
    public const string NothingDebugged = DebuggerWords.NothingDebugged;

    /// <summary>Set Data Breakpoint asked of a row of Variables that is no member of a value, as the debugger's own answer
    /// says it.</summary>
    public const string DataBreakpointNotAMember = DebuggerWords.NotAMember;

    /// <summary>A run configuration given no name, which names it in the list and on the toolbar.</summary>
    public const string ConfigurationUnnamed = "A configuration needs a name";

    /// <summary>Why an edit left files as they were: each was edited after the edit was worked out, so its places no longer
    /// stand where it was made for.</summary>
    public static string EditedMeanwhile(int files) => $"{Plural.Agreeing(files, "it was", "they were")} edited meanwhile";

    /// <summary>What an edit did, and the files it left as they were for being edited meanwhile: "Renamed in 3 files, but not
    /// in A.cs and B.cs: they were edited meanwhile"; what it did alone where it left none.</summary>
    public static string EditedMeanwhile(string done, IReadOnlyList<string> files) =>
        files.Count == 0 ? done : $"{done}, but not in {Plural.Listed(files)}: {EditedMeanwhile(files.Count)}";

    /// <summary>An edit said not applied to the files it left as they were for being edited meanwhile: "Extract Method was not
    /// applied to A.cs: it was edited meanwhile".</summary>
    public static string NotAppliedEditedMeanwhile(string what, IReadOnlyList<string> files) =>
        $"{what} was not applied to {Plural.Listed(files)}: {EditedMeanwhile(files.Count)}";

    /// <summary>A JSON property's rename whose property was edited before the rename could be placed.</summary>
    public const string PropertyEditedMeanwhile = "Rename was not applied: the property was edited meanwhile";

    /// <summary>Go to Declaration found nothing to go to where it was asked.</summary>
    public const string NoDeclaration = "Cannot find a declaration to go to";

    /// <summary>No project of the folder is a Reqnroll or SpecFlow one whose bindings the C# server has read.</summary>
    public const string NoReqnrollProject = "No Reqnroll or SpecFlow project's step definitions are read: open its .feature files, or wait for C# to load";

    /// <summary>A feature's diagnostics with no step definitions read to match its steps against: the C# language server,
    /// which reads them, runs and did not answer for them (<paramref name="serverRuns"/>), or runs for no folder.</summary>
    public static string StepsUnmatched(bool serverRuns) =>
        StepsUnmatchedFor(serverRuns ? "the C# language server did not answer for them" : CSharpNotRunning);

    /// <summary>A feature's diagnostics where its project's last load failed with an error (<paramref name="loadError"/>), so
    /// the C# language server, which reads the step definitions, holds none of the project's.</summary>
    public static string StepsUnmatched(string project, string loadError) =>
        StepsUnmatchedFor($"the C# language server took in nothing of {project}, whose load failed: {loadError}");

    private static string StepsUnmatchedFor(string why) =>
        $"No step definitions were read to match its steps against ({why}): no step is marked undefined, nor an argument its definition cannot take";

    /// <summary>A step's phrase asked to be written where the editor shows no .feature file.</summary>
    public const string NoFeature = "The editor shows no .feature file: open the feature to write the step into";

    /// <summary>The Step Library's action asked with no definition chosen there.</summary>
    public const string NoStepChosen = "No step definition is chosen in the Step Library";

    /// <summary>Go to Declaration of an overload an unloaded project's source no longer declares, gone to by its name.</summary>
    public const string OverloadNotInSource = "This overload is not in the source, which has changed since its assembly was built: went to the declarations of that name";

    /// <summary>What a start or an action under way was working in has ended: its folder was left or closed.</summary>
    public const string SolutionClosed = "The solution was closed";

    /// <summary>A debug session or Hot Reload asked of a folder the window is leaving, whose debugging is ending.</summary>
    public const string WorkspaceEnding = "The workspace is ending";

    /// <summary>A project named (as it is shown) that the folder's projects do not hold.</summary>
    public static string NotOfFolder(string project) => $"{project} is not one of the folder's projects";

    /// <summary>A row of the Explorer (named as it is shown) that is no project, which an action on a project was asked of.</summary>
    public static string NotAProject(string name) => $"{name} is not a project";

    /// <summary>A row of the Explorer (named as it is shown) that Include in Project was asked of, which is no file a shared
    /// project's items file leaves out.</summary>
    public static string NotLeftOut(string name) => $"{name} is no file a shared project leaves out";

    /// <summary>A row of the Explorer (named as it is shown) that Exclude from Project was asked of, which is no file a
    /// shared project's items file names.</summary>
    public static string NotSharedFile(string name) => $"{name} is no file of a shared project";

    /// <summary>A shared project (named as it is shown), which an action on a project that builds was asked of.</summary>
    public static string SharedProject(string name) => $"{name} is a shared project: its files build in the projects importing it";

    /// <summary>A program, container, resource, game or run (named as it is shown) that has not started, or has ended.</summary>
    public static string NotRunning(string name) => $"{name} is not running";

    /// <summary>A service of a run of services (each named as it is shown) that the run has not started, or that has ended.</summary>
    public static string NotRunning(string service, string run) => $"{service} of {run} is not running";

    /// <summary>Hot Reload asked of a program the debugger no longer runs, or never ran.</summary>
    public const string ProgramNotRunning = "The program is not running";

    /// <summary>Hot Reload asked of a program whose build (its project, or one it references) is of another configuration or
    /// platform than the one the language server reads the code as, the folder's: its edits would be made for other code.</summary>
    public static string BuiltUnlike(string program, string project, string runs, string read) =>
        $"{program} runs {project} built as {runs}, while the editor's code is read as the folder builds it, as {read}: Hot Reload would make its changes "
        + $"for code {program} does not run, so it applies none. Choose {runs} for the folder, or start {program} as the folder builds, to apply them";

    /// <summary>The container engine's CLI answers, but its engine does not, and it gave no words of its own.</summary>
    public const string EngineNotRunning = "The container engine is not running";

    /// <summary>An image built for x86-64 alone (SQL Server's, <see cref="BackingService.X64Only"/>) on an Arm engine without
    /// Rosetta: one that runs no x86-64 image, or emulates x86-64 otherwise (QEMU), under which SQL Server fails as it starts.
    /// Docker Desktop and Podman are told how they would use Rosetta.</summary>
    public static string X64OnlyUnrunnable(string what, Containers.EngineState engine) =>
        $"{what} can't run on this {(OperatingSystem.IsMacOS() ? "Mac" : "machine")}: its image is built for x86-64 alone, and {engine.EngineName} "
        + (engine.Amd64 == Containers.Amd64Runs.Never ? "runs no x86-64 image here" : $"emulates x86-64 here without Rosetta, under which {what} fails as it starts")
        + (engine.DockerDesktop ? ". Choose the Apple Virtualization framework and turn on Use Rosetta for x86_64/amd64 emulation on Apple Silicon in Docker Desktop's settings (General)"
            : engine.IsPodman ? ". Run it on a Podman machine that uses Rosetta" : string.Empty);

    /// <summary>A profile asked for while another run of the window is profiled or starting to be: one records at a time.</summary>
    public const string Profiling = "A run is already being profiled";

    /// <summary>An AppHost run whose resource connection (<see cref="Aspire.AspireAppRun.Interactions"/>) is not made yet.</summary>
    public const string AppHostStarting = "The AppHost resource connection is still starting";

    /// <summary>A database action with none of the explorer's connections chosen.</summary>
    public const string NoDatabaseConnection = "Choose a database connection first";

    /// <summary>Open Table Data with no table or view chosen in the Database window.</summary>
    public const string NoDatabaseTable = "Choose a table or view first";

    /// <summary>Stop Database Operation with none running.</summary>
    public const string NoDatabaseOperation = "No database operation is running";

    /// <summary>Run Statement with no SQL file shown.</summary>
    public const string NoSqlFile = "Open a SQL file or console first";

    /// <summary>Explain Plan of a statement that is no query or change of rows (DDL, a session's SET), or an EXPLAIN already.</summary>
    public const string NoPlan = "Only a query or a change of rows has a plan to explain";

    /// <summary>Query History, Explain Plan or a console's own action with no console of a connection shown.</summary>
    public const string NoSqlConsole = "Open a query console of a connection first";

    /// <summary>Run Statement with the caret where no statement stands.</summary>
    public const string NoSqlStatement = "No statement here to run";

    /// <summary>Run Statement while the console runs one.</summary>
    public const string SqlRunning = "A statement is running in this console";

    /// <summary>Stop Statement with none running in the file shown.</summary>
    public const string NoSqlRunning = "No statement is running in this console";

    /// <summary>A database action while the Database window reads a connection.</summary>
    public const string DatabaseBusy = "A database operation is under way";

    /// <summary>A table's changes, or an import, on a database that writes no row by its key.</summary>
    public static string NoRowEdits(string kind) => $"{kind} changes rows by statements, not one by one: change them in a query console";

    /// <summary>A DuckDB connection opened where its engine could not be had: a release's download that failed or did not match.</summary>
    public static string NoDuckDbEngine(string why) => $"DuckDB could not be opened: {why}";

    /// <summary>Manual commit on a database whose statements each stand alone.</summary>
    public static string NoTransactions(string kind) => $"{kind} runs each statement alone, with no transaction to commit or roll back";

    /// <summary>SQL's own reading of a database that speaks no SQL (its DDL, a plan, an import of rows, a whole table's
    /// export): a document database's documents and Redis's keys are read through their own clients, in their own queries.</summary>
    public static string NotSql(string kind) => $"{kind} is read in its own queries, in a console of it, not as SQL's tables";

    /// <summary>Set Time to Live with no Redis key chosen in the explorer or shown among the editors.</summary>
    public const string NoRedisKey = "Choose a Redis key first";

    /// <summary>A time to live that is no whole number of seconds above 0 (none keeps the key for good).</summary>
    public const string NoTimeToLive = "Seconds, a whole number above 0, or none to keep the key for good";

    /// <summary>A Redis key's time to live set where the key is gone (it expired, or another deleted it).</summary>
    public static string KeyGone(string key) => $"{key} is gone: read its database again";

    /// <summary>A DynamoDB item added without its table's key, which every item holds.</summary>
    public static string ItemLacksKey(string key) => $"An item added needs its key, {key}";

    /// <summary>A DynamoDB table's changes, refused whole as an item they change no longer stands as it was read.</summary>
    public static string ItemChanged(string table) => $"An item of {table} changed since it was read, so nothing was written: read the table again";

    /// <summary>A DynamoDB connection naming an AWS profile the person's AWS files hold none of.</summary>
    public static string NoAwsProfile(string profile) => $"No AWS profile {profile} in your AWS credentials or config files";

    /// <summary>A kind of database a cloud sign-in does not sign in to.</summary>
    public static string NoCloudSignIn(string kind) => $"{kind} on AWS takes no IAM authentication: sign in by a Secrets Manager secret or a password";

    /// <summary>A kind of database no Azure server signs in to by Microsoft Entra ID.</summary>
    public static string NoEntraSignIn(string kind) =>
        $"{kind} takes no Microsoft Entra ID sign-in: Azure's SQL databases, PostgreSQL and MySQL servers do";

    /// <summary>A cloud sign-in that names no region, to a host that names none either.</summary>
    public static string NoAwsRegion(string host) => $"{host} names no AWS region, nor does the profile: give the region in the connection's settings";

    /// <summary>IAM authentication signs in as the connection's own user, which it names none of.</summary>
    public const string NoIamUser = "IAM authentication signs in as the database user the connection names: give its user";

    /// <summary>Redshift's IAM sign-in to a host that names no cluster or workgroup, with none given.</summary>
    public static string NoRedshiftCluster(string host) => $"{host} names no Redshift cluster or workgroup: give its name in the connection's cloud sign-in";

    /// <summary>Redshift refused the credentials an IAM sign-in asked of it.</summary>
    public static string RedshiftRefusedIam(string cluster, string said) => $"Redshift gave no credentials for {cluster}: {said}";

    /// <summary>A Secrets Manager secret that could not be read, or held no sign-in.</summary>
    public static string SecretUnread(string secret, string said) => $"Secrets Manager's secret {secret} gave no sign-in: {said}";

    /// <summary>An AWS profile Passenger signs in with names something it cannot sign in by.</summary>
    public static string AwsProfileUnusable(string profile, string what) => $"AWS profile {profile} signs in by {what}, which Passenger cannot sign in with";

    /// <summary>An AWS profile's credential_process failed or answered what AWS's credentials are not.</summary>
    public static string AwsProcessFailed(string profile, string said) => $"AWS profile {profile}'s credential_process gave no credentials: {said}";

    /// <summary>An AWS profile that signs in through IAM Identity Center has no sign-in that holds.</summary>
    public static string AwsSignInNeeded(string profile) =>
        $"AWS profile {profile} signs in through IAM Identity Center and has no sign-in that holds: choose Sign In… on its connection's menu";

    /// <summary>Sign In asked of a connection that signs in by none of the sign-ins the person makes in their browser.</summary>
    public const string NoBrowserSignIn = "The chosen connection signs in through neither Microsoft Entra ID nor an AWS profile's IAM Identity Center";

    /// <summary>A Microsoft Entra ID account a connection signs in as, with no sign-in that holds; or none signed in.</summary>
    public static string AzureSignInNeeded(string? account) => account is null
        ? "No Microsoft Entra ID account is signed in: choose Sign In… on the connection's menu"
        : $"Microsoft Entra ID account {account} has no sign-in that holds: choose Sign In… on its connection's menu";

    /// <summary>A connection that names no Microsoft Entra ID account, with several signed in.</summary>
    public const string AzureAccountUnchosen = "Several Microsoft Entra ID accounts are signed in: choose the connection's account in its settings";

    /// <summary>Microsoft Entra ID's sign-in failed, or Azure refused what was asked of it.</summary>
    public static string AzureRefused(string what, string said) => $"Azure refused {what}: {said}";

    /// <summary>IAM Identity Center's device sign-in was refused, or ran out before the person approved it.</summary>
    public static string AwsSignInNotApproved(string profile) => $"AWS profile {profile}'s sign-in was not approved in the browser before its code ran out";

    /// <summary>A Redis key's changes, refused whole as a value they change no longer stands as it was read.</summary>
    public static string KeyChanged(string key) => $"{key} changed since it was read, so nothing was written: read it again";

    /// <summary>A stream's entry changed, which Redis never does: its entries are added and deleted alone.</summary>
    public const string StreamEntriesFixed = "A stream's entries are only added and deleted, never changed";

    /// <summary>A Cosmos DB console of a connection that names no database, whose containers it would query.</summary>
    public static string NoCosmosDatabase(string connection) => $"{connection} names no database: add Database=… to its connection string to query its containers";

    /// <summary>A console's file run against a connection whose queries are in another language: MongoDB's shell or Redis's
    /// commands on SQL's databases, or SQL on MongoDB or Redis.</summary>
    public static string ConsoleOfAnother(string language, string kind) => $"A {language} file does not run against {kind}: open a console of the connection";

    /// <summary>A schema diagram or comparison of a database whose keys and constraints these read in no way of its own.</summary>
    public static string NoSchemaReads(string kind) => $"Schema diagrams and comparisons read SQLite, PostgreSQL, SQL Server and MySQL, not {kind}";

    /// <summary>A statement, or a table's changes, that would change data or schema on a connection marked read-only.</summary>
    public static string ReadOnlyConnection(string connection) => $"{connection} is marked read-only: nothing that changes its data or schema runs there";

    /// <summary>Show DDL, or a script made from it, of an object its database gives no definition of (dropped since it was read).</summary>
    public static string NoDefinition(string name) => $"The database gives no definition of {name}: read it again in Database";

    /// <summary>Show DDL or a script with no table, view, routine, trigger, index or column chosen in the Database window.</summary>
    public const string NoDatabaseObject = "Choose a table, view, routine, trigger, index or column first";

    /// <summary>A change asked of a table or view with no primary key, by which a row is found again to write it.</summary>
    public static string NoPrimaryKey(string table) => $"{table} has no primary key, so none of its rows can be found again to change";

    /// <summary>A table's changes reaching a row deleted since it was read: nothing of them was written.</summary>
    public static string RowGone(string table, string key) => $"The row {key} of {table} was deleted since it was read, so nothing was written: read the table again";

    /// <summary>A table's changes reaching a row changed elsewhere since it was read: nothing of them was written.</summary>
    public static string RowChanged(string table, string key) => $"The row {key} of {table} changed since it was read, so nothing was written: read the table again";

    /// <summary>A cell of a column read as true or false written with other words.</summary>
    public static string NotTrueOrFalse(string text) => $"'{text}' is neither true nor false";

    /// <summary>A table's Previous Page on its first page.</summary>
    public const string NoPreviousPage = "This is the first page";

    /// <summary>A table's Next Page on its last page.</summary>
    public const string NoNextPage = "This is the last page";

    /// <summary>A table's Stop with nothing of it being read or written.</summary>
    public const string DatabaseNotReading = "Nothing of this table is being read or written";

    /// <summary>Submit or Revert with a table's changes all written or reverted.</summary>
    public const string NoPendingChanges = "No changes are pending";

    /// <summary>A table's Undo with no change of it to take back.</summary>
    public const string NoPendingUndo = "No change of this table to undo";

    /// <summary>A table's Redo with no change of it taken back.</summary>
    public const string NoPendingRedo = "No change of this table to redo";

    /// <summary>Delete Rows with no row chosen in a table that may change.</summary>
    public const string NoRowChosen = "Choose a row first";

    /// <summary>Commit or Rollback in a console that commits each statement as it runs.</summary>
    public const string AutoCommits = "This console commits each statement as it runs";

    /// <summary>Commit or Rollback in a console in manual commit with no transaction open.</summary>
    public const string NoTransaction = "No transaction is open in this console";

    /// <summary>A change of a console's commit, or of its connection, while its transaction is open.</summary>
    public const string TransactionOpen = "Commit or roll back the open transaction first";

    /// <summary>Copy as SQL Inserts of cells that are no database's (a CSV file's), whose table and SQL none says.</summary>
    public const string NoInsertTable = "Only a database's rows are copied as SQL inserts";

    /// <summary>Copy As with no table's cells having the keys.</summary>
    public const string NoTableCells = "Choose a table's cells first";

    /// <summary>Export Data with no table or view chosen, no table shown and no result of a console's shown.</summary>
    public const string NothingToExport = "Choose a table or view, or show a table's data or a console's result, first";

    /// <summary>Export Data with no file named to write.</summary>
    public const string NoExportFile = "Name the file to write";

    /// <summary>Export Data as SQL inserts with no table named for them to go into.</summary>
    public const string NoInsertTableNamed = "Name the table the inserts go into";

    /// <summary>Export Data into a folder that is not there.</summary>
    public static string NoFolderThere(string folder) => $"{folder} is not there to write into";

    /// <summary>A connection's SSH tunnel whose bastion gave a host key the person has not trusted.</summary>
    public static string SshHostKeyUnknown(string host) => $"{host}'s SSH host key is not trusted yet: edit the connection to see and trust it";

    /// <summary>A connection's SSH tunnel whose bastion gave another host key than the one the person trusted.</summary>
    public static string SshHostKeyChanged(string host, string seen) =>
        $"{host}'s SSH host key changed to {seen} since it was trusted, so nothing was sent: edit the connection to trust the new key if its administrator changed it";

    /// <summary>A connection's SSH tunnel that could not be opened.</summary>
    public static string SshUnreachable(string host, string why) => $"The SSH tunnel through {host} could not be opened: {why}";

    /// <summary>Why only the person answers whether to trust an SSH host key.</summary>
    public const string SshHostKeyByThePerson = "Only the person answers whether to trust an SSH host key: no agent's input reaches the question";

    /// <summary>A connection through an SSH tunnel with no bastion, or no user on it, named.</summary>
    public const string NoSshHost = "Name the SSH host and its user";

    /// <summary>Import Data with none of the file's columns going into the table.</summary>
    public const string NoColumnImported = "Choose a column of the table for at least one of the file's columns";

    /// <summary>Import Data with no file named to read.</summary>
    public const string NoImportFile = "Name the file to read";

    /// <summary>Import Data into a new table with no name for it.</summary>
    public const string NoNewTableNamed = "Name the new table";

    /// <summary>Import Data with nothing chosen to import into: no connection, database, schema or table.</summary>
    public const string NothingToImportInto = "Choose a connection, database, schema or table to import into first";

    /// <summary>Import Data while what was asked to import into is no longer read (its database's tables not read yet).</summary>
    public const string ImportNotRead = "The file or the tables are still being read";

    /// <summary>A file's cell its column's type does not read.</summary>
    public static string NotAValue(string text, string kind, string column) => $"'{(text.Length > 60 ? text[..60] + "…" : text)}' is no {kind} for {column}";

    /// <summary>An import stopped by a row the database or its column's type refused: nothing of it was written.</summary>
    public static string RowRefused(long row, string file, string why) =>
        $"Row {row.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture)} of {file} was refused, so nothing was written: {why}";

    /// <summary>Export Data over a file open with changes not saved, which the export would lose.</summary>
    public static string UnsavedThere(string name) => $"{name} is open with changes not saved: save or close it first";

    /// <summary>The Migrations window, or an EF Core action, in a folder none of whose projects references EF Core.</summary>
    public const string NoEfCore = "No project here uses EF Core";

    /// <summary>A Migrations action on a context before the project's contexts were listed.</summary>
    public const string NoEfContext = "List the project's contexts first";

    /// <summary>A connection string asked to open in the Database window that names no database of a kind it opens.</summary>
    public static string NoDatabaseOf(string connection) => $"{connection} names no SQLite, PostgreSQL, SQL Server or MySQL database the Database window opens";

    /// <summary>An agent's database query holding a statement that would change something, which its reads never do.</summary>
    public static string AgentsQueryWrites(string statement, string what) =>
        $"\"{statement}\" {what}: an agent's database queries only read, so nothing ran";

    /// <summary>A Requests action on a command with none chosen.</summary>
    public const string NoRequestCommand = "Choose a database command in the Requests window first";

    /// <summary>A command whose app told no database Passenger opens (its connection of another provider, or none told).</summary>
    public const string NoCommandDatabase = "Its app told no SQLite, PostgreSQL, SQL Server or MySQL database it ran on";

    /// <summary>A command's database that no connection of the Database window reaches.</summary>
    public static string NoConnectionReaching(string database, string at) =>
        $"No connection in the Database window reaches {database} at {at}: add one with New Database Connection…";

    /// <summary>A context's database asked for where nothing tells the connection it uses.</summary>
    public static string NoEfConnection(string context, string environment) =>
        $"Nothing tells {context}'s connection string in the {environment} environment: name it in the startup project's settings or its run's ConnectionStrings__ variables";

    /// <summary>A publish asked for with no folder chosen to publish into.</summary>
    public const string NoOutputFolder = "An output folder is needed";

    /// <summary>A refusal's clause (<see cref="CSharpNotRunning"/>) said alone, as a sentence of its own.</summary>
    public static string Sentence(string clause) => clause.Length == 0 ? clause : char.ToUpperInvariant(clause[0]) + clause[1..];

    /// <summary>Why a program cannot be profiled, or its heap read (<paramref name="doing"/> it), now: the debugger holds it
    /// paused, and its runtime answers the profiler only as it runs.</summary>
    public static string PausedInDebugger(string program, string doing) => $"{program} is paused in the debugger: continue it to {doing}";

    /// <summary>Why a Blazor app's WebAssembly half is not debugged in the browser a run names (<paramref name="where"/>, a
    /// place in words that begins a sentence): <paramref name="program"/> is known to be no Chromium browser, the one kind
    /// .NET's browser debug proxy drives.</summary>
    public static string NotChromium(string where, string program) =>
        $"{where} names {program}, which is no Chromium browser, and only a Chromium browser debugs them: name Chrome, Edge, Chromium or Brave there to debug them";

    /// <summary>A program's metrics asked for while the debugger holds it paused, read once it runs.</summary>
    public static string MetricsWhilePaused(string program) => $"{program} is paused in the debugger: its metrics come once it runs";

    /// <summary>Why a service of a run of services is not started.</summary>
    public const string DontStart = "its run configuration says Don't start";

    /// <summary>A service its run left unstarted (<see cref="DontStart"/>), as it is named, and what starts it as its run's kind
    /// starts one (<see cref="Run.ServiceRun.StartsIt"/>).</summary>
    public static string NotStarted(string service, string startsIt) => $"{service} is not started: {DontStart}; {startsIt}";

    /// <summary>Why no match of a search is replaced: replacing every one would make the text longer than a replacement
    /// may (<see cref="SearchResult.TooLong"/>), worked out before any is made; null when it would not.</summary>
    public static string? TooLongReplaced(SearchResult found, string replacement) =>
        found.ReplacedLength(replacement) is var length && found.TooLong(length)
            ? string.Create(CultureInfo.InvariantCulture, $"the text replaced would be {length:N0} characters long, more than {SearchResult.MostReplaced:N0}")
            : null;

    /// <summary>A folder at a project's top that the SDK reads by its name (<see cref="Explorer.SdkFolder"/>), named as it is
    /// shown, which Rename, Delete and Cut leave where it is.</summary>
    public static string ReadBySdk(Explorer.SdkFolder folder, string name) => folder == Explorer.SdkFolder.WebRoot
        ? $"{name} is the project's web root: ASP.NET Core serves its static files from it"
        : $"{name} holds the project's launch and publish profiles";

    /// <summary>A row Delete only takes out of the solution or a project, its files staying on disk, chosen with others,
    /// which Delete moves to the Trash at once: Remove takes it chosen alone, asking its own questions.</summary>
    public static string RemovedAlone(string name, string from) =>
        $"Delete moves several files and folders to the Trash at once, but {name} only leaves {from}, its files staying on disk: choose it alone to remove it";

    /// <summary>A file or folder moved or copied into a folder where its name is a folder holding it (Docs/Docs put beside
    /// Docs): replacing that would take it along first, so it stays where it is.</summary>
    public static string ReplacesItsFolder(string name) => $"{name} cannot replace the folder it is in";

    /// <summary>A file or <paramref name="folder"/> of a NuGet package folder, every project's copy of that version, which no
    /// restore writes again.</summary>
    public static string OfPackage(string name, string package, bool folder = false) => $"{name} is {PackagesOwn(package, folder)}";

    /// <summary>What a file or <paramref name="folder"/> of a NuGet package folder is (<see cref="OfPackage"/>), said after
    /// its name where a list of files left as they were names each.</summary>
    public static string PackagesOwn(string package, bool folder = false) =>
        $"a {(folder ? "folder" : "file")} of the package {package}, as NuGet restored it for every project";

    /// <summary>A file shown read-only (decompiled, installed, carried by the app, a copy to read), which nothing edits or
    /// saves.</summary>
    public static string ReadOnly(string name) => $"{name} is read-only";

    /// <summary>A path an agent named that its tools may not reach (<see cref="Mcp.AgentReach"/>): outside the open folder
    /// and the projects its trusted solution and AppHosts name beside it, and to read, outside the files made or installed to
    /// be read too; <paramref name="changes"/> for one it would change.</summary>
    public static string OutOfReach(string path, string? folder, bool changes) =>
        $"{path} is outside {(folder is null ? "any open folder" : $"the open folder {folder}")}: an agent's tools {(changes ? "change" : "read and change")} "
        + "only the folder's files and, where it is trusted, those of the projects its solution and AppHosts name beside it"
        + (changes ? string.Empty
            : ", and read outside them only what Go to Declaration leads to: the packages its projects restore, the SDK's MSBuild files, "
            + "and the copies Passenger makes to be read (a source generator's code, a library's decompiled or linked source)");

    /// <summary>A path an agent named that is never asked of the disk (<see cref="FoundPaths"/>), as asking would reach
    /// another machine, open a device or mount a folder.</summary>
    public static string NeverAsked(string path) =>
        $"{path} is never asked of the disk: it names another machine's share, a device or a folder mounted from another machine, or is no path";

    /// <summary>A console's or a test's output, which only its program writes; a line typed at its end goes to a program
    /// reading its input.</summary>
    public const string OutputReadOnly = "The output is read-only";

    /// <summary>Input for a program the debugger attached to rather than launched, which reads its standard input
    /// <paramref name="from"/> (<see cref="ToolWindows.DebugTab.InputFrom"/>), never from its Debug console.</summary>
    public static string InputFromStarter(string name, string from) =>
        $"{name}'s Debug console takes no input: the debugger attached to it, so it reads its standard input {from}";

    /// <summary>Input for a session debugging another part of a program beside <paramref name="companion"/>'s (a game's
    /// GDScript, an app's code in the browser), which takes the program's input.</summary>
    public static string InputBeside(string name, string companion) =>
        $"{name}'s Debug console takes no input: the program's input goes to {companion}'s session";

    /// <summary>Input for a launched program whose input was ended in its Debug console
    /// (<see cref="ToolWindows.OutputView.EndOfInput"/>).</summary>
    public static string InputEnded(string name) =>
        $"{name}'s Debug console takes no more input: its input was ended ({KeyWords.Of(ToolWindows.OutputView.EndOfInput)}), so it reads none";

    /// <summary>A merge's own sides, which it takes changes from into the result between them.</summary>
    public const string MergeSideReadOnly = "A merge's sides are read-only: edit the result between them";

    /// <summary>A few lines shown of a file (a usage's, a search's match, a place gone to), not the file itself.</summary>
    public const string PreviewReadOnly = "The preview is read-only: open its file to change it";

    /// <summary>A text box shown to read and copy from (an error's details, a log, a response, a licence).</summary>
    public const string TextReadOnly = "The text is read-only";

    /// <summary>What a thing says, shown as text to read (Read as Text), never the thing itself.</summary>
    public const string OnlyToRead = "The text is only to read: Escape goes back to where you were";

    /// <summary>Read as Text where the keys are on nothing that says anything as text.</summary>
    public const string NothingToRead = "Nothing here to read as text: put the keys in the editor, the terminal, a diff, the Tests window, Problems or the status bar";

    /// <summary>Read as Text in the editor where nothing at the caret says anything.</summary>
    public const string NothingAtCaret = "Nothing at the caret to read as text: no problem is on its line, and its language says nothing of it";

    /// <summary>Read as Text asked while its own text is open.</summary>
    public const string ReadingAlready = "Its text is open: Escape goes back to where you were";

    /// <summary>Why edits found in a file as Passenger read it are not made once the file is another text.</summary>
    public static string ChangedSinceRead(string file) =>
        $"{file} changed after Passenger read it, so the places found there may be where they were before: nothing was changed; try again in a moment";

    /// <summary>Why a rename renamed nothing: <paramref name="why"/>, said as a clause.</summary>
    public static string NotRenamed(string old, string why) => $"{old} was not renamed: {why}";

    /// <summary>A rename stopped at a file whose text is not what was read of it: the name's place in it, or a server's.</summary>
    public static string ChangedAsRead(string old, string file) => NotRenamed(old, $"{file} changed as it was read");

    /// <summary>An agent's act in Godot's editor from its own Passenger (<c>--mcp</c>), which no editor of the person's reaches.</summary>
    public const string GodotEditorOfPerson = $"{Mcp.McpTools.GodotEditor} acts in the person's own Godot editor, which connects to the Passenger window they use, never to an agent's own: attach to that window (--mcp-attach)";

    /// <summary>An agent's act in Godot's editor on a project whose editor is not connected: what puts Passenger's addon in the
    /// project (<paramref name="addon"/> false), or what opens the editor, a configuration of the folder's where one does.</summary>
    public static string NoGodotEditor(string project, bool addon, string? opens) =>
        !addon ? $"Passenger's addon is not in {project}: Install Passenger's Godot Addon… puts it there (asking the person), and Godot's editor connects as it opens the project"
        : opens is not null ? $"No Godot editor on {project} is connected: run {opens} to open one, whose Passenger's addon connects to this window"
        : $"No Godot editor on {project} is connected: open the project in Godot's editor, whose Passenger's addon connects to this window";

    /// <summary>An agent's act in Godot's editor naming no scene by its path in the folder, which holds several Godot projects
    /// whose editors each take such an act: the project is told by the scene's path alone.</summary>
    public static string GodotProjectUnnamed(IReadOnlyList<string> projects) =>
        $"The folder holds {Numbers.Said(projects.Count)} Godot projects ({Plural.Listed(projects)}): name the scene by its path in the folder, such as {projects[0]}/main.tscn, so the act goes to that project's Godot editor";

    /// <summary>An agent's act in Godot's editor whose addon is older than one that takes it.</summary>
    public static string GodotAddonOlder(string project, int version) =>
        $"Passenger's addon in {project} is version {version}, which takes no agent's acts: Install Passenger's Godot Addon… updates it, and Godot's editor takes it as it next opens the project";

    /// <summary>A change an agent asked for that would write over what the person's Godot editor holds of a file
    /// (<see cref="Passenger.Workspace.GodotHolds"/>), which only the person may choose to.</summary>
    public static string HeldInGodot(string held) => $"{held}, which only the person may choose to write over: save it in Godot first";

    /// <summary>A run of Reformat Code, Code Cleanup or Optimize Imports (<paramref name="what"/>) over what the Explorer has
    /// chosen (<paramref name="of"/>, "of src/App") that holds no file it lays out.</summary>
    public static string NothingToTidy(string what, string of) => $"{what} found no file it lays out {of}";

    /// <summary>A C# file Settings' C# formatter would have dotnet format lay out that is in none of the folder's projects (or
    /// is a file-based program), which dotnet format opens no workspace for.</summary>
    public static string DotnetFormatsProjects(string name) => $"dotnet format lays out a project's files, and {name} is in none of the folder's projects";

    /// <summary>dotnet format, which Settings' C# formatter chose, ended with an exit code other than 0, saying why.</summary>
    public static string DotnetFormatFailed(int exit, string said) =>
        $"dotnet format failed (exit code {exit}){(said.Length > 0 ? $": {said}" : string.Empty)}";

    /// <summary>A file of generated code as Roslyn tells it (<see cref="CSharp.GeneratedCode.GeneratedAmongAsync"/>), which a
    /// run over many files leaves as what generates it writes it.</summary>
    public static string GeneratedLeft(string file) => $"{file} is generated code: what generates it writes it again, so it was left as it is";

    /// <summary>A source generator's code copied to read (<see cref="CSharp.GeneratedCode"/>), which the C# language server
    /// holds no file of, with what made it and for which project where known (<see cref="CSharp.GeneratedCode.OriginOf"/>).</summary>
    public static string Generated(string name, string? origin = null) =>
        $"{name} is a source generator's code{(origin is null ? string.Empty : $", {origin},")} copied to read: the C# language server answers in the code it is made from";

    /// <summary>A copy of a source generator's code whose document the C# language server no longer has, shown as it was
    /// last made.</summary>
    public static string GeneratedNoLonger(string name) => $"No generator makes {name} now: shown as it was last made";

    /// <summary>A file whose grammar has yet to read it whole, so the lines inside its strings are not yet known.</summary>
    public static string ReadingStrings(string name) => $"{name} is still being read, to tell the lines inside its strings";

    /// <summary>A file whose grammar failed as it read it, and stopped, so the lines inside its strings will never be known.</summary>
    public static string GrammarFailed(string name, string language, string why) =>
        $"Passenger can't tell which lines of {name} begin inside a string: its {language} grammar failed reading it ({why})";

    /// <summary>A file in a language with no parse or grammar here, whose lines may begin inside a string that converting
    /// their indentation would change.</summary>
    public static string UnreadStrings(string name) => $"Passenger can't tell which lines of {name} begin inside a string, whose text a conversion would change";

    /// <summary>A file of a language tabs can't indent, which a conversion to them would break.</summary>
    public static string SpacesAlone(string name, string language) => $"{language} takes no tabs in its indentation, so {name} would no longer read as {language}";

    /// <summary>A file of a language whose blocks are its indentation, a line of which a conversion could move to another block
    /// or leave the file refused.</summary>
    public static string BlockAtRisk(string name, string language, int line) =>
        $"Converted, line {line} of {name} could read as another block, as {language} counts tabs and spaces apart";

    /// <summary>Why the file shown cannot be saved (the one a diff's tab edits, <see cref="Workspace.OpenDocument.Saves"/>): none is
    /// shown, it is read-only, or it has nothing to save.</summary>
    public static string? NeedsUnsaved(ActionContext context) =>
        context.File?.Saves is not { } file ? NoFile : file.ReadOnlyWhy ?? (file.IsModified ? null : $"{file.Name} is saved");

    /// <summary>Why an action on the file shown cannot run: none is shown.</summary>
    public static string? NeedsFile(ActionContext context) => context.File is null ? NoFile : null;

    /// <summary>Why a line of the file shown cannot be acted on: none is shown, or its tab holds no lines (a preview, a workbook).</summary>
    public static string? NeedsLines(ActionContext context) =>
        context.File is not { } file ? NoFile : file.HoldsText ? null : $"{file.Name} is {file.ShownAs}, with no lines";

    /// <summary>A project whose app runs on a device, an emulator or a simulator has none chosen to run on.</summary>
    public static string NoRunTarget(string project) =>
        $"{project} runs on a device, an emulator or a simulator: {ChooseRunTarget}";

    /// <summary>How the person chooses where an app runs, which ends <see cref="NoRunTarget"/>.</summary>
    public const string ChooseRunTarget = "choose one beside the run configuration";

    /// <summary>A target of a platform Passenger does not run apps on yet.</summary>
    public static string NotYetOn(string target) =>
        $"Passenger does not run apps on {target} yet: it runs them on Android devices and emulators and on iOS simulators";

    /// <summary>An iOS app's simulators asked for on a system other than macOS.</summary>
    public const string IosOnMacAlone = "iOS apps run on simulators only on a Mac, with Xcode";

    /// <summary>The iOS workload could not list simulators (no workload, no Xcode), with how to get them.</summary>
    public static string NoIos(string problem) =>
        $"iOS's simulators could not be listed ({problem}): iOS apps need the .NET ios workload (dotnet workload install ios) and the Xcode it names, "
        + "with an iOS simulator runtime (Xcode's Settings, Components)";

    /// <summary>A Mac Catalyst app profiled, whose sandbox keeps the profiler's port out of its reach.</summary>
    public static string MacCatalystUnprofiled(string app) =>
        $"Passenger's profiler does not record a Mac Catalyst app yet, so {app} cannot be profiled: Run starts it";

    /// <summary>A program the debugger cannot follow, by its name, and why (<see cref="X86DebuggerMissing"/>).</summary>
    public static string Undebugged(string program, string why) => $"Could not debug {program}: {why}";

    /// <summary>A 32-bit program debugged where the debugger's 32-bit host, which a release downloads the first time one is
    /// (<see cref="Debugging.X86DebuggerSource"/>), could not be had, and why: its download failed, did not match the
    /// release's SHA256SUMS, or would not unpack.</summary>
    public static string X86DebuggerMissing(string why) => $"it {DotnetProcesses.RunsAs32Bit}, and {why}";

    /// <summary>A running program the debugger cannot attach to (a test host a runner started), by the name its tab takes, and why.</summary>
    public static string Unattached(string name, string why) => $"Cannot attach to {name}: {why}";

    /// <summary>A program built for 32-bit x86 run off Windows, the one system .NET runs x86 programs on.</summary>
    public const string X86OnWindowsAlone = "it is built for 32-bit x86 (its PlatformTarget), which .NET runs only on Windows";

    /// <summary>A 32-bit x86 program whose x86 .NET installation, in <paramref name="root"/>, lacks what it runs on, each by
    /// the installer that brings it and its version (<see cref="Run.X86Programs.Installers"/>), with where to download each.</summary>
    public static string X86RuntimeMissing(string root, IReadOnlyList<(string Installer, string Version)> lacking)
    {
        string named = Plural.Listed([.. lacking.Select(l => $"{l.Installer} {l.Version}")]);
        string where = string.Join(", ", lacking.Select(l => $"https://dotnet.microsoft.com/download/dotnet/{l.Version}").Distinct(StringComparer.Ordinal));
        return $"it {DotnetProcesses.RunsAs32Bit}, on the x86 .NET in {PathNames.Shown(root, null)}, which has no {named}: install the {named} for x86, from {where}";
    }

    /// <summary>An Android app run where this machine (<paramref name="host"/>, "Linux on Arm64") builds none.</summary>
    public static string AndroidBuiltElsewhere(string host) => $"Android apps are built only on {AndroidHosts.Builders}: {AndroidHosts.Why(host)}";

    /// <summary>The Android workload could not list devices (no workload, no Android SDK, no Java), with how to get them.</summary>
    public static string NoAndroid(string problem) =>
        $"Android's devices could not be listed ({problem}): Android apps need the .NET android workload (dotnet workload install android), the Android SDK "
        + "(its folder in ANDROID_HOME, or Android Studio's) and a JDK (its folder in JAVA_HOME)";

    /// <summary>The Android SDK lacks the platform the workload builds against, named as its sdkmanager names the package.</summary>
    public static string NoAndroidPlatform(string package) =>
        $"The Android SDK lacks {package}, the platform the .NET android workload builds against: install it with the SDK's sdkmanager "
        + $"(sdkmanager \"{package}\") or Android Studio's SDK Manager";

    /// <summary>The Android workload listed no device or emulator, with how to get one: the SDK's avdmanager named where the SDK
    /// has it (<paramref name="avdManager"/>).</summary>
    public static string NoAndroidTarget(string? avdManager) =>
        "No Android device is connected and no emulator is made: connect a device with USB debugging on, or make an emulator in Android Studio's Device Manager"
        + (avdManager is null ? string.Empty : $" or with the Android SDK's avdmanager ({avdManager} create avd)");

    /// <summary>An agent's Restart to Update, by its ide_action or its input into Passenger's window: the person restarts
    /// Passenger, which closes their windows.</summary>
    public const string RestartIsThePersons = "Only the person restarts Passenger into its update, which closes their windows and ends what runs there: "
        + "ask them to, with the update's notice in the status bar";

    /// <summary>An attached agent's Exit, by its ide_action or its input into Passenger's window, or its close of the person's
    /// last window: the person quits Passenger, which ends what runs there.</summary>
    public const string QuitIsThePersons = "Only the person quits Passenger or closes its last window, which ends what runs there and installs an update made ready: "
        + "ask them to";

    /// <summary>An agent's Close Solution, by its ide_action or its input into Passenger's window, attached or in its own
    /// Passenger: the person closes the solution, which ends what runs there.</summary>
    public const string CloseSolutionIsThePersons = "Only the person closes the solution, which ends what runs there and leaves its folder: ask them to";

    /// <summary>An attached agent's input closing a window that Settings' Confirm before closing asks the person about, or
    /// answering that question: the person closes it.</summary>
    public const string CloseIsThePersons = "Only the person closes this window, which Passenger asks them about first as Settings says (Confirm before closing): "
        + "ask them to";

    /// <summary>A remote's menu in the branch popup, with none of its branches fetched.</summary>
    public static string NothingFetched(string remote) => $"No branch of {remote} is fetched yet: Fetch reads them";

    /// <summary>An agent's input answering whether a checkout discards the uncommitted changes: the person discards them.</summary>
    public const string DiscardIsThePersons = "Only the person discards their uncommitted changes as a branch is checked out, which cannot be undone: ask them to";

    /// <summary>A checkout's question of uncommitted changes, where git would refuse to carry them to <paramref name="to"/>:
    /// the files it would write over there, or git's own words where it named none.</summary>
    public static string CannotBring(string to, IReadOnlyList<string> files, string said) => files.Count == 0
        ? $"Git cannot bring them to {to}: {said}"
        : $"Git cannot bring them: {to} changes {(files.Count <= 3 ? Plural.Listed(files) : $"{string.Join(", ", files.Take(2))} and {Plural.Of(files.Count - 2, "more file")}")} too, "
            + "which checking it out would write over";

    /// <summary>An agent's install of what a mobile project needs: the person installs it, accepting what it asks.</summary>
    public const string AgentInstallsMobile = "Only the person installs what a mobile project needs (a workload downloads from NuGet, the Android SDK asks them to accept "
        + "its licences): ask them to, with the banner over Passenger's window";

    /// <summary>Install Workloads with every workload the projects need installed.</summary>
    public const string NoWorkloadLacking = "No project of the folder lacks a .NET workload";

    /// <summary>Install Android SDK and Java with nothing of Android's lacking.</summary>
    public const string NothingAndroidLacking = "No Android project of the folder lacks the Android SDK, its packages or a JDK";

    /// <summary>Keep .NET's Assemblies Whole with no Mac Catalyst head whose Debug build trims them.</summary>
    public const string NothingTrimmed = "No Mac Catalyst head of the folder has a Debug build that trims .NET's own assemblies";

    /// <summary>Make the Mobile Edits with no mobile head needing an edit Passenger makes.</summary>
    public const string NoMobileEdits = "No mobile app's head of the folder needs an edit Passenger makes";

    /// <summary>A .NET installed for every user that installs workloads into itself, not each user's own folder, which takes
    /// an administrator's rights: <paramref name="command"/> as an administrator runs it (<see cref="WorkloadInstalls.AsAdministrator"/>).</summary>
    public static string WorkloadsNeedAdministrator(string root, string command) =>
        $"The .NET in {PathNames.Shown(root, null)} was installed for every user, so its workloads install only with an administrator's rights: run "
        + $"{command} in a terminal{(OperatingSystem.IsWindows() ? " run as administrator" : string.Empty)}";

    /// <summary>A Functions app an AppHost asks for, with no Azure Functions Core Tools' func on the PATH to host it.</summary>
    public static string NoFunc(string project) =>
        $"{project} is an Azure Functions app, which Azure Functions Core Tools' func runs, and none is on the PATH: install them "
        + "(https://learn.microsoft.com/azure/azure-functions/functions-run-local#install-the-azure-functions-core-tools)";

    /// <summary>A service an AppHost's orchestrator asked to start with a variable loading code from where another user may
    /// write (<see cref="LoadingVariables"/>), which it is not.</summary>
    public static string LoadsOthersCode(string project, string why) => $"{project} was not started: {why}";

    /// <summary>A service an AppHost's orchestrator asked to start outside the open folder and what it reaches beside it
    /// (<see cref="ProjectSession.Reaches"/>).</summary>
    public static string NotReached(string project) =>
        $"{project} was not started: it lies outside the open folder and the projects its solution and AppHosts name beside it";

    /// <summary>A service an AppHost in the open folder names by path in a folder no workspace lends its trust to
    /// (<see cref="WorkspaceTrust.WhyNotLent"/>), which its orchestrator asked to start.</summary>
    public static string NotLent(string project, string folder, Unlent why) =>
        $"{project} was not started: its AppHost names it in {Unlendable(folder, why)}, to which the open folder lends none of its trust, "
        + "as that would trust what you never chose: open a folder holding both";

    /// <summary>A folder no workspace lends its trust to, said with why.</summary>
    private static string Unlendable(string folder, Unlent why) => why switch
    {
        Unlent.Root => $"the disk's root {folder}",
        Unlent.HoldsOpen => $"{folder}, which holds the open folder",
        Unlent.HoldsHome when PathNames.Same(folder, PathNames.Canonical(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))) => $"your home folder {folder}",
        Unlent.HoldsHome => $"{folder}, which holds your home folder",
        _ => folder,
    };

    /// <summary>A service only an agent's edit of an AppHost in the open folder names by path beside it, which lends it no
    /// trust (<see cref="Mcp.AgentReach.WroteFor"/>), its orchestrator asking to start it.</summary>
    public static string NamedByAgent(string project) =>
        $"{project} was not started: only an agent's edit of its AppHost names it beside the open folder, which lends it no trust until you save that AppHost yourself";

    /// <summary>Profile of a Functions app (its own project's, or an AppHost's), whose worker func starts and the profiler
    /// does not reach.</summary>
    public static string FunctionsUnprofiled(string service) =>
        $"{service} is an Azure Functions app, whose worker Azure Functions Core Tools start: Passenger does not profile it yet";

    /// <summary>Run with Hot Reload of a Functions app, whose worker func starts, which runs on without it.</summary>
    public const string FunctionsUnreloaded = "Passenger does not apply Hot Reload to an Azure Functions app's worker, which Azure Functions Core Tools start, yet";

    /// <summary>A debugged Functions app whose host started no worker within its time.</summary>
    public static string NoFunctionsWorker(TimeSpan limit) =>
        $"Azure Functions Core Tools started no worker for it within {Durations.Said(limit)}";

    /// <summary>The project has no device chosen to run on, where the chooser lists none.</summary>
    public const string NoTargets = "No device, emulator or simulator is listed for this project: connect a device, or make an emulator with Android Studio's Device Manager";

    /// <summary>A change of NuGet packages asked for while another of the folder's runs.</summary>
    public const string PackageChangeUnderWay = "A package change is under way";

    /// <summary>A NuGet package's Install, Update or Downgrade while the audit's feeds are still asked which versions have
    /// known vulnerabilities, so the version put in might be one they name.</summary>
    public const string VulnerabilitiesUnread = "The feeds have not yet said which versions have known vulnerabilities";

    /// <summary>An action on the NuGet window's chosen package with none chosen.</summary>
    public const string NoPackageChosen = "No package is chosen in the NuGet window";

    /// <summary>Update in All Projects of a package no project references.</summary>
    public static string ReferencedByNoProject(string id) => $"No project references {id}";

    /// <summary>An action putting the chosen version of a package in, where its feeds offer none.</summary>
    public static string NoVersionChosen(string id) => $"No version of {id} is chosen";

    /// <summary>Update in All Projects where every project referencing the package has the chosen version or a newer one.</summary>
    public static string UpdatedEverywhere(string id, string version) => $"Every project referencing {id} has {version} or a newer version";

    /// <summary>Compare with… on a memory snapshot, the only one its run took.</summary>
    public const string NoOtherSnapshot = "The run took no other snapshot to compare this one with";

    /// <summary>An action on the NuGet window's updates while another of its tabs is shown, or it is not built yet.</summary>
    public const string UpdatesNotShown = "Open the Updates tab first";

    /// <summary>Update Selected with no row chosen in the NuGet window's Updates tab.</summary>
    public const string NoUpdateChosen = "No update is chosen in the Updates tab";

    /// <summary>A changed file's Go to Source or Delete where the change deleted it.</summary>
    public const string FileDeleted = "The file is deleted";

    /// <summary>Add to .gitignore on a file Git tracks, where only an unversioned one is ignored.</summary>
    public const string Tracked = "Git tracks this file: only an unversioned file is added to .gitignore";

    /// <summary>Add to .gitignore on what the Commit window does not list as unversioned: tracked, or ignored already.</summary>
    public const string NotUnversioned = "Git tracks it or ignores it already: only an unversioned file or folder is added to .gitignore";

    /// <summary>Compare with Local on a file the commit deleted.</summary>
    public const string DeletedByCommit = "The commit deleted this file, so it holds none to compare";

    /// <summary>A problem's Go to Source or Show Context Actions where it is said of no file (a build's, the toolset's).</summary>
    public const string ProblemNamesNoFile = "The problem names no file";

    /// <summary>A problem's Open Link where it gives none.</summary>
    public const string ProblemHasNoLink = "The problem gives no link";

    /// <summary>Show Context Actions on a problem of the toolset or the environment, which no code action fixes.</summary>
    public const string ToolsetUnfixed = "No context action fixes a problem of the toolset or the environment";

    /// <summary>Show Context Actions on a failed binding whose source's type has no member near its path.</summary>
    public const string BindingUnfixed = "No member of the binding's source is near enough its path to offer";

    /// <summary>A test's Go to Source where its runner gave no file for it.</summary>
    public const string TestUnplaced = "The test's runner gives no file for it";

    /// <summary>The Coverage window's Go to Source on a project's or a namespace's row, or a type's whose file the report's
    /// paths did not lead to.</summary>
    public const string CoverageUnplaced = "The coverage report names no file of this row's on this machine";

    /// <summary>Mark Object on a row whose value is no object (a number, a bool), which is copied as it goes.</summary>
    public const string NoObjectToMark = "The value is no object to mark: a number or a struct is copied as it goes";

    /// <summary>Compare Clones Side by Side and Extract Method from Clones with no group of clones chosen in the Clones window.</summary>
    public const string NoCloneChosen = "Choose a group of clones in the Clones window";

    /// <summary>Compare with First Clone on a group's first clone.</summary>
    public const string FirstClone = "This is the group's first clone";

    /// <summary>Find Clones of Selection in a file that is not C#.</summary>
    public const string NotCSharpStatements = "Find Clones of Selection compares C# statements, and the file is not C#";

    /// <summary>Create Fake or Fill Arguments with Doubles in a file that is not C#.</summary>
    public const string DoublesAreCSharp = "Test doubles stand in for C# types, and the file is not C#";

    /// <summary>Fill Arguments with Doubles where the caret is in no call missing arguments a double stands in for, or its
    /// project references no mocking library.</summary>
    public const string NothingToFill = "Fill Arguments with Doubles fills the missing arguments of the call the caret is in with doubles of NSubstitute, Moq or FakeItEasy, where its project references one";

    /// <summary>Create Fake in a folder with no C# test project for a fake to go into.</summary>
    public const string NoTestProject = "The folder has no C# test project for a fake to go into";

    /// <summary>Create Fake where the caret is on no interface or class a test project sees and a fake can stand in for.</summary>
    public const string NoFakeHere = "Create Fake makes a fake of the interface or class whose name the caret is on, where a test project sees it";

    /// <summary>Create Fake where the test project has the fake's file already.</summary>
    public static string FakeThere(string file, string project) => $"Fakes/{file} is already in {project}";

    /// <summary>Why a class of the solution's own has no fake (sealed, static, or no constructor a class deriving from it
    /// outside can call), and the two refactorings, by their titles, that give the code using it a seam a fake fills.</summary>
    public static string FakeNeedsSeam(string type, string seam, string extract, string introduce) =>
        $"{type} {SeamOf(seam)}, so no fake can stand in for it: {extract} on it, then {introduce} where it is used, give the code a seam a fake of the interface fills";

    /// <summary>Why no fake can stand in for a class, by the extension's word for it.</summary>
    public static string SeamOf(string seam) => seam switch
    {
        Roslyn.Extension.FakeSeams.Sealed => "is sealed",
        Roslyn.Extension.FakeSeams.Static => "is static",
        _ => "has no constructor a class deriving from it can call",
    };

    /// <summary>Go to Source on a node of the dependency map the compiler declares with no file of the solution's.</summary>
    public const string MapNodeUnplaced = "This node of the map is written in no file of the solution's";

    /// <summary>Copy Path on a module made as the program runs, or read from its memory alone.</summary>
    public const string ModuleUnplaced = "The module has no file: the program made it, or the debugger read it from its memory";

    /// <summary>Clear Coverage and Export Coverage before a run with coverage, or once it is cleared.</summary>
    public const string NoCoverage = "No coverage is measured: Run All Tests with Coverage measures it";

    /// <summary>tests_run asked to debug and measure coverage at once.</summary>
    public const string CoverageDebugged = "Coverage is not measured while debugging: a debugged test host runs its code as built, for breakpoints and Hot Reload";

    /// <summary>A stack frame's Go to Source where its code has no source the debugger knows (no symbols, or code the
    /// runtime made).</summary>
    public const string FrameUnplaced = "The frame has no source file the debugger knows";

    /// <summary>A stack frame's Go to Source with no frame chosen.</summary>
    public const string NoFrameChosen = "No frame is chosen";

    /// <summary>A snapshot point set where the debugger is not Passenger's .NET one (a page's WebAssembly, GDScript).</summary>
    public const string SnapshotsOfDotnet = "Snapshot points are taken by Passenger's .NET debugger alone: this debugger stops or logs at a breakpoint";

    /// <summary>A value of a snapshot or a recorded moment shown, which no evaluation, setting, watch on it or mark reaches:
    /// its program has run on.</summary>
    public const string SnapshotValue = "This value was read at a past moment, a snapshot's or a recording's, and the program has run on since: run to a breakpoint to change or watch it";

    /// <summary>Step Back with no moment recorded before the stop.</summary>
    public const string NothingRecorded = "Nothing is recorded before this stop: Record Calls records the calls of the program's own code as it runs";

    /// <summary>Step Back at the earliest moment a recording keeps.</summary>
    public const string EarliestMoment = "This is the earliest moment the recording keeps";

    /// <summary>Step Forward at the live stop.</summary>
    public const string LiveStop = "This is the live stop: Step Back goes to the moments recorded before it";

    /// <summary>Record Calls where the debugger is not Passenger's .NET one (a page's WebAssembly, GDScript).</summary>
    public const string RecordsDotnet = "Calls are recorded by Passenger's .NET debugger alone";

    /// <summary>No snapshot point has taken a capture in the folder.</summary>
    public const string NoSnapshots = "No snapshot point has taken a snapshot yet";

    /// <summary>A menu's list (the recent solutions) while it lists nothing.</summary>
    public const string NothingListed = "Nothing is listed here yet";

    /// <summary>Copy where no text is selected.</summary>
    public const string NothingSelected = "No text is selected";

    /// <summary>Copy or Select All in a table with no cells.</summary>
    public const string NoCells = "The table has no cells";

    /// <summary>Add to Watches on a row that is a watch.</summary>
    public const string WatchedAlready = "It is a watch already";

    /// <summary>A data breakpoint's item while the debugger has not yet said whether one can be set on the row.</summary>
    public const string AskingDebugger = "The debugger has not yet said whether one can be set here";

    /// <summary>Go to Source on a breakpoint that stands on no line (an exception's, a binding error's, a script error's).</summary>
    public const string BreakpointOnNoLine = "The breakpoint stands on no line";

    /// <summary>Why Toggle Method Breakpoint cannot run where the caret is.</summary>
    public const string NoMethodAtCaret = "No C# method is declared at the caret";

    /// <summary>Remove on a breakpoint the debugger always keeps (every exception, binding errors, script errors), which
    /// is turned off rather than removed.</summary>
    public const string BreakpointKept = "The debugger always keeps this breakpoint: turn it off instead";

    /// <summary>Show in the file manager of a recent solution gone from disk.</summary>
    public const string RecentGone = "It is gone from disk";

    /// <summary>Go to Source on a step definition or hook whose method is not found where its project was read.</summary>
    public const string MethodUnplaced = "Its method is not found in the project's source";

    /// <summary>Find Usages of a step definition no feature step binds, or whose steps are not counted yet.</summary>
    public static string StepUses(int? uses) => uses is null ? "Its feature steps are not counted yet" : "No feature step binds it";

    /// <summary>A rebase on a branch while HEAD is detached, with no branch of its own.</summary>
    public const string Detached = "HEAD is detached: no branch is checked out";

    /// <summary>Clear Filters while the list is shown whole as at first.</summary>
    public const string NotNarrowed = "The list is neither narrowed nor ordered otherwise";

    /// <summary>Maximize of a tool window that floats or is shown over the editors for a moment.</summary>
    public const string Unmaximized = "Only a docked tool window is maximized";

    /// <summary>An Aspire resource's command while another runs.</summary>
    public const string CommandRunning = "A command of the resource is running";

    /// <summary>An Aspire resource's command the resource says cannot run now.</summary>
    public const string CommandDisabled = "The resource says it cannot run now";
}
