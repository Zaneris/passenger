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
        if (!Continues(heard))
        {
            count = 0;
        }
        count += files;
        last = heard > last ? heard : last;
        return count > AtOnce;
    }

    private bool Continues(DateTime heard) => heard < last + Quiet;
}
