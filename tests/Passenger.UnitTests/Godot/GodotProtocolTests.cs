using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>Godot's remote debug protocol as Passenger reads and writes it: the messages a running Godot 4.7 game sent
/// (their bytes taken from one, after their length), its scene tree, an inspected node, its output and its monitors, and an
/// error encoded as Godot encodes one; what Passenger sends read back as sent; and a property's value read from the text a
/// person types.</summary>
public class GodotProtocolTests
{
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
        GodotMonitors? monitors = GodotMonitors.Read(Message(Monitors).Data);
        Assert.True(monitors is { Fps: 1, Memory: 41726031, Objects: 1500, Resources: 1, Nodes: 2, Orphans: 0 }, $"The monitors read as {monitors}");
        Assert.StartsWith("1 FPS · 0.0 ms process · 39.8 MB · 1,500 objects · 2 nodes", monitors!.Said, StringComparison.Ordinal);
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
