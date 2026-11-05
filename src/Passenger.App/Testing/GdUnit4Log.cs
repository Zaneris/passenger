using Passenger.App.Run;

namespace Passenger.App.Testing;

/// <summary>What GdUnit4's adapter says in a run, read for why its tests that need Godot's engine never ran: before it
/// starts Godot it builds its test runner into the game, through dotnet build and then Godot's headless editor, saying the
/// compiler's errors only among its informational words and ending the engine's run with no result for those tests. A
/// failed build is said once for each test assembly (by its name, as the adapter scopes its words), with the errors it
/// had. A run's: its messages come one at a time.</summary>
public sealed class GdUnit4Log
{
    /// <summary>What the adapter puts ahead of each of its words, then the test assembly's name in brackets.</summary>
    private const string Prefix = "[GdUnit4] [";

    /// <summary>The most compiler errors said of one build.</summary>
    private const int MostErrors = 3;

    private readonly Dictionary<string, List<Problem>> errors = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> causes = new(StringComparer.Ordinal);

    /// <summary>Takes in a message the adapter said: the words Passenger says of a build of its runner that failed, the first
    /// time the adapter says one failed for its assembly; else null.</summary>
    public string? Heard(string message)
    {
        if (!message.StartsWith(Prefix, StringComparison.Ordinal) || message.IndexOf("] ", Prefix.Length, StringComparison.Ordinal) is not (> 0 and var close))
        {
            return null;
        }
        string assembly = message[Prefix.Length..close];
        string said = message[(close + "] ".Length)..];
        if (Output(said) is { } line)
        {
            if (BuildOutput.Parse(line) is { Severity: ProblemSeverity.Error } problem)
            {
                List<Problem> kept = errors.TryGetValue(assembly, out List<Problem>? before) ? before : errors[assembly] = [];
                if (kept.Count < MostErrors && !kept.Any(k => k.Code == problem.Code && k.Message == problem.Message && k.Path == problem.Path && k.Position == problem.Position))
                {
                    kept.Add(problem);
                }
            }
            return null;
        }
        if (Failed(said) is not { } build || causes.ContainsKey(assembly))
        {
            return null;
        }
        IReadOnlyList<Problem> had = errors.GetValueOrDefault(assembly) ?? [];
        string cause = $"GdUnit4's {build} of its test runner into {assembly} failed, so Godot ran none of its tests that need the engine: "
            + (had.Count == 0 ? said : string.Join("; ", had.Select(Shown))) + ".";
        if (had.Any(OlderBase))
        {
            cause += " This Godot's C# source generators cannot build a class whose base class an older Godot's built, as GdUnit4's test runner's is:"
                + " these tests run on the Godot GdUnit4 was built for, or with a GdUnit4 built for this one.";
        }
        causes[assembly] = cause;
        return cause;
    }

    /// <summary>Why the tests of a test assembly (by its name) that need the engine never ran, where the adapter said a build of
    /// its runner failed; else null.</summary>
    public string? Why(string assembly) => causes.GetValueOrDefault(assembly);

    /// <summary>A line a program the adapter ran wrote (dotnet's, Godot's), as the adapter passes it on; else null.</summary>
    private static string? Output(string said)
    {
        int at = said.IndexOf(": ", StringComparison.Ordinal);
        return at > 0 && said[..at].TrimEnd() is { } source && (source.EndsWith("/out", StringComparison.Ordinal) || source.EndsWith("/err", StringComparison.Ordinal))
            ? said[(at + ": ".Length)..]
            : null;
    }

    /// <summary>The build the adapter says failed, by the words that name it; else null.</summary>
    private static string? Failed(string said)
    {
        if (said.StartsWith("dotnet build failed with exit code: ", StringComparison.Ordinal))
        {
            return "dotnet build";
        }
        const string editor = "Rebuilding Godot Project ends with exit code: ";
        return said.StartsWith(editor, StringComparison.Ordinal) && said[editor.Length..].Trim() != "0" ? "build through Godot's editor" : null;
    }

    private static string Shown(Problem problem) =>
        $"{(problem.Path is { } path ? $"{Path.GetFileName(path)}({problem.Position.Line + 1},{problem.Position.Column + 1}): " : string.Empty)}error {problem.Code}: {problem.Message}";

    /// <summary>Whether a compiler error says a script's base class lacks what a newer Godot's generators make in every script
    /// class: the base was built by an older Godot's.</summary>
    private static bool OlderBase(Problem problem) => problem.Code == "CS0117" && problem.Message.Contains("'GodotInternal'", StringComparison.Ordinal);
}
