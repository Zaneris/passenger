using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Godot;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Files and folders copied in the Explorer within a Godot project Godot has opened, left as Godot's own duplicate
/// leaves them: every copy given a uid of its own (a script's .uid, a scene's header, a binary scene's or resource's, an
/// imported file's .import), a folder's scenes naming the files copied with them, a file's copy given its .uid, the
/// originals as they were; Godot's headless editor then opens the project with no two files of one uid, and loads a copied
/// scene from the copies. A file moved is named at its new place by the binary scenes and resources naming it, which Godot
/// loads it from. Without a Godot 4.4 or later, which keeps a script's uid beside it, each test skips saying why.</summary>
public sealed class GodotExplorerCopyTests : IDisposable
{
    private const string Script = "extends Node2D\n\n\nfunc _ready() -> void:\n\tprint(\"player\")\n";

    private const string Scene = "[gd_scene load_steps=3 format=3 uid=\"uid://bplayerscene1\"]\n\n"
        + "[ext_resource type=\"Script\" uid=\"uid://cplayerscrpt1\" path=\"res://actors/player.gd\" id=\"1_s\"]\n"
        + "[ext_resource type=\"Texture2D\" path=\"res://actors/icon.svg\" id=\"2_t\"]\n\n"
        + "[node name=\"Player\" type=\"Node2D\"]\nscript = ExtResource(\"1_s\")\n\n"
        + "[node name=\"Sprite\" type=\"Sprite2D\" parent=\".\"]\ntexture = ExtResource(\"2_t\")\n";

    /// <summary>An editor plugin that, where its variable says so, saves as a person's editor saves them a resource
    /// (actors/look.res, an AtlasTexture of icon.svg, plain) and a scene (actors/level.scn, player.gd's Node2D showing
    /// look.res and icon.svg, compressed), then quits.</summary>
    private const string Maker = "@tool\nextends EditorPlugin\n\n\nfunc _enter_tree() -> void:\n\tif OS.get_environment(\"COPIES_MAKE\") != \"\":\n\t\t_make.call_deferred()\n\n\n"
        + "func _make() -> void:\n\tvar files := EditorInterface.get_resource_filesystem()\n\tfor i in 600:\n\t\tawait get_tree().process_frame\n"
        + "\t\tif not files.is_scanning() and i > 30:\n\t\t\tbreak\n"
        + "\tvar look := AtlasTexture.new()\n\tlook.atlas = load(\"res://actors/icon.svg\")\n\tlook.region = Rect2(0, 0, 8, 8)\n"
        + "\tprint(\"saved look \", ResourceSaver.save(look, \"res://actors/look.res\"))\n\tfiles.update_file(\"res://actors/look.res\")\n"
        + "\tvar level := Node2D.new()\n\tlevel.name = \"Level\"\n\tlevel.set_script(load(\"res://actors/player.gd\"))\n"
        + "\tfor shown in [[\"Look\", load(\"res://actors/look.res\")], [\"Icon\", load(\"res://actors/icon.svg\")]]:\n"
        + "\t\tvar sprite := Sprite2D.new()\n\t\tsprite.name = shown[0]\n\t\tsprite.texture = shown[1]\n\t\tlevel.add_child(sprite)\n\t\tsprite.owner = level\n"
        + "\tvar scene := PackedScene.new()\n\tscene.pack(level)\n"
        + "\tprint(\"saved level \", ResourceSaver.save(scene, \"res://actors/level.scn\", ResourceSaver.FLAG_COMPRESS))\n\tlevel.free()\n\tget_tree().quit()\n";

    /// <summary>A script that loads the copied scene, saying where the script and textures its nodes take came from.</summary>
    private const string Loader = "extends SceneTree\n\n\nfunc _init() -> void:\n\tvar level: Node = load(\"res://levels/actors/level.scn\").instantiate()\n"
        + "\tvar look: AtlasTexture = level.get_node(\"Look\").texture\n"
        + "\tprint(\"loaded \", level.get_script().resource_path, \" \", look.resource_path, \" \", look.atlas.resource_path, \" \", level.get_node(\"Icon\").texture.resource_path)\n"
        + "\tlevel.free()\n\tquit()\n";

    private readonly string folder = PathNames.Canonical(TestFolders.Create("godot-copy-"));
    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public GodotExplorerCopyTests()
    {
        Assert.SkipWhen(godot is not { WhyNotScripts: null, Version: { } version } || !Version.TryParse(string.Join('.', version.Split('.').Take(2)), out Version? numbers)
            || numbers < new Version(4, 4), godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4.4 or later to run these tests"
            : $"The Godot found keeps no script's uid beside it: {godot.WhyNotScripts ?? $"Godot {godot.Version} is older than 4.4"}");
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Copies\"\n");
        TestFolders.Write(folder, "actors/player.gd", Script);
        TestFolders.Write(folder, "actors/player.gd.uid", "uid://cplayerscrpt1\n");
        TestFolders.Write(folder, "actors/player.tscn", Scene);
        TestFolders.Write(folder, "actors/icon.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\"><rect width=\"16\" height=\"16\" fill=\"#478cbf\"/></svg>\n");
        Directory.CreateDirectory(Path.Combine(folder, "levels"));
    }

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(folder + "-home");
    }

    /// <summary>A folder holding a script, a scene and an imported texture, then the script alone, copied and pasted into
    /// another folder of the project: each copy takes a uid none of the project's other files gives, the scene copied names
    /// the copied script and texture, the script copied alone has a .uid of its own, and the originals keep theirs, so Godot
    /// opens the project finding no two files of one uid.</summary>
    [AvaloniaFact]
    public void A_folder_and_a_file_copied_in_a_godot_project_take_uids_of_their_own_as_godots_duplicate_gives_them()
    {
        // Godot has opened the project, so the texture has the .import Godot gives it.
        Assert.True(Godot(["--import"]) is { } first && first.Contains("Godot Engine", StringComparison.Ordinal), "Godot's first import wrote no log of its run");
        string icon = Path.Combine(folder, "actors", "icon.svg.import");
        Assert.True(File.Exists(icon), "Godot's import made no .import for the texture");
        Dictionary<string, string> originals = Directory.EnumerateFiles(Path.Combine(folder, "actors")).ToDictionary(f => f, File.ReadAllText);
        string iconUid = GodotUids.OfBeside(icon, originals[icon])!;

        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.Confirm = (_, _, _) => Task.FromResult(true);
            Paste(window, Path.Combine(folder, "actors"), "Copied actors to levels");
            Paste(window, Path.Combine(folder, "actors", "player.gd"), "Copied player.gd to levels");
        }
        finally
        {
            window.Close();
        }

        string copies = Path.Combine(folder, "levels", "actors");
        string script = Uid(Path.Combine(copies, "player.gd.uid"));
        string texture = GodotUids.OfBeside(Path.Combine(copies, "icon.svg.import"), File.ReadAllText(Path.Combine(copies, "icon.svg.import")))!;
        GodotFile scene = GodotFile.Parse(File.ReadAllText(Path.Combine(copies, "player.tscn")));
        string alone = Uid(Path.Combine(folder, "levels", "player.gd.uid"));
        string[] uids = [script, texture, scene.Header!.Text("uid")!, alone];
        Assert.True(uids.Distinct().Count() == 4 && !uids.Intersect(["uid://cplayerscrpt1", "uid://bplayerscene1", iconUid]).Any(),
            $"A copy keeps a uid of its original or of another copy, so Godot would find two files of one uid: {string.Join(", ", uids)}");
        Assert.Equal([("res://levels/actors/player.gd", script), ("res://levels/actors/icon.svg", null)], scene.Externals.Select(e => (e.Path, e.Uid)));
        Assert.Equal(Script, File.ReadAllText(Path.Combine(folder, "levels", "player.gd")));
        Assert.True(originals.All(o => File.ReadAllText(o.Key) == o.Value), "The copies changed a file of what they were copied from");

        string said = Godot(["--import"]) ?? string.Empty;
        Assert.True(said.Contains("Godot Engine", StringComparison.Ordinal), $"Godot's import of the copies wrote no log of its run: {said}");
        Assert.False(said.Contains("UID duplicate", StringComparison.Ordinal), $"Godot found two files of one uid once the copies were made:\n{said}");
        Assert.True(originals.All(o => File.ReadAllText(o.Key) == o.Value), "Godot's import changed a file the copies were made from, so its uid was taken for another's");
    }

    /// <summary>A folder holding a scene and a resource Godot's editor saved as bytes (the scene compressed, as the editor
    /// saves one, the resource plain), with the script and texture they name, copied and pasted into another folder of the
    /// project: Godot's import finds no two files of one uid, the originals are as they were, and the copied scene loads its
    /// script and textures from the copies. A resource of a binary format Passenger doesn't know, copied, stays as it was,
    /// the paste saying so.</summary>
    [AvaloniaFact]
    public void A_folders_binary_scene_and_resource_copied_in_a_godot_project_take_uids_of_their_own_and_load_the_copies()
    {
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Copies\"\n\n"
            + "[editor_plugins]\n\nenabled=PackedStringArray(\"res://addons/maker/plugin.cfg\")\n");
        TestFolders.Write(folder, "addons/maker/plugin.cfg", "[plugin]\n\nname=\"maker\"\ndescription=\"\"\nauthor=\"\"\nversion=\"1\"\nscript=\"plugin.gd\"\n");
        TestFolders.Write(folder, "addons/maker/plugin.gd", Maker);
        Assert.True(Godot(["--import"]) is { } first && first.Contains("Godot Engine", StringComparison.Ordinal), "Godot's first import wrote no log of its run");
        string made = Godot(["--editor", "--quit-after", "6000"], new Dictionary<string, string> { ["COPIES_MAKE"] = "1" }) ?? string.Empty;
        Assert.True(made.Contains("saved look 0", StringComparison.Ordinal) && made.Contains("saved level 0", StringComparison.Ordinal),
            $"Godot's editor saved no binary resource and scene to copy:\n{made}");
        string level = Path.Combine(folder, "actors", "level.scn");
        Assert.True(File.ReadAllBytes(level).AsSpan().StartsWith("RSCC"u8) && File.ReadAllBytes(Path.Combine(folder, "actors", "look.res")).AsSpan().StartsWith("RSRC"u8),
            "Godot's editor saved the scene uncompressed or the resource compressed, so the copy reads neither form it was to");
        Dictionary<string, byte[]> originals = Directory.EnumerateFiles(Path.Combine(folder, "actors")).ToDictionary(f => f, File.ReadAllBytes);

        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Paste(window, Path.Combine(folder, "actors"), "Copied actors to levels");
        }
        finally
        {
            window.Close();
        }

        string copies = Path.Combine(folder, "levels", "actors");
        Assert.True(File.ReadAllBytes(Path.Combine(copies, "level.scn")).AsSpan().StartsWith("RSCC"u8), "The scene's copy was not left compressed as its original was");
        Assert.True(originals.All(o => File.ReadAllBytes(o.Key).AsSpan().SequenceEqual(o.Value)), "The copy changed a file of what it was copied from");
        string said = Godot(["--import"]) ?? string.Empty;
        Assert.True(said.Contains("Godot Engine", StringComparison.Ordinal), $"Godot's import of the copies wrote no log of its run: {said}");
        Assert.False(said.Contains("UID duplicate", StringComparison.Ordinal), $"Godot found two files of one uid once the copies were made:\n{said}");
        Assert.True(originals.All(o => File.ReadAllBytes(o.Key).AsSpan().SequenceEqual(o.Value)), "Godot's import changed a file the copy was made from, so its uid was taken for another's");

        string loader = TestFolders.Write(folder + "-home", "load.gd", Loader);
        string loaded = Godot(["--script", loader]) ?? string.Empty;
        Assert.True(loaded.Contains("loaded res://levels/actors/player.gd res://levels/actors/look.res res://levels/actors/icon.svg res://levels/actors/icon.svg", StringComparison.Ordinal),
            $"Godot's load of the copied scene took its script or textures from elsewhere than the copies:\n{loaded}");
        Assert.False(loaded.Contains("ERROR", StringComparison.Ordinal) || loaded.Contains("invalid UID", StringComparison.Ordinal),
            $"Godot's load of the copied scene met a name it could not follow:\n{loaded}");

        // The resource as a Godot whose binary format is 7 would write it: its format, after the magic and four numbers.
        byte[] newer = File.ReadAllBytes(Path.Combine(folder, "actors", "look.res"));
        newer[20] = 7;
        string unknown = Path.Combine(folder, "actors", "newer.res");
        File.WriteAllBytes(unknown, newer);
        window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            GodotSceneTests.Choose(window, unknown);
            Ui.Run(window, EditorActions.Copy);
            GodotSceneTests.Choose(window, Path.Combine(folder, "levels"));
            Ui.Run(window, EditorActions.Paste);
            Ui.WaitFor(() => window.Status is { } said && said.StartsWith("Copied newer.res to levels; newer.res keeps its original's uid: Godot 4.", StringComparison.Ordinal)
                && said.EndsWith("wrote it in the binary format 7, which Passenger doesn't rewrite", StringComparison.Ordinal),
                () => $"the paste of newer.res to say it keeps its original's uid: {window.Status}");
        }
        finally
        {
            window.Close();
        }
        Assert.Equal(newer, File.ReadAllBytes(Path.Combine(folder, "levels", "newer.res")));
    }

    /// <summary>A texture a compressed scene and a plain resource Godot's editor saved name, moved in the Explorer: each
    /// names it at its new path, keeping its uid and every other byte, as one command Undo takes back whole and Redo makes
    /// again, and Godot, finding each by the path written where it knows no uid, loads the scene with the texture from its
    /// new place. A resource of a binary format Passenger doesn't know is left as it was, the move saying so.</summary>
    [AvaloniaFact]
    public void A_texture_moved_in_a_godot_project_is_followed_by_the_binary_scene_and_resource_naming_it()
    {
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Copies\"\n\n"
            + "[editor_plugins]\n\nenabled=PackedStringArray(\"res://addons/maker/plugin.cfg\")\n");
        TestFolders.Write(folder, "addons/maker/plugin.cfg", "[plugin]\n\nname=\"maker\"\ndescription=\"\"\nauthor=\"\"\nversion=\"1\"\nscript=\"plugin.gd\"\n");
        TestFolders.Write(folder, "addons/maker/plugin.gd", Maker);
        Assert.True(Godot(["--import"]) is { } first && first.Contains("Godot Engine", StringComparison.Ordinal), "Godot's first import wrote no log of its run");
        string made = Godot(["--editor", "--quit-after", "6000"], new Dictionary<string, string> { ["COPIES_MAKE"] = "1" }) ?? string.Empty;
        Assert.True(made.Contains("saved look 0", StringComparison.Ordinal) && made.Contains("saved level 0", StringComparison.Ordinal),
            $"Godot's editor saved no binary resource and scene to move a texture under:\n{made}");
        string level = Path.Combine(folder, "actors", "level.scn");
        string look = Path.Combine(folder, "actors", "look.res");
        Dictionary<string, byte[]> saved = new() { [level] = File.ReadAllBytes(level), [look] = File.ReadAllBytes(look) };
        Assert.True(saved[level].AsSpan().StartsWith("RSCC"u8) && saved[look].AsSpan().StartsWith("RSRC"u8),
            "Godot's editor saved the scene uncompressed or the resource compressed, so the move reads neither form it was to");
        string icon = Path.Combine(folder, "actors", "icon.svg");
        string moved = Path.Combine(folder, "levels", "icon.svg");
        // The resource as a Godot whose binary format is 7 would write it: its format, after the magic and four numbers.
        byte[] newer = [.. saved[look]];
        newer[20] = 7;
        File.WriteAllBytes(Path.Combine(folder, "actors", "newer.res"), newer);

        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            GodotSceneTests.Choose(window, icon);
            Ui.Run(window, EditorActions.Cut);
            GodotSceneTests.Choose(window, Path.Combine(folder, "levels"));
            Ui.Run(window, EditorActions.Paste);
            Ui.WaitFor(() => window.Status is { } said && said.StartsWith("Moved icon.svg to levels; actors/newer.res is left as it was: Godot 4.", StringComparison.Ordinal)
                && said.EndsWith("wrote it in the binary format 7, which Passenger doesn't rewrite", StringComparison.Ordinal),
                () => $"the paste of icon.svg to say it was moved, and newer.res left as it was: {window.Status}");
            AssertFollowed();
            Assert.Equal(newer, File.ReadAllBytes(Path.Combine(folder, "actors", "newer.res")));
            Assert.True(File.Exists(moved + ".import") && !File.Exists(icon + ".import"), "The texture's .import did not go with it");

            window.Confirm = (_, _, _) => Task.FromResult(true);
            GodotSceneTests.Choose(window, Path.Combine(folder, "levels"));
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => File.Exists(icon) && saved.All(s => File.ReadAllBytes(s.Key).AsSpan().SequenceEqual(s.Value)),
                () => $"Undo to move the texture back and put the scene and resource as Godot saved them: {window.Status}");
            GodotSceneTests.Choose(window, Path.Combine(folder, "actors"));
            Ui.Run(window, EditorActions.Redo);
            Ui.WaitFor(() => File.Exists(moved), () => $"Redo to move the texture again: {window.Status}");
            AssertFollowed();
        }
        finally
        {
            window.Close();
        }

        // Godot then finds each resource the scene names by the path it writes, as it does where it knows no uid.
        File.Delete(Path.Combine(folder, GodotUids.CacheFile));
        string loader = TestFolders.Write(folder + "-home", "load.gd", Loader.Replace("res://levels/actors/level.scn", "res://actors/level.scn", StringComparison.Ordinal));
        string loaded = Godot(["--script", loader]) ?? string.Empty;
        Assert.True(loaded.Contains("loaded res://actors/player.gd res://actors/look.res res://levels/icon.svg res://levels/icon.svg", StringComparison.Ordinal),
            $"Godot's load of the scene took the texture from elsewhere than where it moved:\n{loaded}");

        // Each names the texture at its new path, its uid kept, and every other byte is as Godot saved it.
        void AssertFollowed()
        {
            foreach ((string path, byte[] before) in saved)
            {
                GodotBinaryResource original = GodotBinaryResource.Read(before, out _)!;
                byte[] now = File.ReadAllBytes(path);
                IReadOnlyList<GodotBinaryExternal> named = [.. original.Externals.Select(e => e.Path == "res://actors/icon.svg" ? e with { Path = "res://levels/icon.svg" } : e)];
                Assert.True(GodotBinaryResource.Read(now, out string? unread)?.Externals.SequenceEqual(named) == true,
                    $"{Path.GetFileName(path)} does not name the texture at its new path, its uid kept: {unread ?? string.Join(", ", GodotBinaryResource.Read(now, out _)!.Externals)}");
                Assert.True(original.With(null, named).AsSpan().SequenceEqual(now), $"{Path.GetFileName(path)} changed more than the texture's path");
            }
        }
    }

    /// <summary>A file or folder chosen in the Explorer, copied and pasted into the folder levels, its status waited for.</summary>
    private void Paste(MainWindow window, string path, string done)
    {
        GodotSceneTests.Choose(window, path);
        Ui.Run(window, EditorActions.Copy);
        GodotSceneTests.Choose(window, Path.Combine(folder, "levels"));
        Ui.Run(window, EditorActions.Paste);
        Ui.WaitFor(() => window.Status == done, () => $"the paste of {Path.GetFileName(path)} to say it was copied: {window.Status}");
    }

    private static string Uid(string path)
    {
        Assert.True(File.Exists(path), $"{Path.GetFileName(path)} was not made beside the copy");
        return GodotUids.OfBeside(path, File.ReadAllText(path)) ?? throw new Xunit.Sdk.XunitException($"{Path.GetFileName(path)} gives no uid: {File.ReadAllText(path)}");
    }

    /// <summary>Headless Godot run on the project with the arguments given, in user folders of the test's own and with the
    /// variables given, until it ends: what it wrote to its log.</summary>
    private string? Godot(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? variables = null)
    {
        string log = Path.Combine(folder + "-home", $"godot-{Guid.NewGuid():N}.log");
        ProcessStartInfo start = ChildProcesses.Command(godot!.Executable, [GodotExecutables.Headless, "--path", folder, .. arguments, "--log-file", log], folder);
        foreach ((string name, string value) in GodotTests.Game.Home(folder + "-home").Concat(variables ?? new Dictionary<string, string>()))
        {
            start.Environment[name] = value;
        }
        Process process = ChildProcesses.Start(start);
        Ui.PumpUntil(() => !DotnetProcesses.Runs(process.Id), $"Godot to end {string.Join(' ', arguments)}", TimeSpan.FromMinutes(2));
        return File.Exists(log) ? File.ReadAllText(log) : null;
    }
}
