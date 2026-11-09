using System.Buffers.Binary;
using Passenger.Core;
using Passenger.Profiler;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>A message of Godot's remote debug protocol: its name ("scene:scene_tree", "error"), the thread it is of, and its
/// data. Each goes over the connection as an Array Variant of those three, its length in four bytes before it.</summary>
public sealed record GodotMessage(string Name, long Thread, IReadOnlyList<object?> Data)
{
    /// <summary>The thread a debugger's message goes to: the game's main one, which reads the scene messages.</summary>
    public const long MainThread = 1;

    /// <summary>The most a frame may hold, as Godot's own peer allows.</summary>
    public const int Largest = 8 << 20;

    /// <summary>The message a frame's bytes (after its length) hold.</summary>
    public static GodotMessage Read(ReadOnlySpan<byte> frame) => GodotVariant.Read(frame) is List<object?> and [string name, long thread, List<object?> data]
        ? new GodotMessage(name, thread, data)
        : throw new GodotVariantException("A message is no array of its name, thread and data");

    /// <summary>A message for the game's main thread as it goes over the connection, its length first.</summary>
    public static byte[] Frame(string name, params object?[] data) => Frame(name, MainThread, data);

    /// <summary>A message for one of the game's threads (one stopped in a script reads only its own) as it goes over the
    /// connection, its length first.</summary>
    public static byte[] Frame(string name, long thread, IReadOnlyList<object?> data)
    {
        byte[] body = GodotVariant.Write(new List<object?> { name, thread, data.ToList() });
        byte[] frame = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }
}

/// <summary>The ids under which a game's live editing knows the nodes Passenger's own live edits reach: below zero, where the
/// ids Godot's editor gives the nodes it edits live, counted up from one, never come, so neither side's are the other's.</summary>
public static class GodotLiveEditIds
{
    /// <summary>The path of Passenger's helper (<see cref="GodotAgent"/>).</summary>
    public const long Agent = -7046;

    /// <summary>The path of a node a resource is set on.</summary>
    public const long Tuned = -7047;

    /// <summary>Whether an id is one Godot's editor may give a node it edits live.</summary>
    public static bool IsEditors(long id) => id > 0;
}

/// <summary>The names of the messages Passenger sends and reads, as Godot's debugger names them.</summary>
public static class GodotWords
{
    public const string RequestSceneTree = "scene:request_scene_tree";
    public const string SceneTree = "scene:scene_tree";
    public const string InspectObjects = "scene:inspect_objects";
    public const string SetObjectProperty = "scene:set_object_property";
    public const string Output = "output";
    public const string Error = "error";
    public const string ProcessId = "set_pid";
    public const string Monitors = "performance:profile_frame";
    public const string MonitorNames = "performance:profile_names";
    public const string SkipBreakpoints = "set_skip_breakpoints";
    public const string IgnoreErrorBreaks = "set_ignore_error_breaks";
    public const string DebugEnter = "debug_enter";
    public const string DebugExit = "debug_exit";
    public const string Continue = "continue";
    public const string Step = "step";
    public const string Next = "next";
    public const string Out = "out";
    public const string Break = "break";
    public const string Breakpoint = "breakpoint";
    public const string GetStackDump = "get_stack_dump";
    public const string StackDump = "stack_dump";
    public const string GetStackFrameVars = "get_stack_frame_vars";
    public const string StackFrameVars = "stack_frame_vars";
    public const string StackFrameVar = "stack_frame_var";
    public const string Evaluate = "evaluate";
    public const string EvaluationReturn = "evaluation_return";
    public const string ReloadScripts = "reload_scripts";
    public const string Screenshot = "scene:rq_screenshot";
    public const string Screenshotted = "game_view:get_screenshot";

    /// <summary>A game on macOS's embedded display tells the id of the context it shares its layer through.</summary>
    public const string EmbeddedContext = "game_view:set_context_id";

    /// <summary>A game on the embedded display asks for a mouse mode (visible, hidden, captured, confined).</summary>
    public const string EmbeddedMouseMode = "game_view:mouse_set_mode";

    /// <summary>A game on the embedded display moved the pointer to a place of its window (<see cref="GameFit.Warped"/>).</summary>
    public const string EmbeddedWarpMouse = "game_view:warp_mouse";

    /// <summary>What the embedded display takes from the program it is shown in: its size in pixels, an input event in its
    /// codec's bytes, its state, a window event, an application notification, and the mouse mode it is to take.</summary>
    public const string EmbedWindowSize = "embed:window_size";
    public const string EmbedEvent = "embed:event";
    public const string EmbedState = "embed:ds_state";
    public const string EmbedWindowEvent = "embed:win_event";
    public const string EmbedNotification = "embed:notification";
    public const string EmbedMouseMode = "embed:mouse_set_mode";
    public const string LiveSetRoot = "scene:live_set_root";
    public const string LiveNodePath = "scene:live_node_path";
    public const string LiveCreateNode = "scene:live_create_node";
    public const string LiveNodePropertyResource = "scene:live_node_prop_res";
    public const string LiveNodeCall = "scene:live_node_call";
    public const string ProfileServers = "profiler:servers";
    public const string ProfileVisual = "profiler:visual";
    public const string FunctionSignature = "servers:function_signature";
    public const string ServersFrame = "servers:profile_frame";
    public const string ServersTotal = "servers:profile_total";
    public const string VisualFrame = "visual:profile_frame";
    public const string Hardware = "visual:hardware_info";
    public const string SuspendChanged = "scene:suspend_changed";
    public const string NextFrame = "scene:next_frame";
    public const string SelectSetup = "scene:runtime_node_select_setup";
    public const string SelectType = "scene:runtime_node_select_set_type";
    public const string SelectNodeType = "scene:runtime_node_select_set_node_type";
    public const string SelectAvoidLocked = "scene:runtime_node_select_set_avoid_locked";
    public const string SelectPreferGroup = "scene:runtime_node_select_set_prefer_group";
    public const string ClearSelection = "scene:clear_selection";
    public const string ObjectsSelected = "remote_objects_selected";
    public const string NothingSelected = "remote_nothing_selected";
    public const string MuteAudio = "scene:debug_mute_audio";

    /// <summary>The function of Godot's that takes a picture of the game, whose errors say why it took none.</summary>
    public const string ScreenshotFunction = "_msg_rq_screenshot";
}

/// <summary>What a click in a game picks under Godot's runtime node selection, as its <c>set_type</c> numbers it: nothing
/// (the game takes its own input), its 2D nodes and controls, or its 3D nodes.</summary>
public enum GodotPicking
{
    Off = 0,
    In2D = 1,
    In3D = 2,
}

/// <summary>Godot's runtime node selection as Passenger sets it up in a game: one node a click, picked where a point is within
/// eight pixels of a 2D node without a rectangle, its outline drawn in the colour given.</summary>
public static class GodotPicks
{
    /// <summary>The most pixels from a 2D node's origin a click picks it at, where it has no rectangle of its own.</summary>
    public const long GrabRadius = 8;

    /// <summary>The settings <c>runtime_node_select_setup</c> takes: one node a pick, the grab radius, and the outline's
    /// colour (red, green, blue from 0 to 1) for a 2D node's rectangle, a 3D node's box and a dragged area.</summary>
    public static GodotDictionary Settings(double red, double green, double blue)
    {
        GodotMath Colour(double alpha) => new(GodotType.Color, [red, green, blue, alpha]);
        return new GodotDictionary(
        [
            new("debugger/max_node_selection", 1L),
            new("editors/polygon_editor/point_grab_radius", GrabRadius),
            new("editors/2d/selection_rectangle_color", Colour(1)),
            new("editors/3d/selection_box_color", Colour(1)),
            new("box_selection_fill_color", Colour(0.25)),
            new("box_selection_stroke_color", Colour(0.75)),
        ]);
    }

    /// <summary>The messages that set what a click picks in a game on a Godot of a version ("4.7.2.stable…", "4.8-beta2…"):
    /// <c>set_node_type</c> from 4.8, <c>set_type</c> before it; both where the version is not known, as a running game
    /// passes over a <c>runtime_node_select_</c> message it lacks.</summary>
    public static IReadOnlyList<string> TypeMessages(string? version) =>
        System.Text.RegularExpressions.Regex.Match(version ?? string.Empty, @"^(\d+)\.(\d+)") is { Success: true } read
        && int.TryParse(read.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out int major)
        && int.TryParse(read.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture, out int minor)
            ? [major > 4 || major == 4 && minor >= 8 ? GodotWords.SelectNodeType : GodotWords.SelectType]
            : [GodotWords.SelectNodeType, GodotWords.SelectType];

    /// <summary>The node a <c>remote_objects_selected</c> message names first, or null for data of another shape.</summary>
    public static GodotObjectState? Picked(IReadOnlyList<object?> data) => GodotObjectState.Read(data) is [var first, ..] ? first : null;
}

/// <summary>A node of a running game's scene tree: its object's id, name, type (a script's class or path where it has one),
/// the scene file it was made from, whether it shows, and its children; <c>Deeper</c> where it has children past
/// <see cref="DeepestNesting"/>, which are not read.</summary>
public sealed record GodotNode(ulong Id, string Name, string Type, string SceneFile, bool Visible, IReadOnlyList<GodotNode> Children, bool Deeper = false)
{
    private const int HasVisible = 1;
    private const int VisibleFlag = 2;

    /// <summary>How deep a tree is read: past any scene made by hand, and short of what a walk of the tree reaches on a
    /// thread's stack, which bounds every such walk; a game can nest nodes as deep as it likes.</summary>
    public const int DeepestNesting = 256;

    /// <summary>The tree a <c>scene:scene_tree</c> message holds: the nodes depth first, six values each (children's count,
    /// name, type, id, scene file, view flags), read as deep as <see cref="DeepestNesting"/>; null for data of another shape.</summary>
    public static GodotNode? Tree(IReadOnlyList<object?> data)
    {
        // The nodes read whose children are still to come, outermost first, each with how many are left.
        List<(long Left, ulong Id, string Name, string Type, string Scene, bool Visible, List<GodotNode>? Children, bool Deeper)> open = [];
        for (int at = 0; at + 6 <= data.Count; at += 6)
        {
            if (data[at] is not long count || data[at + 1] is not string name || data[at + 2] is not string type
                || data[at + 3] is not long id || data[at + 4] is not string scene || data[at + 5] is not long flags)
            {
                return null;
            }
            bool visible = (flags & HasVisible) == 0 || (flags & VisibleFlag) != 0;
            bool kept = open.Count < DeepestNesting;
            open.Add((count, (ulong)id, name, type, scene, visible, kept ? [] : null, false));
            while (open[^1].Left <= 0)
            {
                (_, ulong doneId, string doneName, string doneType, string doneScene, bool doneVisible, List<GodotNode>? children, bool deeper) = open[^1];
                GodotNode done = new(doneId, doneName, doneType, doneScene, doneVisible, children ?? [], deeper);
                open.RemoveAt(open.Count - 1);
                if (open.Count == 0)
                {
                    return done;
                }
                (long Left, ulong Id, string Name, string Type, string Scene, bool Visible, List<GodotNode>? Children, bool Deeper) parent = open[^1];
                open[^1] = parent with { Left = parent.Left - 1, Deeper = parent.Deeper || children is null };
                if (children is not null)
                {
                    parent.Children?.Add(done);
                }
            }
        }
        return null;
    }

    /// <summary>The nodes from this one down, depth first.</summary>
    public IEnumerable<GodotNode> All()
    {
        Stack<GodotNode> left = new([this]);
        while (left.TryPop(out GodotNode? node))
        {
            yield return node;
            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                left.Push(node.Children[i]);
            }
        }
    }
}

/// <summary>A property of an object a game sent: its name, Variant type, hint and hint's words, usage flags, value, and the
/// category or group it comes under ("Node2D", "Transform").</summary>
public sealed record GodotProperty(string Name, GodotType Type, long Hint, string HintString, long Usage, object? Value, string Category)
{
    /// <summary>Usage flags Godot gives a property that is a heading of the ones after it.</summary>
    private const long CategoryFlag = 128;
    private const long GroupFlags = 64 | 256;
    private const long ReadOnlyFlag = 1 << 28;

    /// <summary>The hint of a value too big to send.</summary>
    private const long TooBig = 25;

    /// <summary>Whether the game takes a value set for it: one of a type text can give, not read-only, sent whole.</summary>
    public bool Settable => (Usage & ReadOnlyFlag) == 0 && Hint != TooBig && Name != "script"
        && Type is GodotType.Bool or GodotType.Int or GodotType.Float or GodotType.String or GodotType.StringName or GodotType.NodePath
            or GodotType.Vector2 or GodotType.Vector2I or GodotType.Vector3 or GodotType.Vector3I or GodotType.Vector4 or GodotType.Vector4I
            or GodotType.Rect2 or GodotType.Rect2I or GodotType.Color or GodotType.Transform2D or GodotType.Basis or GodotType.Transform3D
            or GodotType.Aabb or GodotType.Plane or GodotType.Quaternion or GodotType.Projection or GodotType.Array or GodotType.Dictionary
            or >= GodotType.PackedByteArray;

    /// <summary>The properties an object's data holds (each of name, type, hint, hint string, usage and value), its
    /// categories and groups taken as the headings of those after them rather than as properties.</summary>
    public static IReadOnlyList<GodotProperty> Of(IReadOnlyList<object?> properties)
    {
        List<GodotProperty> read = [];
        string category = string.Empty;
        string group = string.Empty;
        foreach (object? item in properties)
        {
            if (item is not (List<object?> and [string name, long type, long hint, string hintString, long usage, var value]))
            {
                continue;
            }
            if ((usage & CategoryFlag) != 0)
            {
                category = name;
                group = string.Empty;
                continue;
            }
            if ((usage & GroupFlags) != 0)
            {
                group = name;
                continue;
            }
            string under = group.Length > 0 ? $"{category}: {group}" : category;
            read.Add(new GodotProperty(name, (GodotType)type, hint, hintString, usage, value, under));
        }
        return read;
    }
}

/// <summary>An object of a running game as it was inspected: its id, class and properties.</summary>
public sealed record GodotObjectState(ulong Id, string Class, IReadOnlyList<GodotProperty> Properties)
{
    /// <summary>The objects a <c>scene:inspect_objects</c> message holds.</summary>
    public static IReadOnlyList<GodotObjectState> Read(IReadOnlyList<object?> data) =>
        [.. data.OfType<List<object?>>().Where(o => o is [long, string, List<object?>])
            .Select(o => new GodotObjectState((ulong)(long)o[0]!, (string)o[1]!, GodotProperty.Of((List<object?>)o[2]!)))];
}

/// <summary>A frame of the stack an error or warning was raised on: its file (a path, or a <c>res://</c> one), function and line.</summary>
public sealed record GodotFrame(string File, string Function, int Line);

/// <summary>An error or a warning a game raised: when (milliseconds since its start), where (file, function, line), what it
/// said and its description, and the stack it was raised on, the script's frames among the engine's.</summary>
public sealed record GodotError(long Milliseconds, string File, string Function, int Line, string Message, string Description, bool Warning,
    IReadOnlyList<GodotFrame> Frames)
{
    /// <summary>The error an <c>error</c> message holds, or null for data of another shape.</summary>
    public static GodotError? Read(IReadOnlyList<object?> data)
    {
        if (data is not [long hours, long minutes, long seconds, long milliseconds, string file, string function, long line, string message,
            string description, bool warning, long size, ..])
        {
            return null;
        }
        List<GodotFrame> frames = [];
        for (int at = 11; at + 2 < data.Count && frames.Count < size / 3; at += 3)
        {
            if (data[at] is string frameFile && data[at + 1] is string frameFunction && data[at + 2] is long frameLine)
            {
                frames.Add(new GodotFrame(frameFile, frameFunction, (int)frameLine));
            }
        }
        return new GodotError((((hours * 60) + minutes) * 60 + seconds) * 1000 + milliseconds, file, function, (int)line, message, description, warning, frames);
    }

    /// <summary>The file and line (from one) it is placed at among a project's files under <paramref name="folder"/>: the place
    /// it was raised at, else the first frame of its stack, that is the project's own (<paramref name="local"/> turning a
    /// <c>res://</c> path into one), outside the build's output in <c>.godot</c>; no file where none is (an engine error).</summary>
    public (string? Path, int Line) PlaceIn(string folder, Func<string, string?> local)
    {
        string output = Path.Combine(folder, ".godot");
        foreach ((string file, int line) in Frames.Select(f => (f.File, f.Line)).Prepend((File, Line)))
        {
            string path = file.Length > 0 ? local(file) ?? string.Empty : string.Empty;
            if (line > 0 && Path.IsPathFullyQualified(path) && Passenger.Core.PathNames.IsUnder(path, folder) && !Passenger.Core.PathNames.IsUnder(path, output))
            {
                return (path, line);
            }
        }
        return (null, 0);
    }

    /// <summary>What it says, as one line: its message, and its description where it has one.</summary>
    public string Said => Description.Length > 0 && Message.Length > 0 ? $"{Message}: {Description}" : Message.Length > 0 ? Message : Description;
}

/// <summary>A game's performance monitors as it last sent them, once a second: frames a second, the time a frame's process and
/// physics took (seconds), static memory (bytes), its counts of objects, resources, nodes and orphan nodes, and the custom
/// monitors its code added (<c>Performance.add_custom_monitor</c>), each by its id ("Game/Score") with its value, null for
/// one that gave no number.</summary>
public sealed record GodotMonitors(double Fps, double Process, double Physics, double Memory, double Objects, double Resources, double Nodes, double Orphans)
{
    /// <summary>The meter the engine's own monitors are charted under.</summary>
    public const string Engine = "Godot";

    public IReadOnlyList<(string Id, double? Value)> Custom { get; init; } = [];

    /// <summary>The monitors a <c>performance:profile_frame</c> message holds, by the place Godot's Performance gives each,
    /// the custom ones last, one a name the game last said (<see cref="Names"/>); null for data too short.</summary>
    public static GodotMonitors? Read(IReadOnlyList<object?> data, IReadOnlyList<string> custom)
    {
        double? Number(object? value) => value switch
        {
            double real => real,
            long whole => whole,
            _ => null,
        };
        double At(int i) => Number(data[i]) ?? 0;
        if (data.Count < 11)
        {
            return null;
        }
        int first = data.Count - custom.Count;
        return new GodotMonitors(At(0), At(1), At(2), At(4), At(7), At(8), At(9), At(10))
        {
            Custom = first < 11 ? [] : [.. custom.Select((id, i) => (id, Number(data[first + i])))],
        };
    }

    /// <summary>The ids of the custom monitors a <c>performance:profile_names</c> message holds, in the order the frames give
    /// their values: its first array (the types after it a newer Godot sends), else the message's own strings.</summary>
    public static IReadOnlyList<string> Names(IReadOnlyList<object?> data) =>
        data is [List<object?> names, ..] ? [.. names.Select(name => GodotVariant.Shown(name))] : [.. data.Select(name => GodotVariant.Shown(name))];

    /// <summary>The monitors as readings a metrics chart takes (the Aspire window's Stats pane takes a program's): the engine's under
    /// <see cref="Engine"/>, a custom one under its id's category (before its last /, "Custom" where it has none).</summary>
    public IReadOnlyList<Passenger.Profiler.MetricReading> Readings()
    {
        List<Passenger.Profiler.MetricReading> readings =
        [
            Reading(Engine, "Frames a second", string.Empty, Fps),
            Reading(Engine, "Frame time", "s", Process),
            Reading(Engine, "Physics time", "s", Physics),
            Reading(Engine, "Static memory", "By", Memory),
            Reading(Engine, "Objects", string.Empty, Objects),
            Reading(Engine, "Nodes", string.Empty, Nodes),
        ];
        foreach ((string id, double? value) in Custom)
        {
            if (value is double number)
            {
                int slash = id.LastIndexOf('/');
                readings.Add(Reading(slash > 0 ? id[..slash] : "Custom", slash > 0 ? id[(slash + 1)..] : id, string.Empty, number));
            }
        }
        return readings;

        static Passenger.Profiler.MetricReading Reading(string meter, string name, string unit, double value) =>
            new(meter, name, unit, Passenger.Profiler.MetricKind.Gauge, string.Empty, value, null);
    }

    /// <summary>The monitors in a line of words: "60 FPS · 1.2 ms process · 41.7 MB · 1,500 objects · 2 nodes".</summary>
    public string Said => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{Fps:0} FPS · {Process * 1000:0.0} ms process · {Sizes.Said((long)Memory)} · {Objects:N0} objects · {Nodes:N0} nodes");
}

/// <summary>What Godot's own profilers send while turned on (<see cref="GodotWords.ProfileServers"/>, <see cref="GodotWords.ProfileVisual"/>):
/// a script function's signature by the number its frames name it by, each frame's times (seconds, as Godot sends them, read
/// as milliseconds), the renderer's stages, and the hardware it draws with.</summary>
public static class GodotProfile
{
    /// <summary>The number and signature a <c>servers:function_signature</c> message holds ("res://wander.gd::10::wander", 2).</summary>
    public static (int Number, string Signature)? Signature(IReadOnlyList<object?> data) =>
        data is [var name, long number] && Text(name) is { } signature ? ((int)number, signature) : null;

    /// <summary>The frame a <c>servers:profile_frame</c> message holds: its number, frame, process, physics, physics step and
    /// script times, then each server's functions' times (the count of servers, each its name, the count of values after it,
    /// and each function's name and time), then each script function's number, calls and own, whole and Godot-API times (the
    /// count of values, five a function); placed nowhere yet. Null for data of another shape.</summary>
    public static GameFrame? Frame(IReadOnlyList<object?> data)
    {
        if (data.Count < 8 || data[0] is not long number || data[6] is not long serverCount)
        {
            return null;
        }
        List<ServerWork> servers = [];
        int at = 7;
        for (long s = 0; s < serverCount; s++)
        {
            if (at + 1 >= data.Count || Text(data[at]) is not { } server || data[at + 1] is not long values || values < 0 || at + 2 + values > data.Count)
            {
                return null;
            }
            for (int v = at + 2; v + 1 < at + 2 + values; v += 2)
            {
                servers.Add(new ServerWork(server, Text(data[v]) ?? string.Empty, Ms(data[v + 1])));
            }
            at += 2 + (int)values;
        }
        if (at >= data.Count || data[at] is not long scriptValues || scriptValues < 0 || at + 1 + scriptValues > data.Count)
        {
            return null;
        }
        List<ScriptWork> scripts = [];
        for (int v = at + 1; v + 4 < at + 1 + scriptValues; v += 5)
        {
            if (data[v] is long function && data[v + 1] is long calls)
            {
                scripts.Add(new ScriptWork((int)function, (int)calls, Ms(data[v + 2]), Ms(data[v + 3]), Ms(data[v + 4])));
            }
        }
        return new GameFrame((int)number, 0, Ms(data[1]), Ms(data[2]), Ms(data[3]), Ms(data[4]), Ms(data[5]), servers, scripts);
    }

    /// <summary>The renderer's stages a <c>visual:profile_frame</c> message holds: its frame's number in the renderer's own count,
    /// the count of values after, and each stage's name and CPU and GPU times (milliseconds, as Godot sends them).</summary>
    public static IReadOnlyList<RenderArea> Rendered(IReadOnlyList<object?> data)
    {
        if (data is not [_, long values, ..] || values < 0 || 2 + values > data.Count)
        {
            return [];
        }
        List<RenderArea> areas = [];
        for (int v = 2; v + 2 < 2 + values; v += 3)
        {
            areas.Add(new RenderArea(Text(data[v]) ?? string.Empty, Number(data[v + 1]), Number(data[v + 2])));
        }
        return areas;
    }

    /// <summary>The GPU a <c>visual:hardware_info</c> message names (after the CPU); empty for a game drawing with none.</summary>
    public static string Gpu(IReadOnlyList<object?> data) => data is [_, var gpu, ..] && Text(gpu) is { } named ? named : string.Empty;

    private static string? Text(object? value) => value switch
    {
        string text => text,
        GodotName name => name.Name,
        _ => null,
    };

    private static double Number(object? value) => value switch
    {
        double real => real,
        float single => single,
        long whole => whole,
        _ => 0,
    };

    /// <summary>Seconds as Godot sends them, in milliseconds.</summary>
    private static double Ms(object? value) => Number(value) * 1000;
}
