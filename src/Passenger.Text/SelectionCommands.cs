namespace Passenger.Text;

/// <summary>Commands that change only the carets: occurrence selection and caret cloning. None makes more carets than
/// <see cref="SelectionSet.MaxCount"/>; one that would have says so.</summary>
public static class SelectionCommands
{
    /// <summary>Select Next Occurrence (Alt+Shift+.): first the word under each empty caret, then the
    /// next copy of the primary selection after the last caret, wrapping, as a new primary.</summary>
    public static SelectionSet SelectNextOccurrence(TextSnapshot text, SelectionSet selections)
    {
        if (selections.Items.Any(s => s.IsEmpty))
        {
            return selections.With(s =>
            {
                if (!s.IsEmpty)
                {
                    return s;
                }
                TextRange word = TextNavigation.WordAt(text, s.Caret);
                return new Selection(word.Start, word.End);
            });
        }
        string needle = text.GetText(selections.Primary.Start, selections.Primary.Length);
        int from = selections.Items[^1].End;
        int? found = Find(text, needle, from, skip: selections);
        return found is { } at ? selections.Add(new Selection(at, at + needle.Length)) : selections;
    }

    /// <summary>Select All Occurrences (Alt+Shift+;): a selection on every copy of the primary selection, or of the word
    /// under the caret, the first <see cref="SelectionSet.MaxCount"/> of them (<paramref name="limited"/> past that),
    /// the one the caret was on always among them and copies overlapping it left out.</summary>
    public static SelectionSet SelectAllOccurrences(TextSnapshot text, SelectionSet selections, out bool limited)
    {
        limited = false;
        Selection primary = selections.Primary;
        TextRange range = primary.IsEmpty ? TextNavigation.WordAt(text, primary.Caret) : primary.Range;
        if (range.Length == 0)
        {
            return selections;
        }
        string needle = text.GetText(range);
        List<Selection> all = [];
        int primaryIndex = -1;
        foreach (int at in OccurrencesAround(text, needle, range))
        {
            if (all.Count == SelectionSet.MaxCount)
            {
                limited = true;
                break;
            }
            if (at == range.Start)
            {
                primaryIndex = all.Count;
            }
            all.Add(new Selection(at, at + needle.Length));
        }
        if (primaryIndex < 0 && limited)
        {
            all[^1] = new Selection(range.Start, range.End);
            primaryIndex = all.Count - 1;
        }
        return SelectionSet.Create(all, Math.Max(0, primaryIndex));
    }

    /// <summary>The copies of <paramref name="needle"/> apart from each other, in order: those wholly before
    /// <paramref name="range"/>, its own start, then those after it.</summary>
    private static IEnumerable<int> OccurrencesAround(TextSnapshot text, string needle, TextRange range)
    {
        foreach (int at in text.Occurrences(needle))
        {
            if (at + needle.Length > range.Start)
            {
                break;
            }
            yield return at;
        }
        yield return range.Start;
        foreach (int at in text.Occurrences(needle, range.End))
        {
            yield return at;
        }
    }

    /// <summary>Unselect Occurrence (Alt+Shift+,): drops the primary caret, the last added, if there
    /// are others.</summary>
    public static SelectionSet UnselectLast(SelectionSet selections)
    {
        if (!selections.HasMultiple)
        {
            return selections;
        }
        List<Selection> rest = selections.Items.Where((_, i) => i != selections.PrimaryIndex).ToList();
        return SelectionSet.Create(rest, rest.Count - 1);
    }

    /// <summary>A caret at the end of every line each selection covers (Alt+Shift+G), on the first
    /// <see cref="SelectionSet.MaxCount"/> lines (<paramref name="limited"/> past them).</summary>
    public static SelectionSet CaretPerLine(TextSnapshot text, SelectionSet selections, out bool limited)
    {
        limited = false;
        List<Selection> carets = [];
        foreach (int line in TextEditing.TouchedLines(text, selections))
        {
            if (carets.Count == SelectionSet.MaxCount)
            {
                limited = true;
                break;
            }
            carets.Add(Selection.At(text.GetLine(line).End));
        }
        return carets.Count == 0 ? selections : SelectionSet.Create(carets, carets.Count - 1);
    }

    /// <summary>The first copy of the needle at or after <paramref name="from"/> that no selection starts at, else the first
    /// before it, wrapping.</summary>
    private static int? Find(TextSnapshot text, string needle, int from, SelectionSet skip)
    {
        foreach (int at in text.Occurrences(needle, from, overlapping: true))
        {
            if (!StartsSelection(skip, at))
            {
                return at;
            }
        }
        foreach (int at in text.Occurrences(needle, 0, overlapping: true))
        {
            if (at >= from)
            {
                break;
            }
            if (!StartsSelection(skip, at))
            {
                return at;
            }
        }
        return null;
    }

    private static bool StartsSelection(SelectionSet selections, int offset)
    {
        int low = 0;
        int high = selections.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (selections.Items[middle].Start < offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low < selections.Count && selections.Items[low].Start == offset;
    }
}
