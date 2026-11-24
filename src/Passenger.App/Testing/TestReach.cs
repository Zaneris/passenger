using Passenger.Text;

namespace Passenger.App.Testing;

/// <summary>Which tests run which lines, kept in the folder's state so it lasts from one session to the next: each test
/// method (its rows together) by its identity, with the lines of each file it ran and how its run ended, and each file it
/// ran by its path, with a hash of each of its lines as they were when mapped, so a file changed since is compared line by
/// line to find what the change reaches (<see cref="Changed"/>). Bounded: the tests mapped latest, and the files they ran.</summary>
public sealed partial record TestReachState
{
    /// <summary>The most tests kept, those mapped longest ago going first.</summary>
    public const int MostTests = 20_000;

    public Dictionary<string, ReachedTest> Tests { get; init; } = [];

    public Dictionary<string, MappedFile> Files { get; init; } = [];

    /// <summary>Within the bounds: each test of an outcome this build has, the files only those tests run.</summary>
    public TestReachState Clamped()
    {
        Dictionary<string, ReachedTest> tests = new(StringComparer.Ordinal);
        foreach ((string identity, ReachedTest? test) in (Tests ?? []).Where(t => t.Value is not null).OrderByDescending(t => t.Value.Mapped).Take(MostTests))
        {
            if (Enum.IsDefined(test.State) && test.Lines is not null)
            {
                tests[identity] = test with { Lines = test.Lines.Where(l => l.Value is { Count: > 0 } s && s.Count % 2 == 0).ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal) };
            }
        }
        HashSet<string> ran = [.. tests.Values.SelectMany(t => t.Lines.Keys)];
        Dictionary<string, MappedFile> files = new(StringComparer.Ordinal);
        foreach ((string path, MappedFile? file) in Files ?? [])
        {
            if (file?.Hashes is not null && ran.Contains(path))
            {
                files[path] = file;
            }
        }
        return new TestReachState { Tests = tests, Files = files };
    }

    /// <summary>The tests mapped now, each in place of what was kept of it, and the files they ran as they read now
    /// (<paramref name="hashes"/> by path); every other test's lines in those files carried to where they stand now
    /// (<see cref="Changed"/> has found them unchanged), within the bounds.</summary>
    public TestReachState With(IReadOnlyDictionary<string, ReachedTest> mapped, IReadOnlyDictionary<string, uint[]> hashes, IReadOnlyDictionary<string, IReadOnlyList<int>>? coverable = null)
    {
        Dictionary<string, MappedFile> files = new(Files, StringComparer.Ordinal);
        Dictionary<string, ReachedTest> tests = new(Tests, StringComparer.Ordinal);
        foreach ((string path, uint[] now) in hashes)
        {
            if (files.TryGetValue(path, out MappedFile? was) && !was.Hashes.AsSpan().SequenceEqual(now))
            {
                int[] moved = Moved(was.Hashes, now);
                foreach ((string identity, ReachedTest test) in tests.ToArray())
                {
                    if (!mapped.ContainsKey(identity) && test.Lines.TryGetValue(path, out List<int>? stretches))
                    {
                        tests[identity] = test with { Lines = new(test.Lines, StringComparer.Ordinal) { [path] = Stretches.Of(Stretches.Lines(stretches).Select(l => l <= moved.Length ? moved[l - 1] : 0).Where(l => l > 0)) } };
                    }
                }
            }
            files[path] = new MappedFile { Hashes = now, Coverable = coverable?.GetValueOrDefault(path) is { } lines ? Stretches.Of(lines) : files.GetValueOrDefault(path)?.Coverable ?? [] };
        }
        foreach ((string identity, ReachedTest test) in mapped)
        {
            tests[identity] = test;
        }
        return (this with { Tests = tests, Files = files }).Clamped();
    }

    /// <summary>The tests a file's change reaches, as it reads now (<paramref name="now"/>, a hash of each line): those that
    /// ran a line changed or taken out since it was mapped, or the line before or after one put in; none for a file no test
    /// ran, or unchanged.</summary>
    public IReadOnlyList<string> Changed(string path, uint[] now)
    {
        if (!Files.TryGetValue(path, out MappedFile? was) || was.Hashes.AsSpan().SequenceEqual(now))
        {
            return [];
        }
        HashSet<int> reached = [];
        foreach (DiffHunk hunk in LineDiff.Hunks(Named(was.Hashes), Named(now)))
        {
            // Lines from one: a stretch changed or taken out is its own; one put in reaches what stood around it.
            int first = hunk.OldCount == 0 ? hunk.OldStart : hunk.OldStart + 1;
            int last = hunk.OldCount == 0 ? hunk.OldStart + 1 : hunk.OldStart + hunk.OldCount;
            for (int line = first; line <= last; line++)
            {
                reached.Add(line);
            }
        }
        return [.. Tests.Where(t => t.Value.Lines.TryGetValue(path, out List<int>? stretches) && Stretches.Lines(stretches).Any(reached.Contains)).Select(t => t.Key)];
    }

    /// <summary>The tests that ran a line of a file (from one) as it was mapped.</summary>
    public IEnumerable<string> Running(string path, int line) =>
        Tests.Where(t => t.Value.Lines.TryGetValue(path, out List<int>? stretches) && Stretches.Holds(stretches, line)).Select(t => t.Key);

    /// <summary>Where each line of a text as mapped (from one) stands in it now, zero for a line changed or taken out.</summary>
    private static int[] Moved(uint[] was, uint[] now)
    {
        int[] moved = new int[was.Length];
        int from = 0;
        int to = 0;
        foreach (DiffHunk hunk in LineDiff.Hunks(Named(was), Named(now)))
        {
            for (; from < hunk.OldStart; from++, to++)
            {
                moved[from] = to + 1;
            }
            from += hunk.OldCount;
            to += hunk.NewCount;
        }
        for (; from < was.Length; from++, to++)
        {
            moved[from] = to + 1;
        }
        return moved;
    }

    private static string[] Named(uint[] hashes) => [.. hashes.Select(h => h.ToString("x8", System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>A hash of each line of a text, the same in every process (FNV-1a over its characters), its break left out.</summary>
    public static uint[] LinesOf(string text)
    {
        string[] lines = LineDiff.Lines(text);
        uint[] hashes = new uint[lines.Length];
        for (int at = 0; at < lines.Length; at++)
        {
            uint hash = 2166136261;
            foreach (char c in lines[at])
            {
                hash = (hash ^ c) * 16777619;
            }
            hashes[at] = hash;
        }
        return hashes;
    }
}

/// <summary>A test method as its last mapped run left it: when that was, how it ended, and the lines it ran, by file, as
/// stretches (<see cref="Stretches"/>).</summary>
public sealed record ReachedTest
{
    public DateTimeOffset Mapped { get; init; }

    public TestState State { get; init; }

    public Dictionary<string, List<int>> Lines { get; init; } = [];
}

/// <summary>A file a mapped test ran, as it was then: a hash of each line, and its lines any test could run (where its
/// code is), as stretches.</summary>
public sealed record MappedFile
{
    public uint[] Hashes { get; init; } = [];

    public List<int> Coverable { get; init; } = [];
}

/// <summary>Lines (from one) as stretches, each its first line and how many follow on from it: what a test's lines keep
/// as, its runs of lines in a method being long.</summary>
public static class Stretches
{
    /// <summary>Lines, in any order, as stretches in order.</summary>
    public static List<int> Of(IEnumerable<int> lines)
    {
        List<int> stretches = [];
        foreach (int line in lines.Distinct().Order())
        {
            if (stretches.Count > 0 && stretches[^2] + stretches[^1] == line)
            {
                stretches[^1]++;
            }
            else
            {
                stretches.Add(line);
                stretches.Add(1);
            }
        }
        return stretches;
    }

    public static IEnumerable<int> Lines(IReadOnlyList<int> stretches)
    {
        for (int at = 0; at + 1 < stretches.Count; at += 2)
        {
            for (int line = stretches[at]; line < stretches[at] + stretches[at + 1]; line++)
            {
                yield return line;
            }
        }
    }

    /// <summary>Whether a line is in the stretches.</summary>
    public static bool Holds(IReadOnlyList<int> stretches, int line)
    {
        for (int at = 0; at + 1 < stretches.Count; at += 2)
        {
            if (line >= stretches[at] && line < stretches[at] + stretches[at + 1])
            {
                return true;
            }
        }
        return false;
    }
}
