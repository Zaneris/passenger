namespace Passenger.Roslyn.Extension;

/// <summary>How well each item of a C# completion list fits where it is asked for, as Roslyn's own matching says (its
/// match priority: a symbol of the type the place takes above one of no type it takes, 0 for no preference), said beside
/// the list's items as runs in their order: a priority, then how many items in a row have it. Most items of a list share
/// one, so a list of thousands says it in a few numbers; a list whose items all have 0 says nothing. Compiled into both
/// the extension, which writes it, and Passenger, which reads it.</summary>
public static class CompletionPriorities
{
    /// <summary>The list's member holding the runs.</summary>
    public const string Member = "matchPriorities";

    /// <summary>The runs of <paramref name="count"/> items' priorities, or null where every item's is 0.</summary>
    public static int[]? Runs(int count, Func<int, int> priorityOf)
    {
        List<int> runs = [];
        bool any = false;
        for (int i = 0; i < count;)
        {
            int priority = priorityOf(i);
            int length = 1;
            while (i + length < count && priorityOf(i + length) == priority)
            {
                length++;
            }
            any |= priority != 0;
            runs.Add(priority);
            runs.Add(length);
            i += length;
        }
        return any ? [.. runs] : null;
    }

    /// <summary>Each of <paramref name="count"/> items' priority from its list's runs; null where there are none, or where
    /// they do not cover exactly that many items.</summary>
    public static int[]? Each(int[]? runs, int count)
    {
        if (runs is null || runs.Length % 2 != 0)
        {
            return null;
        }
        int[] each = new int[count];
        int at = 0;
        for (int r = 0; r < runs.Length; r += 2)
        {
            int length = runs[r + 1];
            if (length <= 0 || length > count - at)
            {
                return null;
            }
            Array.Fill(each, runs[r], at, length);
            at += length;
        }
        return at == count ? each : null;
    }
}
