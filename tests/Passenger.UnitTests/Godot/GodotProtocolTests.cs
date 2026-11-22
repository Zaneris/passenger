using Passenger.App.Godot;
using GodotType = Passenger.Workspace.GodotType;

namespace Passenger.UnitTests.Godot;

/// <summary>Godot's remote debug protocol as Passenger reads and writes it: the messages a running Godot 4.7 game sent
/// (their bytes taken from one, after their length), its scene tree, an inspected node, its output and its monitors, and an
/// error encoded as Godot encodes one; what Passenger sends read back as sent; a property's value read from the text a
/// person types; and Passenger's live-edit ids apart from the editor's.</summary>
public class GodotProtocolTests
{
    /// <summary>The game's trees, inspected nodes, output, monitors and errors read as Godot sent them, ending
    /// nothing.</summary>
    [Fact]
    public void The_games_messages_read_as_godot_sent_them() => Cases.All(
        (nameof(Passengers_live_edit_ids_are_apart_from_every_id_the_editor_gives_its_live_edits),
            Passengers_live_edit_ids_are_apart_from_every_id_the_editor_gives_its_live_edits),
        (nameof(A_scene_tree_the_game_sent_reads_as_its_root_window_with_the_scenes_node_under_it),
            A_scene_tree_the_game_sent_reads_as_its_root_window_with_the_scenes_node_under_it),
        (nameof(A_scene_tree_nested_past_what_a_stack_holds_reads_to_its_bound_and_says_more_lies_below),
            A_scene_tree_nested_past_what_a_stack_holds_reads_to_its_bound_and_says_more_lies_below),
        (nameof(An_inspected_node_reads_its_properties_under_their_categories_and_says_which_can_be_set),
            An_inspected_node_reads_its_properties_under_their_categories_and_says_which_can_be_set),
        (nameof(An_error_reads_with_its_stack_and_is_placed_at_the_first_place_that_is_the_projects),
            An_error_reads_with_its_stack_and_is_placed_at_the_first_place_that_is_the_projects),
        (nameof(What_Passenger_sends_reads_back_as_sent_its_length_first), What_Passenger_sends_reads_back_as_sent_its_length_first),
        (nameof(Bytes_counting_more_than_they_hold_are_refused_before_anything_is_made_of_them),
            Bytes_counting_more_than_they_hold_are_refused_before_anything_is_made_of_them));

    /// <summary>A typed value reads as its type does and a number is written at its type's precision.</summary>
    [Fact]
    public void Typed_values_read_and_are_written_as_godots_variant_text_does() => Cases.All(
        (nameof(A_typed_value_reads_as_its_type_does), A_typed_value_reads_as_its_type_does),
        (nameof(A_number_is_written_as_Godots_variant_text_writes_it_at_its_types_precision),
            A_number_is_written_as_Godots_variant_text_writes_it_at_its_types_precision));

    /// <summary>A typed value reads one way in the tree and in Variables, as GDScript where a paused frame
    /// can.</summary>
    [Fact]
    public Task A_typed_value_reads_in_the_tree_and_in_variables_or_says_why_not() => Cases.AllAsync(
        (nameof(A_typed_value_reads_one_way_in_the_tree_and_in_Variables_its_type_first_a_strings_literal_as_it_says),
            A_typed_value_reads_one_way_in_the_tree_and_in_Variables_its_type_first_a_strings_literal_as_it_says));

    private void Passengers_live_edit_ids_are_apart_from_every_id_the_editor_gives_its_live_edits()
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

    private void A_scene_tree_the_game_sent_reads_as_its_root_window_with_the_scenes_node_under_it()
    {
        GodotMessage message = Message(Tree);
        Assert.Equal((GodotWords.SceneTree, GodotMessage.MainThread), (message.Name, message.Thread));
        GodotNode? root = GodotNode.Tree(message.Data);
        Assert.True(root is { Name: "root", Type: "Window", Children: [{ Name: "Main", Type: "res://Main.cs", SceneFile: "res://main.tscn", Visible: true, Children: [] }] },
            $"The tree read as {root}, so the Live Visual Tree would not show the game's nodes as they run");
        Assert.Equal(27598521817UL, root!.Children[0].Id);
    }

    /// <summary>A game can nest nodes as deep as it likes, and Godot sends its tree without a stack's limit: the tree reads to
    /// its bound on a small stack, the node there saying it has more below, and the rest of the message is read past.</summary>
    private void A_scene_tree_nested_past_what_a_stack_holds_reads_to_its_bound_and_says_more_lies_below()
    {
        const int Deep = 100_000;
        List<object?> data = [];
        for (int i = 0; i < Deep; i++)
        {
            data.AddRange([i == Deep - 1 ? 0L : 1L, $"Link{i}", "Node", (long)i, string.Empty, 0L]);
        }
        data.AddRange([0L, "After", "Node", -1L, string.Empty, 0L]);
        GodotNode? root = null;
        SmallStack.Run(() => root = GodotNode.Tree(data));
        Assert.NotNull(root);
        List<GodotNode> all = [];
        SmallStack.Run(() => all = [.. root!.All()]);
        Assert.Equal(GodotNode.DeepestNesting, all.Count);
        Assert.Equal([.. Enumerable.Range(0, GodotNode.DeepestNesting).Select(i => $"Link{i}")], all.Select(n => n.Name));
        Assert.True(all[^1] is { Children: [], Deeper: true } && all.Take(all.Count - 1).All(n => !n.Deeper), "The node at the bound did not say more lies below it, or another did");
    }

    private void An_inspected_node_reads_its_properties_under_their_categories_and_says_which_can_be_set()
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

    private void An_error_reads_with_its_stack_and_is_placed_at_the_first_place_that_is_the_projects()
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

    private void What_Passenger_sends_reads_back_as_sent_its_length_first()
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

    private void A_typed_value_reads_as_its_type_does() => Cases.Each<(GodotType Type, string Text, string Shown)>(
    [
        (GodotType.Vector2, "(1, 2.5)", "(1, 2.5)"),
        (GodotType.Color, "(1, 0.5, 0)", "(1, 0.5, 0, 1)"),
        (GodotType.Bool, "True", "true"),
        (GodotType.String, " kept as typed ", " kept as typed "),
        (GodotType.Transform2D, "1, 0, 0, 1, 100, 50", "x: (1, 0), y: (0, 1), origin: (100, 50)"),
        (GodotType.Transform3D, "basis: ((1, 0, 0), (0, 1, 0), (0, 0, 1)), origin: (1e2, -2, 3)", "basis: ((1, 0, 0), (0, 1, 0), (0, 0, 1)), origin: (100, -2, 3)"),
        (GodotType.Quaternion, "(0, 0, 0, 1)", "(0, 0, 0, 1)"),
    ], row => A_typed_value_reads_as_its_type_does(row.Type, row.Text, row.Shown));

    private void A_typed_value_reads_as_its_type_does(GodotType type, string text, string shown)
    {
        (object? value, string? whyNot) = GodotVariant.Parse(type, text);
        Assert.True(whyNot is null && GodotVariant.Shown(value) == shown, $"'{text}' as a {type} read as {GodotVariant.Shown(value)} ({whyNot})");
    }

    /// <summary>Each number as Godot's variant text writes it at the precision its type holds: a single's shortest digits for a
    /// vector's number, a double's for a float, fixed from 1e-4 to 1e15 and with an exponent beyond, zero unsigned, and
    /// Godot's words for the rest; each read back as the number it was at that precision.</summary>
    private void A_number_is_written_as_Godots_variant_text_writes_it_at_its_types_precision() => Cases.Each<(double Value, bool Single, string Written)>(
    [
        (0.30000001192092896, true, "0.3"),
        (0.30000001192092896, false, "0.30000001192092896"),
        (1234567.25, true, "1234567.2"),
        (1e10, true, "10000000000"),
        (1e15, false, "1e+15"),
        (-0.0, false, "0"),
        (double.NaN, false, "nan"),
        (double.NegativeInfinity, false, "inf_neg"),
    ], row => A_number_is_written_as_Godots_variant_text_writes_it_at_its_types_precision(row.Value, row.Single, row.Written));

    private void A_number_is_written_as_Godots_variant_text_writes_it_at_its_types_precision(double value, bool single, string written)
    {
        Assert.Equal(written, GodotText.Number(value, single));
        Assert.Equal(written, single ? GodotVariant.Shown((float)value) : GodotVariant.Shown(value));
        (object? read, string? whyNot) = GodotVariant.Parse(GodotType.Float, written);
        Assert.True(whyNot is null && read is double back && (double.IsNaN(value) ? double.IsNaN(back) : single ? (float)back == (float)value : back == value),
            $"{written} read back as {read} ({whyNot}), not {value}");
    }

    private Task A_typed_value_reads_one_way_in_the_tree_and_in_Variables_its_type_first_a_strings_literal_as_it_says() => Cases.EachAsync<(GodotType Type, string Text, string Shown)>(
    [
        (GodotType.String, "\"tock\"", "tock"),
        (GodotType.String, "\"a\" + \"b\"", "\"a\" + \"b\""),
        (GodotType.NodePath, "^\"/root/Main\"", "/root/Main"),
        (GodotType.Float, "1", "1"),
    ], row => A_typed_value_reads_one_way_in_the_tree_and_in_Variables_its_type_first_a_strings_literal_as_it_says(row.Type, row.Text, row.Shown));

    private async Task A_typed_value_reads_one_way_in_the_tree_and_in_Variables_its_type_first_a_strings_literal_as_it_says(GodotType type, string text, string shown)
    {
        (object? value, string? whyNot) = await GodotVariant.ReadAsync(type, text, script: null);
        (object? paused, string? pausedWhyNot) = await GodotVariant.ReadAsync(type, text, _ => Task.FromResult<(object?, string?)>((null, "no GDScript was to be read")));
        Assert.True(whyNot is null && pausedWhyNot is null && GodotVariant.Shown(value) == shown && GodotVariant.Shown(paused) == shown,
            $"'{text}' as a {type} read as {GodotVariant.Shown(value)} ({whyNot}) in the tree and {GodotVariant.Shown(paused)} ({pausedWhyNot}) in Variables, not {shown}");
    }

    private void Bytes_counting_more_than_they_hold_are_refused_before_anything_is_made_of_them()
    {
        byte[] array = [28, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F];
        Assert.Throws<GodotVariantException>(() => GodotVariant.Read(array));
        byte[] text = [4, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F, 0x41];
        Assert.Throws<GodotVariantException>(() => GodotVariant.Read(text));
        Assert.Throws<GodotVariantException>(() => GodotMessage.Read(GodotVariant.Write(new List<object?> { "output" })));
    }
}
