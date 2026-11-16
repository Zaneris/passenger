using System.Text.RegularExpressions;
using Passenger.App.CSharp;
using Passenger.App.Run;
using Passenger.Workspace.Gherkin;

namespace Passenger.App.Bdd;

/// <summary>A project's load problem about a .feature file of its own, as Reqnroll's code-behind generator refuses one while
/// the project builds to load, named by the project host from the project's folder with its place ("Features/A.feature(2,1):
/// "): said without Gherkin's repeat of that place, and an error where the file, read as the generator reads it in its
/// project's language, holds no feature said as the file's mark says it (<see cref="ReqnrollConfig.NoFeatureWhy"/>), at the
/// mark's place, so the lines it refused are one problem. Read on the language server's thread, where it is heard.</summary>
internal static partial class FeatureLoadProblems
{
    public static ProjectLoadProblem Said(ProjectLoadProblem problem)
    {
        if (Named().Match(problem.Message) is not { Success: true } named || Path.GetDirectoryName(problem.Project) is not { Length: > 0 } folder)
        {
            return problem;
        }
        string file = named.Groups["file"].Value;
        ProjectLoadProblem said = problem with { Message = $"{file}{named.Groups["place"].Value}: {GherkinPlace().Replace(named.Groups["message"].Value, "")}" };
        if (problem.Severity != ProblemSeverity.Error)
        {
            return said;
        }
        string text;
        try
        {
            text = File.ReadAllText(Path.GetFullPath(file, folder));
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return said;
        }
        ReqnrollConfig config = ReqnrollConfig.In(folder);
        FeatureReading reading = FeatureReading.Read(text, GherkinDialects.Default(config.FeatureLanguage));
        return reading.Errors.FirstOrDefault(e => e.NoFeature) is { } none
            ? problem with { Message = $"{file}({none.Line + 1},{none.Column + 1}): {config.NoFeatureWhy(reading)}" }
            : said;
    }

    [GeneratedRegex(@"^(?<file>[^\r\n]+?\.feature)(?<place>\(\d+(?:,\d+)?\))?: (?<message>[\s\S]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Named();

    /// <summary>The place Gherkin's reader writes before its message, "(2:1): ", which the file's place says already.</summary>
    [GeneratedRegex(@"^\(\d+:\d+\):\s*")]
    private static partial Regex GherkinPlace();
}
