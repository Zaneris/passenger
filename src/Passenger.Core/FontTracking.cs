using System.Buffers.Binary;

namespace Passenger.Core;

/// <summary>A font's own tracking, its 'trak' table, which macOS's text drawing applies to every run in the font: how much
/// further apart its letters are set at a size, for its normal track, a straight line between the sizes the table names and
/// the nearest's past them; and the tracking a shaper given no size sets by the same table. Pure.</summary>
public static class FontTracking
{
    /// <summary>The tracking at a size, in the size's own units, added after each letter; null where the table has no normal
    /// track for horizontal text or cannot be read.</summary>
    public static double? Of(ReadOnlySpan<byte> trak, int unitsPerEm, double size) => Track(trak, unitsPerEm, size, alone: false);

    /// <summary>A track's tracking at a size: the normal track's, else, where <paramref name="alone"/>, the one track the
    /// table names; null for neither or a table that cannot be read.</summary>
    private static double? Track(ReadOnlySpan<byte> trak, int unitsPerEm, double size, bool alone)
    {
        if (unitsPerEm <= 0 || size <= 0 || trak.Length < 12)
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
        for (int i = 0; i < tracks; i++)
        {
            int entry = data + 8 + (8 * i);
            if (BinaryPrimitives.ReadInt32BigEndian(trak[entry..]) != 0 && !(alone && tracks == 1))
            {
                continue;
            }
            int values = BinaryPrimitives.ReadUInt16BigEndian(trak[(entry + 6)..]);
            if (values + (2L * sizes) > trak.Length)
            {
                return null;
            }
            return Interpolated(size, trak.Slice((int)sizeTable, 4 * sizes), trak.Slice(values, 2 * sizes)) / unitsPerEm * size;
        }
        return null;
    }

    /// <summary>The tracking a shaper sets at a size that tracks every size as the table gives its own,
    /// <paramref name="shaperSize"/>, by the normal track, else by the one track a table names alone: 0 where neither.</summary>
    public static double Shaped(ReadOnlySpan<byte> trak, int unitsPerEm, double size, double shaperSize) =>
        (Track(trak, unitsPerEm, shaperSize, alone: true) ?? 0) / shaperSize * size;

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
}
