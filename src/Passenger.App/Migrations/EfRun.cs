using System.Diagnostics;
using System.Text;
using Passenger.App.ToolWindows;

namespace Passenger.App.Migrations;

/// <summary>What one <c>dotnet ef</c> came to, read from its <c>--prefix-output</c> lines: its exit code, what it answered
/// (its <c>data:</c> lines, JSON with <c>--json</c>), its errors and warnings each a line, and every line as written.</summary>
public sealed record EfOutcome(int ExitCode, string Data, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, IReadOnlyList<string> Lines)
{
    /// <summary>By its exit code alone: an error EF only logs is one it went past (a new database's missing history
    /// table).</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>A command that never ran (no .NET, a folder gone), its reason its one error.</summary>
    public static EfOutcome Unstarted(string why) => new(-1, string.Empty, [why], [], [why]);
}

/// <summary>Runs <c>dotnet ef</c> into a console (<see cref="ConsoleCommand"/>), each line read by its prefix as it comes
/// (<c>data:</c>, <c>error:</c>, <c>warn:</c>, <c>info:</c>; a line with none is dotnet's own), its <c>data:</c> lines,
/// what it answers, kept out of the console.</summary>
public static class EfRun
{
    /// <summary>The width of every prefix, padded: <c>data:    </c>, <c>error:   </c>, <c>verbose: </c>.</summary>
    private const int PrefixWidth = 9;

    public static async Task<EfOutcome> RunAsync(ProcessStartInfo start, ConsoleSink console, CancellationToken stop)
    {
        List<string> lines = [];
        List<string> data = [];
        List<string> errors = [];
        List<string> warnings = [];
        object gate = new();
        int exitCode;
        try
        {
            exitCode = (await ConsoleCommand.RunAsync(console, start, stop, Read, WithoutData)).ExitCode;
        }
        catch (Exception failed) when (Core.ChildFailures.Of(failed))
        {
            return EfOutcome.Unstarted($"Could not start {Path.GetFileName(start.FileName)}: {failed.Message}");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            exitCode = -1;
        }
        lock (gate)
        {
            return new EfOutcome(exitCode, string.Join('\n', data), [.. errors], [.. warnings], [.. lines]);
        }

        void Read(string line)
        {
            lock (gate)
            {
                lines.Add(line);
                (string kind, string said) = Split(line);
                switch (kind)
                {
                    case "data":
                        data.Add(said);
                        break;
                    case "error":
                        errors.Add(said);
                        break;
                    case "warn":
                        warnings.Add(said);
                        break;
                }
            }
        }
    }

    /// <summary>Whole lines as the console shows them: their <c>data:</c> lines left out.</summary>
    private static string WithoutData(string text)
    {
        if (!text.Contains("data:", StringComparison.Ordinal))
        {
            return text;
        }
        StringBuilder shown = new();
        int at = 0;
        while (at < text.Length)
        {
            int end = text.IndexOf('\n', at);
            int next = end < 0 ? text.Length : end + 1;
            if (Split(text[at..next].TrimEnd('\r', '\n')).Kind != "data")
            {
                shown.Append(text, at, next - at);
            }
            at = next;
        }
        return shown.ToString();
    }

    /// <summary>A line's prefix and what follows it; "" for a line with none, which is dotnet's own.</summary>
    public static (string Kind, string Said) Split(string line)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon is > 0 and < PrefixWidth && line[..colon] is "data" or "error" or "warn" or "info" or "verbose")
        {
            string kind = line[..colon];
            string said = line.Length >= PrefixWidth && string.IsNullOrWhiteSpace(line[(colon + 1)..PrefixWidth]) ? line[PrefixWidth..] : line[(colon + 1)..].TrimStart();
            return (kind, said);
        }
        return (string.Empty, line);
    }
}
