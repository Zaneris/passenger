using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>Godot's remote debug protocol as Passenger reads and writes it: the messages a running Godot 4.7 game sent
/// (their bytes taken from one, after their length), its scene tree, an inspected node, its output and its monitors, and an
/// error encoded as Godot encodes one; what Passenger sends read back as sent; a property's value read from the text a
/// person types; and Passenger's live-edit ids apart from the editor's.</summary>
public class GodotProtocolTests
{
    [Fact]
    public void Passengers_live_edit_ids_are_apart_from_every_id_the_editor_gives_its_live_edits()
    {
        long[] ours = [GodotLiveEditIds.Agent, GodotLiveEditIds.Tuned];

        Assert.Equal(ours.Length, ours.Distinct().Count());
        Assert.DoesNotContain(ours, GodotLiveEditIds.IsEditors);
        // The editor counts its ids up from one, as far as an int goes.
        Assert.True(GodotLiveEditIds.IsEditors(1) && GodotLiveEditIds.IsEditors(int.MaxValue) && ours.All(id => id >= int.MinValue),
            "Passenger's ids are to fit the int the game reads them as, below every id the editor gives");
    }

    private const string Tree =
        "HAAAAAMAAAAEAAAAEAAAAHNjZW5lOnNjZW5lX3RyZWUCAAAAAQAAABwAAAAMAAAAAgAAAAEAAAAEAAAABAAAAHJvb3QEAAAABgAAAFdpbmRvdwAA"
        + "AgABAM0FAFIGAAAABAAAAAAAAAACAAAAAAAAAAIAAAAAAAAABAAAAAQAAABNYWluBAAAAA0AAAByZXM6Ly9NYWluLmNzAAAAAgABANkFAG0GAAAA"
        + "BAAAAA8AAAByZXM6Ly9tYWluLnRzY24AAgAAAAAAAAA=";

    private const string Inspected =
        "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";

    private const string Output =
        "HAAAAAMAAAAEAAAABgAAAG91dHB1dAAAAgAAAAEAAAAcAAAAAgAAACIAAAABAAAACQAAAGZyYW1lIDEwAAAAAB4AAAABAAAAAAAAAA==";

    private const string Monitors =
        "HAAAAAMAAAAEAAAAGQAAAHBlcmZvcm1hbmNlOnByb2ZpbGVfZnJhbWUAAAACAAAAAQAAABwAAAA7AAAAAwAAAAAAgD8DAAAAAAAAAAMAAAAAAAAA"
        + "AwAAAAAAAAADAAEAAAAAeILlg0EDAAEAAAAAED99hEEDAAAAAACARQMAAAAAgLtEAwAAAAAAgD8DAAAAAAAAQAMAAAAAAAAAAwAAAAAAAAADAAAA"
        + "AAAAAAMAAAAAAAAAAwAAAAAAAAADAAAAAAAAAAMAAAAAAAAAAwAAAAAAAAADAAAAAAAAAAMAAAAAAAAAAwAAAAAAAAADAAAAAAAAAAMAAAAAAAAA"
        + "AwAAAAAAAAADAAAAAAAAAAMAAAAAAAAAAwAAAAAAAAADAAAAAAAAAAMAAAAAAAAAAwAAAAAAAAADAAAAAAAAAAMAAAAAAAAAAwAAAAAAAAADAAAA"
        + "AAAAAAMAAAAAAAAAAwAAAAAAAAADAAAAAAAAAAMAAAAAAAAAAwAAAAAAAAADAAAAAAAAAAMAAAAAAAAAAwAAAAAAAAADAAAAAAAAAAMAAAAAAAAA"
        + "AwAAAAAAAAADAAAAAAAAAAMAAAAAAAAAAwAAAAAAAAADAAAAAAAAAAMAAAAAAAAAAwAAAAAAAAADAAAAAAAAAAMAAAAAAAAAAwAAAAAAAAADAAAA"
        + "AAAAAAMAAAAAAAAAAwAAAAAAAAADAAAAAAAAAAMAAAAAAAAA";

    /// <summary>An error as Godot encodes one, a script's push of it at Main.cs:19 under GodotSharp's frame.</summary>
    private const string Error =
        "HAAAAAMAAAAEAAAABQAAAGVycm9yAAAAAgAAAAEAAAAcAAAAFAAAAAIAAAAAAAAAAgAAAAAAAAACAAAAAAAAAAIAAAAuAgAABAAAAA0AAAByZXM6"
        + "Ly9NYWluLmNzAAAABAAAABoAAAB2b2lkIE1haW4uX1Byb2Nlc3MoZG91YmxlKQAAAgAAABMAAAAEAAAAEQAAAFNwaW5uZXIgZmVsbCBvdmVyAAAA"
        + "BAAAAAAAAAABAAAAAAAAAAIAAAAJAAAABAAAAD4AAAAvcm9vdC9nb2RvdC9tb2R1bGVzL21vbm8vZ2x1ZS9Hb2RvdFNoYXJwL0dvZG90U2hhcnAv"
        + "Q29yZS9HRC5jcwAABAAAAB8AAAB2b2lkIEdvZG90LkdELlB1c2hFcnJvcihzdHJpbmcpAAIAAABuAQAABAAAABYAAAAvZ2FtZXMvc3Bpbm5lci9N"
        + "YWluLmNzAAAEAAAAGgAAAHZvaWQgTWFpbi5fUHJvY2Vzcyhkb3VibGUpAAACAAAAEwAAAAQAAABSAAAAL3Jvb3QvZ29kb3QvbW9kdWxlcy9tb25v"
        + "L2dsdWUvR29kb3RTaGFycC9Hb2RvdFNoYXJwL0dlbmVyYXRlZC9Hb2RvdE9iamVjdHMvTm9kZS5jcwAABAAAACgAAABib29sIEdvZG90Lk5vZGUu"
        + "SW52b2tlR29kb3RDbGFzc01ldGhvZCgpAgAAAF4KAAA=";

    private static GodotMessage Message(string base64) => GodotMessage.Read(Convert.FromBase64String(base64));

    [Fact]
    public void A_scene_tree_the_game_sent_reads_as_its_root_window_with_the_scenes_node_under_it()
    {
        GodotMessage message = Message(Tree);
        Assert.Equal((GodotWords.SceneTree, GodotMessage.MainThread), (message.Name, message.Thread));
        GodotNode? root = GodotNode.Tree(message.Data);
        Assert.True(root is { Name: "root", Type: "Window", Children: [{ Name: "Main", Type: "res://Main.cs", SceneFile: "res://main.tscn", Visible: true, Children: [] }] },
            $"The tree read as {root}, so the Live Visual Tree would not show the game's nodes as they run");
        Assert.Equal(27598521817UL, root!.Children[0].Id);
    }

    [Fact]
    public void An_inspected_node_reads_its_properties_under_their_categories_and_says_which_can_be_set()
    {
        GodotObjectState node = Assert.Single(GodotObjectState.Read(Message(Inspected).Data));
        Assert.Equal((27598521817UL, "Node"), (node.Id, node.Class));
        GodotProperty greeting = node.Properties.Single(p => p.Name == "Greeting");
        Assert.True(greeting is { Type: GodotType.String, Value: "frame", Category: "Main", Settable: true }, $"The script's Greeting read as {greeting}");
        Assert.True(node.Properties.Single(p => p.Name == "process_mode") is { Type: GodotType.Int, Value: 0L, Category: "Node: Process", Settable: true },
            "process_mode should read as a settable whole number under Node's Process group");
        Assert.False(node.Properties.Single(p => p.Name == "Node/multiplayer_authority").Settable, "A read-only property was offered to be set");
        Assert.False(node.Properties.Single(p => p.Name == "script").Settable, "The node's script was offered to be set from text");
        Assert.Equal("/root/Main", GodotVariant.Shown(node.Properties.Single(p => p.Name == "Node/path").Value));
        Assert.DoesNotContain(node.Properties, p => p.Name is "Main" or "Node" or "Process" or "Thread Group");
    }

    [Fact]
    public void Output_strings_lose_the_zero_Godot_ends_each_with_and_monitors_read_by_their_place()
    {
        GodotMessage output = Message(Output);
        Assert.True(output is { Name: GodotWords.Output, Data: [string[] lines, int[] kinds] } && lines.SequenceEqual(["frame 10"]) && kinds.SequenceEqual([0]),
            $"The game's output read as {GodotVariant.Shown(output.Data.ToList())}");
        GodotMonitors? monitors = GodotMonitors.Read(Message(Monitors).Data, []);
        Assert.True(monitors is { Fps: 1, Memory: 41726031, Objects: 1500, Resources: 1, Nodes: 2, Orphans: 0, Custom: [] }, $"The monitors read as {monitors}");
        Assert.StartsWith("1 FPS · 0.0 ms process · 39.8 MB · 1,500 objects · 2 nodes", monitors!.Said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_games_custom_monitors_are_named_as_it_said_them_and_charted_under_their_category()
    {
        IReadOnlyList<string> names = GodotMonitors.Names([new List<object?> { new GodotName("Game/Score"), "Speed" }, new List<object?> { 0L, 0L }]);
        Assert.Equal(["Game/Score", "Speed"], names);
        List<object?> frame = [60.0, 0.016, 0.002, .. Enumerable.Repeat<object?>(0L, 20), 42L, "not a number"];
        GodotMonitors? monitors = GodotMonitors.Read(frame, names);
        Assert.True(monitors is { Fps: 60, Custom: [("Game/Score", 42), ("Speed", null)] }, $"The monitors read as {monitors} with {string.Join(", ", monitors?.Custom ?? [])}");
        Assert.Equal([("Godot", "Frames a second", 60.0), ("Godot", "Frame time", 0.016), ("Game", "Score", 42.0)],
            monitors!.Readings().Where(r => r.Instrument is "Frames a second" or "Frame time" or "Score").Select(r => (r.Meter, r.Instrument, r.Value)));
        Assert.DoesNotContain(monitors.Readings(), r => r.Instrument == "Speed");
    }

    [Fact]
    public void An_error_reads_with_its_stack_and_is_placed_at_the_first_place_that_is_the_projects()
    {
        GodotError? error = GodotError.Read(Message(Error).Data);
        Assert.True(error is { File: "res://Main.cs", Function: "void Main._Process(double)", Line: 19, Message: "Spinner fell over", Warning: false, Milliseconds: 558 },
            $"The error read as {error}");
        Assert.Equal(3, error!.Frames.Count);
        // The game's folder as this system names one, the frames' paths taken there.
        string root = Path.Combine(Path.GetTempPath(), "spinner");
        string Local(string path) => path.StartsWith("res://", StringComparison.Ordinal) ? Path.Combine(root, path[6..])
            : path.StartsWith("/games/spinner/", StringComparison.Ordinal) ? Path.Combine(root, path["/games/spinner/".Length..].Replace('/', Path.DirectorySeparatorChar))
            : path;
        string script = Path.Combine(root, "Main.cs");
        Assert.Equal((script, 19), error.PlaceIn(root, Local));
        GodotError engine = error with { File = "core/debugger/remote_debugger.cpp", Frames = [] };
        Assert.Equal((null, 0), engine.PlaceIn(root, Local));
        GodotError generated = error with { File = string.Empty, Frames = [new GodotFrame("/games/spinner/.godot/mono/temp/obj/Main.g.cs", "x", 3), error.Frames[1]] };
        Assert.Equal((script, 19), generated.PlaceIn(root, Local));
    }

    [Fact]
    public void What_Passenger_sends_reads_back_as_sent_its_length_first()
    {
        object?[] data = [27598521817L, "Greeting", "hello", true, 4_000_000_000L, 0.5, 0.1, new GodotName("ready"),
            new GodotNodePath(["root", "Main"], ["position"], true), new GodotMath(GodotType.Vector2, [1, 2.5]), new GodotMath(GodotType.Vector3I, [1, -2, 3]),
            new GodotMath(GodotType.Color, [0.5, 0.25, 1, 1]), new List<object?> { 1L, "two" }, null];
        byte[] frame = GodotMessage.Frame(GodotWords.SetObjectProperty, data);
        Assert.Equal(frame.Length - 4, BitConverter.ToInt32(frame, 0));
        GodotMessage read = GodotMessage.Read(frame.AsSpan(4));
        Assert.Equal(GodotWords.SetObjectProperty, read.Name);
        Assert.Equal(data.Length, read.Data.Count);
        for (int i = 0; i < data.Length; i++)
        {
            string sent = GodotVariant.Shown(data[i] is int whole ? (long)whole : data[i]);
            Assert.True(sent == GodotVariant.Shown(read.Data[i]), $"Sent {sent}, read back {GodotVariant.Shown(read.Data[i])}");
        }
    }

    [Theory]
    [InlineData(GodotType.Vector2, "(1, 2.5)", "(1, 2.5)")]
    [InlineData(GodotType.Vector2I, "3,4", "(3, 4)")]
    [InlineData(GodotType.Color, "(1, 0.5, 0)", "(1, 0.5, 0, 1)")]
    [InlineData(GodotType.Int, " 42 ", "42")]
    [InlineData(GodotType.Bool, "True", "true")]
    [InlineData(GodotType.NodePath, "/root/Main:position", "/root/Main:position")]
    [InlineData(GodotType.String, " kept as typed ", " kept as typed ")]
    [InlineData(GodotType.Transform2D, "x: (1, 0), y: (0, 1), origin: (100, 50.5)", "x: (1, 0), y: (0, 1), origin: (100, 50.5)")]
    [InlineData(GodotType.Transform2D, "1, 0, 0, 1, 100, 50", "x: (1, 0), y: (0, 1), origin: (100, 50)")]
    [InlineData(GodotType.Transform3D, "basis: ((1, 0, 0), (0, 1, 0), (0, 0, 1)), origin: (1e2, -2, 3)", "basis: ((1, 0, 0), (0, 1, 0), (0, 0, 1)), origin: (100, -2, 3)")]
    [InlineData(GodotType.Aabb, "position: (0, 0, 0), size: (2, 2, 2)", "position: (0, 0, 0), size: (2, 2, 2)")]
    [InlineData(GodotType.Plane, "normal: (0, 1, 0), d: 4", "normal: (0, 1, 0), d: 4")]
    [InlineData(GodotType.Quaternion, "(0, 0, 0, 1)", "(0, 0, 0, 1)")]
    public void A_typed_value_reads_as_its_type_does(GodotType type, string text, string shown)
    {
        (object? value, string? whyNot) = GodotVariant.Parse(type, text);
        Assert.True(whyNot is null && GodotVariant.Shown(value) == shown, $"'{text}' as a {type} read as {GodotVariant.Shown(value)} ({whyNot})");
    }

    [Theory]
    [InlineData(GodotType.Vector2, "1")]
    [InlineData(GodotType.Vector2I, "1.5, 2")]
    [InlineData(GodotType.Int, "many")]
    [InlineData(GodotType.Transform3D, "1, 0, 0")]
    public void A_typed_value_its_type_cannot_read_says_what_it_takes(GodotType type, string text)
    {
        (object? value, string? whyNot) = GodotVariant.Parse(type, text);
        Assert.True(value is null && whyNot is { Length: > 0 }, $"'{text}' as a {type} read as {GodotVariant.Shown(value)}, where it should say what it takes");
    }

    [Theory]
    [InlineData(GodotType.String, "\"tock\"", "tock")]
    [InlineData(GodotType.String, "tock", "tock")]
    [InlineData(GodotType.String, "\"say \\\"hi\\\"\\n\"", "say \"hi\"\n")]
    [InlineData(GodotType.String, "\"a\" + \"b\"", "\"a\" + \"b\"")]
    [InlineData(GodotType.StringName, "&\"ready\"", "ready")]
    [InlineData(GodotType.NodePath, "^\"/root/Main\"", "/root/Main")]
    [InlineData(GodotType.Vector2, "(1, 2)", "(1, 2)")]
    [InlineData(GodotType.Float, "1", "1")]
    public async Task A_typed_value_reads_one_way_in_the_tree_and_in_Variables_its_type_first_a_strings_literal_as_it_says(GodotType type, string text, string shown)
    {
        (object? value, string? whyNot) = await GodotVariant.ReadAsync(type, text, script: null);
        (object? paused, string? pausedWhyNot) = await GodotVariant.ReadAsync(type, text, _ => Task.FromResult<(object?, string?)>((null, "no GDScript was to be read")));
        Assert.True(whyNot is null && pausedWhyNot is null && GodotVariant.Shown(value) == shown && GodotVariant.Shown(paused) == shown,
            $"'{text}' as a {type} read as {GodotVariant.Shown(value)} ({whyNot}) in the tree and {GodotVariant.Shown(paused)} ({pausedWhyNot}) in Variables, not {shown}");
    }

    [Fact]
    public async Task A_typed_value_its_type_cannot_read_is_read_as_GDScript_where_a_paused_frame_can_and_refused_saying_both_where_neither_reads_it()
    {
        Assert.Equal(1.5, (await GodotVariant.ReadAsync(GodotType.Int, "speed * 0.5", text => Task.FromResult<(object?, string?)>((text == "speed * 0.5" ? 1.5 : null, null)))).Value);
        (object? running, string? whyNot) = await GodotVariant.ReadAsync(GodotType.Int, "speed * 0.5", script: null);
        Assert.True(running is null && whyNot == "a whole number", $"With no frame to read GDScript in, 'speed * 0.5' as an int read as {running} ({whyNot})");
        (object? unread, string? said) = await GodotVariant.ReadAsync(GodotType.Int, "nope", _ => Task.FromResult<(object?, string?)>((null, "Identifier \"nope\" not declared")));
        Assert.True(unread is null && said == "a whole number, or GDScript that reads as one (Identifier \"nope\" not declared)", $"'nope' as an int read as {unread} ({said})");
    }

    [Fact]
    public void Bytes_counting_more_than_they_hold_are_refused_before_anything_is_made_of_them()
    {
        byte[] array = [28, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F];
        Assert.Throws<GodotVariantException>(() => GodotVariant.Read(array));
        byte[] text = [4, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F, 0x41];
        Assert.Throws<GodotVariantException>(() => GodotVariant.Read(text));
        Assert.Throws<GodotVariantException>(() => GodotMessage.Read(GodotVariant.Write(new List<object?> { "output" })));
    }
}
