using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Godot;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Files and folders copied in the Explorer within a Godot project Godot has opened, left as Godot's own duplicate
/// leaves them: every copy given a uid of its own (a script's .uid, a scene's header, an imported file's .import), a
/// folder's scene naming the script and texture copied with it, a file's copy given its .uid, the originals as they were;
/// Godot's headless editor then opens the project with no two files of one uid. Without a Godot 4.4 or later, which keeps
/// a script's uid beside it, each test skips saying why.</summary>
public sealed class GodotExplorerCopyTests : IDisposable
{
    private const string Script = "extends Node2D\n\n\nfunc _ready() -> void:\n\tprint(\"player\")\n";

    private const string Scene = "[gd_scene load_steps=3 format=3 uid=\"uid://bplayerscene1\"]\n\n"
        + "[ext_resource type=\"Script\" uid=\"uid://cplayerscrpt1\" path=\"res://actors/player.gd\" id=\"1_s\"]\n"
        + "[ext_resource type=\"Texture2D\" path=\"res://actors/icon.svg\" id=\"2_t\"]\n\n"
        + "[node name=\"Player\" type=\"Node2D\"]\nscript = ExtResource(\"1_s\")\n\n"
        + "[node name=\"Sprite\" type=\"Sprite2D\" parent=\".\"]\ntexture = ExtResource(\"2_t\")\n";

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
        Assert.True(Godot("--import") is { } first && first.Contains("Godot Engine", StringComparison.Ordinal), "Godot's first import wrote no log of its run");
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

        string said = Godot("--import") ?? string.Empty;
        Assert.True(said.Contains("Godot Engine", StringComparison.Ordinal), $"Godot's import of the copies wrote no log of its run: {said}");
        Assert.False(said.Contains("UID duplicate", StringComparison.Ordinal), $"Godot found two files of one uid once the copies were made:\n{said}");
        Assert.True(originals.All(o => File.ReadAllText(o.Key) == o.Value), "Godot's import changed a file the copies were made from, so its uid was taken for another's");
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

    /// <summary>Godot's headless editor run on the project with the arguments given, in user folders of the test's own, until
    /// it ends: what it wrote to its log.</summary>
    private string? Godot(string arguments)
    {
        string log = Path.Combine(folder + "-home", $"godot-{Guid.NewGuid():N}.log");
        ProcessStartInfo start = ChildProcesses.Command(godot!.Executable, [GodotExecutables.Headless, "--path", folder, arguments, "--log-file", log], folder);
        foreach ((string name, string value) in GodotTests.Game.Home(folder + "-home"))
        {
            start.Environment[name] = value;
        }
        Process process = ChildProcesses.Start(start);
        Ui.PumpUntil(() => !DotnetProcesses.Runs(process.Id), $"Godot to end {arguments}", TimeSpan.FromMinutes(2));
        return File.Exists(log) ? File.ReadAllText(log) : null;
    }
}
