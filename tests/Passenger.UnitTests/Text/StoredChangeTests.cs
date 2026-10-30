using System.Text;
using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>How a file's text came to be stored another way (<see cref="FileText.StoredChange"/>), which a comparison of
/// its decoded lines never sees: a byte order mark added or removed, or another encoding, and nothing where the bytes
/// would be the same.</summary>
public class StoredChangeTests
{
    [Fact]
    public void A_byte_order_mark_added_or_removed_is_said_so()
    {
        FileText plain = FileText.FromBytes(Encoding.UTF8.GetBytes("class A\n{\n}\n"))!;
        Assert.Equal("Byte order mark added", plain.StoredChange(new UTF8Encoding(true), bom: true));
        FileText marked = FileText.FromBytes([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("class A\n")])!;
        Assert.Equal("Byte order mark removed", marked.StoredChange(new UTF8Encoding(false), bom: false));
    }

    [Fact]
    public void Another_encoding_is_said_from_which_to_which()
    {
        FileText utf8 = FileText.FromBytes(Encoding.UTF8.GetBytes("Notes: café\n"))!;
        Assert.Equal("Encoding changed from UTF-8 to UTF-16 LE", utf8.StoredChange(Encoding.Unicode, bom: true));
        FileText utf16 = FileText.FromBytes([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("Notes: café\n")])!;
        Assert.Equal("Encoding changed from UTF-16 LE to UTF-8 BOM", utf16.StoredChange(new UTF8Encoding(true), bom: true));
        Assert.Equal("Encoding changed from UTF-8 to ISO-8859-1", utf8.StoredChange(Encoding.Latin1, bom: false));
    }

    [Fact]
    public void Stored_as_the_same_bytes_nothing_changed()
    {
        FileText utf8 = FileText.FromBytes(Encoding.UTF8.GetBytes("Notes: café\n"))!;
        Assert.Null(utf8.StoredChange(new UTF8Encoding(false), bom: false));
        // ASCII is the same bytes in UTF-8 and Latin-1.
        FileText ascii = FileText.FromBytes(Encoding.ASCII.GetBytes("plain\n"))!;
        Assert.Null(ascii.StoredChange(Encoding.Latin1, bom: false));
    }
}
