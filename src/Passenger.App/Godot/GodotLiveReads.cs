using System.Globalization;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>What a running game is asked of a script: its <c>res://</c> path, the node chosen (its id, 0 for none), and the
/// members whose values are wanted.</summary>
public sealed record GodotScriptAsk(string Script, ulong Chosen, IReadOnlyList<string> Names);

/// <summary>A member's value as Passenger's helper read it: the value, or the words it gave for one it does not send (an
/// object, a container's first items), and a node path's node.</summary>
public readonly record struct GodotMemberValue(object? Value, string? Words = null, string? Node = null, bool IsPath = false);

/// <summary>What a script asked of came to: how many nodes run it (or a script it is the base of), the node read (0 for
/// none: no node runs it, or several do and none of them is chosen), that node's name, and each member read by its name;
/// a member the node does not hold (a static one, one of a type Godot does not know) is left out.</summary>
public sealed record GodotScriptRead(int Count, ulong Node, string NodeName, IReadOnlyDictionary<string, GodotMemberValue> Values)
{
    /// <summary>The answers the helper's <c>values</c> call gave, one for each ask in order: each its count, node id, node
    /// name and, for each name asked, an empty list for a member not held, [value], [words, 1] for an object or a
    /// container, or [path, 2, node] for a node path and the node it reaches ("" for none). Null for an answer of another
    /// shape.</summary>
    public static IReadOnlyList<GodotScriptRead>? Read(IReadOnlyList<object?> said, IReadOnlyList<GodotScriptAsk> asks)
    {
        if (said is not [List<object?> answers, ..] || answers.Count != asks.Count)
        {
            return null;
        }
        List<GodotScriptRead> read = [];
        for (int i = 0; i < asks.Count; i++)
        {
            if (answers[i] is not (List<object?> and [long count, long node, string name, List<object?> entries]) || entries.Count != asks[i].Names.Count)
            {
                return null;
            }
            Dictionary<string, GodotMemberValue> values = [];
            for (int e = 0; e < entries.Count; e++)
            {
                GodotMemberValue? value = entries[e] switch
                {
                    List<object?> and [var plain] => new GodotMemberValue(plain),
                    List<object?> and [string words, 1L] => new GodotMemberValue(null, words),
                    List<object?> and [var path, 2L, string reached] => new GodotMemberValue(path, Node: reached, IsPath: true),
                    _ => null,
                };
                if (value is { } held)
                {
                    values[asks[i].Names[e]] = held;
                }
            }
            read.Add(new GodotScriptRead((int)count, (ulong)node, name, values));
        }
        return read;
    }
}

/// <summary>A running game's values as the code's hints say them: numbers rounded as Godot's inspector steps them (to a
/// thousandth), a vector's parts in parentheses, a string in quotes, a node path with the node it reaches.</summary>
public static class GodotLiveWords
{
    /// <summary>The longest hint; a longer one is cut.</summary>
    public const int Longest = 60;

    /// <summary>A member's hint: <c>speed: 312.5</c>.</summary>
    public static string Hint(string member, GodotMemberValue read) => Cut($"{member}: {Of(read)}");

    /// <summary>What a value read says.</summary>
    public static string Of(GodotMemberValue read) => read switch
    {
        { Words: { } words } => words,
        { IsPath: true } => $"{Brief(read.Value)} → {(read.Node is { Length: > 0 } node ? node : "no node")}",
        _ => Brief(read.Value),
    };

    /// <summary>A value in short: a real number to a thousandth (a smaller one to three figures), a vector's parts so, a
    /// string or a name quoted, the rest as Godot's inspector writes them.</summary>
    public static string Brief(object? value) => value switch
    {
        double real => Number(real),
        string text => $"\"{text}\"",
        GodotNodePath path => path.ToString(),
        GodotMath { Type: GodotType.Vector2 or GodotType.Vector3 or GodotType.Vector4 or GodotType.Quaternion or GodotType.Rect2 or GodotType.Color } math =>
            $"({string.Join(", ", math.Values.Select(Number))})",
        _ => GodotVariant.Shown(value, quoted: true),
    };

    /// <summary>A real number rounded to a thousandth, a whole one without a fraction; one smaller than that to three figures.</summary>
    public static string Number(double value)
    {
        if (!double.IsFinite(value))
        {
            return GodotText.Number(value);
        }
        double rounded = Math.Round(value, 3);
        if (rounded == 0)
        {
            return value == 0 ? "0" : value.ToString("G3", CultureInfo.InvariantCulture);
        }
        return GodotText.Number(rounded);
    }

    /// <summary>A value's numbers, for a chart of it: a number, true as 1 and false as 0, a vector's or colour's parts; null
    /// for a value of no numbers, or of more than four.</summary>
    public static double[]? Parts(object? value) => value switch
    {
        long whole => [whole],
        double real => [real],
        bool flag => [flag ? 1 : 0],
        GodotMath { Values.Count: <= 4 } math => [.. math.Values],
        _ => null,
    };

    /// <summary>The names of a value's parts, as Godot names them: x, y, z, w; a colour's r, g, b, a; a rectangle's x, y,
    /// width, height; a plane's normal and its d.</summary>
    public static IReadOnlyList<string> PartNames(object? value) => value switch
    {
        GodotMath { Type: GodotType.Color } => ["r", "g", "b", "a"],
        GodotMath { Type: GodotType.Rect2 or GodotType.Rect2I } => ["x", "y", "width", "height"],
        GodotMath { Type: GodotType.Plane } => ["x", "y", "z", "d"],
        GodotMath => ["x", "y", "z", "w"],
        _ => [],
    };

    private static string Cut(string words) => words.Length <= Longest ? words : words[..(Longest - 1)] + "…";
}

/// <summary>The last minute of each member's readings that have numbers, by the node and the member: at most one a second
/// (a reading in the same second as the last takes its place), those older than a minute let go, and a member whose value
/// comes with another count of numbers begun again. The times are given, in milliseconds.</summary>
public sealed class GodotLiveHistory
{
    /// <summary>How far back it keeps, in milliseconds.</summary>
    public const long Span = 60_000;

    private readonly Dictionary<(ulong Node, string Member), List<(long At, double[] Parts)>> kept = [];

    /// <summary>A member's value read at a time; nothing for a value of no numbers.</summary>
    public void Add(ulong node, string member, long at, object? value)
    {
        if (GodotLiveWords.Parts(value) is not { } parts)
        {
            kept.Remove((node, member));
            return;
        }
        if (!kept.TryGetValue((node, member), out List<(long At, double[] Parts)>? readings))
        {
            kept[(node, member)] = readings = [];
        }
        if (readings.Count > 0 && readings[^1].Parts.Length != parts.Length)
        {
            readings.Clear();
        }
        if (readings.Count > 0 && readings[^1].At / 1000 == at / 1000)
        {
            readings[^1] = (at, parts);
        }
        else
        {
            readings.Add((at, parts));
        }
        int old = 0;
        while (old < readings.Count && readings[old].At < at - Span)
        {
            old++;
        }
        readings.RemoveRange(0, old);
    }

    /// <summary>A member's readings within the minute before <paramref name="now"/>, the oldest first.</summary>
    public IReadOnlyList<(long At, double[] Parts)> Of(ulong node, string member, long now) =>
        kept.TryGetValue((node, member), out List<(long At, double[] Parts)>? readings) ? [.. readings.Where(r => r.At >= now - Span)] : [];

    /// <summary>Lets go of every member read last more than a minute before <paramref name="now"/>.</summary>
    public void Forget(long now)
    {
        foreach ((ulong, string) key in kept.Where(p => p.Value.Count == 0 || p.Value[^1].At < now - Span).Select(p => p.Key).ToList())
        {
            kept.Remove(key);
        }
    }

    public void Clear() => kept.Clear();
}
