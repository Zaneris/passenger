using System.Globalization;
using Passenger.App.Mobile;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Why an action cannot run for want of what it acts on, named once, so the same want reads the same wherever an
/// action, a tool or a status line says it: the status bar, a greyed menu item's tip and an agent's ide_action alike. Its
/// words are Refusals.resx's, each a Words said in the person's language to them and in English to an agent; the members
/// here choose among them or give them what they name.</summary>
public static partial class Refusals
{
    /// <summary>The window's scale is at the end of Settings' Display scale, for Zoom In and Zoom Out.</summary>
    public static Words ZoomedAsFarAsItGoes(bool up) => up ? WindowAsLargeAsItGoes : WindowAsSmallAsItGoes;

    /// <summary>Closed tool windows keep their stripe icons or tabs, so Close is Hide.</summary>
    public static Words ClosedKeepIcons => ClosedKeepIconsAt(SettingsDialog.PlaceOfPage(SettingsDialog.Appearance));

    /// <summary>A regex stopped at its time limit, wherever that cuts an answer short (<see cref="TextSearch.RegexStopped"/>).</summary>
    public static Words RegexStopped => TextSearch.RegexStopped;

    /// <summary><see cref="RegexStopped"/> naming where it stopped: a file, or what it was matching.</summary>
    public static Words RegexStoppedIn(string where) => TextSearch.RegexStoppedIn(where);

    /// <summary>The folder open is not trusted, so Passenger runs none of its code (<see cref="WorkspaceTrust"/>): no build,
    /// run, debug, test, profile, restore, Hot Reload, C# language server or previewer of its.</summary>
    public static Words Untrusted => WorkspaceTrust.Untrusted;

    /// <summary>The folder open is not trusted, so an agent's package search asks none of the feeds its nuget.config names,
    /// which may be any server.</summary>
    public static Words UntrustedFeeds => UntrustedFeedsOf(Mcp.McpTools.NugetSearch);

    /// <summary>What the banner over an untrusted folder below the top of a Git repository says: its Trust Folder trusts the
    /// repository (<see cref="WorkspaceTrust.FolderToTrust"/>).</summary>
    public static Words UntrustedInRepository(string repository) => UntrustedInRepositoryNamed(Path.GetFileName(repository));

    /// <summary>What the banner over a folder no longer trusted says while what the person started there runs on.</summary>
    public static Words UntrustedRunsOn(IReadOnlyList<string> running) => UntrustedWhileRunning(Plural.Listed(Words.Names(running)), running.Count);

    /// <summary>A branch checked out in another working tree of the repository, which git checks out in one at a time and
    /// deletes only once none has it.</summary>
    public static Words InWorktree(string branch, string folder) => CheckedOutInWorktree(branch, PathNames.Shown(folder, null));

    /// <summary>Inspect This where the caret's symbol (named, where there is one) has no value, calls or types to inspect.</summary>
    public static Words NothingToInspect(string? symbol) => symbol is null ? NoSymbolToInspect : SymbolHasNothingToInspect(symbol);

    /// <summary>A Godot before 4.4 has no embedding of a game in another program's window.</summary>
    public static Words GameEmbedsFrom(string? version) => version is null ? GameEmbedsFromNoVersion : GameEmbedsFromVersion(version);

    /// <summary>On macOS a game draws in the Game window through Godot's embedded display, from Godot 4.5.</summary>
    public static Words GameEmbedsOnMacFrom(string? version) => version is null ? GameEmbedsOnMacFromNoVersion : GameEmbedsOnMacFromVersion(version);

    /// <summary>A game's window never came to the Game window (it ended first, or none came in time), with Godot's last
    /// errors where its console has any.</summary>
    public static Words GameWindowNeverCame(string game, string? godotSaid) =>
        godotSaid is null ? GameWindowNeverCameFor(game) : GameWindowNeverCameSaying(game, godotSaid);

    /// <summary>A C# file come under a project that the language server has not loaded into it (<see
    /// cref="CSharp.CSharpService.UnplacedAsync"/>), whose answers about the solution would leave it out.</summary>
    public static Words NotTakenIn(string file, string project) => NotTakenInAfter(file, project, Durations.Words(CSharp.Patience.Limit));

    /// <summary>A command (<paramref name="what"/>) asked of a file the C# language server is ready but has not taken in
    /// yet: one come under a project (<paramref name="project"/>) whose load of it has not ended, else one it is still to open.</summary>
    public static Words NotInServerYet(string what, string file, string? project) =>
        project is null ? NotOpenInServerYet(what, file) : NotLoadedIntoServerYet(what, file, project);

    /// <summary>A project the C# language server was asked to load again (its file or an import changed) that it has not
    /// loaded since (<see cref="CSharp.CSharpService.UnplacedAsync"/>), whose answers would be of the project as it was.</summary>
    public static Words NotLoadedAgain(string project) => NotLoadedAgainAfter(project, Durations.Words(CSharp.Patience.Limit));

    /// <summary>What an answer given before the C# language server took C# files come under its projects into them went
    /// without (<see cref="CSharp.CSharpService.UntakenAsync"/>), the files named as shown.</summary>
    public static Words Untaken(IReadOnlyList<string> files) => UntakenFiles(FirstNamed(files), files.Count);

    /// <summary>What an answer of the solution's whole code went without once the wait for the C# language server ran out:
    /// the files come under its projects it has not taken in (<see cref="Untaken"/>), and the projects it answered from as
    /// they were before they changed, not loaded again yet.</summary>
    public static Words WentWithout(IReadOnlyList<string> files, IReadOnlyList<string> projects) => (files.Count, projects.Count) switch
    {
        (> 0, > 0) => WentWithoutBoth(Untaken(files), ProjectsAsTheyWere(FirstNamed(projects), projects.Count)),
        (> 0, _) => Untaken(files),
        (_, > 0) => ProjectsAsTheyWere(FirstNamed(projects), projects.Count),
        _ => string.Empty,
    };

    /// <summary>The first three names, and how many more.</summary>
    private static Words FirstNamed(IReadOnlyList<string> names) =>
        names.Count > 3 ? NamesAndMore(string.Join(", ", names.Take(3)), names.Count - 3) : string.Join(", ", names);

    /// <summary>The folder's projects have no .NET SDK to load, build and run them with: the one its global.json asks
    /// for (named from <paramref name="folder"/>) is not installed, none is, or dotnet found none for another reason it
    /// gives.</summary>
    public static Words NoSdk(MissingSdk missing, string? folder) => missing switch
    {
        { Asked: { } asked } => SdkAskedNotInstalled(asked, missing.GlobalJson is { } file ? PathNames.Shown(PathNames.Canonical(file), folder) : GlobalJsonFile),
        { NoHost: true } => NoDotnet,
        { ForOtherProcessor: true } => OnlyNativeSdk,
        { NoneInstalled: true } => OnlyRuntimeInstalled,
        _ => NoSdkFound(ChildResult.FirstLineOf(missing.Said)),
    };

    /// <summary>global.json's file name, as a refusal names it where it gives no path.</summary>
    private const string GlobalJsonFile = "global.json";

    /// <summary>A C# code tool asked about a file of a project the language server met an error loading and holds no code
    /// of, with what it met.</summary>
    public static Words ProjectNotLoaded(string project, string error) => ProjectNotLoadedWith(project, error.TrimEnd('.'));

    /// <summary>A C# code tool's answer, empty, while projects the language server met an error loading may hold what it
    /// looked for.</summary>
    public static Words NoneWhileUnloaded(string none, IReadOnlyList<(string Project, string Error)> failed) =>
        NoneWhileProjectsUnloaded(none, Plural.Listed(Words.Names(failed.Select(f => f.Project))), string.Join("; ", failed.Select(f => $"{f.Project}: {f.Error}")));

    /// <summary>A C# code tool asked while the language server is still loading the folder's projects after the wait.</summary>
    public static Words StillLoading(TimeSpan waited, string state) =>
        state.Length > 0 ? StillLoadingAfter(Durations.Words(waited), state) : StillLoadingStarting(Durations.Words(waited));

    /// <summary>A code tool asked of a file whose language server (<paramref name="server"/>, "YAML") is still starting
    /// after the wait.</summary>
    public static Words StillStarting(string server, TimeSpan waited) => StillStartingAfter(server, Durations.Words(waited));

    /// <summary>A C# code tool asked while what a restore brought, or a load again, is still to reach the language server
    /// after the wait: <paramref name="coming"/> says what, as the agent was told it waited for.</summary>
    public static Words StillComing(TimeSpan waited, string coming) => StillComingAfter(Durations.Words(waited), coming);

    /// <summary>An x64 Passenger the system translates found only a .NET SDK of the system's own processor, whose MSBuild it
    /// cannot load (<see cref="MissingSdk.ForOtherProcessor"/>).</summary>
    public static Words OnlyNativeSdk => OnlyNativeSdkOn(TranslatingSystem);

    /// <summary>An x64 Passenger the system translates with no .NET SDK at all: no .NET, or a runtime alone, in
    /// <paramref name="runtime"/>.</summary>
    public static Words TranslatedWithoutSdk(string? runtime) =>
        runtime is null ? TranslatedWithoutDotnet(TranslatingSystem) : TranslatedWithRuntimeAlone(PathNames.Shown(runtime, null), TranslatingSystem);

    /// <summary>Install .NET SDK in an x64 Passenger the system translates: the SDK it installs, the system's own processor's,
    /// is one this Passenger cannot load.</summary>
    public static Words InstallsOnlyNative => InstallsOnlyNativeOn(TranslatingSystem);

    /// <summary>The system translating an x64 Passenger, as its refusals choose their words by: macOS's Rosetta, or another's
    /// emulation.</summary>
    private static string TranslatingSystem => OperatingSystem.IsMacOS() ? "macos" : "other";

    /// <summary>What .NET the machine or the folder lacks, as the banner, an action's refusal and an agent say it; null with
    /// nothing lacking.</summary>
    public static Words? Dotnet(DotnetWant want, string? folder) => want.Lack switch
    {
        DotnetLack.NoDotnet => NoDotnet,
        DotnetLack.RuntimeOnly => want.Root is { } root ? OnlyRuntimeInstalledIn(PathNames.Shown(root, null)) : OnlyRuntimeInstalled,
        DotnetLack.OtherProcessor => want.SystemsOwn is null ? TranslatedWithoutSdk(want.Root) : OnlyNativeSdk,
        DotnetLack.GlobalJsonSdk or DotnetLack.NoSdk => NoSdk(want.Missing!, folder),
        DotnetLack.OlderThanProject when want.ChosenBy is { } asked => asked.RollForward is { } roll
            ? SdkTooOldChosenRolling(want.Newer!.Value.Project, want.Newer.Value.Framework, want.Sdk ?? string.Empty, PathNames.Shown(asked.Path, folder),
                asked.Version ?? string.Empty, roll, want.Builders.Count, Plural.Listed(Words.Names(want.Builders)))
            : SdkTooOldChosen(want.Newer!.Value.Project, want.Newer.Value.Framework, want.Sdk ?? string.Empty, PathNames.Shown(asked.Path, folder),
                asked.Version ?? string.Empty, want.Builders.Count, Plural.Listed(Words.Names(want.Builders))),
        DotnetLack.OlderThanProject => SdkTooOld(want.Newer!.Value.Project, want.Newer.Value.Framework, want.Sdk ?? string.Empty),
        DotnetLack.OffPath => SdkOffPath(want.Sdk ?? string.Empty, PathNames.Shown(want.Root!, null)),
        _ => null,
    };

    /// <summary>An agent's Add or Remove the passenger-ide Command, or its input answering the person's question of it: the
    /// person's shell is theirs to change.</summary>
    public static Words CommandIsThePersons => CommandIsThePersonsOf(PassengerCommand.Name);

    /// <summary>Add or Remove the passenger-ide Command where Passenger's installer makes the command (Windows) or
    /// install.sh does (Linux); null on macOS, where the person does.</summary>
    public static Words? CommandMadeElsewhere =>
        OperatingSystem.IsWindows() ? CommandMadeByInstaller(PassengerCommand.Name)
        : OperatingSystem.IsMacOS() ? null
        : CommandMadeByInstallScript(PassengerCommand.Name);

    /// <summary>The passenger-ide command while the window has not yet read what stands at its place.</summary>
    public static Words CommandStillRead => CommandPlaceStillRead(PassengerCommand.Name);

    /// <summary>The passenger-ide command of a Passenger in no app bundle, as a build from source is not.</summary>
    public static Words CommandOfNoBundle => CommandOfNoAppBundle(PassengerCommand.Name);

    /// <summary>The passenger-ide command of a Passenger macOS runs from a copy of its own, which goes as it ends.</summary>
    public static Words CommandOfTranslocated(string bundle) => CommandOfTranslocatedCopy(bundle, PassengerCommand.Name);

    /// <summary>The passenger-ide command of a Passenger on a disk image, which goes as the image is ejected.</summary>
    public static Words CommandOfDiskImage(string bundle) => CommandOfDiskImageCopy(bundle, PassengerCommand.Name);

    /// <summary>The passenger-ide command where the app's bundle carries no launcher for it.</summary>
    public static Words CommandNotCarried(string launcher) => CommandNotCarriedAt(PassengerCommand.Name, launcher);

    /// <summary>The passenger-ide command's place held by another program, a folder, or a link to one: the person's own.</summary>
    public static Words CommandOthers(string link) => CommandPlaceOthers(PathNames.Shown(link, null));

    /// <summary>Add the passenger-ide Command where it is there, leading to this Passenger, its folder on the PATH.</summary>
    public static Words CommandThere(string link) => CommandPlaceTaken(PathNames.Shown(link, null));

    /// <summary>Remove the passenger-ide Command where nothing is at its place.</summary>
    public static Words NoCommand(string link) => NoCommandAt(PassengerCommand.Name, PathNames.Shown(link, null));

    /// <summary>Install .NET SDK with nothing it installs mending the want: the newest SDK found, and where; or, for a
    /// Passenger the system translates, which loads none of the system's own SDKs, that it installs none it loads.</summary>
    public static Words NothingToInstall(DotnetMachine? machine, DotnetWant want) =>
        want.Lack == DotnetLack.OtherProcessor || want is { Translated: true, Install: not null } ? InstallsOnlyNative
        : machine?.NewestSdk is { } sdk && machine.Found is { } found ? SdkInstalledAlready(want.Sdk ?? sdk.ToString(), PathNames.Shown(found.Root, null))
        : StillLookingForDotnet;

    /// <summary>Add .NET to the Shell's PATH where the shell's PATH has it already.</summary>
    public static Words OnPathAlready(string root) => OnShellPathAlready(PathNames.Shown(root, null));

    /// <summary>No run configuration to run, debug or profile (the <paramref name="verb"/>): the folder has no SDK to
    /// load its projects with (<see cref="NoSdk"/>), or no executable project.</summary>
    public static Words NothingTo(string verb, MissingSdk? missing, string? folder) =>
        missing is null ? NoExecutableProjectTo(verb) : NoSdk(missing, folder);

    /// <summary>A Godot game's GoDotTest tests asked to run where no scene of the game calls GoTest.RunTests, so a run would
    /// start Godot and run none until stopped: said before it starts.</summary>
    public static Words GoDotTestNeverRuns(string game) => GoDotTestNeverRunsIn(game, Testing.GoDotTests.TestFolder);

    /// <summary>Run Tests at the caret took no test of the method or class there once the tests were looked for: why, where
    /// its project's tests could not be found (<see cref="TestsNotFound"/>), else what is likeliest.</summary>
    public static Words NoTestOf(string name, string? unfound) => unfound is null ? NoTestFound(name) : NoTestFoundBecause(name, unfound);

    /// <summary>A test chosen of one of the classes GoDotTest cannot tell apart (<see cref="Testing.GoDotTests.Alike"/>).</summary>
    public static Words GoDotTestAlike(string suite, IReadOnlyList<string> classes) => GoDotTestAlikeClasses(Plural.Listed(Words.Names(classes)), suite);

    /// <summary>A game's GoDotTest tests asked to run where they cannot start (<see cref="Testing.GoDotTests.Start"/>): no
    /// scene runs them, or the scene its run settings name is not the game's or runs none; null where they can.</summary>
    public static Words? GoDotTestStart(string game, Testing.GoDotTests.Start start)
    {
        Words file = start.Settings is { } settings ? PathNames.Shown(settings, null) : TheRunSettingsFile;
        return start.Problem switch
        {
            Testing.GoDotTests.StartProblem.NoScene => GoDotTestNeverRuns(game),
            Testing.GoDotTests.StartProblem.ChosenMissing => GoDotTestSceneMissing(file, start.Scene ?? string.Empty, game, Testing.GoDotTests.SceneParameter),
            Testing.GoDotTests.StartProblem.ChosenRunsNone => GoDotTestSceneRunsNone(file, start.Scene ?? string.Empty, game, Testing.GoDotTests.SceneParameter),
            _ => null,
        };
    }

    /// <summary>What a build whose projects' assemblies hold Avalonia XAML never compiled says (<see cref="Passenger.Xaml.Metadata.UncompiledXaml"/>),
    /// and a preview whose previewer Avalonia's runtime ended for it: the build that last compiled their C# stopped before
    /// Avalonia's XAML compiler finished, and each build since took that as up to date. A rebuild runs both compilers again.</summary>
    public static Words XamlUncompiled(IReadOnlyList<string> projects) => XamlUncompiledIn(projects.Count, Plural.Listed(Words.Names(projects)));

    /// <summary>An agent's call whose question the window took away as it left the folder it was asked in (<paramref
    /// name="left"/>), for another or none, before the person answered: an answer would have allowed it where it no longer is.</summary>
    public static Words FolderChanged(string? left) => left is null ? FolderChangedFromNone : FolderChangedFrom(left);

    /// <summary>A debugger's action asked for with no program debugged, as the debugger's own answer says it.</summary>
    public static Words NothingDebugged => DebuggerWords.NothingDebugged;

    /// <summary>Set Data Breakpoint asked of a row of Variables that is no member of a value, as the debugger's own answer
    /// says it.</summary>
    public static Words DataBreakpointNotAMember => DebuggerWords.NotAMember;

    /// <summary>What an edit did, and the files it left as they were for being edited meanwhile: "Renamed in 3 files, but not
    /// in A.cs and B.cs: they were edited meanwhile"; what it did alone where it left none.</summary>
    public static Words EditedMeanwhile(string done, IReadOnlyList<string> files) =>
        files.Count == 0 ? done : DoneButNotIn(done, Plural.Listed(Words.Names(files)), files.Count);

    /// <summary>An edit said not applied to the files it left as they were for being edited meanwhile: "Extract Method was not
    /// applied to A.cs: it was edited meanwhile".</summary>
    public static Words NotAppliedEditedMeanwhile(string what, IReadOnlyList<string> files) => NotAppliedTo(what, Plural.Listed(Words.Names(files)), files.Count);

    /// <summary>A feature's diagnostics with no step definitions read to match its steps against: the C# language server,
    /// which reads them, runs and did not answer for them (<paramref name="serverRuns"/>), or runs for no folder.</summary>
    public static Words StepsUnmatched(bool serverRuns) => serverRuns ? StepsUnmatchedUnanswered : StepsUnmatchedNoServer;

    /// <summary>A feature's diagnostics where its project's last load failed with an error (<paramref name="loadError"/>), so
    /// the C# language server, which reads the step definitions, holds none of the project's.</summary>
    public static Words StepsUnmatched(string project, string loadError) => StepsUnmatchedLoadFailed(project, loadError);

    /// <summary>A service of a run of services (each named as it is shown) that the run has not started, or that has ended.</summary>
    public static Words NotRunning(string service, string run) => ServiceNotRunning(service, run);

    /// <summary>An image built for x86-64 alone (SQL Server's, <see cref="BackingService.X64Only"/>) on an Arm engine without
    /// Rosetta: one that runs no x86-64 image, or emulates x86-64 otherwise (QEMU), under which SQL Server fails as it starts.
    /// Docker Desktop and Podman are told how they would use Rosetta.</summary>
    public static Words X64OnlyUnrunnable(string what, Containers.EngineState engine) => X64OnlyUnrunnableOn(what, OperatingSystem.IsMacOS() ? "mac" : "other",
        engine.EngineName, engine.Amd64 == Containers.Amd64Runs.Never ? "never" : "emulated", engine.DockerDesktop ? "desktop" : engine.IsPodman ? "podman" : "other");

    /// <summary>A Microsoft Entra ID account a connection signs in as, with no sign-in that holds; or none signed in.</summary>
    public static Words AzureSignInNeeded(string? account) => account is null ? NoEntraAccountSignedIn : EntraAccountNotSignedIn(account);

    /// <summary>A DynamoDB table's changes past the most one transaction writes, refused before any is written.</summary>
    public static Words TooManyItemChanges(int changes, int most) =>
        TooManyItemChangesAtOnce(most.ToString(CultureInfo.InvariantCulture), changes.ToString(CultureInfo.InvariantCulture));

    /// <summary>A file's cell its column's type does not read.</summary>
    public static Words NotAValue(string text, string kind, string column) => NotAValueOf(text.Length > 60 ? text[..60] + "…" : text, kind, column);

    /// <summary>A refusal's clause said alone, as a sentence of its own, its first letter made a capital.</summary>
    public static string Sentence(string clause) => clause.Length == 0 ? clause : char.ToUpperInvariant(clause[0]) + clause[1..];

    /// <summary>Why no match of a search is replaced: replacing every one would make the text longer than a replacement
    /// may (<see cref="SearchResult.TooLong"/>), worked out before any is made; null when it would not.</summary>
    public static Words? TooLongReplaced(SearchResult found, string replacement) =>
        found.ReplacedLength(replacement) is var length && found.TooLong(length) ? ReplacedTooLong(length, SearchResult.MostReplaced) : null;

    /// <summary>A folder at a project's top that the SDK reads by its name (<see cref="Explorer.SdkFolder"/>), named as it is
    /// shown, which Rename, Delete and Cut leave where it is.</summary>
    public static Words ReadBySdk(Explorer.SdkFolder folder, string name) => folder == Explorer.SdkFolder.WebRoot ? WebRootReadBySdk(name) : ProfilesReadBySdk(name);

    /// <summary>A file or <paramref name="folder"/> of a NuGet package folder, every project's copy of that version, which no
    /// restore writes again.</summary>
    public static Words OfPackage(string name, string package, bool folder = false) => NamedOfPackage(name, FileOrFolder(folder), package);

    /// <summary>What a file or <paramref name="folder"/> of a NuGet package folder is (<see cref="OfPackage"/>), said after
    /// its name where a list of files left as they were names each.</summary>
    public static Words PackagesOwn(string package, bool folder = false) => OfPackageFolder(FileOrFolder(folder), package);

    /// <summary>A NuGet package's file shown read-only, as its banner says it.</summary>
    public static Words PackagesFileReadOnly(string package) => PackagesFileShownReadOnly(package);

    private static string FileOrFolder(bool folder) => folder ? "folder" : "file";

    /// <summary>A path an agent named that its tools may not reach (<see cref="Mcp.AgentReach"/>): outside the open folder
    /// and the projects its trusted solution and AppHosts name beside it, and to read, outside the files made or installed to
    /// be read too; <paramref name="changes"/> for one it would change.</summary>
    public static Words OutOfReach(string path, string? folder, bool changes) =>
        folder is null ? OutOfReachOfAny(path, changes ? "true" : "false") : OutOfReachOfFolder(path, folder, changes ? "true" : "false");

    /// <summary>Input for a launched program whose input was ended in its Debug console
    /// (<see cref="ToolWindows.OutputView.EndOfInput"/>).</summary>
    public static Words InputEnded(string name) => InputEndedBy(name, KeyWords.Of(ToolWindows.OutputView.EndOfInput));

    /// <summary>A rename stopped at a file whose text is not what was read of it: the name's place in it, or a server's.</summary>
    public static Words ChangedAsRead(string old, string file) => NotRenamed(old, FileChangedAsRead(file));

    /// <summary>An agent's act in Godot's editor from its own Passenger (<c>--mcp</c>), which no editor of the person's reaches.</summary>
    public static Words GodotEditorOfPerson => GodotEditorOfPersonBy(Mcp.McpTools.GodotEditor);

    /// <summary>An agent's act in Godot's editor on a project whose editor is not connected: what puts Passenger's addon in the
    /// project (<paramref name="addon"/> false), or what opens the editor, a configuration of the folder's where one does.</summary>
    public static Words NoGodotEditor(string project, bool addon, string? opens) =>
        !addon ? NoGodotAddonIn(project) : opens is not null ? NoGodotEditorRun(project, opens) : NoGodotEditorOpen(project);

    /// <summary>An agent's act in Godot's editor naming no scene by its path in the folder, which holds several Godot projects
    /// whose editors each take such an act: the project is told by the scene's path alone.</summary>
    public static Words GodotProjectUnnamed(IReadOnlyList<string> projects) => GodotProjectsUnnamed(projects.Count, Plural.Listed(Words.Names(projects)), projects[0]);

    /// <summary>An agent's act in Godot's editor whose addon is older than one that takes it.</summary>
    public static Words GodotAddonOlder(string project, int version) => GodotAddonOlderAt(project, version.ToString(CultureInfo.InvariantCulture));

    /// <summary>dotnet format, which Settings' C# formatter chose, ended with an exit code other than 0, saying why.</summary>
    public static Words DotnetFormatFailed(int exit, string said) => said.Length > 0
        ? DotnetFormatFailedSaying(exit.ToString(CultureInfo.InvariantCulture), said)
        : DotnetFormatFailedWith(exit.ToString(CultureInfo.InvariantCulture));

    /// <summary>A source generator's code copied to read (<see cref="CSharp.GeneratedCode"/>), which the C# language server
    /// holds no file of, with what made it and for which project where known (<see cref="CSharp.GeneratedCode.OriginOf"/>).</summary>
    public static Words Generated(string name, string? origin = null) => origin is null ? GeneratedCopy(name) : GeneratedCopyOf(name, origin);

    /// <summary>A file of a language whose blocks are its indentation, a line of which a conversion could move to another block
    /// or leave the file refused.</summary>
    public static Words BlockAtRisk(string name, string language, int line) => LineAtRisk(line.ToString(CultureInfo.InvariantCulture), name, language);

    /// <summary>Why the file shown cannot be saved (the one a diff's tab edits, <see cref="Workspace.OpenDocument.Saves"/>): none is
    /// shown, it is read-only, or it has nothing to save.</summary>
    public static Words? NeedsUnsaved(ActionContext context) =>
        context.File?.Saves is not { } file ? NoFile : file.ReadOnlyWhy ?? (file.IsModified ? null : IsSaved(file.Name));

    /// <summary>Why an action on the file shown cannot run: none is shown.</summary>
    public static Words? NeedsFile(ActionContext context) => context.File is null ? NoFile : null;

    /// <summary>Why a line of the file shown cannot be acted on: none is shown, or its tab holds no lines (a preview, a workbook).</summary>
    public static Words? NeedsLines(ActionContext context) =>
        context.File is not { } file ? NoFile : file.HoldsText ? null : ShownWithNoLines(file.Name, file.ShownAs ?? string.Empty);

    /// <summary>A 32-bit program debugged where the debugger's 32-bit host, which a release downloads the first time one is
    /// (<see cref="Debugging.X86DebuggerSource"/>), could not be had, and why: its download failed, did not match the
    /// release's SHA256SUMS, or would not unpack.</summary>
    public static Words X86DebuggerMissing(string why) => X86DebuggerMissingBecause(DotnetProcesses.RunsAs32Bit, why);

    /// <summary>A 32-bit x86 program whose x86 .NET installation, in <paramref name="root"/>, lacks what it runs on, each by
    /// the installer that brings it and its version (<see cref="Run.X86Programs.Installers"/>), with where to download each.</summary>
    public static Words X86RuntimeMissing(string root, IReadOnlyList<(string Installer, string Version)> lacking) => X86RuntimeLacking(
        DotnetProcesses.RunsAs32Bit, PathNames.Shown(root, null), Plural.Listed(Words.Names(lacking.Select(l => $"{l.Installer} {l.Version}"))),
        string.Join(", ", lacking.Select(l => $"https://dotnet.microsoft.com/download/dotnet/{l.Version}").Distinct(StringComparer.Ordinal)));

    /// <summary>An Android app run where this machine (<paramref name="host"/>, "Linux on Arm64") builds none.</summary>
    public static Words AndroidBuiltElsewhere(string host) => AndroidBuiltOnlyOn(AndroidHosts.Builders, AndroidHosts.Why(host));

    /// <summary>The Android workload listed no device or emulator, with how to get one: the SDK's avdmanager named where the SDK
    /// has it (<paramref name="avdManager"/>).</summary>
    public static Words NoAndroidTarget(string? avdManager) => avdManager is null ? NoAndroidDeviceOrEmulator : NoAndroidDeviceOrEmulatorMade(avdManager);

    /// <summary>A checkout's question of uncommitted changes, where git would refuse to carry them to <paramref name="to"/>:
    /// the files it would write over there, or git's own words where it named none.</summary>
    public static Words CannotBring(string to, IReadOnlyList<string> files, string said) => files.Count == 0
        ? GitCannotBringSaying(to, said)
        : GitCannotBringOver(to, files.Count <= 3 ? Plural.Listed(Words.Names(files)) : TwoFilesAndMore(files[0], files[1], files.Count - 2));

    /// <summary>A .NET installed for every user that installs workloads into itself, not each user's own folder, which takes
    /// an administrator's rights: <paramref name="command"/> as an administrator runs it (<see cref="WorkloadInstalls.AsAdministrator"/>).</summary>
    public static Words WorkloadsNeedAdministrator(string root, string command) =>
        WorkloadsNeedAdministratorOn(PathNames.Shown(root, null), command, OperatingSystem.IsWindows() ? "windows" : "other");

    /// <summary>A service an AppHost in the open folder names by path in a folder no workspace lends its trust to
    /// (<see cref="WorkspaceTrust.WhyNotLent"/>), which its orchestrator asked to start, said with why it lends none.</summary>
    public static Words NotLent(string project, string folder, Unlent why) => NotLentFrom(project, why switch
    {
        Unlent.Root => "root",
        Unlent.HoldsOpen => "holdsOpen",
        Unlent.HoldsHome when PathNames.Same(folder, PathNames.Canonical(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))) => "home",
        Unlent.HoldsHome => "holdsHome",
        _ => "other",
    }, folder);

    /// <summary>A debugged Functions app whose host started no worker within its time.</summary>
    public static Words NoFunctionsWorker(TimeSpan limit) => NoFunctionsWorkerWithin(Durations.Words(limit));

    /// <summary>Find Usages of a step definition no feature step binds, or whose steps are not counted yet.</summary>
    public static Words StepUses(int? uses) => uses is null ? StepUsesNotCounted : NoStepBinds;
}
