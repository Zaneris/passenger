using System.Buffers.Binary;
using System.Text;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Godot's uid:// names as Godot keeps them: an id written in its base 34, the uid a .uid or an .import file gives
/// the file beside it, the cache its editor writes, and a project's scenes leading from a uid to its file, a scene's own
/// header first.</summary>
public class GodotUidsTests
{
    [Fact]
    public void An_id_is_written_as_godot_writes_it()
    {
        // The id Godot's editor gave a script, with the uid it wrote in the script's .uid file.
        Assert.Equal("uid://cel14ck1jyeh5", GodotUids.Text(5077935448757199916));
        Assert.Equal("uid://a", GodotUids.Text(0));
        Assert.Null(GodotUids.Text(-1));
    }

    [Fact]
    public void A_uid_and_an_import_file_give_the_file_beside_them_its_uid()
    {
        Assert.Equal("uid://cel14ck1jyeh5", GodotUids.OfBeside("/game/Main.cs.uid", "uid://cel14ck1jyeh5\n"));
        Assert.Equal("uid://bicon", GodotUids.OfBeside("/game/icon.svg.import", "[remap]\n\nimporter=\"texture\"\ntype=\"CompressedTexture2D\"\nuid=\"uid://bicon\"\npath=\"res://.godot/imported/icon.svg-1.ctex\"\n"));
        Assert.Null(GodotUids.OfBeside("/game/broken.cs.uid", "nothing"));
        Assert.Equal("/game/Main.cs", GodotUids.BesideOf("/game/Main.cs.uid"));
        Assert.True(GodotUids.IsBeside("/game/icon.svg.import"));
        Assert.False(GodotUids.IsBeside("/game/Main.cs"));
    }

    [Fact]
    public void The_cache_godots_editor_writes_is_read_entry_by_entry_and_one_cut_short_ends_it()
    {
        byte[] cache = Cache((5077935448757199916, "res://Main.cs"), (1, "res://main.tscn"));
        Assert.Equal([("uid://cel14ck1jyeh5", "res://Main.cs"), ("uid://b", "res://main.tscn")], GodotUids.ReadCache(cache));
        Assert.Equal([("uid://cel14ck1jyeh5", "res://Main.cs")], GodotUids.ReadCache(cache.AsSpan(0, cache.Length - 3)));
        Assert.Empty(GodotUids.ReadCache([1, 2]));
    }

    [Fact]
    public void A_projects_scenes_lead_from_a_uid_to_its_file_a_scenes_own_header_first()
    {
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile>
        {
            ["res://player.tscn"] = GodotFile.Parse("[gd_scene format=3 uid=\"uid://bplayer\"]\n\n[node name=\"Player\" type=\"Node2D\"]\n"),
        }, new Dictionary<string, string> { ["uid://bplayer"] = "res://old_player.tscn", ["uid://cmain"] = "res://Main.cs" }, cached: true);
        Assert.Equal("res://player.tscn", scenes.ResOfUid("uid://bplayer"));
        Assert.Equal(Path.GetFullPath("/game/player.tscn"), scenes.FileOf("uid://bplayer"));
        Assert.Equal(Path.GetFullPath("/game/Main.cs"), scenes.FileOf("uid://cmain"));
        Assert.Null(scenes.FileOf("uid://bgone"));
        Assert.True(scenes.KeepsUids);
    }

    private static byte[] Cache(params (long Id, string Res)[] entries)
    {
        List<byte> bytes = [];
        byte[] four = new byte[4];
        byte[] eight = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(four, (uint)entries.Length);
        bytes.AddRange(four);
        foreach ((long id, string res) in entries)
        {
            BinaryPrimitives.WriteInt64LittleEndian(eight, id);
            bytes.AddRange(eight);
            byte[] path = Encoding.UTF8.GetBytes(res);
            BinaryPrimitives.WriteUInt32LittleEndian(four, (uint)path.Length);
            bytes.AddRange(four);
            bytes.AddRange(path);
        }
        return [.. bytes];
    }
}
