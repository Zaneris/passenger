using System.Buffers.Binary;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A font's 'trak' table read as macOS's text drawing applies it, the normal track's value at a size, by a straight
/// line between the sizes the table names and as the nearest past them, in the size's own units; and as a shaper given no
/// size applies it, in whole units of the em; neither for a face with no 'STAT' table.</summary>
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

    /// <summary>PingFang SC's one loose track, 21 units of 1000 at 12 points and 13 at 24, which a shaper told no size sets at
    /// every size and macOS, by its normal track alone, never.</summary>
    [Fact]
    public void A_shaper_tracks_every_size_by_the_normal_track_at_its_own_size_else_by_a_lone_track()
    {
        Assert.Equal(-12, FontTracking.ByShaper(Table, styled: true, 13));
        byte[] pingFang = Trak([10, 11, 12, 13, 14, 15, 16, 17, 20, 22, 28, 32, 36, 50], (1, [22, 22, 21, 20, 20, 20, 20, 19, 19, 13, 13, 9, 8, 4]));
        Assert.Equal(21, FontTracking.ByShaper(pingFang, styled: true, 12));
        Assert.Equal(0, FontTracking.ByName(pingFang, styled: true, 1000, 12), 9);
        Assert.Equal(0, FontTracking.ByName(pingFang, styled: true, 1000, 24), 9);
        Assert.Equal(0, FontTracking.ByShaper([], styled: true, 12));
    }

    [Fact]
    public void With_no_normal_track_a_shaper_tracks_by_a_straight_line_between_the_tracks_either_side_else_by_the_nearest()
    {
        Assert.Equal(0, FontTracking.ByShaper(Trak([12, 13], (-1, [-28, -40]), (1, [28, 40])), styled: true, 12));
        Assert.Equal(-14, FontTracking.ByShaper(Trak([12, 13], (-1, [-28, -40]), (3, [28, 40])), styled: true, 12));
        Assert.Equal(-28, FontTracking.ByShaper(Trak([12, 13], (-2, [-56, -60]), (-1, [-28, -40])), styled: true, 12));
        Assert.Equal(28, FontTracking.ByShaper(Trak([12, 13], (1, [28, 40]), (2, [56, 60])), styled: true, 12));
    }

    [Fact]
    public void A_shaper_sets_whole_units_of_the_em_half_a_unit_rounded_away_from_zero()
    {
        byte[] between = Trak([10, 14], (0, [-11, -14]));
        Assert.Equal(-13, FontTracking.ByShaper(between, styled: true, 12));
        Assert.Equal(-12.5 / 2048 * 12, FontTracking.Of(between, 2048, 12)!.Value, 9);
    }

    /// <summary>Hoefler Text's tracks with no 'STAT' table: neither macOS nor a shaper sets them; San Francisco's normal track
    /// with one, which both set.</summary>
    [Fact]
    public void Only_a_face_with_a_style_table_is_tracked_by_name_or_by_a_shaper()
    {
        byte[] hoefler = Trak([2, 10, 12, 24, 196, 197], (-1, [8, -25, -33, -55, -150, -150]), (0, [100, 0, -20, -30, -100, -100]), (2, [170, 170, 170, 170, 170, 170]));
        Assert.Equal(0, FontTracking.ByName(hoefler, styled: false, 2000, 13), 9);
        Assert.Equal(0, FontTracking.ByShaper(hoefler, styled: false, 12));
        Assert.Equal(-12.0 / 2048 * 13, FontTracking.ByName(Table, styled: true, 2048, 13), 9);
        Assert.Equal(0, FontTracking.ByShaper(Table, styled: true, 12));
        Assert.Equal(-20, FontTracking.ByShaper(hoefler, styled: true, 12));
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
