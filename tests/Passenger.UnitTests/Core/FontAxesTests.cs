using System.Buffers.Binary;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A variable font's 'fvar' table read for its axes, as macOS's text drawing sets them: each axis's least, default
/// and greatest value, a value held between its least and greatest.</summary>
public class FontAxesTests
{
    /// <summary>San Francisco's own axes: width, optical size, grade and weight.</summary>
    private static readonly byte[] Table = Fvar(("wdth", 30, 100, 150), ("opsz", 17, 28, 96), ("GRAD", 400, 400, 1000), ("wght", 1, 400, 1000));

    [Fact]
    public void Each_axis_is_read_with_its_least_default_and_greatest_value_in_the_tables_order()
    {
        IReadOnlyList<FontAxis> axes = FontAxes.Of(Table);
        Assert.Equal(((string[])["wdth", "opsz", "GRAD", "wght"]).Select(FontAxes.TagOf), axes.Select(a => a.Tag));
        FontAxis opsz = axes.Single(a => a.Tag == FontAxes.OpticalSize);
        Assert.Equal((17.0, 28.0, 96.0), (opsz.Least, opsz.Default, opsz.Greatest));
        Assert.Equal(700, axes.Single(a => a.Tag == FontAxes.Weight).Held(700));
    }

    [Fact]
    public void An_optical_size_below_the_axis_is_its_least_and_one_above_its_greatest()
    {
        FontAxis opsz = FontAxes.Of(Table).Single(a => a.Tag == FontAxes.OpticalSize);
        Assert.Equal(17, opsz.Held(13));
        Assert.Equal(20, opsz.Held(20));
        Assert.Equal(96, opsz.Held(120));
    }

    [Fact]
    public void A_table_cut_short_or_of_another_version_has_no_axes()
    {
        Assert.Empty(FontAxes.Of(Table.AsSpan(0, 40)));
        Assert.Empty(FontAxes.Of([]));
        byte[] other = [.. Table];
        BinaryPrimitives.WriteUInt16BigEndian(other, 2);
        Assert.Empty(FontAxes.Of(other));
    }

    /// <summary>An 'fvar' table of axes alone: its header, then each axis's record.</summary>
    private static byte[] Fvar(params (string Tag, double Least, double Default, double Greatest)[] axes)
    {
        const int Start = 16;
        const int Size = 20;
        byte[] table = new byte[Start + (Size * axes.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(table, 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4), Start);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(8), (ushort)axes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(10), Size);
        for (int i = 0; i < axes.Length; i++)
        {
            Span<byte> axis = table.AsSpan(Start + (Size * i));
            BinaryPrimitives.WriteUInt32BigEndian(axis, FontAxes.TagOf(axes[i].Tag));
            BinaryPrimitives.WriteInt32BigEndian(axis[4..], (int)(axes[i].Least * 65536));
            BinaryPrimitives.WriteInt32BigEndian(axis[8..], (int)(axes[i].Default * 65536));
            BinaryPrimitives.WriteInt32BigEndian(axis[12..], (int)(axes[i].Greatest * 65536));
        }
        return table;
    }
}
