using System.Globalization;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>A running game's value as a scene writes it (<see cref="Scene"/>: <c>Vector2(100, 50)</c>, <c>2.0</c>,
/// <c>Color(1, 0, 0, 1)</c>) and as C# writes it for a member's default (<see cref="CSharp"/>: <c>new Vector2(100f, 50f)</c>,
/// <c>2f</c>), each number as <see cref="Number"/> writes it: a single's as a single, so 0.3 sent as one reads 0.3.</summary>
public static class GodotText
{
    /// <summary>A number as the Godot Engine's VariantWriter writes it (core/variant/variant_parser.cpp, MIT): the shortest
    /// digits reading back as the single it is where a single holds it, else as the double, in fixed notation from 1e-4 up to
    /// 1e15 and as <c>1.5e-05</c> beyond, as its grisu2 lays them out; zero as 0, and nan, inf and inf_neg.</summary>
    public static string Number(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }
        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "inf_neg";
        }
        if (value == 0)
        {
            return "0";
        }
        string shortest = (double)(float)value == value
            ? ((float)value).ToString("R", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);
        bool negative = shortest[0] == '-';
        string unsigned = negative ? shortest[1..] : shortest;
        int e = unsigned.IndexOfAny(['E', 'e']);
        string mantissa = e < 0 ? unsigned : unsigned[..e];
        int exponent = e < 0 ? 0 : int.Parse(unsigned.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        string digits = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        int point = (dot < 0 ? mantissa.Length : dot) + exponent;
        int lead = digits.Length - digits.TrimStart('0').Length;
        digits = digits[lead..].TrimEnd('0');
        point -= lead;
        int count = digits.Length;
        string written = point >= count && point <= 15 ? digits + new string('0', point - count)
            : point > 0 && point <= 15 ? $"{digits[..point]}.{digits[point..]}"
            : point > -4 && point <= 0 ? $"0.{new string('0', -point)}{digits}"
            : $"{(count == 1 ? digits : $"{digits[0]}.{digits[1..]}")}e{(point - 1 < 0 ? '-' : '+')}{Math.Abs(point - 1).ToString("00", CultureInfo.InvariantCulture)}";
        return negative ? "-" + written : written;
    }

    /// <summary>The step Godot's inspector shows a float with no range of its own at (its interface/inspector/default_float_step).</summary>
    public const double InspectorStep = 0.001;

    private static readonly double[] StepFractions = [0.9999, 0.09999, 0.009999, 0.0009999, 0.00009999, 0.000009999, 0.0000009999,
        0.00000009999, 0.000000009999, 0.0000000009999];

    /// <summary>How many decimals Godot's inspector shows a number with at a step, as its Math::range_step_decimals counts
    /// them: the step's own (0.001's three, a whole step's none), up to ten, and as many as a double holds for no step.</summary>
    public static int StepDecimals(double step)
    {
        if (step < 0.0000000000001)
        {
            return 15;
        }
        double fraction = Math.Abs(step) - Math.Truncate(Math.Abs(step));
        int found = Array.FindIndex(StepFractions, least => fraction >= least);
        return Math.Max(0, found);
    }

    /// <summary>A number as Godot's inspector shows it at a step: rounded to the step's decimals, in its shortest form.</summary>
    public static string Inspected(double value, double step) => double.IsFinite(value) ? Number(Math.Round(value, StepDecimals(step))) : Number(value);

    /// <summary>A value as a scene's property writes it, or null for one a scene cannot hold as text (an object).</summary>
    public static string? Scene(object? value) => value switch
    {
        null => "null",
        bool flag => flag ? "true" : "false",
        long whole => whole.ToString(CultureInfo.InvariantCulture),
        double real => Real(real),
        string text => GodotWrites.Quoted(text),
        GodotName name => "&" + GodotWrites.Quoted(name.Name),
        GodotNodePath path => $"NodePath({GodotWrites.Quoted(path.ToString())})",
        GodotMath math => $"{SceneName(math.Type)}({string.Join(", ", math.Values.Select(v => math.Type is GodotType.Vector2I or GodotType.Vector3I or GodotType.Vector4I or GodotType.Rect2I ? ((long)v).ToString(CultureInfo.InvariantCulture) : Number(v)))})",
        GodotDictionary dictionary => Each(dictionary.Entries.SelectMany(e => new[] { e.Key, e.Value })) is { } parts
            ? Typed($"{{{string.Join(", ", parts.Chunk(2).Select(p => $"{p[0]}: {p[1]}"))}}}", dictionary.KeyType, dictionary.ValueType)
            : null,
        List<object?> items => Each(items) is { } parts ? Typed($"[{string.Join(", ", parts)}]", items is GodotArray typed ? typed.Element : default, null) : null,
        string[] or byte[] or int[] or long[] or float[] or double[] or GodotPackedMath => GodotVariant.Items(value) is { } items && Each(items) is { } parts
            ? $"{SceneName(GodotVariant.TypeOf(value))}({string.Join(", ", value is GodotPackedMath ? parts.Select(p => p[(p.IndexOf('(', StringComparison.Ordinal) + 1)..^1]) : parts)})"
            : null,
        _ => null,
    };

    /// <summary>Each value as a scene writes it, or null where one cannot be written.</summary>
    private static List<string>? Each(IEnumerable<object?> values)
    {
        List<string> written = [];
        foreach (object? value in values)
        {
            if (Scene(value) is not { } one)
            {
                return null;
            }
            written.Add(one);
        }
        return written;
    }

    /// <summary>A container's text in its type's form where it is typed ("Array[int]([1, 2])"); as it is where it is not.</summary>
    private static string Typed(string text, GodotElement first, GodotElement? second) =>
        first.Kind == 0 && second is not { Kind: not 0 } ? text
        : second is { } values ? $"Dictionary[{ElementName(first)}, {ElementName(values)}]({text})"
        : $"Array[{ElementName(first)}]({text})";

    private static string ElementName(GodotElement element) => element.Kind switch
    {
        0 => "Variant",
        1 => GodotVariant.TypeName(element.Type),
        _ => element.Name,
    };

    /// <summary>A scalar real as a scene writes it: a whole one with ".0", so it reads back as a real.</summary>
    private static string Real(double value)
    {
        string written = Number(value);
        return written.Contains('.', StringComparison.Ordinal) || written.Contains('E', StringComparison.OrdinalIgnoreCase) || written.StartsWith("inf", StringComparison.Ordinal)
            || written == "nan" ? written : written + ".0";
    }

    /// <summary>A Variant type's name as a scene writes a value of it.</summary>
    private static string SceneName(GodotType type) => GodotVariant.TypeName(type);

    /// <summary>A value as C# writes a member's default, for the member's declared type (<paramref name="declared"/>: "float",
    /// "double", an enum's name), or null for one C# is not written for here. An enum's value is its name among the
    /// choices given.</summary>
    public static string? CSharp(object? value, string declared, IReadOnlyList<GodotChoice>? choices = null)
    {
        string type = declared.Trim();
        return value switch
        {
            bool flag => flag ? "true" : "false",
            long whole when choices?.FirstOrDefault(c => c.Value is long v && v == whole) is { } choice && !IsInteger(type) => $"{type}.{choice.Name}",
            long whole => whole.ToString(CultureInfo.InvariantCulture),
            double real when type is "double" or "System.Double" or "Double" => Literal(real, "d"),
            double real => Literal(real, "f"),
            string text => CSharpLiterals.Quoted(text),
            GodotName name => CSharpLiterals.Quoted(name.Name),
            GodotNodePath path => $"new NodePath({CSharpLiterals.Quoted(path.ToString())})",
            GodotMath { Type: GodotType.Vector2I or GodotType.Vector3I or GodotType.Vector4I or GodotType.Rect2I } math =>
                $"new {CSharpName(math.Type)}({string.Join(", ", math.Values.Select(v => ((long)v).ToString(CultureInfo.InvariantCulture)))})",
            GodotMath { Type: GodotType.Vector2 or GodotType.Vector3 or GodotType.Vector4 or GodotType.Color or GodotType.Rect2 or GodotType.Quaternion or GodotType.Plane } math =>
                $"new {CSharpName(math.Type)}({string.Join(", ", math.Values.Select(v => Literal(v, "f")))})",
            _ => null,
        };
    }

    private static bool IsInteger(string type) => type is "int" or "long" or "short" or "byte" or "uint" or "ulong" or "ushort" or "sbyte"
        or "Int32" or "Int64" or "System.Int32" or "System.Int64";

    private static string CSharpName(GodotType type) => type switch
    {
        GodotType.Vector2I => "Vector2I",
        GodotType.Vector3I => "Vector3I",
        GodotType.Vector4I => "Vector4I",
        GodotType.Rect2I => "Rect2I",
        _ => type.ToString(),
    };

    /// <summary>A real as a C# literal of a suffix's type: "2f", "0.5f", "1.5d" as "1.5".</summary>
    private static string Literal(double value, string suffix)
    {
        if (double.IsNaN(value))
        {
            return suffix == "f" ? "float.NaN" : "double.NaN";
        }
        if (double.IsInfinity(value))
        {
            return (suffix == "f" ? "float." : "double.") + (value > 0 ? "PositiveInfinity" : "NegativeInfinity");
        }
        string written = suffix == "f" ? ((float)value).ToString("R", CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture);
        return suffix == "f" ? written + "f" : written.Contains('.', StringComparison.Ordinal) || written.Contains('E', StringComparison.OrdinalIgnoreCase) ? written : written + ".0";
    }
}
