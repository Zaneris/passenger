using System.Buffers.Binary;

namespace Passenger.Core;

/// <summary>A variable font's axes, its 'fvar' table: each axis's tag and the least, default and greatest value it takes.
/// macOS's text drawing sets a face's optical size axis to the size of the words it draws, held between the axis's own
/// least and greatest. Pure.</summary>
public static class FontAxes
{
    /// <summary>The optical size axis, 'opsz'.</summary>
    public static readonly uint OpticalSize = TagOf("opsz");

    /// <summary>The weight axis, 'wght'.</summary>
    public static readonly uint Weight = TagOf("wght");

    /// <summary>The table's axes in its order; none where it cannot be read.</summary>
    public static IReadOnlyList<FontAxis> Of(ReadOnlySpan<byte> fvar)
    {
        if (fvar.Length < 16 || BinaryPrimitives.ReadUInt16BigEndian(fvar) != 1)
        {
            return [];
        }
        int start = BinaryPrimitives.ReadUInt16BigEndian(fvar[4..]);
        int count = BinaryPrimitives.ReadUInt16BigEndian(fvar[8..]);
        int size = BinaryPrimitives.ReadUInt16BigEndian(fvar[10..]);
        if (size < 20 || start + ((long)count * size) > fvar.Length)
        {
            return [];
        }
        List<FontAxis> axes = new(count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> axis = fvar[(start + (i * size))..];
            axes.Add(new FontAxis(BinaryPrimitives.ReadUInt32BigEndian(axis), Fixed(axis[4..]), Fixed(axis[8..]), Fixed(axis[12..])));
        }
        return axes;
    }

    /// <summary>A 16.16 fixed number.</summary>
    private static double Fixed(ReadOnlySpan<byte> at) => BinaryPrimitives.ReadInt32BigEndian(at) / 65536.0;

    /// <summary>An OpenType tag's four letters as the number tables name it by.</summary>
    public static uint TagOf(string tag) => BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(tag));
}

/// <summary>One axis of a variable font: its tag and the values it takes.</summary>
public readonly record struct FontAxis(uint Tag, double Least, double Default, double Greatest)
{
    /// <summary>A value held between the axis's least and greatest.</summary>
    public double Held(double value) => Math.Clamp(value, Least, Math.Max(Least, Greatest));
}
