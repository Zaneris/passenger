using System.Globalization;
using Passenger.Text;

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

    /// <summary>Go to Declaration found nothing to go to where it was asked.</summary>
    public const string NoDeclaration = "Cannot find a declaration to go to";

    /// <summary>What a start or an action under way was working in has ended: its folder was left or closed.</summary>
    public const string SolutionClosed = "The solution was closed";

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

    /// <summary>Why an action on the file shown cannot run: none is shown.</summary>
    public static string? NeedsFile(ActionContext context) => context.File is null ? NoFile : null;

    /// <summary>Why a line of the file shown cannot be acted on: none is shown, or its tab holds no lines (a preview, a workbook).</summary>
    public static string? NeedsLines(ActionContext context) =>
        context.File is not { } file ? NoFile : file.HoldsText ? null : $"{file.Name} is {file.ShownAs}, with no lines";
}
