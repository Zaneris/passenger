using System.Text;
using Passenger.App.Editors;

namespace Passenger.UnitTests.Editors;

/// <summary>Files landed whole, whatever they hold: each byte kept as they land, and through Undo and Redo, only while each
/// file is as the command left it.</summary>
public sealed class LandedFilesTests : IDisposable
{
    /// <summary>An ELF's first bytes with one that is no UTF-8 and a NUL, which a read as text changes.</summary>
    private static readonly byte[] Binary = [0x7F, 0x45, 0x4C, 0x46, 0xFF, 0x00, 0x80, 0x0A];

    /// <summary>Text no save writes back as it found it: a byte order mark, CRLF breaks and a lone CR.</summary>
    private static readonly byte[] Text = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("one\r\ntwo\rthree\n")];

    private readonly string folder = Directory.CreateTempSubdirectory("landed-files-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private string Staged(string name, byte[] bytes)
    {
        string path = Path.Combine(folder, "staged", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Target(string name) => Path.Combine(folder, "open", name);

    private LandedFiles Landing() => new(Path.Combine(folder, "kept"));

    private static string? Land(LandedFiles landed, params (string From, string To)[] files) => landed.Land(files, new HashSet<string>(), _ => { });

    [Fact]
    public void A_binary_file_and_text_no_save_keeps_land_byte_for_byte_and_undo_takes_them_away_and_redo_gives_them_back()
    {
        string binary = Target("certs/server.pfx");
        string text = Target("notes.txt");
        LandedFiles landed = Landing();
        Assert.Null(Land(landed, (Staged("server.pfx", Binary), binary), (Staged("notes.txt", Text), text)));
        Assert.Equal(Binary, File.ReadAllBytes(binary));
        Assert.Equal(Text, File.ReadAllBytes(text));

        Assert.Null(landed.Unlike(redo: false));
        Assert.Null(landed.Put(redo: false, _ => { }));
        Assert.False(File.Exists(binary) || File.Exists(text), "Undo left files the command made");

        Assert.Null(landed.Unlike(redo: true));
        Assert.Null(landed.Put(redo: true, _ => { }));
        Assert.Equal(Binary, File.ReadAllBytes(binary));
        Assert.Equal(Text, File.ReadAllBytes(text));
    }

    [Fact]
    public void A_binary_file_landed_over_is_put_back_byte_for_byte_never_deleted_as_one_the_command_made()
    {
        string target = Target("server.pfx");
        byte[] had = [0x00, 0xFF, 0xC0, 0x80, 0x0D];
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, had);
        LandedFiles landed = Landing();
        Assert.Null(Land(landed, (Staged("server.pfx", Binary), target)));
        Assert.Equal(Binary, File.ReadAllBytes(target));

        Assert.Null(landed.Put(redo: false, _ => { }));
        Assert.Equal(had, File.ReadAllBytes(target));
        Assert.Null(landed.Put(redo: true, _ => { }));
        Assert.Equal(Binary, File.ReadAllBytes(target));
    }

    [Fact]
    public void A_file_that_already_holds_the_bytes_is_no_part_of_the_command()
    {
        string target = Target("same.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, Binary);
        LandedFiles landed = Landing();
        Assert.Null(Land(landed, (Staged("same.bin", Binary), target)));
        Assert.Empty(landed.Paths);
    }

    [Fact]
    public void A_file_changed_since_is_named_and_stays_as_it_is()
    {
        string target = Target("server.pfx");
        LandedFiles landed = Landing();
        Assert.Null(Land(landed, (Staged("server.pfx", Binary), target)));
        byte[] changed = [.. Binary, 0x01];
        File.WriteAllBytes(target, changed);
        Assert.Equal("server.pfx has been changed since", landed.Unlike(redo: false));
        File.Delete(target);
        Assert.Equal("server.pfx is no longer there", landed.Unlike(redo: false));
    }

    [Fact]
    public void A_file_made_again_since_undo_is_named_before_redo_writes_over_it()
    {
        string target = Target("server.pfx");
        LandedFiles landed = Landing();
        Assert.Null(Land(landed, (Staged("server.pfx", Binary), target)));
        Assert.Null(landed.Put(redo: false, _ => { }));
        File.WriteAllBytes(target, [0x01]);
        Assert.Equal("server.pfx is already there", landed.Unlike(redo: true));
    }

    [Fact]
    public void A_file_with_its_own_undo_is_written_and_kept_out_of_what_undo_puts_back()
    {
        string target = Target("open.txt");
        LandedFiles landed = Landing();
        Assert.Null(landed.Land([(Staged("open.txt", Text), target)], new HashSet<string> { target }, _ => { }));
        Assert.Equal(Text, File.ReadAllBytes(target));
        Assert.Empty(landed.Paths);
    }

    [Fact]
    public void A_write_the_disk_refuses_says_why_those_before_it_staying_for_undo_to_take_back()
    {
        string first = Target("first.bin");
        string refused = Target("read-only.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(refused)!);
        File.WriteAllBytes(refused, [0x01]);
        new FileInfo(refused).IsReadOnly = true;
        LandedFiles landed = Landing();
        string? why = Land(landed, (Staged("first.bin", Binary), first), (Staged("read-only.bin", Binary), refused));
        Assert.Equal("read-only.bin is read-only", why);
        Assert.Equal([first], landed.Paths);
        Assert.Equal([(byte)0x01], File.ReadAllBytes(refused));
        Assert.Null(landed.Put(redo: false, _ => { }));
        Assert.False(File.Exists(first), "Taken back, the file the command made stayed");
        new FileInfo(refused).IsReadOnly = false;
    }
}
