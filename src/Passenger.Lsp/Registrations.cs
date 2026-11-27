namespace Passenger.Lsp;

/// <summary>What a server registered, by each registration's id, and every item of every registration as one list, made
/// again only as it is read after a registration came or went: a load's hundreds of registrations, come one at a time and
/// read once the burst has been taken in, cost what they hold rather than its square. Unlocked: its owner holds its own lock
/// around each call.</summary>
internal sealed class Registrations<T>
{
    private readonly Dictionary<string, IReadOnlyList<T>> registered = new(StringComparer.Ordinal);

    /// <summary>Every item as last made, or null once a registration came or went since.</summary>
    private List<T>? all = [];

    /// <summary>A registration taken, over any of the same id.</summary>
    public void Set(string id, IReadOnlyList<T> items)
    {
        registered[id] = items;
        all = null;
    }

    /// <summary>A registration let go; false where there was none of the id.</summary>
    public bool Remove(string id)
    {
        if (!registered.Remove(id))
        {
            return false;
        }
        all = null;
        return true;
    }

    /// <summary>Every item of every registration, in no order to rely on.</summary>
    public IReadOnlyList<T> All
    {
        get
        {
            if (all is null)
            {
                int count = 0;
                foreach (IReadOnlyList<T> items in registered.Values)
                {
                    count += items.Count;
                }
                all = new List<T>(count);
                foreach (IReadOnlyList<T> items in registered.Values)
                {
                    all.AddRange(items);
                }
            }
            return all;
        }
    }
}
