using System.Globalization;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Why an action cannot run for want of what it acts on, named once, so the same want reads the same wherever an
/// action, a tool or a status line says it: the status bar, a greyed menu item's tip and an agent's ide_action alike.</summary>
public static class Refusals
{
    /// <summary>No file is shown in the editor.</summary>
    public const string NoFile = "No file is open";

    /// <summary>The file shown is no Godot scene, nor a script a scene runs, of a Godot project the folder builds a game of.</summary>
    public const string NoGodotScene = "No scene of a Godot game is open: open a .tscn, or a script one of its nodes runs";

    /// <summary>The window has no folder open (a solution, a project or a folder of files).</summary>
    public const string NoFolder = "No folder is open";

    /// <summary>The folder open has no solution (a project opened alone, or a folder of files).</summary>
    public const string NoSolution = "No solution is open";

    /// <summary>The folder open is in no Git working tree.</summary>
    public const string NoRepository = "The folder is not in a Git repository";

    /// <summary>The folder open is in a Git working tree already, which Create Git Repository would make.</summary>
    public const string InRepository = "The folder is in a Git repository already";

    /// <summary>The folder has no C# language server: it has neither a solution nor a project, or its window starts none.</summary>
    public const string NoCSharp = "No C# language server runs for this folder";

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
        $"{file} is not loaded into {project} yet: the C# language server has not taken it in after {CSharp.Patience.Limit.TotalSeconds:0} s, "
        + "and would answer without it; try again once it has";

    /// <summary>What an answer given before the C# language server took C# files come under its projects into them went
    /// without (<see cref="CSharp.CSharpService.UntakenAsync"/>), the files named as shown.</summary>
    public static string Untaken(IReadOnlyList<string> files) =>
        $"Without {string.Join(", ", files.Take(3))}{(files.Count > 3 ? $" and {files.Count - 3} more" : string.Empty)}, which the C# language server "
        + $"has not loaded into {(files.Count == 1 ? "its project" : "their projects")} yet";

    /// <summary>The folder's projects have no .NET SDK to load, build and run them with: the one its global.json asks
    /// for (named from <paramref name="folder"/>) is not installed, none is, or dotnet found none for another reason it
    /// gives.</summary>
    public static string NoSdk(MissingSdk missing, string? folder) => missing switch
    {
        { Asked: { } asked } => $"The .NET SDK {asked} that {(missing.GlobalJson is { } file ? PathNames.Shown(PathNames.Canonical(file), folder) : "global.json")} "
            + "asks for is not installed: install it, or have global.json name one that is",
        { NoHost: true } => NoDotnet,
        { NoneInstalled: true } => $"Only the .NET runtime is installed: {NeedsTheSdk}",
        _ => $"No .NET SDK was found for this folder: {ChildResult.FirstLineOf(missing.Said)}",
    };

    /// <summary>No dotnet at all, runtime or SDK, was found.</summary>
    public const string NoDotnet = $"No .NET is installed: files open and edit, and Git and the terminal work, but {NeedsTheSdk}";

    private const string NeedsTheSdk = "projects need the .NET SDK to load, build, run and test";

    /// <summary>What .NET the machine or the folder lacks, as the banner, an action's refusal and an agent say it; null with
    /// nothing lacking.</summary>
    public static string? Dotnet(DotnetWant want, string? folder) => want.Lack switch
    {
        DotnetLack.NoDotnet => NoDotnet,
        DotnetLack.RuntimeOnly => $"Only the .NET runtime is installed{(want.Root is { } root ? $", in {PathNames.Shown(root, null)}" : string.Empty)}: {NeedsTheSdk}",
        DotnetLack.GlobalJsonSdk or DotnetLack.NoSdk => NoSdk(want.Missing!, folder),
        DotnetLack.OlderThanProject when want.ChosenBy is { } asked => $"{want.Newer!.Value.Project} targets .NET {want.Newer.Value.Framework}, which the .NET SDK {want.Sdk} "
            + $"does not build: {PathNames.Shown(asked.Path, folder)} chose it, asking for {asked.Version}{(asked.RollForward is { } roll ? $" with rollForward {roll}" : string.Empty)}; "
            + (want.Builders.Count > 0
                ? $"the {Plural.Noun(want.Builders.Count, ".NET SDK")} {Plural.Listed(want.Builders)} installed {(want.Builders.Count == 1 ? "builds" : "build")} it: have global.json ask for {(want.Builders.Count == 1 ? "it" : "one of them")}"
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

    /// <summary>An agent's Add .NET to the Shell's PATH: the person's shell is theirs to change.</summary>
    public const string AgentChangesPath = "An agent never changes the person's shell PATH: ask the person, who does it with the banner over Passenger's window, "
        + "or Tools, Add .NET to the Shell's PATH…";

    /// <summary>The banner's word of a C# file of no project shown while the machine has no .NET SDK: what Roslyn's server
    /// cannot give it, and why.</summary>
    public static string LooseWithoutSdk(string file) =>
        $"{file} is a C# file of no project, which knows no framework types until a .NET SDK is installed: Roslyn reads a loose file's framework only from a project the SDK's MSBuild builds";

    /// <summary>Install .NET SDK with nothing lacking: the newest SDK found, and where.</summary>
    public static string NothingToInstall(DotnetMachine? machine, DotnetWant want) =>
        machine?.NewestSdk is { } sdk && machine.Found is { } found
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

    /// <summary>An action on a Godot project asked for in a folder that holds none.</summary>
    public const string NoGodotProject = "The open folder holds no Godot project (project.godot)";

    /// <summary>A build asked of a folder with no solution or project file MSBuild could build, which it would refuse in its own
    /// words (MSB1003).</summary>
    public const string NothingToBuild = "The open folder has no solution or project to build: open one, or a folder that holds one";

    /// <summary>A build asked of a folder whose one kind of project is a Godot project of GDScript alone, which builds nothing.</summary>
    public const string NothingToBuildOfScripts = "The open folder's Godot project is GDScript alone, which Godot runs as it is: there is nothing to build (Run, Debug and Profile start its game)";

    /// <summary>A Godot game's GoDotTest tests asked to run where no scene of the game calls GoTest.RunTests, so a run would
    /// start Godot and run none until stopped: said before it starts.</summary>
    public static string GoDotTestNeverRuns(string game) =>
        $"{game} never runs its GoDotTest tests: no scene of it calls GoTest.RunTests, neither its main scene nor a test scene (GoDotTest's README makes one in {Testing.GoDotTests.TestFolder}), so Godot is not started for them";

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

    /// <summary>An AppHost the folder's session no longer runs, asked of by an agent's call or an Aspire tool: it stopped,
    /// or the folder was left.</summary>
    public const string AppHostNotOwned = "The AppHost is no longer owned by the workspace";

    /// <summary>An AppHost's folder left, or the AppHost stopped or started again, while what was asked of it waited.</summary>
    public const string AppHostChanged = "The workspace or owned AppHost changed";

    /// <summary>Why a file shown has no outline for Structure and File Structure: they list what its language server
    /// outlines, and none has it open.</summary>
    public static string NoOutline(string file) => $"{file} has no outline: no language server has it open";

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

    /// <summary>Go to Declaration found nothing to go to where it was asked.</summary>
    public const string NoDeclaration = "Cannot find a declaration to go to";

    /// <summary>What a start or an action under way was working in has ended: its folder was left or closed.</summary>
    public const string SolutionClosed = "The solution was closed";

    /// <summary>Why a program cannot be profiled, or its heap read (<paramref name="doing"/> it), now: the debugger holds it
    /// paused, and its runtime answers the profiler only as it runs.</summary>
    public static string PausedInDebugger(string program, string doing) => $"{program} is paused in the debugger: continue it to {doing}";

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

    /// <summary>A file shown read-only (decompiled, installed, carried by the app, a copy to read), which nothing edits or
    /// saves.</summary>
    public static string ReadOnly(string name) => $"{name} is read-only";

    /// <summary>An agent's act in Godot's editor from its own Passenger (<c>--mcp</c>), which no editor of the person's reaches.</summary>
    public const string GodotEditorOfPerson = $"{Mcp.McpTools.GodotEditor} acts in the person's own Godot editor, which connects to the Passenger window they use, never to an agent's own: attach to that window (--mcp-attach)";

    /// <summary>An agent's act in Godot's editor on a project whose editor is not connected: what puts Passenger's addon in the
    /// project (<paramref name="addon"/> false), or what opens the editor, a configuration of the folder's where one does.</summary>
    public static string NoGodotEditor(string project, bool addon, string? opens) =>
        !addon ? $"Passenger's addon is not in {project}: Install Passenger's Godot Addon… puts it there (asking the person), and Godot's editor connects as it opens the project"
        : opens is not null ? $"No Godot editor on {project} is connected: run {opens} to open one, whose Passenger's addon connects to this window"
        : $"No Godot editor on {project} is connected: open the project in Godot's editor, whose Passenger's addon connects to this window";

    /// <summary>An agent's act in Godot's editor whose addon is older than one that takes it.</summary>
    public static string GodotAddonOlder(string project, int version) =>
        $"Passenger's addon in {project} is version {version}, which takes no agent's acts: Install Passenger's Godot Addon… updates it, and Godot's editor takes it as it next opens the project";

    /// <summary>A change an agent asked for that would write over what the person's Godot editor holds of a file
    /// (<see cref="Passenger.Workspace.GodotHolds"/>), which only the person may choose to.</summary>
    public static string HeldInGodot(string held) => $"{held}, which only the person may choose to write over: save it in Godot first";

    /// <summary>A source generator's code copied to read (<see cref="CSharp.GeneratedCode"/>), which the C# language server
    /// answers nothing in, with what made it and for which project where known (<see cref="CSharp.GeneratedCode.OriginOf"/>).</summary>
    public static string Generated(string name, string? origin = null) =>
        $"{name} is a source generator's code{(origin is null ? string.Empty : $", {origin},")} copied to read: the C# language server answers in the code it is made from";

    /// <summary>A file whose grammar has yet to read it whole, so the lines inside its strings are not yet known.</summary>
    public static string ReadingStrings(string name) => $"{name} is still being read, to tell the lines inside its strings";

    /// <summary>Why the file shown cannot be saved (the one a diff's tab edits, <see cref="Workspace.OpenDocument.Saves"/>): none is
    /// shown, it is read-only, or it has nothing to save.</summary>
    public static string? NeedsUnsaved(ActionContext context) =>
        context.File?.Saves is not { } file ? NoFile : file.IsReadOnly ? ReadOnly(file.Name) : file.IsModified ? null : $"{file.Name} is saved";

    /// <summary>Why an action on the file shown cannot run: none is shown.</summary>
    public static string? NeedsFile(ActionContext context) => context.File is null ? NoFile : null;

    /// <summary>Why a line of the file shown cannot be acted on: none is shown, or its tab holds no lines (a preview, a workbook).</summary>
    public static string? NeedsLines(ActionContext context) =>
        context.File is not { } file ? NoFile : file.HoldsText ? null : $"{file.Name} is {file.ShownAs}, with no lines";
}
