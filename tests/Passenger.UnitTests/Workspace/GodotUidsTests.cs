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
        GodotScenes scenes = new(Path.GetFullPath("/game"), new Dictionary<string, GodotFile>
        {
            ["res://player.tscn"] = GodotFile.Parse("[gd_scene format=3 uid=\"uid://bplayer\"]\n\n[node name=\"Player\" type=\"Node2D\"]\n"),
        }, new Dictionary<string, string> { ["uid://bplayer"] = "res://old_player.tscn", ["uid://cmain"] = "res://Main.cs" }, cached: true);
        Assert.Equal("res://player.tscn", scenes.ResOfUid("uid://bplayer"));
        Assert.Equal(Path.GetFullPath("/game/player.tscn"), scenes.FileOf("uid://bplayer"));
        Assert.Equal(Path.GetFullPath("/game/Main.cs"), scenes.FileOf("uid://cmain"));
        Assert.Null(scenes.FileOf("uid://bgone"));
        Assert.True(scenes.KeepsUids);
    }

    [Fact]
    public void A_folders_copy_takes_new_uids_and_its_scenes_name_the_files_copied_with_them_as_godots_duplicate_leaves_them()
    {
        const string Scene = "[gd_scene load_steps=4 format=3 uid=\"uid://bplayer\"]\n\n"
            + "[ext_resource type=\"Script\" uid=\"uid://cscript\" path=\"res://a/player.gd\" id=\"1_s\"]\n"
            + "[ext_resource type=\"Texture2D\" uid=\"uid://bicon\" path=\"res://a/old_icon.svg\" id=\"2_t\"]\n"
            + "[ext_resource type=\"Texture2D\" path=\"res://a/keep.png\" id=\"3_k\"]\n"
            + "[ext_resource type=\"Script\" uid=\"uid://celse\" path=\"res://a/player.gd\" id=\"4_e\"]\n"
            + "[ext_resource type=\"Script\" path=\"res://shared/util.gd\" id=\"5_u\"]\n\n"
            + "[node name=\"Player\" type=\"Node2D\"]\nscript = ExtResource(\"1_s\")\n";
        const string Import = "[remap]\n\nimporter=\"texture\"\ntype=\"CompressedTexture2D\"\nuid=\"uid://bicon\"\npath=\"res://.godot/imported/icon.svg-1.ctex\"\n";
        const string Kept = "[remap]\n\nimporter=\"keep\"\n";
        IReadOnlyList<GodotCopy> copied = GodotUids.Copied([
            new GodotCopy("res://a/player.tscn", "res://b/player.tscn", Scene, null, null),
            new GodotCopy("res://a/player.gd", "res://b/player.gd", null, "uid://cscript\n", null),
            new GodotCopy("res://a/icon.svg", "res://b/icon.svg", null, null, Import),
            new GodotCopy("res://a/keep.png", "res://b/keep.png", null, null, Kept),
            new GodotCopy("res://a/notes.txt", "res://b/notes.txt", null, null, null),
        ], within: true, uid => uid == "uid://celse", new Random(7));

        string script = GodotUids.OfBeside("player.gd.uid", copied[1].Uid!)!;
        string icon = GodotUids.OfBeside("icon.svg.import", copied[2].Import!)!;
        GodotFile scene = GodotFile.Parse(copied[0].Text!);
        string[] uids = [scene.Header!.Text("uid")!, script, icon];
        Assert.True(uids.All(u => u.StartsWith(GodotUids.Scheme, StringComparison.Ordinal)) && uids.Distinct().Count() == 3
            && !uids.Intersect(["uid://bplayer", "uid://cscript", "uid://bicon"]).Any(), $"The copies kept a uid of their originals: {string.Join(", ", uids)}");
        Assert.Equal(Import.Replace("uid://bicon", icon, StringComparison.Ordinal), copied[2].Import);
        Assert.Equal(Kept, copied[3].Import);
        Assert.Equal(new GodotCopy("res://a/notes.txt", "res://b/notes.txt", null, null, null), copied[4]);
        // By the uid a copied file gave, whatever path it writes; by path where it writes no uid; a uid another file gives and a file not copied, as written.
        Assert.Equal(
        [
            ("res://b/player.gd", script),
            ("res://b/icon.svg", icon),
            ("res://b/keep.png", null),
            ("res://a/player.gd", "uid://celse"),
            ("res://shared/util.gd", null),
        ], scene.Externals.Select(e => (e.Path, e.Uid)));
        Assert.EndsWith("[node name=\"Player\" type=\"Node2D\"]\nscript = ExtResource(\"1_s\")\n", copied[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_files_copy_takes_new_uids_and_names_what_its_original_named()
    {
        const string Scene = "[gd_scene format=3 uid=\"uid://bplayer\"]\n\n[ext_resource type=\"Script\" uid=\"uid://cscript\" path=\"res://a/player.gd\" id=\"1_s\"]\n";
        IReadOnlyList<GodotCopy> copied = GodotUids.Copied([new GodotCopy("res://a/player.tscn", "res://a/player_2.tscn", Scene, null, null),
            new GodotCopy("res://a/player.gd", "res://b/player.gd", null, "uid://cscript\r\n", null)], within: false, _ => false, new Random(7));

        GodotFile scene = GodotFile.Parse(copied[0].Text!);
        Assert.NotEqual("uid://bplayer", scene.Header!.Text("uid"));
        Assert.Equal([("res://a/player.gd", "uid://cscript")], scene.Externals.Select(e => (e.Path, e.Uid)));
        Assert.Matches("^uid://[a-y0-8]+\r\n$", copied[1].Uid);
        Assert.NotEqual("uid://cscript\r\n", copied[1].Uid);
        Assert.Equal("nothing\n", GodotUids.Copied([new GodotCopy("res://a/broken.gd", "res://b/broken.gd", null, "nothing\n", null)], within: false, _ => false, new Random(7))[0].Uid);
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
