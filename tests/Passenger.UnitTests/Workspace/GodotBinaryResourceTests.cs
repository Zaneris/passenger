using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Resources and scenes Godot writes as bytes, read as far as their uids and the resources they name and written
/// again with others as Godot rewrites one in place: Godot 4.7's own compressed scene and plain resource, a big-endian one,
/// and those of a newer format, another compression or cut short, left unread saying why.</summary>
public class GodotBinaryResourceTests
{
    /// <summary>A scene Godot 4.7's editor saved compressed (res://actors/level.scn): a Node2D with player.gd and two sprites,
    /// one of look.res, one of icon.svg.</summary>
    internal static readonly byte[] Level = Convert.FromBase64String(
        "UlNDQwIAAAAAEAAAlgMAANsBAAAotS/9YJYCjQ4ANlVKPDBxksbBISVKOFKZ0oeBgQzS0GUCB6M3su9JZH8a2/dy933XQ4sbdlRO4rHyewXjRLRm806kfXz7"
        + "hBCSAjQARAA1ANLp5GBVsv4A6wmDBOTauSbWdlraYrVS4/8xAxcyYGxAEaHF/6xQm+QU09iFf8BP+Tt+6AONho4aNmaoiHFik4yswG0PJMRpUILGA11qrGB2"
        + "JwNPCNFYzRCa4ReAwcGKFywomIB2JpWoIthERTLubCyKLAekSb3YAtuWuvVwmp8/wXJC9iLLwG0W5ouUNH8AY3YvbkcmzehlwVJPGOT4Pd5pQLwQREQ8rCiy"
        + "/Aj+Ekgkkr+B+2TUjDbIpoRv6VTC5nP6F3Dvw2TUEG4b4fBcoBQWEhU6fIxwbzkUqLnxg/wKlur2mbAKR6iRsVGIZIKRIC0sSBoDYAKRYnQ3EsD1PMoqgWXB"
        + "TCXrbz0DswFQUrDE69+eGOfUcWk1xtlHMv/9N/hXcQ2Upk4gwsKgSwT/e5BVc2jNICPxjgzzADGAJ8wGeEvZlG5It/tdOWV/ThoRRxzlFBo6NpL9qmxwxwqF"
        + "nrzswB546CfMgqDFEjtIzBr0nzv+qIxHFjxFt0Jfaob8oXcEvp8WqoStQiAMUlNDQw==");

    /// <summary>A resource Godot 4.7's editor saved plain (res://actors/look.res): an AtlasTexture of icon.svg.</summary>
    internal static readonly byte[] Look = Convert.FromBase64String(
        "UlNSQwAAAAAAAAAABAAAAAcAAAAGAAAADQAAAEF0bGFzVGV4dHVyZQAAAAAAAAAAAAMAAABlK+CNJgIyGwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
        + "AAAAAAAAAAAAAAAAAAAABwAAABgAAAByZXNvdXJjZV9sb2NhbF90b19zY2VuZQAOAAAAcmVzb3VyY2VfbmFtZQAGAAAAYXRsYXMABwAAAHJlZ2lvbgAHAAAA"
        + "bWFyZ2luAAwAAABmaWx0ZXJfY2xpcAAHAAAAc2NyaXB0AAEAAAAKAAAAVGV4dHVyZTJEABYAAAByZXM6Ly9hY3RvcnMvaWNvbi5zdmcAltKp3v7wB1YBAAAA"
        + "GwAAAGxvY2FsOi8vQXRsYXNUZXh0dXJlXzFkcmo4ADUBAAAAAAAADQAAAEF0bGFzVGV4dHVyZQADAAAAAgAAABgAAAADAAAAAAAAAAMAAAALAAAAAAAAAAAA"
        + "AAAAAABBAAAAQQYAAAABAAAAUlNSQw==");

    [Fact]
    public void Godots_own_scene_and_resource_are_read_as_far_as_their_uids_and_the_resources_they_name()
    {
        GodotBinaryResource level = Read(Level);
        Assert.Equal("uid://bd1m7ab3qhdf3", level.Uid);
        Assert.Equal(
        [
            new GodotBinaryExternal("Script", "res://actors/player.gd", "uid://cplayerscrpt1"),
            new GodotBinaryExternal("Texture2D", "res://actors/look.res", "uid://26i3rvh6jh06"),
            new GodotBinaryExternal("Texture2D", "res://actors/icon.svg", "uid://cuk6yyqqtgnlm"),
        ], level.Externals);
        GodotBinaryResource look = Read(Look);
        Assert.Equal("uid://26i3rvh6jh06", look.Uid);
        Assert.Equal([new GodotBinaryExternal("Texture2D", "res://actors/icon.svg", "uid://cuk6yyqqtgnlm")], look.Externals);
        Assert.True(GodotBinaryResource.Starts(Level) && GodotBinaryResource.Starts(Look) && !GodotBinaryResource.Starts("<svg"u8));
    }

    [Fact]
    public void A_resource_given_other_names_keeps_all_else_and_given_its_own_back_is_as_godot_wrote_it()
    {
        foreach (byte[] file in (byte[][])[Level, Look])
        {
            GodotBinaryResource original = Read(file);
            IReadOnlyList<GodotBinaryExternal> moved = [.. original.Externals.Select((e, i) => e with { Path = e.Path.Replace("res://actors/", "res://levels/actors/"), Uid = i == 0 ? "uid://bmoved" : e.Uid })];
            byte[] copy = original.With("uid://cnewcopy", moved);

            GodotBinaryResource read = Read(copy);
            Assert.Equal("uid://cnewcopy", read.Uid);
            Assert.Equal(moved, read.Externals);
            Assert.True(copy.AsSpan(0, 4).SequenceEqual(file.AsSpan(0, 4)), "The copy was not left compressed or plain as its original was");
            // Back to its own names, the stream is the original's: every internal resource's place moved as the paths' lengths did.
            Assert.Equal(Stream(file), Stream(read.With(original.Uid, original.Externals)));
            Assert.Same(file, original.With(null, original.Externals));
        }
        // A plain one's bytes come back exactly.
        Assert.Equal(Look, Read(Read(Look).With("uid://cnewcopy", [new GodotBinaryExternal("Texture2D", "res://a/b/c/icon.svg", "uid://bmoved")])).With("uid://26i3rvh6jh06", Read(Look).Externals));
    }

    [Fact]
    public void A_big_endian_resource_is_read_and_written_in_its_own_byte_order_its_internal_resources_found_where_they_moved()
    {
        byte[] file = Built(big: true, format: 6, 1234567, ("Script", "res://a/player.gd", 42));
        GodotBinaryResource read = Read(file);
        Assert.Equal(GodotUids.Text(1234567), read.Uid);
        Assert.Equal([new GodotBinaryExternal("Script", "res://a/player.gd", GodotUids.Text(42))], read.Externals);

        byte[] copy = read.With(GodotUids.Text(7654321), [new GodotBinaryExternal("Script", "res://levels/a/player.gd", GodotUids.Text(43))]);
        GodotBinaryResource again = Read(copy);
        Assert.Equal(GodotUids.Text(7654321), again.Uid);
        Assert.Equal([new GodotBinaryExternal("Script", "res://levels/a/player.gd", GodotUids.Text(43))], again.Externals);
        // The internal resource's place, after its path, names its body: its type's length and name.
        int entry = copy.AsSpan().IndexOf("local://Resource_1\0"u8) + "local://Resource_1\0".Length;
        int body = (int)BinaryPrimitives.ReadUInt64BigEndian(copy.AsSpan(entry));
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(body)));
        Assert.Equal("Resource\0", Encoding.UTF8.GetString(copy, body + 4, 9));
    }

    [Fact]
    public void A_newer_format_another_compression_or_a_file_cut_short_is_left_unread_saying_why()
    {
        Assert.Null(GodotBinaryResource.Read(Built(big: false, format: 7, 1), out string? newer));
        Assert.Contains("binary format 7", newer, StringComparison.Ordinal);
        byte[] deflated = [.. Level];
        BinaryPrimitives.WriteUInt32LittleEndian(deflated.AsSpan(4), 1);
        Assert.Null(GodotBinaryResource.Read(deflated, out string? compressed));
        Assert.Contains("compressed", compressed, StringComparison.Ordinal);
        Assert.Null(GodotBinaryResource.Read(Look[..120], out string? cut));
        Assert.NotNull(cut);
        Assert.Null(GodotBinaryResource.Read(Level[..200], out string? blocks));
        Assert.NotNull(blocks);
        byte[] broken = [.. Level];
        broken[20] ^= 0x5A;
        Assert.Null(GodotBinaryResource.Read(broken, out string? frame));
        Assert.NotNull(frame);
        Assert.Null(GodotBinaryResource.Read("<svg/>"u8.ToArray(), out string? none));
        Assert.Null(none);
    }

    [Fact]
    public void A_uids_text_names_the_id_godot_reads_from_it()
    {
        Random random = new(7);
        for (int i = 0; i < 100; i++)
        {
            long id = random.NextInt64();
            Assert.Equal(id, GodotUids.Id(GodotUids.Text(id)!));
        }
        Assert.Equal(0, GodotUids.Id("uid://a"));
        Assert.Null(GodotUids.Id("uid://"));
        Assert.Null(GodotUids.Id("uid://<invalid>"));
        Assert.Null(GodotUids.Id("res://a"));
    }

    internal static GodotBinaryResource Read(byte[] file) =>
        GodotBinaryResource.Read(file, out string? unread) ?? throw new Xunit.Sdk.XunitException($"Godot's binary format was not read: {unread}");

    /// <summary>A file's stream: a plain one's bytes, a compressed one's blocks decompressed in turn.</summary>
    private static byte[] Stream(byte[] file)
    {
        if (file.AsSpan().StartsWith("RSRC"u8))
        {
            return file;
        }
        int size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8));
        int total = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(12));
        int count = total / size + 1;
        byte[] stream = new byte[total];
        int at = 16 + count * 4;
        for (int i = 0; i < count; i++)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(16 + i * 4));
            Assert.True(ZstandardDecoder.TryDecompress(file.AsSpan(at, length), stream.AsSpan(i * size), out _), $"Block {i} is no Zstandard frame");
            at += length;
        }
        return stream;
    }

    /// <summary>A plain resource of Godot's binary format with the uid and external resources given and one internal resource
    /// of no properties, written in the byte order asked.</summary>
    private static byte[] Built(bool big, uint format, long uid, params (string Type, string Path, long Uid)[] externals)
    {
        MemoryStream bytes = new();
        bytes.Write("RSRC"u8);
        Put(bytes, big ? 1u : 0u, false);
        Put(bytes, 0, false);
        foreach (uint value in (uint[])[4, 7, format])
        {
            Put(bytes, value, big);
        }
        Text(bytes, "Resource", big);
        Put(bytes, 0UL, big);
        Put(bytes, 3, big);
        Put(bytes, (ulong)uid, big);
        for (int i = 0; i < 11; i++)
        {
            Put(bytes, 0, big);
        }
        Put(bytes, 1, big);
        Text(bytes, "script", big);
        Put(bytes, (uint)externals.Length, big);
        foreach ((string type, string path, long id) in externals)
        {
            Text(bytes, type, big);
            Text(bytes, path, big);
            Put(bytes, (ulong)id, big);
        }
        Put(bytes, 1, big);
        Text(bytes, "local://Resource_1", big);
        long offset = bytes.Position;
        Put(bytes, 0UL, big);
        long body = bytes.Position;
        Text(bytes, "Resource", big);
        Put(bytes, 0, big);
        bytes.Write("RSRC"u8);
        bytes.Position = offset;
        Put(bytes, (ulong)body, big);
        return bytes.ToArray();

        static void Text(Stream to, string text, bool big)
        {
            byte[] written = Encoding.UTF8.GetBytes(text + '\0');
            Put(to, (uint)written.Length, big);
            to.Write(written);
        }
    }

    private static void Put(Stream to, uint value, bool big)
    {
        byte[] bytes = new byte[4];
        if (big)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        }
        to.Write(bytes);
    }

    private static void Put(Stream to, ulong value, bool big)
    {
        byte[] bytes = new byte[8];
        if (big)
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        }
        to.Write(bytes);
    }
}
