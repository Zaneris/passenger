namespace Passenger.App.Explorer;

/// <summary>The files the Explorer's filter shows, chosen from the folder's indexed files by name alone, off the window's
/// thread: those whose names begin with its text first, then those holding it further on, each kind in the index's order,
/// at most <see cref="Most"/> of them, with how many matched in all.</summary>
public static class ExplorerFilter
{
    /// <summary>The most files the tree is narrowed to: so many, each in a folder of its own, are placed and drawn in little
    /// more time than a few, where a one-letter filter over a large folder would open nearly every folder in it.</summary>
    public const int Most = 500;

    /// <summary>The files chosen, by the index's paths, and how many files' names hold the text in all.</summary>
    public sealed record Matches(IReadOnlyList<string> Files, int Total)
    {
        /// <summary>How many matched beyond those chosen.</summary>
        public int More => Total - Files.Count;
    }

    /// <summary>The files among <paramref name="files"/> (paths from the folder) whose names hold <paramref name="text"/>,
    /// ignoring case, at most <paramref name="most"/>, those beginning with it first.</summary>
    public static Matches Of(IReadOnlyList<string> files, string text, int most = Most)
    {
        List<string> starting = [];
        List<string> holding = [];
        int total = 0;
        foreach (string file in files)
        {
            int at = Path.GetFileName(file.AsSpan()).IndexOf(text, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                continue;
            }
            total++;
            List<string> kind = at == 0 ? starting : holding;
            if (kind.Count < most)
            {
                kind.Add(file);
            }
        }
        if (starting.Count < most)
        {
            starting.AddRange(holding.Take(most - starting.Count));
        }
        return new Matches(starting, total);
    }
}
