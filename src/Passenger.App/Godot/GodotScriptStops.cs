using System.Text;

namespace Passenger.App.Godot;

/// <summary>A stop of a game's GDScript as <c>debug_enter</c> tells it: whether the game may step on from it (not from a
/// script error, which only goes on), the error that stopped it ("Breakpoint" at a breakpoint or a step's end, nothing at a
/// pause), whether a script's stack is there to read (none at a pause outside every script), and the thread it is on.</summary>
public sealed record GodotBreak(bool CanStep, string Error, bool HasStack, long Thread)
{
    /// <summary>What Godot says of a stop at a breakpoint or a step's end.</summary>
    public const string AtBreakpoint = "Breakpoint";

    /// <summary>The stop a <c>debug_enter</c> message's data holds, or null for data of another shape.</summary>
    public static GodotBreak? Read(IReadOnlyList<object?> data) =>
        data is [bool step, string error, bool stack, long thread] ? new GodotBreak(step, error, stack, thread) : null;

    /// <summary>Whether a script error stopped it.</summary>
    public bool IsError => Error.Length > 0 && Error != AtBreakpoint;
}

/// <summary>A frame of a stopped script's stack: its file (a <c>res://</c> path), its line from one, and its function.</summary>
public sealed record GodotScriptFrame(string File, int Line, string Function)
{
    /// <summary>The frames a <c>stack_dump</c> message holds, the innermost first (three values each after their count of
    /// values); null for data of another shape.</summary>
    public static IReadOnlyList<GodotScriptFrame>? Stack(IReadOnlyList<object?> data)
    {
        if (data is not [long size, ..] || size < 0 || 1 + size > data.Count || size % 3 != 0)
        {
            return null;
        }
        List<GodotScriptFrame> frames = [];
        for (int at = 1; at + 2 <= size; at += 3)
        {
            if (data[at] is not string file || data[at + 1] is not long line || data[at + 2] is not string function)
            {
                return null;
            }
            frames.Add(new GodotScriptFrame(file, (int)line, function));
        }
        return frames;
    }
}

/// <summary>Where a frame's variable is: a local of its function, a member of its script's instance (<c>self</c> first), a
/// global (an autoload), or an evaluation's answer.</summary>
public enum GodotScope
{
    Local,
    Member,
    Global,
    Evaluated,
}

/// <summary>A variable of a stopped frame (<c>stack_frame_var</c>) or an evaluation's answer (<c>evaluation_return</c>): its
/// name (the expression, for an answer), where it is, its Variant type, its value (null too where it was too big to send), and
/// the class of an object's value.</summary>
public sealed record GodotScriptVariable(string Name, GodotScope Scope, GodotType Type, object? Value, string Hint)
{
    /// <summary>The variable a message's data holds, or null for data of another shape.</summary>
    public static GodotScriptVariable? Read(IReadOnlyList<object?> data) =>
        data is [string name, long scope, long type, var value, string hint] && scope is >= 0 and <= 3 && type is >= 0 and <= (long)GodotType.PackedVector4Array
            ? new GodotScriptVariable(name, (GodotScope)scope, (GodotType)type, value, hint)
            : null;

    /// <summary>The count of variables a <c>stack_frame_vars</c> message says follow it, or null for data of another shape.</summary>
    public static int? Count(IReadOnlyList<object?> data) => data is [long count] && count is >= 0 and <= int.MaxValue ? (int)count : null;

    /// <summary>Its type as GDScript names it: <c>float</c>, <c>Vector2i</c>, an object's class.</summary>
    public string TypeName => GodotScriptValues.TypeName(Type, Hint);

    /// <summary>Its value as a person reads it, a string in quotes and an object by its class and id.</summary>
    public string Shown => GodotScriptValues.Shown(Value, Hint);
}

/// <summary>GDScript's values as the debugger shows them, and what lies inside one: an array's items, a dictionary's entries,
/// a vector's components.</summary>
public static class GodotScriptValues
{
    /// <summary>A Variant type as GDScript names it, an object's by the class it is where that is known.</summary>
    public static string TypeName(GodotType type, string hint = "") => type switch
    {
        GodotType.Nil => "null",
        GodotType.Bool => "bool",
        GodotType.Int => "int",
        GodotType.Float => "float",
        GodotType.Object => hint.Length > 0 ? hint : "Object",
        GodotType.Aabb => "AABB",
        GodotType.Rid => "RID",
        GodotType.Vector2I or GodotType.Vector3I or GodotType.Vector4I or GodotType.Rect2I => type.ToString()[..^1] + "i",
        _ => type.ToString(),
    };

    /// <summary>A value as the debugger shows it: a string in quotes, an object by its class (<paramref name="hint"/>) and id.</summary>
    public static string Shown(object? value, string hint = "") => value switch
    {
        GodotObjectId id => $"<{(hint.Length > 0 ? hint : "Object")}#{id.Id}>",
        _ => GodotVariant.Shown(value, quoted: true),
    };

    /// <summary>What a value holds, each by the name a person reads it by and the GDScript that reads it from the value's own
    /// (<paramref name="expression"/>): an array's items, a dictionary's entries, a vector's or colour's components; none
    /// for a plain value, or for an object, whose properties the game is asked for.</summary>
    public static IReadOnlyList<(string Name, string? Expression, object? Value)> Inside(object? value, string? expression) => value switch
    {
        List<object?> items => [.. items.Select((item, i) => ($"[{i}]", Indexed(expression, $"{i}"), item))],
        GodotDictionary dictionary => [.. dictionary.Entries.Select(e => (GodotVariant.Shown(e.Key, quoted: true), Indexed(expression, GodotVariant.Shown(e.Key, quoted: true)), e.Value))],
        GodotMath math when Components(math.Type) is { } names && names.Length == math.Values.Count =>
            [.. names.Select((name, i) => (name, expression is null ? null : $"{expression}.{name}", IsWhole(math.Type) ? (object?)(long)math.Values[i] : math.Values[i]))],
        string[] strings => [.. strings.Select((s, i) => ($"[{i}]", Indexed(expression, $"{i}"), (object?)s))],
        byte[] => [],
        GodotPackedMath packed => [.. packed.Items.Select((item, i) => ($"[{i}]", Indexed(expression, $"{i}"), (object?)item))],
        Array packed => [.. packed.Cast<object?>().Select((item, i) => ($"[{i}]", Indexed(expression, $"{i}"), item is float f ? (double)f : item is int n ? (long)n : item))],
        _ => [],
    };

    private static string? Indexed(string? expression, string index) => expression is null ? null : $"{expression}[{index}]";

    private static bool IsWhole(GodotType type) => type is GodotType.Vector2I or GodotType.Vector3I or GodotType.Vector4I or GodotType.Rect2I;

    /// <summary>The names of a value's numbers, where GDScript names each.</summary>
    private static string[]? Components(GodotType type) => type switch
    {
        GodotType.Vector2 or GodotType.Vector2I => ["x", "y"],
        GodotType.Vector3 or GodotType.Vector3I => ["x", "y", "z"],
        GodotType.Vector4 or GodotType.Vector4I or GodotType.Quaternion => ["x", "y", "z", "w"],
        GodotType.Color => ["r", "g", "b", "a"],
        _ => null,
    };

    /// <summary>The Variant type of a value as read.</summary>
    public static GodotType TypeOf(object? value) => value switch
    {
        null => GodotType.Nil,
        bool => GodotType.Bool,
        long => GodotType.Int,
        double => GodotType.Float,
        string => GodotType.String,
        GodotName => GodotType.StringName,
        GodotNodePath => GodotType.NodePath,
        GodotMath math => math.Type,
        GodotObjectId or GodotObject => GodotType.Object,
        GodotRid => GodotType.Rid,
        GodotCallable => GodotType.Callable,
        GodotSignal => GodotType.Signal,
        GodotDictionary => GodotType.Dictionary,
        List<object?> => GodotType.Array,
        byte[] => GodotType.PackedByteArray,
        int[] => GodotType.PackedInt32Array,
        long[] => GodotType.PackedInt64Array,
        float[] => GodotType.PackedFloat32Array,
        double[] => GodotType.PackedFloat64Array,
        string[] => GodotType.PackedStringArray,
        GodotPackedMath packed => packed.Type,
        _ => GodotType.Array,
    };

    /// <summary>Why an evaluation failed, as the error it raised says it: its description (Godot's own words of the parse or
    /// the call) where it has one, the check that failed where not.</summary>
    public static string Failure(GodotError error) =>
        (error.Description.Length > 0 ? error.Description : error.Message).Replace("There was previously a parse error: ", string.Empty, StringComparison.Ordinal).TrimEnd('.');

    /// <summary>Whether a value has anything inside it to show.</summary>
    public static bool HasInside(object? value) => value is GodotObjectId || Inside(value, null).Count > 0;
}

/// <summary>GDScript breakpoints as Godot names them: a file by its <c>res://</c> path under the project's folder, and the
/// list <c>--breakpoints</c> gives a game at its start, so a breakpoint in what runs first (a <c>_ready</c>) stops it.</summary>
public static class GodotScriptBreakpoints
{
    /// <summary>The flag that sets a game's first breakpoints.</summary>
    public const string Flag = "--breakpoints";

    /// <summary>The flag that has a game go on past its script errors.</summary>
    public const string IgnoreErrors = "--ignore-error-breaks";

    /// <summary>The value of <see cref="Flag"/> for breakpoints at lines from one: each <c>res://path:line</c>, a space in a path
    /// as <c>%20</c> (the list takes none), apart by commas; null for none.</summary>
    public static string? Argument(IEnumerable<(string Resource, int Line)> breakpoints)
    {
        string[] each = [.. breakpoints.Select(b => $"{b.Resource.Replace(" ", "%20", StringComparison.Ordinal)}:{b.Line}")];
        return each.Length == 0 ? null : string.Join(',', each);
    }

    /// <summary>Whether GDScript can stop at a line of a script's text (from one): one with code on it, not blank and not a
    /// comment alone.</summary>
    public static bool IsCodeLine(string text, int line)
    {
        string[] lines = text.Split('\n');
        if (line < 1 || line > lines.Length)
        {
            return false;
        }
        string code = lines[line - 1].Trim();
        return code.Length > 0 && !code.StartsWith('#');
    }

    /// <summary>The expression a breakpoint's condition is asked as, whose value is whether it holds: the game's own truth of
    /// its value, as its <c>if</c> reads it, which no value sent back could always say (an empty array, an identity transform,
    /// a value too large to send, a null Callable).</summary>
    public static string Truth(string condition) => $"not not ({condition})";

    /// <summary>A tracepoint's message in pieces: its own words, and each <c>{expression}</c> to be read in the frame it stops
    /// in, a <c>,nq</c> after it left out; <c>{{</c> and <c>}}</c> are braces.</summary>
    public static IReadOnlyList<(string Text, bool Expression)> Pieces(string message)
    {
        List<(string, bool)> pieces = [];
        StringBuilder words = new();
        for (int at = 0; at < message.Length; at++)
        {
            char c = message[at];
            if ((c == '{' || c == '}') && at + 1 < message.Length && message[at + 1] == c)
            {
                words.Append(c);
                at++;
                continue;
            }
            int end = c == '{' ? message.IndexOf('}', at + 1) : -1;
            if (end < 0)
            {
                words.Append(c);
                continue;
            }
            if (words.Length > 0)
            {
                pieces.Add((words.ToString(), false));
                words.Clear();
            }
            string expression = message[(at + 1)..end];
            int format = expression.LastIndexOf(",nq", StringComparison.Ordinal);
            pieces.Add((format >= 0 && format == expression.Length - 3 ? expression[..format] : expression, true));
            at = end;
        }
        if (words.Length > 0)
        {
            pieces.Add((words.ToString(), false));
        }
        return pieces;
    }
}

/// <summary>What a game debugged with its GDScript starts with: its breakpoints by file and line from one, and whether its
/// script errors stop it.</summary>
public sealed record GodotScriptStart(IReadOnlyList<(string Resource, int Line)> Breakpoints, bool StopAtErrors);
