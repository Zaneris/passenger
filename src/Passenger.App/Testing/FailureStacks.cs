namespace Passenger.App.Testing;

/// <summary>A failure's stack as its runner tells it, each stack in it shown once. NUnit's adapter writes the test's stack,
/// then each assertion's after its number ("1) ", "2) "): for one failure the same stack again, for several the stacks its
/// message already gives with each failure. A numbered stack said already, there or in the message, is left out; every
/// other line stays as the runner wrote it.</summary>
public static class FailureStacks
{
    public static string? Once(string? stack, string? message)
    {
        if (string.IsNullOrEmpty(stack))
        {
            return stack;
        }
        List<int> starts = [];
        int line = 0;
        while (line < stack.Length)
        {
            if (stack.AsSpan(line).StartsWith($"{starts.Count + 1}) ", StringComparison.Ordinal))
            {
                starts.Add(line);
            }
            int end = stack.IndexOf('\n', line);
            line = end < 0 ? stack.Length : end + 1;
        }
        if (starts.Count == 0)
        {
            return stack;
        }
        string top = stack[..starts[0]];
        string[] said = [.. Lines(top), .. Lines(message ?? string.Empty)];
        List<string> kept = [];
        for (int at = 0; at < starts.Count; at++)
        {
            int start = starts[at];
            string numbered = stack[start..(at + 1 < starts.Count ? starts[at + 1] : stack.Length)];
            string[] lines = Lines(numbered[$"{at + 1}) ".Length..]);
            if (lines.Length > 0 && !Within(said, lines))
            {
                kept.Add(numbered);
            }
        }
        return kept.Count == starts.Count ? stack : string.Concat(kept.Prepend(top)).TrimEnd();
    }

    /// <summary>A text's lines that say anything, each without the whitespace around it.</summary>
    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whether some lines come one after another, all of them, in others.</summary>
    private static bool Within(string[] lines, string[] run)
    {
        for (int from = 0; from + run.Length <= lines.Length; from++)
        {
            if (lines.AsSpan(from, run.Length).SequenceEqual(run))
            {
                return true;
            }
        }
        return false;
    }
}
