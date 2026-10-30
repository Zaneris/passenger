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

    /// <summary>The window has no folder open (a solution, a project or a folder of files).</summary>
    public const string NoFolder = "No folder is open";

    /// <summary>The folder open has no solution (a project opened alone, or a folder of files).</summary>
    public const string NoSolution = "No solution is open";

    /// <summary>The folder open is in no Git working tree.</summary>
    public const string NoRepository = "The folder is not in a Git repository";

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
        { NoneInstalled: true } => "No .NET SDK is installed: install one to load, build and run projects",
        _ => $"No .NET SDK was found for this folder: {missing.Said.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()}",
    };

    /// <summary>No run configuration to run, debug or profile (the <paramref name="verb"/>): the folder has no SDK to
    /// load its projects with (<see cref="NoSdk"/>), or no executable project.</summary>
    public static string NothingTo(string verb, MissingSdk? missing, string? folder) =>
        missing is null ? $"Nothing to {verb}: no executable project" : NoSdk(missing, folder);

    /// <summary>Why a file shown has no outline for Structure and File Structure: they list what its language server
    /// outlines, and none has it open.</summary>
    public static string NoOutline(string file) => $"{file} has no outline: no language server has it open";

    /// <summary>An action whose whole work is to move the person's keys (Focus Editor), asked for by an agent, which never
    /// moves them.</summary>
    public const string PersonsKeys = "It moves the person's keys, which only the person does";

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

    /// <summary>Why the file shown cannot be saved: none is shown, it is read-only, or it has nothing to save.</summary>
    public static string? NeedsUnsaved(ActionContext context) =>
        context.File is not { } file ? NoFile : file.IsReadOnly ? ReadOnly(file.Name) : file.IsModified ? null : $"{file.Name} is saved";

    /// <summary>Why an action on the file shown cannot run: none is shown.</summary>
    public static string? NeedsFile(ActionContext context) => context.File is null ? NoFile : null;

    /// <summary>Why a line of the file shown cannot be acted on: none is shown, or its tab holds no lines (a preview, a workbook).</summary>
    public static string? NeedsLines(ActionContext context) =>
        context.File is not { } file ? NoFile : file.HoldsText ? null : $"{file.Name} is {file.ShownAs}, with no lines";
}
