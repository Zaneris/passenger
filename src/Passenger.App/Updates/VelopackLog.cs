namespace Passenger.App.Updates;

/// <summary>Velopack's log as its writers write it, a line each (<c>[update:15696] [16:31:55] [ERROR] Apply error: …</c>):
/// what Update.exe said as it failed to apply a version, which only its log keeps.</summary>
public static class VelopackLog
{
    /// <summary>Why the last apply of an app's version failed, in Update.exe's words: the error its process logged after the
    /// last line starting it on that version's package (<c>Running: …Update.exe … apply --package …\App-1.2.3-…</c>), with
    /// Velopack's own "Error applying package: " left off; null where that apply logged no error, or none started.</summary>
    public static string? WhyNotApplied(IReadOnlyList<string> lines, string appId, string version)
    {
        string package = $"{appId}-{version}-";
        int started = -1;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (Text(lines[i]) is { } text && text.StartsWith("Running: ", StringComparison.Ordinal)
                && text.Contains(" apply ", StringComparison.Ordinal) && text.Contains(package, StringComparison.OrdinalIgnoreCase))
            {
                started = i;
                break;
            }
        }
        if (started < 0)
        {
            return null;
        }
        // Its process, as the line after names it; else any Update.exe's line.
        string? update = null;
        for (int i = started + 1; i < lines.Count; i++)
        {
            if (Text(lines[i]) is { } text && text.StartsWith("Process started with ID: ", StringComparison.Ordinal))
            {
                update = $"[update:{text["Process started with ID: ".Length..].Trim()}]";
                break;
            }
        }
        for (int i = started + 1; i < lines.Count; i++)
        {
            string line = lines[i];
            if (!line.StartsWith(update ?? "[update:", StringComparison.Ordinal) || Level(line) != "ERROR")
            {
                continue;
            }
            string error = Text(line)!;
            foreach (string said in (string[])["Apply error: ", "Error applying package: "])
            {
                if (error.StartsWith(said, StringComparison.Ordinal))
                {
                    error = error[said.Length..];
                }
            }
            return error.Length == 0 ? null : error;
        }
        return null;
    }

    /// <summary>A line's level, the third bracket: INFO, WARN, ERROR, Debug.</summary>
    private static string? Level(string line) => Brackets(line) is { } at && line.IndexOf(']', at.Level) is int end and > 0
        ? line[(at.Level + 1)..end].ToUpperInvariant() : null;

    /// <summary>What a line says after its writer, time and level.</summary>
    private static string? Text(string line) => Brackets(line) is { } at && line.IndexOf("] ", at.Level, StringComparison.Ordinal) is int end and > 0
        ? line[(end + 2)..] : null;

    /// <summary>Where a line's third bracket opens, past its writer's and its time's.</summary>
    private static (int Writer, int Level)? Brackets(string line)
    {
        if (!line.StartsWith('['))
        {
            return null;
        }
        int time = line.IndexOf("] [", StringComparison.Ordinal);
        int level = time < 0 ? -1 : line.IndexOf("] [", time + 3, StringComparison.Ordinal);
        return level < 0 ? null : (time, level + 2);
    }
}
