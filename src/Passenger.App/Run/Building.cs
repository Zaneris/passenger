using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>A problem a build or the language server reported: where it is and what it says.</summary>
public sealed partial record Problem(ProblemSeverity Severity, string Message, string? Code, string? Path, TextPosition Position, string? Project)
{
    public string Where => Path is null ? Project ?? string.Empty : $"{System.IO.Path.GetFileName(Path)}:{Position.Line + 1}:{Position.Column + 1}";

    /// <summary>The page the message points to for more (a vulnerable package's advisory), which Problems links.</summary>
    public Uri? Link { get; init; }

    /// <summary>The words of the message the link is drawn on (an ESLint rule's name), or null for its address, which the
    /// message then has in it.</summary>
    public string? LinkText { get; init; }

    /// <summary>The first web address in a message, less the punctuation ending its sentence; null without one.</summary>
    public static Uri? LinkIn(string message) =>
        Address().Match(message) is { Success: true } found
            && Uri.TryCreate(found.Value.TrimEnd('.', ',', ';', ':', ')', ']'), UriKind.Absolute, out Uri? link) ? link : null;

    [GeneratedRegex(@"https?://[^\s""'<>]+")]
    private static partial Regex Address();
}

public enum ProblemSeverity
{
    Error,
    Warning,
}

/// <summary>How a build ended, as each caller hears it.</summary>
public enum BuildStatus
{
    Succeeded,
    Failed,
    Stopped,

    /// <summary>Not begun: another build was under way, or there was nothing to build.</summary>
    Refused,

    /// <summary>The .NET command line could not be started (no SDK, a folder gone).</summary>
    NotStarted,
}

/// <summary>A build's end and what to tell the person about it, with what it found and its last words: the build's own,
/// whatever the Problems and Build windows show by the time a caller reads them.</summary>
public sealed record BuildOutcome(BuildStatus Status, string Message)
{
    public bool Succeeded => Status == BuildStatus.Succeeded;

    /// <summary>The errors and warnings the build reported, each once, as Problems lists them.</summary>
    public IReadOnlyList<Problem> Problems { get; init; } = [];

    /// <summary>The last lines the build wrote.</summary>
    public IReadOnlyList<string> Output { get; init; } = [];

    /// <summary>The folder a publish that succeeded put the app in.</summary>
    public string? Published { get; init; }
}

/// <summary>What a build does: Build, Rebuild (everything again, nothing taken from the last build) and Clean, and
/// Publish, dotnet publish of one project (<see cref="PublishRequest"/>).</summary>
public enum BuildKind
{
    Build,
    Rebuild,
    Clean,
    Publish,
}

/// <summary>What started a build, which decides whether the Build window opens as it begins.</summary>
public enum BuildCause
{
    /// <summary>Asked for: the Build menu's, the Solution view's, the Build window's Rerun, an agent's.</summary>
    Explicit,

    /// <summary>A run configuration's build before Run, Debug or Profile launches it.</summary>
    BeforeLaunch,

    /// <summary>A test run's, which opens no Build window whatever the settings say.</summary>
    Tests,
}

/// <summary>Reads MSBuild's error and warning lines ("File.cs(12,5): error CS0103: ... [Project.csproj]")
/// out of a build's output.</summary>
public static partial class BuildOutput
{
    /// <summary>The arguments a build, rebuild or clean runs with: no terminal logger animation, no repeated summary,
    /// lines not wrapped, so every problem is one line read once. A framework given builds a project for that one alone.
    /// Windows targeting builds a WPF or Windows Forms project off Windows
    /// (<see cref="WindowsTargeting(string, IReadOnlyList{ProjectModel})"/>).</summary>
    public static IReadOnlyList<string> Arguments(string target, BuildKind kind = BuildKind.Build, string? framework = null, bool windowsTargeting = false,
        string? output = null)
    {
        List<string> arguments = kind switch
        {
            BuildKind.Clean => ["clean", target],
            BuildKind.Rebuild => ["build", target, "--no-incremental"],
            _ => ["build", target],
        };
        if (framework is not null)
        {
            arguments.AddRange(["--framework", framework]);
        }
        if (output is not null)
        {
            arguments.AddRange(["--output", output]);
        }
        if (windowsTargeting)
        {
            arguments.Add("-p:EnableWindowsTargeting=true");
        }
        arguments.AddRange(["-nologo", "-tl:off", "-clp:NoSummary;ForceNoAlign"]);
        return arguments;
    }

    /// <summary>Whether a build of a target (the solution, the folder, or one project) needs EnableWindowsTargeting: a
    /// project it builds (the one named and those it references, or every one) is one the project host evaluated with it.
    /// A global property, so every project the build reaches has it, which changes nothing for those that need no Windows
    /// framework.</summary>
    public static bool WindowsTargeting(string target, IReadOnlyList<ProjectModel> projects)
    {
        Dictionary<string, ProjectModel> byPath = new(PathNames.Comparer);
        foreach (ProjectModel each in projects)
        {
            byPath.TryAdd(each.Path, each);
        }
        if (!byPath.ContainsKey(target))
        {
            return projects.Any(p => p.WindowsTargeting);
        }
        HashSet<string> seen = new(PathNames.Comparer);
        Queue<string> next = new([target]);
        while (next.TryDequeue(out string? path))
        {
            if (!seen.Add(path) || !byPath.TryGetValue(path, out ProjectModel? project))
            {
                continue;
            }
            if (project.WindowsTargeting)
            {
                return true;
            }
            foreach (string reference in project.ProjectReferences)
            {
                next.Enqueue(reference);
            }
        }
        return false;
    }

    public static Problem? Parse(string line)
    {
        Match match = Diagnostic().Match(line);
        if (!match.Success)
        {
            return null;
        }
        ProblemSeverity severity = match.Groups["severity"].Value == "error" ? ProblemSeverity.Error : ProblemSeverity.Warning;
        string? path = match.Groups["file"].Value.Trim() is { Length: > 0 } file && !file.StartsWith("MSBUILD", StringComparison.Ordinal) ? file : null;
        // MSBuild names a file as the project or solution it came through spelled it, which may not be the disk's case.
        path = path is not null && System.IO.Path.IsPathFullyQualified(path) ? PathNames.Canonical(path) : path;
        int lineNumber = int.TryParse(match.Groups["line"].Value, out int l) ? l - 1 : 0;
        int column = int.TryParse(match.Groups["column"].Value, out int c) ? c - 1 : 0;
        string? project = match.Groups["project"].Success ? System.IO.Path.GetFileNameWithoutExtension(match.Groups["project"].Value) : null;
        string message = match.Groups["message"].Value.Trim();
        return new Problem(severity, message, match.Groups["code"].Value, path, new TextPosition(lineNumber, column), project) { Link = Problem.LinkIn(message) };
    }

    /// <summary>A compiler's message from a build inside an image, as BuildKit writes it ("#12 3.456 /src/Web/Program.cs(3,1):
    /// error CS1002: ..."), its file the workspace's through the image's paths; null for any other line.</summary>
    public static Problem? Parse(string line, Containers.ContainerPaths paths)
    {
        if (Parse(BuildKitStep().Replace(line, string.Empty)) is not { } problem)
        {
            return null;
        }
        return problem.Path is { } path && path.StartsWith('/') && paths.ToHost(path) is { } host ? problem with { Path = PathNames.Canonical(host) } : problem;
    }

    [GeneratedRegex(@"^#\d+\s+\d+(?:\.\d+)?\s+")]
    private static partial Regex BuildKitStep();

    /// <summary>A XAML compiler's message with the place it repeats at its end (Avalonia's "Line 8, position 6.", WPF's
    /// MarkupCompile's "Line 8 Position 6.") said at another, as the editor counts the file's lines; as it was when it
    /// repeats no such place, or another than <paramref name="was"/> (both from zero).</summary>
    public static string Moved(string message, TextPosition was, TextPosition now) =>
        RepeatedPlace().Replace(message, place =>
            int.TryParse(place.Groups["line"].ValueSpan, out int line) && line == was.Line + 1
                && int.TryParse(place.Groups["column"].ValueSpan, out int column) && column == was.Column + 1
                ? $"{place.Groups["word"].Value} {now.Line + 1}{place.Groups["between"].Value}{now.Column + 1}"
                : place.Value);

    [GeneratedRegex(@"\b(?<word>Line) (?<line>\d{1,9})(?<between>,? [Pp]osition )(?<column>\d{1,9})\b")]
    private static partial Regex RepeatedPlace();

    /// <summary>The problem before with this line's text added, when the line goes on with its message: MSBuild writes each
    /// further line of one (an XAML compiler's stack trace, NuGet's dependency chains) with the same place, code and project,
    /// its text indented past the code. Null when the line is a problem of its own, or none.</summary>
    public static Problem? Continuing(Problem before, string line)
    {
        if (Diagnostic().Match(line) is not { Success: true } match || Parse(line) is not { } more
            || more with { Message = before.Message, Link = before.Link } != before
            || (match.Groups["gap"].Length < 2 && more.Message.Length > 0))
        {
            return null;
        }
        string gap = match.Groups["gap"].Value;
        string message = $"{before.Message}\n{(gap.Length > 1 ? gap[1..] : string.Empty)}{match.Groups["message"].Value.TrimEnd()}";
        return before with { Message = message, Link = Problem.LinkIn(message) };
    }

    /// <summary>The location is the parenthesis just before ": error", so the path may hold parentheses of its own
    /// ("Shop (copy)", "C:\Program Files (x86)"); MSBuild's subcategory may stand before the category (Avalonia's XAML
    /// compiler's "Avalonia error AVLN2000").</summary>
    [GeneratedRegex(@"^\s*(?<file>.*?)(?:\((?<line>\d+)(?:,(?<column>\d+))?(?:[-,]\d+)*\))?\s*:\s*(?:[A-Za-z][\w.]*\s+)?(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:(?<gap>\s*)(?<message>.*?)(?:\s+\[(?<project>[^\]]+)\])?\s*$")]
    private static partial Regex Diagnostic();
}
