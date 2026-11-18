namespace Passenger.App.Editors;

/// <summary>The order things were last used in, the latest first: one that comes forward goes first, one that goes leaves
/// it, and listed against what is open now, those never used follow the used ones in the order given. Of one thread.</summary>
public sealed class UsedOrder<T>
    where T : class
{
    private readonly List<T> used = [];

    /// <summary>One came forward: the latest used now.</summary>
    public void Came(T item)
    {
        used.Remove(item);
        used.Insert(0, item);
    }

    /// <summary>One went for good.</summary>
    public void Went(T item) => used.Remove(item);

    /// <summary>Lets go of each one no longer open.</summary>
    public void Keep(Func<T, bool> open) => used.RemoveAll(item => !open(item));

    /// <summary>What is open, the latest used first, then those never used in the order given.</summary>
    public IReadOnlyList<T> Of(IReadOnlyList<T> open)
    {
        HashSet<T> listed = [.. open];
        List<T> ordered = [.. used.Where(listed.Contains)];
        HashSet<T> placed = [.. ordered];
        ordered.AddRange(open.Where(placed.Add));
        return ordered;
    }
}
