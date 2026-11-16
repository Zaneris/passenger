using System.Text;
using System.Text.RegularExpressions;

namespace Passenger.Workspace.Gherkin;

/// <summary>How a step of a scenario's run went.</summary>
public enum StepOutcome
{
    Passed,
    Failed,
    Skipped,
    Pending,
    Undefined,
    Ambiguous,
}

/// <summary>A step a run of a scenario takes, in the order it takes them: its line (from zero), and its first line as the run's
/// trace writes it, its keyword as written and its text with the example's values in place of its placeholders.</summary>
public sealed record RunStep(int Line, string Said);

/// <summary>How one step of a run went: the binding it ran as the trace names it ("Steps.Given(50)" read as
/// "Steps.Given" and "50", its arguments as converted), and what a failure, a pending step or a binding error said.</summary>
public sealed record StepResult(int Line, string Said, StepOutcome Outcome)
{
    public string? Binding { get; init; }

    public string? Arguments { get; init; }

    public string? Message { get; init; }
}

/// <summary>A scenario's steps read from the trace Reqnroll and SpecFlow write into each test's output: each step's first line,
/// then a line starting "-> " saying how it went ("done:", "error:", "pending:", "skipped", "binding error:", "undefined:", or
/// "No matching step definition found"), with the binding's own output between them; a step that passed says nothing where
/// successful steps are not traced. The steps are taken in their run's order, each found after the one before it.</summary>
public static partial class StepTrace
{
    private const string Said = "-> ";

    /// <summary>The steps a run of a scenario takes: its feature's background's, its rule's, then its own, with an example's
    /// values in place of its placeholders.</summary>
    public static IReadOnlyList<RunStep> StepsOf(FeatureReading reading, FeatureScenario scenario, IReadOnlyDictionary<string, string>? example = null)
    {
        List<RunStep> steps = [];
        foreach (FeatureScenario part in reading.Scenarios.Where(s => s.Kind == ScenarioKind.Background && s.Rule is null)
            .Concat(reading.Scenarios.Where(s => s.Kind == ScenarioKind.Background && s.Rule is not null && ReferenceEquals(s.Rule, scenario.Rule)))
            .Append(scenario))
        {
            foreach (FeatureStep step in part.Steps)
            {
                string text = step.Text;
                if (example is not null && step.Placeholders.Count > 0)
                {
                    text = Placeholder().Replace(text, m => example.TryGetValue(m.Groups[1].Value, out string? value) ? value : m.Value);
                }
                steps.Add(new RunStep(step.Line, (step.Keyword + text).TrimEnd()));
            }
        }
        return steps;
    }

    /// <summary>How each step went as a test's output tells it, up to the last step found in it; a step whose result is unsaid
    /// passed where a later step was reached or the test passed.</summary>
    public static IReadOnlyList<StepResult> Read(string output, IReadOnlyList<RunStep> steps, bool passed)
    {
        string[] lines = [.. output.Split('\n').Select(l => Escape().Replace(l, string.Empty).TrimEnd())];
        List<StepResult> results = [];
        int at = Find(lines, steps.Count > 0 ? steps[0].Said : null, 0);
        for (int i = 0; i < steps.Count && at >= 0; i++)
        {
            int next = i + 1 < steps.Count ? Find(lines, steps[i + 1].Said, at + 1) : -1;
            int end = next < 0 ? lines.Length : next;
            StepResult? result = ResultOf(lines, at + 1, end, steps[i]);
            if (result is null && (next >= 0 || passed))
            {
                result = new StepResult(steps[i].Line, steps[i].Said, StepOutcome.Passed);
            }
            if (result is not null)
            {
                results.Add(result);
            }
            at = next;
        }
        return results;
    }

    private static int Find(string[] lines, string? said, int from)
    {
        if (said is null)
        {
            return -1;
        }
        for (int i = from; i < lines.Length; i++)
        {
            if (lines[i] == said)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>The first line between a step's first line and the next step's that says how it went, read.</summary>
    private static StepResult? ResultOf(string[] lines, int from, int end, RunStep step)
    {
        for (int i = from; i < end; i++)
        {
            string line = lines[i];
            if (!line.StartsWith(Said, StringComparison.Ordinal))
            {
                continue;
            }
            string rest = line[Said.Length..];
            if (rest.Contains("No matching step definition found", StringComparison.Ordinal) || Word(rest, "undefined") is not null)
            {
                return new StepResult(step.Line, step.Said, StepOutcome.Undefined);
            }
            if (Word(rest, "done") is { } done)
            {
                (string ran, string? arguments) = Match(Timed(done));
                return new StepResult(step.Line, step.Said, StepOutcome.Passed) { Binding = ran, Arguments = arguments };
            }
            if (Word(rest, "error") is { } error)
            {
                return new StepResult(step.Line, step.Said, StepOutcome.Failed) { Message = Message(lines, i, end, error) };
            }
            if (Word(rest, "pending") is { } pending)
            {
                int close = Closing(pending);
                (string held, string? arguments) = Match(close < 0 ? pending : pending[..(close + 1)]);
                string? said = close >= 0 && pending.AsSpan(close + 1).StartsWith(": ", StringComparison.Ordinal) ? pending[(close + 3)..] : null;
                return new StepResult(step.Line, step.Said, StepOutcome.Pending) { Binding = held, Arguments = arguments, Message = said };
            }
            if (rest.StartsWith("skipped", StringComparison.Ordinal))
            {
                return new StepResult(step.Line, step.Said, StepOutcome.Skipped) { Message = Word(rest, "skipped") };
            }
            if (Word(rest, "binding error") is { } refused)
            {
                StepOutcome outcome = refused.StartsWith("Ambiguous step definitions", StringComparison.Ordinal) ? StepOutcome.Ambiguous : StepOutcome.Failed;
                return new StepResult(step.Line, step.Said, outcome) { Message = refused };
            }
        }
        return null;
    }

    /// <summary>What follows a result's word and its colon ("done: X" as "X"); null for a line of another word.</summary>
    private static string? Word(string rest, string word) =>
        rest.Length > word.Length + 1 && rest.StartsWith(word, StringComparison.Ordinal) && rest[word.Length] == ':' ? rest[(word.Length + 1)..].TrimStart() : null;

    /// <summary>An error's message, its lines up to the one its duration ends ("(0.0s)").</summary>
    private static string Message(string[] lines, int first, int end, string said)
    {
        StringBuilder message = new(said);
        for (int i = first; i < end; i++)
        {
            string line = i == first ? said : lines[i];
            if (i > first)
            {
                message.Append('\n').Append(line);
            }
            if (Duration().IsMatch(line))
            {
                break;
            }
        }
        return Timed(message.ToString());
    }

    /// <summary>A result's words less the duration Reqnroll ends them with.</summary>
    private static string Timed(string said) => Duration().Replace(said, string.Empty);

    /// <summary>A binding's call as the trace writes it ("Steps.Given(50, \"a\")") as its method and its arguments.</summary>
    private static (string Binding, string? Arguments) Match(string call)
    {
        int open = call.IndexOf('(', StringComparison.Ordinal);
        return open < 0 || !call.EndsWith(')') ? (call, null) : (call[..open], call[(open + 1)..^1]);
    }

    /// <summary>Where a call's argument list closes, its strings' brackets passed over; -1 where it never does.</summary>
    private static int Closing(string call)
    {
        int depth = 0;
        bool quoted = false;
        for (int i = 0; i < call.Length; i++)
        {
            char c = call[i];
            if (quoted)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    [GeneratedRegex("<([^<>]+)>", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\x1b\[[0-9;]*m", RegexOptions.CultureInvariant)]
    private static partial Regex Escape();

    [GeneratedRegex(@" \(\d+[.,]\d+s\)$", RegexOptions.CultureInvariant)]
    private static partial Regex Duration();
}
