using System.Text.RegularExpressions;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.CSharp;

/// <summary>What the language server says to the person, or the IDE says of its load, for the status bar: its words,
/// whether they are an error or a warning (null for neither), and the project load problem they name, which Problems'
/// Toolset, Environment lists.</summary>
public sealed record ServerSaid(string Text, ProblemSeverity? Severity, ProjectLoadProblem? Problem = null);

/// <summary>A warning or error the language server met loading a project (a package with a known vulnerability, an SDK
/// that cannot be found): the project file, canonical, and what MSBuild or NuGet said, less its repeat of the
/// file.</summary>
public sealed record ProjectLoadProblem(string Project, ProblemSeverity Severity, string Message);

/// <summary>The project Roslyn's server makes for the C# files no project of the folder holds (one opened before its project
/// loads, or outside every project): <c>Canonical.csproj</c> in a folder of its own under the temp folder's
/// <c>roslyn-canonical-misc</c>, restored as it is made. The server's own detail, never shown or logged as the person's.</summary>
internal static class CanonicalProject
{
    private const string FileName = "Canonical.csproj";

    private const string Folder = "roslyn-canonical-misc";

    /// <summary>Whether what the server logged names it, by its path.</summary>
    public static bool IsAbout(string logged) =>
        logged.Contains(Folder, StringComparison.Ordinal) && logged.Contains(FileName, StringComparison.Ordinal);

    /// <summary>Whether the server's words name it: by its path, or by its file name alone (a restore's progress) unless a
    /// project of the folder has that name.</summary>
    public static bool IsNamedIn(string text, IEnumerable<string> projects) =>
        IsAbout(text) || (text.Contains(FileName, StringComparison.Ordinal) && !projects.Contains(FileName, StringComparer.OrdinalIgnoreCase));
}

/// <summary>What a line of the server's project system says of a load.</summary>
internal enum LoadReportKind
{
    /// <summary>A warning or error of a project's load.</summary>
    Problem,

    /// <summary>A project loaded with neither.</summary>
    Loaded,

    /// <summary>The server finished loading (or reloading) every project it was loading.</summary>
    BatchEnded,
}

/// <summary>A line Roslyn's project system logs as it loads projects: "Warning while loading P: message" and "Error while
/// loading P: message" (its kind and path its own, the message MSBuild's or NuGet's), "Successfully completed load of P"
/// when there were none, and "Completed (re)load of all projects in T" at a batch's end. The last two are the server's
/// English resources; a server in another language leaves a project's problems in place until it reports again.</summary>
internal sealed partial record LoadReport(LoadReportKind Kind, ProjectLoadProblem? Problem, string? Project)
{
    /// <summary>The report a logged line is, its path canonical (read on the server's thread, where the file system may be
    /// asked); null for any other line, and for one of the server's own project (<see cref="CanonicalProject"/>).</summary>
    public static LoadReport? Read(string logged)
    {
        if (CanonicalProject.IsAbout(logged))
        {
            return null;
        }
        if (ProblemLine().Match(logged) is { Success: true } problem)
        {
            string given = problem.Groups["path"].Value;
            string message = problem.Groups["message"].Value.Trim();
            // MSBuild ends an error with the project it came from, which the line has named already.
            if (message.EndsWith(given, StringComparison.Ordinal))
            {
                message = message[..^given.Length].TrimEnd();
            }
            message = Spaces().Replace(message, " ");
            ProblemSeverity severity = problem.Groups["kind"].Value == "Error" ? ProblemSeverity.Error : ProblemSeverity.Warning;
            return new LoadReport(LoadReportKind.Problem, new ProjectLoadProblem(PathNames.Canonical(given), severity, message), null);
        }
        if (LoadedLine().Match(logged) is { Success: true } loaded)
        {
            return new LoadReport(LoadReportKind.Loaded, null, PathNames.Canonical(loaded.Groups["path"].Value.Trim()));
        }
        return BatchLine().IsMatch(logged) ? new LoadReport(LoadReportKind.BatchEnded, null, null) : null;
    }

    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*(?<kind>Warning|Error) while loading (?<path>.+?\.(?:[A-Za-z]+proj|cs)): (?<message>[\s\S]*)$")]
    private static partial Regex ProblemLine();

    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*Successfully completed load of (?<path>.+?\.(?:[A-Za-z]+proj|cs))\s*$")]
    private static partial Regex LoadedLine();

    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*Completed \(re\)load of all projects")]
    private static partial Regex BatchLine();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>What Roslyn's server logs of a restore it runs as it loads, each line said under "Restoring App.csproj: ": the
/// start ("Running dotnet restore on PATH"), dotnet restore's output, and "Failed to run restore on PATH" when it failed. A
/// restore that fails before it writes a project's assets (a nuget.config that does not read) leaves no load to report
/// its errors, so they are read here: MSBuild's and NuGet's errors, each project's joined, by the project each names, else
/// the path restored. The server's English resources, as <see cref="LoadReport"/>'s; heard on the server's thread.</summary>
internal sealed partial class RestoreReports
{
    private readonly Lock gate = new();

    /// <summary>The errors each restore under way has said, by the name its lines are said under, under <see cref="gate"/>.</summary>
    private readonly Dictionary<string, List<(string? Project, string Message)>> errors = new(StringComparer.Ordinal);

    /// <summary>The problems a logged line ends a failed restore with, one per project with its errors joined; none for
    /// any other line, and none of the server's own project (<see cref="CanonicalProject"/>).</summary>
    public IReadOnlyList<ProjectLoadProblem> Hear(string logged)
    {
        if (StageLine().Match(logged) is not { Success: true } line)
        {
            return [];
        }
        string stage = line.Groups["name"].Value;
        string output = line.Groups["output"].Value;
        lock (gate)
        {
            if (BeganLine().IsMatch(output))
            {
                errors.Remove(stage);
                return [];
            }
            if (ErrorLine().Match(output) is { Success: true } error)
            {
                if (!errors.TryGetValue(stage, out List<(string? Project, string Message)>? said))
                {
                    errors[stage] = said = [];
                }
                string? project = error.Groups["project"].Success ? error.Groups["project"].Value.Trim() : null;
                said.Add((project, Spaces().Replace(error.Groups["message"].Value, " ").Trim()));
                return [];
            }
            if (FailedLine().Match(output) is not { Success: true } failed)
            {
                return [];
            }
            string restored = failed.Groups["path"].Value.Trim();
            errors.Remove(stage, out List<(string? Project, string Message)>? gone);
            List<(string Project, string Message)> each = [.. (gone ?? []).Select(e => (e.Project ?? restored, e.Message))];
            if (each.Count == 0)
            {
                each.Add((restored, "dotnet restore ended with an error; the C# language server's log has what it said"));
            }
            return [.. each.Where(e => !CanonicalProject.IsAbout(e.Project)).GroupBy(e => e.Project, StringComparer.Ordinal)
                .Select(g => new ProjectLoadProblem(PathNames.Canonical(g.Key), ProblemSeverity.Error,
                    $"Restore failed: {string.Join(" ", g.Select(e => e.Message).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal))}"))];
        }
    }

    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*Restoring (?<name>.+?): (?<output>.*)$", RegexOptions.Singleline)]
    private static partial Regex StageLine();

    [GeneratedRegex(@"^Running dotnet restore on ")]
    private static partial Regex BeganLine();

    [GeneratedRegex(@"^Failed to run restore on (?<path>.+)$", RegexOptions.Singleline)]
    private static partial Regex FailedLine();

    /// <summary>MSBuild's error line: "ORIGIN: error CODE: MESSAGE [PROJECT]", its code and project each left out at times.</summary>
    [GeneratedRegex(@"^.*?:\s*error\s*[A-Za-z]*\d*\s*:\s*(?<message>.*?)(?:\s*\[(?<project>[^\[\]]+)\])?\s*$", RegexOptions.Singleline)]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>The folder a project's load or restore error blames that this user cannot write
/// (<see cref="UnwritableFolders"/>), asked of the system by the folder's mode on macOS and Linux, on the server's thread;
/// Windows, whose folders' read-only mark keeps no one from writing, is not asked.</summary>
internal static class FolderAccess
{
    /// <summary>The folder a project's error blames, canonical, or null for none; a folder under the open one counts as the
    /// project's own.</summary>
    public static string? Unwritable(ProjectLoadProblem problem, string? folder) =>
        !OperatingSystem.IsWindows() && UnwritableFolders.Of(problem.Message, problem.Project, folder, Directory.Exists, at => Libc.Access(at, Libc.MayWrite) == 0) is { } found
            ? PathNames.Canonical(found)
            : null;
}
