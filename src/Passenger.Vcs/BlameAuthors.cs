namespace Passenger.Vcs;

/// <summary>Who changed a stretch of a file: the author of most of its lines, how many others changed any, and the newest
/// commit among them. <see cref="Latest"/> is a line's own when none was committed, which <see cref="IsCommitted"/> says.</summary>
public sealed record StretchAuthors(string Top, int Others, BlameLine Latest, bool IsCommitted)
{
    /// <summary>The top author, with a plus and how many others where there are some ("Jane Doe +1").</summary>
    public string Label => Others > 0 ? $"{Top} +{Others}" : Top;
}

/// <summary>The authors of a stretch of a file from Git blame.</summary>
public static class BlameAuthors
{
    /// <summary>What a stretch none of whose lines is committed says.</summary>
    public const string NotCommitted = "Not committed yet";

    /// <summary>The authors of the lines from <paramref name="first"/> to <paramref name="last"/> (from zero, both included, cut
    /// to the blame's lines); none for a stretch the blame does not reach. A line not committed yet counts for no author;
    /// of the commits' authors the top is the one with most lines, the earlier one of those with as many.</summary>
    public static StretchAuthors? Of(IReadOnlyList<BlameLine> blame, int first, int last)
    {
        first = Math.Max(0, first);
        last = Math.Min(last, blame.Count - 1);
        if (first > last)
        {
            return null;
        }
        Dictionary<string, int> lines = [];
        List<string> order = [];
        BlameLine? latest = null;
        for (int i = first; i <= last; i++)
        {
            BlameLine line = blame[i];
            if (!line.IsCommitted)
            {
                continue;
            }
            if (!lines.TryGetValue(line.Author, out int had))
            {
                order.Add(line.Author);
            }
            lines[line.Author] = had + 1;
            if (latest is null || line.Time > latest.Time)
            {
                latest = line;
            }
        }
        if (latest is null)
        {
            return new StretchAuthors(NotCommitted, 0, blame[first], IsCommitted: false);
        }
        string top = order[0];
        foreach (string author in order)
        {
            if (lines[author] > lines[top])
            {
                top = author;
            }
        }
        return new StretchAuthors(top, order.Count - 1, latest, IsCommitted: true);
    }
}
