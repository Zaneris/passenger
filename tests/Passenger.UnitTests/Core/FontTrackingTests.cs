using System.Buffers.Binary;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A font's 'trak' table read as macOS's text drawing applies it: the normal track's value at a size, by a straight
/// line between the sizes the table names and as the nearest past them, in the size's own units.</summary>
public class FontTrackingTests
{
    /// <summary>San Francisco's own sizes and values at 12 to 14 points, its tight track first, 2048 units to the em.</summary>
    private static readonly byte[] Table = Trak([12, 13, 14], (-1, [-28, -40, -50]), (0, [0, -12, -22]));

    [Fact]
    public void The_normal_tracks_value_at_a_size_it_names_is_that_value_in_the_sizes_units()
    {
        Assert.Equal(-12.0 / 2048 * 13, FontTracking.Of(Table, 2048, 13)!.Value, 9);
        Assert.Equal(0, FontTracking.Of(Table, 2048, 12)!.Value, 9);
    }

    [Fact]
    public void Between_two_sizes_it_is_a_straight_line_and_past_the_ends_the_nearest_sizes_value()
    {
        Assert.Equal(-17.0 / 2048 * 13.5, FontTracking.Of(Table, 2048, 13.5)!.Value, 9);
        Assert.Equal(0, FontTracking.Of(Table, 2048, 9)!.Value, 9);
        Assert.Equal(-22.0 / 2048 * 20, FontTracking.Of(Table, 2048, 20)!.Value, 9);
    }

    [Fact]
    public void A_table_with_no_normal_track_or_cut_short_has_no_tracking()
    {
        Assert.Null(FontTracking.Of(Trak([12, 13], (-1, [-28, -40])), 2048, 13));
        Assert.Null(FontTracking.Of(Table.AsSpan(0, 20), 2048, 13));
        Assert.Null(FontTracking.Of([], 2048, 13));
    }

    [Fact]
    public void A_shaper_tracks_every_size_by_the_normal_track_at_its_own_size_else_by_a_lone_track_else_not_at_all()
    {
        Assert.Equal(-12.0 / 2048 * 20, FontTracking.Shaped(Table, 2048, 20, 13), 9);
        byte[] loose = Trak([12, 13, 14], (1, [21, 20, 19]));
        Assert.Null(FontTracking.Of(loose, 1000, 14));
        Assert.Equal(21.0 / 1000 * 14, FontTracking.Shaped(loose, 1000, 14, 12), 9);
        Assert.Equal(0, FontTracking.Shaped(Trak([12, 13], (-1, [-28, -40]), (1, [28, 40])), 2048, 13, 12), 9);
    }

    /// <summary>A 'trak' table of horizontal tracks alone: its header, its track data, each track's entry, its sizes and values.</summary>
    private static byte[] Trak(double[] sizes, params (int Track, short[] Values)[] tracks)
    {
        const int Data = 12;
        int entries = Data + 8;
        int sizeTable = entries + (8 * tracks.Length);
        int values = sizeTable + (4 * sizes.Length);
        byte[] table = new byte[values + (2 * sizes.Length * tracks.Length)];
        BinaryPrimitives.WriteInt32BigEndian(table, 0x10000);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6), Data);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(Data), (ushort)tracks.Length);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(Data + 2), (ushort)sizes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(Data + 4), (uint)sizeTable);
        for (int i = 0; i < tracks.Length; i++)
        {
            int at = values + (2 * sizes.Length * i);
            BinaryPrimitives.WriteInt32BigEndian(table.AsSpan(entries + (8 * i)), tracks[i].Track * 65536);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(entries + (8 * i) + 6), (ushort)at);
            for (int j = 0; j < sizes.Length; j++)
            {
                BinaryPrimitives.WriteInt16BigEndian(table.AsSpan(at + (2 * j)), tracks[i].Values[j]);
            }
        }
        for (int j = 0; j < sizes.Length; j++)
        {
            BinaryPrimitives.WriteInt32BigEndian(table.AsSpan(sizeTable + (4 * j)), (int)(sizes[j] * 65536));
        }
        return table;
    }
}
