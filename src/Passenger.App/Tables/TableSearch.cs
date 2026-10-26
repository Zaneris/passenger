using System.Text.RegularExpressions;
using Passenger.Text;

namespace Passenger.App.Tables;

/// <summary>The cells of a table a query finds, row by row and left to right, each cell's text as the grid shows it
/// (a workbook's numbers formatted); a regex that does not parse says why, and one that runs past its time in a cell stops
/// there and says so.</summary>
public sealed record TableSearch(IReadOnlyList<(int Row, int Column)> Matches, bool Truncated, string? Problem)
{
    public static readonly TableSearch None = new([], false, null);

    /// <summary>Every cell holding a match, at most <see cref="TextSearch.MaxMatches"/>; read off the UI thread (a regex has
    /// <see cref="TextSearch.Apart"/> in each cell), so it gives up as soon as <paramref name="cancel"/> is, a newer query
    /// having replaced it.</summary>
    public static TableSearch Find(Table table, SearchQuery query, CancellationToken cancel)
    {
        if (query.IsEmpty)
        {
            return None;
        }
        SearchPattern pattern;
        try
        {
            pattern = SearchPattern.For(query, TextSearch.Apart);
        }
        catch (ArgumentException error)
        {
            return new TableSearch([], false, error.Message);
        }
        List<(int Row, int Column)> matches = [];
        try
        {
            for (int row = 0; row < table.RowCount && !cancel.IsCancellationRequested; row++)
            {
                for (int column = 0; column < table.ColumnCount; column++)
                {
                    if (!pattern.IsMatch(table.Cell(row, column)))
                    {
                        continue;
                    }
                    if (matches.Count == TextSearch.MaxMatches)
                    {
                        return new TableSearch(matches, true, null);
                    }
                    matches.Add((row, column));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new TableSearch(matches, true, SearchResult.TookTooLong);
        }
        return new TableSearch(matches, false, null);
    }

    /// <summary>The first match at <paramref name="cell"/> or after it (just after, unless <paramref name="including"/>),
    /// wrapping to the first.</summary>
    public int NextFrom((int Row, int Column) cell, bool including)
    {
        for (int i = 0; i < Matches.Count; i++)
        {
            int order = Compare(Matches[i], cell);
            if (order > 0 || (including && order == 0))
            {
                return i;
            }
        }
        return Matches.Count > 0 ? 0 : -1;
    }

    /// <summary>The last match before <paramref name="cell"/>, wrapping to the last.</summary>
    public int PreviousFrom((int Row, int Column) cell)
    {
        for (int i = Matches.Count - 1; i >= 0; i--)
        {
            if (Compare(Matches[i], cell) < 0)
            {
                return i;
            }
        }
        return Matches.Count - 1;
    }

    private static int Compare((int Row, int Column) a, (int Row, int Column) b) =>
        a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Column.CompareTo(b.Column);
}
