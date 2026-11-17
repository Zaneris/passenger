using System.Buffers.Binary;

namespace Passenger.Core;

/// <summary>A font's own tracking, its 'trak' table: how much further apart its letters are set at a size, for a track, a
/// straight line between the sizes the table names and the nearest's past them. macOS's text drawing sets a face it made by
/// name at its normal track (<see cref="ByName"/>); a shaper given no size sets every size at the tracking the table gives its
/// own size, between the tracks around the normal one (<see cref="ByShaper"/>). Either tracks only a face that has a 'STAT'
/// table too. Pure.</summary>
public static class FontTracking
{
    /// <summary>The normal track's tracking at a size, in the size's own units, added after each letter; null where the table
    /// has no normal track for horizontal text or cannot be read.</summary>
    public static double? Of(ReadOnlySpan<byte> trak, int unitsPerEm, double size)
    {
        if (unitsPerEm <= 0 || size <= 0 || Tracks(trak) is not { } read)
        {
            return null;
        }
        for (int i = 0; i < read.Count; i++)
        {
            if (read.TrackOf(trak, i) == 0)
            {
                return read.ValueOf(trak, i, size) is { } value ? value / unitsPerEm * size : null;
            }
        }
        return null;
    }

    /// <summary>The tracking macOS's text drawing sets after each letter at a size, in the size's own units, of a face it made
    /// by name: the normal track's for a face whose <paramref name="styled"/> says it has a 'STAT' table, else none.</summary>
    public static double ByName(ReadOnlySpan<byte> trak, bool styled, int unitsPerEm, double size) =>
        styled ? Of(trak, unitsPerEm, size) ?? 0 : 0;

    /// <summary>The tracking a shaper sets after each letter at every size, in whole units of the em, as it rounds them, of a
    /// face whose <paramref name="styled"/> says it has a 'STAT' table: the table's at <paramref name="shaperSize"/>, by the one
    /// track it names alone, else the normal track, else a straight line between the tracks either side of it, else the
    /// nearest track. 0 for a face without, which it never tracks, or a table that cannot be read.</summary>
    public static int ByShaper(ReadOnlySpan<byte> trak, bool styled, double shaperSize)
    {
        if (!styled || shaperSize <= 0 || Tracks(trak) is not { Count: > 0 } read)
        {
            return 0;
        }
        int below = 0;
        int above = read.Count - 1;
        while (below + 1 < read.Count && read.TrackOf(trak, below + 1) <= 0)
        {
            below++;
        }
        while (above > 0 && read.TrackOf(trak, above - 1) >= 0)
        {
            above--;
        }
        if (read.ValueOf(trak, below, shaperSize) is not { } low || read.ValueOf(trak, above, shaperSize) is not { } high)
        {
            return 0;
        }
        double from = read.TrackOf(trak, below);
        double to = read.TrackOf(trak, above);
        double units = below == above || to == from ? low : low + ((high - low) * (0 - from) / (to - from));
        return (int)Math.Round(units, MidpointRounding.AwayFromZero);
    }

    /// <summary>The horizontal track data of a table that can be read, else null.</summary>
    private static TrackData? Tracks(ReadOnlySpan<byte> trak)
    {
        if (trak.Length < 12)
        {
            return null;
        }
        int data = BinaryPrimitives.ReadUInt16BigEndian(trak[6..]);
        if (data == 0 || data + 8 > trak.Length)
        {
            return null;
        }
        int tracks = BinaryPrimitives.ReadUInt16BigEndian(trak[data..]);
        int sizes = BinaryPrimitives.ReadUInt16BigEndian(trak[(data + 2)..]);
        long sizeTable = BinaryPrimitives.ReadUInt32BigEndian(trak[(data + 4)..]);
        if (sizes == 0 || sizeTable + (4L * sizes) > trak.Length || data + 8 + (8L * tracks) > trak.Length)
        {
            return null;
        }
        return new TrackData(data + 8, tracks, (int)sizeTable, sizes);
    }

    /// <summary>A track's value at a size, from the table's sizes (16.16 fixed) and the track's values for them (font units):
    /// between the two sizes around it by a straight line, past the ends as at the nearest.</summary>
    private static double Interpolated(double size, ReadOnlySpan<byte> sizes, ReadOnlySpan<byte> values)
    {
        int count = sizes.Length / 4;
        double SizeAt(ReadOnlySpan<byte> table, int j) => BinaryPrimitives.ReadInt32BigEndian(table[(4 * j)..]) / 65536.0;
        double ValueAt(ReadOnlySpan<byte> table, int j) => BinaryPrimitives.ReadInt16BigEndian(table[(2 * j)..]);
        if (size <= SizeAt(sizes, 0))
        {
            return ValueAt(values, 0);
        }
        for (int j = 1; j < count; j++)
        {
            double above = SizeAt(sizes, j);
            if (size <= above)
            {
                double below = SizeAt(sizes, j - 1);
                return above <= below ? ValueAt(values, j)
                    : ValueAt(values, j - 1) + ((ValueAt(values, j) - ValueAt(values, j - 1)) * (size - below) / (above - below));
            }
        }
        return ValueAt(values, count - 1);
    }

    /// <summary>Where a table's track entries and sizes stand, and how many of each.</summary>
    private readonly record struct TrackData(int Entries, int Count, int SizeTable, int Sizes)
    {
        /// <summary>A track's value, 0 for the normal one.</summary>
        public double TrackOf(ReadOnlySpan<byte> trak, int i) => BinaryPrimitives.ReadInt32BigEndian(trak[(Entries + (8 * i))..]) / 65536.0;

        /// <summary>A track's value at a size in units of the em; null where its values lie outside the table.</summary>
        public double? ValueOf(ReadOnlySpan<byte> trak, int i, double size)
        {
            int values = BinaryPrimitives.ReadUInt16BigEndian(trak[(Entries + (8 * i) + 6)..]);
            return values + (2L * Sizes) > trak.Length ? null : Interpolated(size, trak.Slice(SizeTable, 4 * Sizes), trak.Slice(values, 2 * Sizes));
        }
    }
}
