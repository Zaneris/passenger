using Passenger.App.Run;

namespace Passenger.App.ToolWindows;

/// <summary>What Problems shows of a tab's problems: those of the severities left on whose message, code or file name
/// holds the text, ignoring case. A window starts with errors and warnings, messages (a server's suggestions, which the
/// editor marks where they are) counted but hidden.</summary>
public sealed record ProblemFilter(bool Errors = true, bool Warnings = true, bool Messages = false, string Text = "")
{
    /// <summary>Errors and warnings, as a window starts.</summary>
    public static readonly ProblemFilter Default = new();

    /// <summary>Whether it lets every problem through.</summary>
    public bool IsNone => Errors && Warnings && Messages && Text.Length == 0;

    /// <summary>Whether problems of a severity are shown.</summary>
    public bool Shows(ProblemSeverity severity) => severity switch
    {
        ProblemSeverity.Error => Errors,
        ProblemSeverity.Warning => Warnings,
        _ => Messages,
    };

    /// <summary>The filter with a severity shown or hidden.</summary>
    public ProblemFilter Showing(ProblemSeverity severity, bool shown) => severity switch
    {
        ProblemSeverity.Error => this with { Errors = shown },
        ProblemSeverity.Warning => this with { Warnings = shown },
        _ => this with { Messages = shown },
    };

    /// <summary>Whether a problem is shown.</summary>
    public bool Passes(Problem problem) => Shows(problem.Severity) && Holds(problem);

    /// <summary>Whether a problem's message, code or file name holds the text, whatever its severity.</summary>
    public bool Holds(Problem problem) =>
        Text.Length == 0
        || problem.Message.Contains(Text, StringComparison.OrdinalIgnoreCase)
        || problem.Code?.Contains(Text, StringComparison.OrdinalIgnoreCase) == true
        || (problem.Path is { } path && Path.GetFileName(path.AsSpan()).Contains(Text, StringComparison.OrdinalIgnoreCase));
}
