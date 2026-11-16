using System.Buffers.Binary;
using System.Text;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A Recycle Bin's record of an item read for the path it was deleted from, in both forms Windows writes: the
/// second, a count of characters before the path, and the first, a field of 260; anything else is no record.</summary>
public sealed class RecycleBinTests
{
    private const string Deleted = @"C:\Users\ted\Shop\Shop.Shared\Models";

    [Fact]
    public void A_record_of_either_form_names_the_path_its_item_was_deleted_from()
    {
        Assert.Equal(Deleted, RecycleBin.DeletedFrom(Record(2, Deleted)));
        Assert.Equal(Deleted, RecycleBin.DeletedFrom(Record(1, Deleted)));
    }

    [Fact]
    public void A_record_cut_short_or_of_another_form_names_nothing()
    {
        byte[] whole = Record(2, Deleted);
        Assert.Null(RecycleBin.DeletedFrom(whole.AsSpan(0, 30)));
        Assert.Null(RecycleBin.DeletedFrom(whole.AsSpan(0, 20)));
        Assert.Null(RecycleBin.DeletedFrom(Record(3, Deleted)));
        Assert.Null(RecycleBin.DeletedFrom(Record(2, string.Empty)));
    }

    /// <summary>A record as Windows writes one: its form, the item's size and when it went, then the path.</summary>
    private static byte[] Record(long form, string path)
    {
        byte[] named = Encoding.Unicode.GetBytes(path + "\0");
        byte[] record = new byte[form == 1 ? 24 + 520 : 28 + named.Length];
        BinaryPrimitives.WriteInt64LittleEndian(record, form);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), 4096);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(16), 134_000_000_000_000_000);
        if (form == 1)
        {
            named.CopyTo(record.AsSpan(24));
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(24), named.Length / 2);
            named.CopyTo(record.AsSpan(28));
        }
        return record;
    }
}
