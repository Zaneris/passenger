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
        DotnetLack.OlderThanProject => $"{want.Newer!.Value.Project} targets .NET {want.Newer.Value.Framework}, which the .NET SDK {want.Sdk} does not build: "
            + $"it needs the .NET {want.Newer.Value.Framework} SDK",
        DotnetLack.OffPath => $"Passenger uses the .NET SDK {want.Sdk} in {PathNames.Shown(want.Root!, null)}, which your shell's PATH does not name, "
            + "so a terminal outside Passenger finds no dotnet",
        _ => null,
    };

    /// <summary>What an agent's ide_action of Install .NET SDK or Add .NET to the Shell's PATH is refused with.</summary>
    public const string AgentInstalls = "An agent never installs software or changes the person's PATH: ask the person, who does it with the banner over "
        + "Passenger's window, or Tools, Install .NET SDK…";

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

    /// <summary>A build asked of a folder whose one kind of project is a Godot project of GDScript alone, which builds nothing.</summary>
    public const string NothingToBuildOfScripts = "The open folder's Godot project is GDScript alone, which Godot runs as it is: there is nothing to build (Run, Debug and Profile start its game)";

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

    /// <summary>Why Context Help has nothing to name in a file shown: neither C#'s server nor GDScript's has it.</summary>
    public static string NoContextHelp(string file) => $"{file} has no context help: Context Help opens Godot's class reference from C# and GDScript";

    /// <summary>Context Help with the caret on nothing of Godot's engine: the project's own code, or .NET's.</summary>
    public const string NoGodotDocumentation = "The caret is on no Godot class or member, whose page in Godot's class reference Context Help opens";

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

    /// <summary>A source generator's code copied to read (<see cref="CSharp.GeneratedCode"/>), which the C# language server
    /// answers nothing in, with what made it and for which project where known (<see cref="CSharp.GeneratedCode.OriginOf"/>).</summary>
    public static string Generated(string name, string? origin = null) =>
        $"{name} is a source generator's code{(origin is null ? string.Empty : $", {origin},")} copied to read: the C# language server answers in the code it is made from";

    /// <summary>A file whose grammar has yet to read it whole, so the lines inside its strings are not yet known.</summary>
    public static string ReadingStrings(string name) => $"{name} is still being read, to tell the lines inside its strings";

    /// <summary>Why the file shown cannot be saved: none is shown, it is read-only, or it has nothing to save.</summary>
    public static string? NeedsUnsaved(ActionContext context) =>
        context.File is not { } file ? NoFile : file.IsReadOnly ? ReadOnly(file.Name) : file.IsModified ? null : $"{file.Name} is saved";

    /// <summary>Why an action on the file shown cannot run: none is shown.</summary>
    public static string? NeedsFile(ActionContext context) => context.File is null ? NoFile : null;

    /// <summary>Why a line of the file shown cannot be acted on: none is shown, or its tab holds no lines (a preview, a workbook).</summary>
    public static string? NeedsLines(ActionContext context) =>
        context.File is not { } file ? NoFile : file.HoldsText ? null : $"{file.Name} is {file.ShownAs}, with no lines";
}
