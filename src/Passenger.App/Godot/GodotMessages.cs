using System.Buffers.Binary;

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
    public static byte[] Frame(string name, params object?[] data)
    {
        byte[] body = GodotVariant.Write(new List<object?> { name, MainThread, data.ToList() });
        byte[] frame = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }
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
    public const string SkipBreakpoints = "set_skip_breakpoints";
    public const string IgnoreErrorBreaks = "set_ignore_error_breaks";
    public const string DebugEnter = "debug_enter";
    public const string Continue = "continue";
}

/// <summary>A node of a running game's scene tree: its object's id, name, type (a script's class or path where it has one),
/// the scene file it was made from, whether it shows, and its children.</summary>
public sealed record GodotNode(ulong Id, string Name, string Type, string SceneFile, bool Visible, IReadOnlyList<GodotNode> Children)
{
    private const int HasVisible = 1;
    private const int VisibleFlag = 2;

    /// <summary>The tree a <c>scene:scene_tree</c> message holds: the nodes depth first, six values each (children's count,
    /// name, type, id, scene file, view flags); null for data of another shape.</summary>
    public static GodotNode? Tree(IReadOnlyList<object?> data)
    {
        int at = 0;
        return data.Count >= 6 ? Read(data, ref at) : null;
    }

    private static GodotNode? Read(IReadOnlyList<object?> data, ref int at)
    {
        if (at + 6 > data.Count || data[at] is not long count || data[at + 1] is not string name || data[at + 2] is not string type
            || data[at + 3] is not long id || data[at + 4] is not string scene || data[at + 5] is not long flags)
        {
            return null;
        }
        at += 6;
        List<GodotNode> children = [];
        for (long i = 0; i < count; i++)
        {
            if (Read(data, ref at) is not { } child)
            {
                return null;
            }
            children.Add(child);
        }
        bool visible = (flags & HasVisible) == 0 || (flags & VisibleFlag) != 0;
        return new GodotNode((ulong)id, name, type, scene, visible, children);
    }

    /// <summary>The nodes from this one down, depth first.</summary>
    public IEnumerable<GodotNode> All() => Children.SelectMany(c => c.All()).Prepend(this);
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
            or GodotType.Rect2 or GodotType.Rect2I or GodotType.Color;

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
    public (string? Path, int Line) PlaceIn(string folder, Func<string, string> local)
    {
        string output = Path.Combine(folder, ".godot");
        foreach ((string file, int line) in Frames.Select(f => (f.File, f.Line)).Prepend((File, Line)))
        {
            string path = file.Length > 0 ? local(file) : string.Empty;
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
/// physics took (seconds), static memory (bytes), and its counts of objects, resources, nodes and orphan nodes.</summary>
public sealed record GodotMonitors(double Fps, double Process, double Physics, double Memory, double Objects, double Resources, double Nodes, double Orphans)
{
    /// <summary>The monitors a <c>performance:profile_frame</c> message holds, by the place Godot's Performance gives each;
    /// null for data too short.</summary>
    public static GodotMonitors? Read(IReadOnlyList<object?> data)
    {
        double At(int i) => data[i] switch
        {
            double real => real,
            long whole => whole,
            _ => 0,
        };
        return data.Count < 11 ? null : new GodotMonitors(At(0), At(1), At(2), At(4), At(7), At(8), At(9), At(10));
    }

    /// <summary>The monitors in a line of words: "60 FPS · 1.2 ms process · 41.7 MB · 1,500 objects · 2 nodes".</summary>
    public string Said => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{Fps:0} FPS · {Process * 1000:0.0} ms process · {Memory / (1024 * 1024):0.0} MB · {Objects:N0} objects · {Nodes:N0} nodes");
}
