namespace Passenger.Text;

/// <summary>Items each on a stretch of one document's text, carried through its changes as marks are
/// (<see cref="TextDocument.MarkAt"/>), so their holder never maps them itself. <see cref="Items"/> reads them back on
/// their ranges once a change, when first asked, and is the same list while none moved. Setting others lets the last
/// ones' ranges go, as <see cref="Clear"/> does when their holder ends. Read on the thread that changes the document.</summary>
public sealed class AnchoredList<T>(Func<T, TextRange> rangeOf, Func<T, TextRange, T> placed)
{
    private TextDocument? document;
    private AnchoredRange[] ranges = [];
    private IReadOnlyList<T> items = [];

    /// <summary>The document's version the items were last read at.</summary>
    private int version;

    public int Count => ranges.Length;

    /// <summary>How many times items were set, dropped or let go: while it stands, <see cref="Items"/> are the same items,
    /// at most moved.</summary>
    public int Given { get; private set; }

    /// <summary>The items, each on its range in the text as it stands.</summary>
    public IReadOnlyList<T> Items
    {
        get
        {
            if (document is not null && document.Version != version)
            {
                version = document.Version;
                items = Read();
            }
            return items;
        }
    }

    /// <summary>Carries items given in the text as it stands, in place of the last ones; items equal to those carried, as
    /// they stand, leave them carried, <see cref="Given"/> and all.</summary>
    public void Set(TextDocument text, IReadOnlyList<T> given)
    {
        if (Holds(text, given))
        {
            return;
        }
        Clear();
        if (given.Count == 0)
        {
            return;
        }
        document = text;
        version = text.Version;
        ranges = new AnchoredRange[given.Count];
        for (int i = 0; i < ranges.Length; i++)
        {
            ranges[i] = text.MarkAt(rangeOf(given[i]));
        }
        items = given;
    }

    /// <summary>Whether the items carried in a document's text are those given, one for one.</summary>
    private bool Holds(TextDocument text, IReadOnlyList<T> given)
    {
        if (given.Count != ranges.Length || (given.Count > 0 && document != text))
        {
            return false;
        }
        IReadOnlyList<T> now = Items;
        for (int i = 0; i < now.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(now[i], given[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Lets the items <paramref name="drops"/> names go, with their ranges, the rest carried on; false when it named
    /// none.</summary>
    public bool Drop(Func<T, bool> drops)
    {
        IReadOnlyList<T> now = Items;
        int first = 0;
        while (first < now.Count && !drops(now[first]))
        {
            first++;
        }
        if (first == now.Count)
        {
            return false;
        }
        List<AnchoredRange> keptRanges = [.. ranges.Take(first)];
        List<T> keptItems = [.. now.Take(first)];
        for (int i = first; i < ranges.Length; i++)
        {
            if (i == first || drops(now[i]))
            {
                ranges[i].Remove();
            }
            else
            {
                keptRanges.Add(ranges[i]);
                keptItems.Add(now[i]);
            }
        }
        ranges = [.. keptRanges];
        items = keptItems;
        Given++;
        if (ranges.Length == 0)
        {
            document = null;
        }
        return true;
    }

    /// <summary>Lets every range go: none is left.</summary>
    public void Clear()
    {
        Given++;
        foreach (AnchoredRange range in ranges)
        {
            range.Remove();
        }
        ranges = [];
        items = [];
        document = null;
    }

    private IReadOnlyList<T> Read()
    {
        T[]? moved = null;
        for (int i = 0; i < ranges.Length; i++)
        {
            TextRange now = ranges[i].Range;
            if (now != rangeOf(items[i]))
            {
                moved ??= [.. items];
                moved[i] = placed(items[i], now);
            }
        }
        return moved ?? items;
    }
}
