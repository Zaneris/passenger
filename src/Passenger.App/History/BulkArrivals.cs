namespace Passenger.App.History;

/// <summary>Whether files Local History does not know, made or changed by another program, come as a bulk arrival (a tree
/// unpacked, copied, moved or checked out into the folder): more than <see cref="AtOnce"/> coming within <see cref="Quiet"/>
/// of one another. A bulk arrival's files are never read as they come, only once one is opened, saved, or changed again
/// after; the files of a few coming together are each read and kept. Asked on the history's queue, in the order the files
/// were heard.</summary>
public sealed class BulkArrivals
{
    /// <summary>How many files the history does not know may come together and each still be read.</summary>
    public const int AtOnce = 100;

    /// <summary>How long after files came those still coming are of the same arrival.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    /// <summary>How long before an arrival was first heard its files may have been made.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromSeconds(2);

    /// <summary>When each bulk arrival's files were made, from its first heard less <see cref="Lead"/> to its last heard.</summary>
    private readonly List<(DateTime From, DateTime To)> bulks = [];

    private DateTime began = DateTime.MinValue;
    private DateTime last = DateTime.MinValue;
    private int count;

    /// <summary>How many files heard at a time may come and each still be read: fewer while others came a moment before,
    /// none during a bulk arrival.</summary>
    public int Room(DateTime heard) => Math.Max(AtOnce - (Continues(heard) ? count : 0), 0);

    /// <summary>Takes in <paramref name="files"/> files heard come together at a time; whether they are of a bulk arrival,
    /// and so not read.</summary>
    public bool Came(int files, DateTime heard)
    {
        if (files == 0)
        {
            return false;
        }
        bool underWay = Continues(heard) && count > AtOnce;
        if (!Continues(heard))
        {
            (began, count) = (heard, 0);
        }
        count += files;
        last = heard > last ? heard : last;
        if (count <= AtOnce)
        {
            return false;
        }
        if (underWay)
        {
            bulks[^1] = (bulks[^1].From, last);
        }
        else
        {
            bulks.Add((began - Lead, last));
        }
        return true;
    }

    /// <summary>Whether a bulk arrival brought a file made at a time: passed over then, so not made when next read.</summary>
    public bool Brought(DateTime made) => bulks.Exists(b => made >= b.From && made <= b.To);

    private bool Continues(DateTime heard) => heard < last + Quiet;
}
